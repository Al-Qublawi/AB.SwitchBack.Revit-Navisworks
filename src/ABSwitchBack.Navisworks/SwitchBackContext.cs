using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.DocumentParts;
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
    /// All Navisworks-side state: the pipe listener, the discovery advert, the trigger
    /// gesture and the currently paired Revit instance. Created once at startup by
    /// SwitchBackWatcher.
    ///
    /// The gesture is detected from the selection events, not from a mouse hook. Ctrl+click
    /// always toggles the clicked item in or out of the Navisworks selection, so the change
    /// events always fire, and comparing the selection before and after identifies exactly
    /// which element was clicked. Nothing runs on the system input path.
    /// </summary>
    internal static class SwitchBackContext
    {
        /// <summary>
        /// Above this many selected items the before/after snapshot is skipped. Hashing
        /// ModelItems crosses into native code, so an unbounded snapshot on a Select All
        /// would be felt by the user.
        /// </summary>
        private const int MaxTrackedSelection = 10000;

        private static readonly object Gate = new object();

        private static PipeServer _server;
        private static InstanceRegistry _registry;
        private static SynchronizationContext _uiContext;
        private static bool _started;
        private static bool _busy;

        private static ClickTrigger _trigger = ClickTrigger.Ctrl;
        private static bool _triggerEnabled;

        // Selection tracking. Navisworks reports only that the selection changed, never
        // what changed, so the previous set is captured in Changing and diffed in Changed.
        private static DocumentCurrentSelection _watchedSelection;
        private static HashSet<ModelItem> _snapshot;
        private static bool _snapshotSkipped;

        public static bool IsRunning { get { return _started; } }
        public static string PipeName { get { return _registry != null ? _registry.Self.PipeName : "(not started)"; } }
        public static bool TriggerArmed { get { return _triggerEnabled && _watchedSelection != null; } }
        public static string TriggerDescription
        {
            get { return _triggerEnabled ? TriggerGesture.Describe(_trigger) : "(trigger disabled)"; }
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

                _triggerEnabled = cfg.EnableClickHook;
                _trigger = TriggerGesture.Parse(cfg.Trigger);

                if (_triggerEnabled)
                {
                    if (_trigger == ClickTrigger.CtrlShift)
                    {
                        Log.Warn("Trigger is Ctrl+Shift+Click. Navisworks intercepts that combination " +
                                 "and expands the pick to the whole model file; Ctrl+Click is recommended.");
                    }
                    Log.Info("Trigger gesture: " + TriggerGesture.Describe(_trigger));
                }
                else
                {
                    Log.Info("Trigger disabled by configuration.");
                }

                try { NavisApp.ActiveDocumentChanged += OnActiveDocumentChanged; }
                catch (Exception ex) { Log.Warn("ActiveDocumentChanged subscription failed: " + ex.Message); }

                SubscribeToSelection();
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
                UnsubscribeFromSelection();

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

        // ------------------------------------------------------------ selection tracking

        private static void SubscribeToSelection()
        {
            try
            {
                Document doc = NavisApp.ActiveDocument;
                if (doc == null) return;

                DocumentCurrentSelection selection = doc.CurrentSelection;
                if (ReferenceEquals(selection, _watchedSelection)) return;

                UnsubscribeFromSelection();

                selection.Changing += OnSelectionChanging;
                selection.Changed += OnSelectionChanged;
                _watchedSelection = selection;
                _snapshot = null;
                _snapshotSkipped = false;

                Log.Info("Selection tracking active.");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not subscribe to selection changes: " + ex.Message);
            }
        }

        private static void UnsubscribeFromSelection()
        {
            try
            {
                if (_watchedSelection != null)
                {
                    _watchedSelection.Changing -= OnSelectionChanging;
                    _watchedSelection.Changed -= OnSelectionChanged;
                }
            }
            catch { }

            _watchedSelection = null;
            _snapshot = null;
            _snapshotSkipped = false;
        }

        /// <summary>
        /// Fires before the selection changes. The snapshot is taken only while the trigger
        /// modifiers are actually held, so ordinary picking and navigation cost nothing.
        /// </summary>
        private static void OnSelectionChanging(object sender, EventArgs e)
        {
            _snapshot = null;
            _snapshotSkipped = false;

            if (!_triggerEnabled) return;

            try
            {
                if (!TriggerGesture.ModifiersHeld(_trigger)) return;

                Document doc = NavisApp.ActiveDocument;
                if (doc == null || doc.IsClear) return;

                ModelItemCollection current = doc.CurrentSelection.SelectedItems;
                if (current == null) return;

                if (current.Count > MaxTrackedSelection)
                {
                    _snapshotSkipped = true;
                    return;
                }

                _snapshot = new HashSet<ModelItem>(current);
            }
            catch (Exception ex)
            {
                Log.Warn("Selection snapshot failed: " + ex.Message);
                _snapshot = null;
            }
        }

        /// <summary>
        /// Fires after the selection changed. A one-item difference in either direction
        /// identifies the clicked element exactly: Ctrl+click ADDS an unselected element and
        /// REMOVES an already selected one, and both cases mean "this is what you clicked".
        /// Bulk changes such as Select All differ by far more than one item and are ignored.
        /// </summary>
        private static void OnSelectionChanged(object sender, EventArgs e)
        {
            HashSet<ModelItem> before = _snapshot;
            bool skipped = _snapshotSkipped;

            _snapshot = null;
            _snapshotSkipped = false;

            if (!_triggerEnabled) return;

            try
            {
                // The modifiers must still be down: this rules out a change that merely
                // happened to follow a snapshot.
                if (!TriggerGesture.ModifiersHeld(_trigger)) return;

                if (skipped)
                {
                    PostToUi(() => ShowWarning("Too many elements selected",
                        "SwitchBack could not work out which element you clicked because more than " +
                        MaxTrackedSelection.ToString(CultureInfo.InvariantCulture) +
                        " elements were already selected.\r\n\r\nPress Esc to clear the selection and try again."));
                    return;
                }

                if (before == null) return;

                Document doc = NavisApp.ActiveDocument;
                if (doc == null || doc.IsClear) return;

                ModelItemCollection current = doc.CurrentSelection.SelectedItems;
                if (current == null || current.Count > MaxTrackedSelection) return;

                var after = new HashSet<ModelItem>(current);
                ModelItem clicked = FindSingleDifference(before, after);
                if (clicked == null) return;

                // Run outside the event so the switch back never re-enters Navisworks'
                // own selection processing.
                PostToUi(() => HandleTrigger(clicked));
            }
            catch (Exception ex)
            {
                Log.Warn("Selection tracking failed: " + ex.Message);
            }
        }

        /// <summary>The one item added, or failing that the one item removed. Null if ambiguous.</summary>
        private static ModelItem FindSingleDifference(HashSet<ModelItem> before, HashSet<ModelItem> after)
        {
            ModelItem added = null;
            int addedCount = 0;
            foreach (ModelItem item in after)
            {
                if (before.Contains(item)) continue;
                added = item;
                if (++addedCount > 1) return null;
            }
            if (addedCount == 1) return added;

            ModelItem removed = null;
            int removedCount = 0;
            foreach (ModelItem item in before)
            {
                if (after.Contains(item)) continue;
                removed = item;
                if (++removedCount > 1) return null;
            }
            return removedCount == 1 ? removed : null;
        }

        // ------------------------------------------------------------ gesture

        /// <summary>Runs on the UI thread, just after the selection settled.</summary>
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

        // ------------------------------------------------------------ plumbing

        private static string DetectVersion()
        {
            string build = PluginBootstrap.BuildVersion;
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
            SubscribeToSelection();
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
