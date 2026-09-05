using System;
using System.Diagnostics;
using System.Reflection;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
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

        private void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
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

                var target = new PushButtonData(
                    "ABSwitchBackTarget", "Navisworks" + Environment.NewLine + "Target",
                    asm, typeof(SelectNavisworksTargetCommand).FullName);
                target.ToolTip = "Choose which running Navisworks instance this Revit session pairs with.";
                target.LongDescription =
                    "Lists every running Navisworks that has SwitchBack loaded, with its version, " +
                    "open document and process id. Use Test to confirm the connection.";
                target.LargeImage = Icons.LogoLarge;
                target.Image = Icons.LogoSmall;
                panel.AddItem(target);

                // No Settings button here by design: Revit only receives. Everything
                // configurable - the trigger gesture and what Revit does with the element -
                // is set from the Navisworks ribbon, which is where the workflow starts.
                var status = new PushButtonData(
                    "ABSwitchBackStatus", "Status" + Environment.NewLine + "and Log",
                    asm, typeof(ShowStatusCommand).FullName);
                status.ToolTip = "Show the SwitchBack listener status and open the log folder.";
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
