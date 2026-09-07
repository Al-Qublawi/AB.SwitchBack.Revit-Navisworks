using System;
using System.Diagnostics;
using System.Reflection;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using TaskDialogResult = Autodesk.Revit.UI.TaskDialogResult;
using TaskDialogCommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons;
using TaskDialogCommandLinkId = Autodesk.Revit.UI.TaskDialogCommandLinkId;
using ABSwitchBack.Core;
using ABSwitchBack.Core.Discovery;
using ABSwitchBack.Core.Ipc;
using ABSwitchBack.Core.Protocol;

namespace ABSwitchBack.Revit
{
    /// <summary>
    /// Revit entry point. Starts a background named pipe listener, advertises this
    /// process so Navisworks can find it, and marshals incoming requests onto the
    /// Revit UI thread through an ExternalEvent.
    /// </summary>
    public sealed class App : IExternalApplication
    {
        private const string TabName = "AB SwitchBack";
        private const string PanelName = "SwitchBack";
        private const string AboutPanelName = "About";

        internal static App Current { get; private set; }

        private PipeServer _server;
        private InstanceRegistry _registry;
        private ExternalEvent _externalEvent;
        private SwitchBackEventHandler _handler;
        private UIControlledApplication _uiCtrlApp;
        private string _versionNumber = "0";

        // Set by the background update check, shown once Revit is idle with a view open.
        private volatile UpdateInfo _pendingUpdate;

        // The window handle is taken from the live UIApplication at the moment it is
        // needed (see SwitchBackEventHandler), so nothing is cached here.
        internal string PipeName { get { return _server != null ? _server.PipeName : "(not started)"; } }
        internal bool ListenerRunning { get { return _server != null && _server.IsRunning; } }

        public Result OnStartup(UIControlledApplication application)
        {
            Current = this;
            _uiCtrlApp = application;

            try
            {
                Log.Init("Revit");
                SwitchBackConfig.EnsureDefaultFile();

                try { _versionNumber = application.ControlledApplication.VersionNumber; }
                catch { _versionNumber = "unknown"; }

                Log.Info("Starting SwitchBack in Revit " + _versionNumber +
                         " (PID " + Process.GetCurrentProcess().Id + ")");

                _handler = new SwitchBackEventHandler();
                _externalEvent = ExternalEvent.Create(_handler);

                _registry = new InstanceRegistry(PipeNames.RoleRevit, "Autodesk Revit", _versionNumber);

                _server = new PipeServer(_registry.Self.PipeName, OnMessageReceived);
                _server.Start();

                BuildRibbon(application);

                // Background: never blocks Revit starting. The result is shown later, from
                // ViewActivated, where a dialog is safe.
                System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    UpdateInfo info = UpdateCheck.Check();
                    if (info != null && info.ShouldNotify)
                    {
                        _pendingUpdate = info;
                        Log.Info("Update available: " + info.LatestVersion +
                                 " (running " + Branding.Version + ")");
                    }
                });

                // Keep the advertised document name current so the Navisworks picker is useful.
                try { application.ViewActivated += OnViewActivated; }
                catch (Exception ex) { Log.Warn("ViewActivated subscription failed: " + ex.Message); }

                try { application.ControlledApplication.DocumentClosed += OnDocumentClosed; }
                catch (Exception ex) { Log.Warn("DocumentClosed subscription failed: " + ex.Message); }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("SwitchBack failed to start.", ex);
                // Never block Revit from loading because of us.
                return Result.Succeeded;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                Log.Info("Shutting down SwitchBack.");

                try { application.ViewActivated -= OnViewActivated; } catch { }
                try { application.ControlledApplication.DocumentClosed -= OnDocumentClosed; } catch { }

                if (_server != null) { _server.Dispose(); _server = null; }
                if (_registry != null) { _registry.Dispose(); _registry = null; }
                if (_externalEvent != null) { _externalEvent.Dispose(); _externalEvent = null; }
            }
            catch (Exception ex)
            {
                Log.Error("Error during shutdown.", ex);
            }
            Current = null;
            return Result.Succeeded;
        }

        /// <summary>
        /// The update notice waits for this: Revit is idle with a document open, which is a
        /// safe moment for a dialog. Popping one during OnStartup can stall the splash
        /// screen or sit behind it unseen.
        /// </summary>
        private void ShowUpdateNoticeOnce()
        {
            UpdateInfo info = _pendingUpdate;
            if (info == null) return;
            _pendingUpdate = null;

            try
            {
                UpdateCheck.MarkNotified(info.LatestVersion);

                var dialog = new TaskDialog(Branding.ProductName);
                dialog.MainInstruction = Branding.ProductName + " " + info.LatestVersion + " is available.";
                dialog.MainContent = "You are running " + Branding.Version + "." + Environment.NewLine +
                                     Environment.NewLine +
                                     "You can turn this check off in Settings, on the Navisworks ribbon.";
                dialog.CommonButtons = TaskDialogCommonButtons.Close;
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the download page");

                if (dialog.Show() == TaskDialogResult.CommandLink1) UpdateCheck.OpenReleasesPage();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not show the update notice: " + ex.Message);
            }
        }

        private void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
                ShowUpdateNoticeOnce();

                if (_registry != null && e.Document != null)
                    _registry.UpdateDocument(e.Document.Title);
            }
            catch (Exception ex) { Log.Warn("ViewActivated handler: " + ex.Message); }
        }

        private void OnDocumentClosed(object sender, DocumentClosedEventArgs e)
        {
            try { if (_registry != null) _registry.UpdateDocument(string.Empty); }
            catch { }
        }

        /// <summary>
        /// Runs on the pipe listener background thread. It must never call the Revit API,
        /// so it only queues the id and raises the external event.
        /// </summary>
        private SwitchBackMessage OnMessageReceived(SwitchBackMessage request)
        {
            int pid = Process.GetCurrentProcess().Id;

            switch (request.Type)
            {
                case SwitchBackMessageType.Ping:
                    string doc = _registry != null ? _registry.Self.Document : string.Empty;
                    return new SwitchBackMessage(SwitchBackMessageType.Pong, pid, 0,
                                                 "Revit " + _versionNumber + " | " + doc);

                case SwitchBackMessageType.Select:
                    if (request.ElementId <= 0)
                        return new SwitchBackMessage(SwitchBackMessageType.Error, pid, request.ElementId,
                                                     "Element id " + request.ElementId + " is not valid.");

                    Log.Info("Received element " + request.ElementId + " from PID " + request.SourcePid);
                    _handler.Enqueue(request.ElementId);

                    // Raise() is explicitly safe from any thread; Revit runs Execute when idle.
                    _externalEvent.Raise();

                    return new SwitchBackMessage(SwitchBackMessageType.Ack, pid, request.ElementId, "Queued");

                default:
                    return new SwitchBackMessage(SwitchBackMessageType.Error, pid, 0,
                                                 "Unsupported message type: " + request.Type);
            }
        }

        private static void BuildRibbon(UIControlledApplication application)
        {
            try
            {
                try { application.CreateRibbonTab(TabName); }
                catch { /* tab already exists */ }

                RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
                string asm = Assembly.GetExecutingAssembly().Location;

                // Revit only receives, so its ribbon is deliberately one diagnostic button.
                // The trigger gesture and everything else configurable is set from the
                // Navisworks ribbon, which is where the workflow starts.
                var status = new PushButtonData(
                    "ABSwitchBackStatus", "Status" + Environment.NewLine + "and Log",
                    asm, typeof(ShowStatusCommand).FullName);
                status.ToolTip = "Listener status, the settings in force, running Navisworks instances, and the log folder.";
                status.LongDescription =
                    "Confirms this Revit session is listening, shows what will happen when an " +
                    "element arrives, and lists every running Navisworks that has SwitchBack " +
                    "loaded so the connection can be tested.";
                status.LargeImage = Icons.StatusLarge;
                status.Image = Icons.StatusSmall;
                panel.AddItem(status);

                // Author and product identity, matching the other AB add-ins.
                RibbonPanel aboutPanel = application.CreateRibbonPanel(TabName, AboutPanelName);

                var about = new PushButtonData(
                    "ABSwitchBackAbout", "About",
                    asm, typeof(AboutCommand).FullName);
                about.ToolTip = Branding.AboutLine;
                about.LargeImage = Icons.LogoLarge;
                about.Image = Icons.LogoSmall;
                aboutPanel.AddItem(about);

                var linkedIn = new PushButtonData(
                    "ABSwitchBackLinkedIn", "LinkedIn",
                    asm, typeof(LinkedInCommand).FullName);
                linkedIn.ToolTip = Branding.LinkedInCaption;
                linkedIn.LongDescription = "Open the author's LinkedIn profile in your browser.";
                linkedIn.LargeImage = Icons.LinkedInLarge;
                linkedIn.Image = Icons.LinkedInSmall;
                aboutPanel.AddItem(linkedIn);
            }
            catch (Exception ex)
            {
                Log.Error("Could not build the ribbon.", ex);
            }
        }
    }
}
