using System;
using System.Diagnostics;
using System.Reflection;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ABAdvTools;
using ABAdvTools.Revit;
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
        /// <summary>SwitchBack's panel on the shared AB Adv Tools tab.</summary>
        private const string PanelName = "SwitchBack";

        internal static App Current { get; private set; }

        private PipeServer _server;
        private InstanceRegistry _registry;
        private ExternalEvent _externalEvent;
        private SwitchBackEventHandler _handler;
        private string _versionNumber = "0";

        // The window handle is taken from the live UIApplication at the moment it is
        // needed (see SwitchBackEventHandler), so nothing is cached here.
        internal string PipeName { get { return _server != null ? _server.PipeName : "(not started)"; } }
        internal bool ListenerRunning { get { return _server != null && _server.IsRunning; } }

        public Result OnStartup(UIControlledApplication application)
        {
            Current = this;

            try
            {
                Log.Init("Revit");
                SwitchBackConfig.EnsureDefaultFile();

                try { _versionNumber = application.ControlledApplication.VersionNumber; }
                catch { _versionNumber = "unknown"; }

                Log.Info("Starting SwitchBack " + Branding.Version + " in Revit " + _versionNumber +
                         " (PID " + Process.GetCurrentProcess().Id + ")");

                _handler = new SwitchBackEventHandler();
                _externalEvent = ExternalEvent.Create(_handler);

                _registry = new InstanceRegistry(PipeNames.RoleRevit, "Autodesk Revit", _versionNumber);

                _server = new PipeServer(_registry.Self.PipeName, OnMessageReceived);
                _server.Start();

                // The AB Adv Tools suite: shared tab and About panel, and a background release
                // check whose notice waits for a view to activate. The SwitchBack settings
                // dialog's "Check for updates" switch still turns the automatic check off.
                // A store build has neither the panel nor the check (see Product).
                try { RevitAdvTools.Initialize(application, Product); }
                catch (Exception ex) { Log.Warn("AB Adv Tools initialisation failed: " + ex.Message); }

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
                RevitAdvTools.Shutdown(application);

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
        /// AB SwitchBack as the AB Adv Tools suite knows it. Details and the folder button carry what
        /// SwitchBack's own About dialog showed before it joined the suite.
        /// </summary>
        private static AdvToolsProduct Product
        {
            get
            {
                var product = new AdvToolsProduct(
                    "SwitchBack",
                    Branding.ProductName,
                    Branding.Tagline,
                    AdvToolsHost.Revit,
                    Branding.ReleaseRepository,
                    typeof(App).Assembly)
                {
                    AutomaticChecksAllowed = delegate { return !Branding.IsStoreBuild && SwitchBackConfig.Load().CheckForUpdates; },

                    // The Marketplace forbids an app's own update mechanism: a store build has no
                    // release channel and does not build the shared panel with its Check for Updates
                    // button. Another AB tool installed beside it still builds that panel.
                    BuildsSharedPanel = !Branding.IsStoreBuild,
                    UpdateSettingsHint = "SwitchBack Settings on the Navisworks ribbon",
                    Details = delegate
                    {
                        return "Ctrl+click an element in Navisworks to select it here, section box it and zoom to it." +
                               Environment.NewLine + "Settings and logs: " + Paths.Root;
                    }
                };
                product.AddAction("Open the settings and log folder", delegate { Branding.OpenDataFolder(); });
                return product;
            }
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
                RibbonPanel panel = RevitAdvTools.GetToolPanel(application, PanelName);
                string asm = Assembly.GetExecutingAssembly().Location;

                // Revit only receives, so its panel is deliberately one diagnostic button.
                // The trigger gesture and everything else configurable is set from the
                // Navisworks ribbon, which is where the workflow starts. About, release
                // checks and LinkedIn live on the suite's shared panel.
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
                AddHelp(panel.AddItem(status));

                var help = new PushButtonData(
                    "ABSwitchBackHelp", "Help",
                    asm, typeof(ShowHelpCommand).FullName);
                help.ToolTip = "Open the AB SwitchBack user guide.";
                help.AvailabilityClassName = typeof(AlwaysAvailable).FullName;
                help.LargeImage = RevitAdvTools.LoadPng("abadv_about_32.png");
                help.Image = RevitAdvTools.LoadPng("abadv_about_16.png");
                AddHelp(panel.AddItem(help));
            }
            catch (Exception ex)
            {
                Log.Error("Could not build the ribbon.", ex);
            }
        }

        /// <summary>F1 on a SwitchBack button opens the installed user guide, when there is one.</summary>
        private static void AddHelp(RibbonItem item)
        {
            string page = Branding.HelpFile;
            if (item == null || page == null) return;
            try { item.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, new Uri(page).AbsoluteUri)); }
            catch (Exception ex) { Log.Warn("Contextual help not set: " + ex.Message); }
        }
    }
}
