using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ABSwitchBack.Core;
using ABSwitchBack.Core.Discovery;
using ABSwitchBack.Core.Ipc;
using ABSwitchBack.Core.UI;

// .NET 8 WinForms (Revit 2025+) ships its own System.Windows.Forms.TaskDialog, which
// collides with Revit's. These aliases pin the Revit types for every version.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using TaskDialogResult = Autodesk.Revit.UI.TaskDialogResult;
using TaskDialogCommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons;
using TaskDialogCommandLinkId = Autodesk.Revit.UI.TaskDialogCommandLinkId;

namespace ABSwitchBack.Revit
{
    /// <summary>Wraps a raw HWND so WinForms dialogs can be owned by the Revit window.</summary>
    internal sealed class HostWindow : IWin32Window
    {
        private readonly IntPtr _handle;
        public HostWindow(IntPtr handle) { _handle = handle; }
        public IntPtr Handle { get { return _handle; } }
    }

    /// <summary>
    /// The single diagnostic entry point on the Revit ribbon: listener state, the settings
    /// currently in force, the running Navisworks instances, and the log folder.
    ///
    /// Revit only ever receives, so there is deliberately nothing to configure or pair here.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ShowStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                App app = App.Current;
                int pid = Process.GetCurrentProcess().Id;
                SwitchBackConfig cfg = SwitchBackConfig.Load();

                string listening = app != null && app.ListenerRunning ? "Listening" : "NOT running";
                string pipe = app != null ? app.PipeName : "(unknown)";

                int navisCount = InstanceRegistry.List(PipeNames.RoleNavisworks).Count;

                string body =
                    "Listener: " + listening + Environment.NewLine +
                    "Pipe: " + pipe + Environment.NewLine +
                    "This Revit PID: " + pid.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                    "Running Navisworks instances: " + navisCount.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                    Environment.NewLine +
                    "Section box: " + (cfg.CreateSectionBox ? "on" : "off") + Environment.NewLine +
                    "Section box margin: " + cfg.SectionBoxMarginMm.ToString("0", CultureInfo.InvariantCulture) + " mm" + Environment.NewLine +
                    "Create a 3D view if needed: " + (cfg.CreateViewIfMissing ? "yes" : "no") + Environment.NewLine +
                    Environment.NewLine +
                    "These are changed from the Settings button on the Navisworks ribbon." + Environment.NewLine +
                    Environment.NewLine +
                    "Config and logs: " + Paths.Root;

                var dialog = new TaskDialog("AB SwitchBack");
                dialog.MainInstruction = "SwitchBack status";
                dialog.MainContent = body;
                dialog.CommonButtons = TaskDialogCommonButtons.Close;
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    "Show running Navisworks instances",
                    "Check that Revit and Navisworks can see each other, and test the connection.");
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the log and settings folder");

                TaskDialogResult result = dialog.Show();

                if (result == TaskDialogResult.CommandLink1)
                {
                    // Read-only: Revit never sends, so there is no destination to choose.
                    InstancePickerForm.ShowReadOnly(
                        new HostWindow(commandData.Application.MainWindowHandle),
                        PipeNames.RoleNavisworks,
                        "SwitchBack - running Navisworks instances",
                        cfg.PipeTimeoutMs);
                }
                else if (result == TaskDialogResult.CommandLink2)
                {
                    Branding.OpenDataFolder();
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("ShowStatusCommand failed.", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Product and author identity, with a link out to LinkedIn.</summary>
    [Transaction(TransactionMode.Manual)]
    public class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var dialog = new TaskDialog(Branding.ProductName);
                dialog.MainInstruction = Branding.ProductName + " " + Branding.Version;
                dialog.MainContent =
                    Branding.Tagline + Environment.NewLine + Environment.NewLine +
                    "Ctrl+click an element in Navisworks to select it here, section box it and zoom to it." +
                    Environment.NewLine + Environment.NewLine +
                    "by " + Branding.Author + Environment.NewLine + Environment.NewLine +
                    "Settings and logs: " + Paths.Root;
                dialog.CommonButtons = TaskDialogCommonButtons.Close;
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, Branding.LinkedInCaption);
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the settings and log folder");

                TaskDialogResult result = dialog.Show();
                if (result == TaskDialogResult.CommandLink1) Branding.OpenLinkedIn();
                else if (result == TaskDialogResult.CommandLink2) Branding.OpenDataFolder();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("AboutCommand failed.", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Opens the author's LinkedIn profile.</summary>
    [Transaction(TransactionMode.Manual)]
    public class LinkedInCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (Branding.OpenLinkedIn()) return Result.Succeeded;

            // Falling back to showing the URL beats failing silently.
            var dialog = new TaskDialog(Branding.ProductName);
            dialog.MainInstruction = Branding.LinkedInCaption;
            dialog.MainContent = Branding.LinkedInUrl;
            dialog.Show();
            return Result.Succeeded;
        }
    }
}
