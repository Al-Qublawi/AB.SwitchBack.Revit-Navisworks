using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using ABSwitchBack.Core;
using ABSwitchBack.Core.Discovery;
using ABSwitchBack.Core.Interop;
using ABSwitchBack.Core.Ipc;
using ABSwitchBack.Core.Protocol;
using ABSwitchBack.Core.UI;

using NavisApp = Autodesk.Navisworks.Api.Application;

namespace ABSwitchBack.Navisworks
{
    /// <summary>
    /// Navisworks-side lifecycle and wiring: the pipe listener, the discovery advert, and
    /// sending the chosen element to Revit.
    ///
    /// Detecting the gesture belongs to <see cref="SelectionWatcher"/>; this class only
    /// reacts to what it reports.
    /// </summary>
    internal static class SwitchBackContext
    {
        private static readonly object Gate = new object();

        private static PipeServer _server;
        private static InstanceRegistry _registry;
        private static SelectionWatcher _watcher;
        private static SynchronizationContext _uiContext;
        private static bool _started;
        private static bool _busy;

        public static bool IsRunning { get { return _started; } }
        public static string PipeName { get { return _registry != null ? _registry.Self.PipeName : "(not started)"; } }

        public static bool TriggerArmed
        {
            get { return _watcher != null && _watcher.Enabled && _watcher.IsAttached; }
        }

        public static string TriggerDescription
        {
            get
            {
                if (_watcher == null || !_watcher.Enabled) return "(trigger disabled)";
                return TriggerGesture.Describe(_watcher.Trigger);
            }
        }

        /// <summary>Called on the Navisworks UI thread at application startup.</summary>
        public static void Start()
        {
            lock (Gate)
            {
                if (_started) return;
                _started = true;
            }

            try
            {
                Log.Init("Navisworks");
                SwitchBackConfig.EnsureDefaultFile();
                SwitchBackConfig cfg = SwitchBackConfig.Load();

                _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

                string version = DetectVersion();
                Log.Info("Starting SwitchBack in Navisworks " + version +
                         " (PID " + Process.GetCurrentProcess().Id + ")");

                _registry = new InstanceRegistry(PipeNames.RoleNavisworks, "Autodesk Navisworks", version);
                UpdateDocumentName();

                _server = new PipeServer(_registry.Self.PipeName, OnMessageReceived);
                _server.Start();

                _watcher = new SelectionWatcher(HandleTrigger, WarnSelectionTooLarge);
                ApplySettings(cfg);

                try { NavisApp.ActiveDocumentChanged += OnActiveDocumentChanged; }
                catch (Exception ex) { Log.Warn("ActiveDocumentChanged subscription failed: " + ex.Message); }

                _watcher.AttachToActiveDocument();
            }
            catch (Exception ex)
            {
                Log.Error("SwitchBack failed to start in Navisworks.", ex);
            }
        }

        public static void Stop()
        {
            try
            {
                Log.Info("Stopping SwitchBack.");
                try { NavisApp.ActiveDocumentChanged -= OnActiveDocumentChanged; } catch { }

                if (_watcher != null) { _watcher.Dispose(); _watcher = null; }
                if (_server != null) { _server.Dispose(); _server = null; }
                if (_registry != null) { _registry.Dispose(); _registry = null; }
            }
            catch (Exception ex)
            {
                Log.Error("Error while stopping SwitchBack.", ex);
            }
            finally
            {
                _started = false;
            }
        }

        /// <summary>
        /// Re-reads the trigger settings. Called at startup and again whenever the user
        /// saves the settings dialog, so a change takes effect immediately with no restart.
        /// </summary>
        public static void ApplySettings(SwitchBackConfig cfg)
        {
            if (cfg == null) cfg = SwitchBackConfig.Load();
            if (_watcher == null) return;

            _watcher.Enabled = cfg.TriggerEnabled;
            _watcher.Trigger = TriggerGesture.Parse(cfg.Trigger);

            if (!_watcher.Enabled)
            {
                Log.Info("Trigger disabled by configuration.");
                return;
            }

            if (TriggerGesture.IsReservedByNavisworks(_watcher.Trigger))
            {
                Log.Warn("Trigger includes Ctrl+Shift. Navisworks reserves that combination and " +
                         "expands the pick to the whole model file; Ctrl+Click is recommended.");
            }

            Log.Info("Trigger gesture: " + TriggerGesture.Describe(_watcher.Trigger));
        }

        /// <summary>Opens the settings dialog and applies the result immediately.</summary>
        public static void ShowSettings()
        {
            try
            {
                if (SettingsForm.Show(null)) ApplySettings(null);
            }
            catch (Exception ex)
            {
                Log.Error("Settings dialog failed.", ex);
                ShowWarning("SwitchBack error", ex.Message);
            }
        }

        // ------------------------------------------------------------ gesture

        /// <summary>Runs on the UI thread when the watcher identifies a clicked element.</summary>
        private static void HandleTrigger(ModelItem item)
        {
            // Guard against a second gesture arriving while a picker dialog is open.
            if (_busy) return;
            _busy = true;
            try
            {
                if (item == null) return;

                long elementId;
                string reason;
                if (!ElementIdExtractor.TryGetRevitElementId(item, out elementId, out reason))
                {
                    Log.Warn("No element id on '" + SafeName(item) + "'.");
                    ShowWarning("No Revit Element ID found", reason);
                    return;
                }

                SendToRevit(elementId);
            }
            catch (Exception ex)
            {
                Log.Error("Trigger handling failed.", ex);
                ShowWarning("SwitchBack error", ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private static void WarnSelectionTooLarge(int cap)
        {
            PostToUi(() => ShowWarning("Too many elements selected",
                "SwitchBack could not work out which element you clicked because more than " +
                cap.ToString(CultureInfo.InvariantCulture) +
                " elements were already selected.\r\n\r\nPress Esc to clear the selection and try again."));
        }

        // ------------------------------------------------------------ plumbing

        private static string DetectVersion()
        {
            string build = PluginMetadata.BuildVersion;
            if (!string.IsNullOrEmpty(build)) return build;

            try
            {
                // Navisworks API major 21 == 2024, so year = major + 2003.
                int major = NavisApp.Version.ApiMajor;
                if (major > 0) return (major + 2003).ToString(CultureInfo.InvariantCulture);
            }
            catch { }

            return "unknown";
        }

        private static void OnActiveDocumentChanged(object sender, EventArgs e)
        {
            UpdateDocumentName();
            if (_watcher != null) _watcher.AttachToActiveDocument();
        }

        private static void UpdateDocumentName()
        {
            try
            {
                if (_registry == null) return;

                Document doc = NavisApp.ActiveDocument;
                if (doc == null || doc.IsClear) { _registry.UpdateDocument(string.Empty); return; }

                string name = null;
                try { name = doc.Title; } catch { }
                if (string.IsNullOrEmpty(name))
                {
                    try { name = System.IO.Path.GetFileName(doc.CurrentFileName); } catch { }
                }

                _registry.UpdateDocument(name ?? string.Empty);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not update document name: " + ex.Message);
            }
        }

        /// <summary>Background thread. Navisworks only answers liveness probes.</summary>
        private static SwitchBackMessage OnMessageReceived(SwitchBackMessage request)
        {
            int pid = Process.GetCurrentProcess().Id;

            if (request.Type == SwitchBackMessageType.Ping)
            {
                string doc = _registry != null ? _registry.Self.Document : string.Empty;
                string version = _registry != null ? _registry.Self.Version : "?";
                return new SwitchBackMessage(SwitchBackMessageType.Pong, pid, 0,
                                             "Navisworks " + version + " | " + doc);
            }

            return new SwitchBackMessage(SwitchBackMessageType.Error, pid, 0,
                                         "Navisworks does not accept " + request.Type + " messages.");
        }

        /// <summary>Resolves the destination, then sends without blocking the UI thread.</summary>
        private static void SendToRevit(long elementId)
        {
            SwitchBackConfig cfg = SwitchBackConfig.Load();

            InstanceInfo target = ResolveTarget(cfg);
            if (target == null) return;

            Log.Info("Sending element " + elementId + " to Revit PID " + target.Pid);

            // Hand our foreground right to Revit so its window can come forward.
            WindowFocus.AllowForProcess(target.Pid);

            string pipeName = target.PipeName;
            int timeout = cfg.PipeTimeoutMs;
            int ownPid = Process.GetCurrentProcess().Id;
            string targetLabel = target.DisplayName;

            Task.Factory.StartNew(() =>
            {
                string error;
                var request = new SwitchBackMessage(SwitchBackMessageType.Select, ownPid, elementId, string.Empty);
                SwitchBackMessage reply = PipeClient.Send(pipeName, request, timeout, out error);

                if (reply != null && reply.Type == SwitchBackMessageType.Ack)
                {
                    Log.Info("Revit acknowledged element " + elementId + ".");
                    return;
                }

                string detail = reply != null && reply.Type == SwitchBackMessageType.Error
                    ? reply.Payload
                    : (error ?? "The destination did not respond.");

                Log.Error("Send failed to " + targetLabel + ": " + detail, null);
                PostToUi(() => ShowWarning("Could not reach Revit", targetLabel + "\r\n\r\n" + detail));
            });
        }

        /// <summary>
        /// Uses the remembered Revit instance when it is still alive, silently picks the
        /// only running one, and asks the user when the choice is genuinely ambiguous.
        /// </summary>
        private static InstanceInfo ResolveTarget(SwitchBackConfig cfg)
        {
            List<InstanceInfo> revits = InstanceRegistry.List(PipeNames.RoleRevit);

            if (revits.Count == 0)
            {
                ShowWarning("No Revit instance found",
                            "No running Revit with SwitchBack loaded was found.\r\n\r\n" +
                            "Start Revit, open the project the model came from, and try again.");
                return null;
            }

            if (cfg.RevitTargetPid > 0)
            {
                foreach (InstanceInfo candidate in revits)
                {
                    if (candidate.Pid == cfg.RevitTargetPid) return candidate;
                }
            }

            if (revits.Count == 1)
            {
                cfg.RevitTargetPid = revits[0].Pid;
                cfg.Save();
                return revits[0];
            }

            InstanceInfo chosen = InstancePickerForm.Show(
                null, PipeNames.RoleRevit, "SwitchBack - choose the destination Revit", cfg.PipeTimeoutMs);

            if (chosen == null) return null;

            cfg.RevitTargetPid = chosen.Pid;
            cfg.Save();
            return chosen;
        }

        /// <summary>Opens the destination picker from the ribbon button.</summary>
        public static void ChooseTargetInteractively()
        {
            try
            {
                SwitchBackConfig cfg = SwitchBackConfig.Load();

                InstanceInfo chosen = InstancePickerForm.Show(
                    null, PipeNames.RoleRevit, "SwitchBack - choose the destination Revit", cfg.PipeTimeoutMs);
                if (chosen == null) return;

                cfg.RevitTargetPid = chosen.Pid;
                cfg.Save();
                Log.Info("Navisworks paired with Revit PID " + chosen.Pid);

                MessageBox.Show(
                    "Paired with:\r\n" + chosen.DisplayName + "\r\n\r\n" +
                    "Use " + TriggerDescription + " on an element to send it to Revit.",
                    "AB SwitchBack", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log.Error("Target selection failed.", ex);
                ShowWarning("SwitchBack error", ex.Message);
            }
        }

        private static void PostToUi(Action action)
        {
            try
            {
                if (_uiContext != null) _uiContext.Post(_ => action(), null);
                else action();
            }
            catch (Exception ex)
            {
                Log.Error("Could not marshal to the UI thread.", ex);
            }
        }

        private static void ShowWarning(string title, string body)
        {
            try
            {
                MessageBox.Show(body, "AB SwitchBack - " + title,
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }

        private static string SafeName(ModelItem item)
        {
            try { return item.DisplayName ?? "item"; }
            catch { return "item"; }
        }
    }
}
