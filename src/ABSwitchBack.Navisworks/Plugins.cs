using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using Autodesk.Navisworks.Api.Plugins;

namespace ABSwitchBack.Navisworks
{
    /// <summary>
    /// EventWatcherPlugin is the only plugin kind Navisworks loads automatically at
    /// startup, which is what the background listener and the selection tracking need.
    /// </summary>
    [Plugin("ABSwitchBackWatcher", "ABSB",
        DisplayName = "AB SwitchBack",
        ToolTip = "Sends the clicked element to Revit")]
    public class SwitchBackWatcher : EventWatcherPlugin
    {
        public override void OnLoaded()
        {
            SwitchBackContext.Start();
        }

        public override void OnUnloading()
        {
            SwitchBackContext.Stop();
        }
    }

    /// <summary>
    /// The "AB SwitchBack" ribbon tab.
    ///
    /// The layout lives in ABSwitchBack.xaml, which Navisworks reads as a loose file in a
    /// locale subfolder beside the plugin DLL, and the button images come from the Icon /
    /// LargeIcon names below, resolved against the Images folder next to the DLL.
    /// </summary>
    [Plugin("ABSwitchBack.Ribbon", "ABSB",
        DisplayName = "AB SwitchBack",
        ToolTip = "Send the clicked element to Revit")]
    [RibbonLayout("ABSwitchBack.xaml")]
    [RibbonTab("ID_ABSBTab", DisplayName = "AB SwitchBack", LoadForCanExecute = true)]
    [Command("ID_ABSB_Target",
        DisplayName = "Revit Target",
        Icon = "logo_16.ico", LargeIcon = "logo_32.ico",
        LoadForCanExecute = true,
        ToolTip = "Choose which running Revit instance receives the elements you click.")]
    [Command("ID_ABSB_Settings",
        DisplayName = "Settings",
        Icon = "settings_16.ico", LargeIcon = "settings_32.ico",
        LoadForCanExecute = true,
        ToolTip = "Choose the trigger gesture, turn it on or off, and set what Revit does.")]
    [Command("ID_ABSB_Status",
        DisplayName = "Status and Log",
        Icon = "status_16.ico", LargeIcon = "status_32.ico",
        LoadForCanExecute = true,
        ToolTip = "Show the SwitchBack connection status and open the log folder.")]
    [Command("ID_ABSB_About",
        DisplayName = "About",
        Icon = "logo_16.ico", LargeIcon = "logo_32.ico",
        ToolTip = "About AB SwitchBack.")]
    [Command("ID_ABSB_LinkedIn",
        DisplayName = "Abdullah Lotfy - LinkedIn",
        Icon = "linkedin_16.ico", LargeIcon = "linkedin_32.ico",
        ToolTip = "Open the author's LinkedIn profile.")]
    public sealed class SwitchBackRibbonPlugin : CommandHandlerPlugin
    {
        public override int ExecuteCommand(string commandId, params string[] parameters)
        {
            try
            {
                switch (commandId)
                {
                    case "ID_ABSB_Target":
                        SwitchBackContext.ChooseTargetInteractively();
                        break;

                    case "ID_ABSB_Settings":
                        SwitchBackContext.ShowSettings();
                        break;

                    case "ID_ABSB_Status":
                        ShowStatus();
                        break;

                    case "ID_ABSB_About":
                        ShowAbout();
                        break;

                    case "ID_ABSB_LinkedIn":
                        OpenLinkedIn();
                        break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "AB SwitchBack",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return 0;
        }

        private static void ShowStatus()
        {
            int revitCount = ABSwitchBack.Core.Discovery.InstanceRegistry
                .List(ABSwitchBack.Core.Ipc.PipeNames.RoleRevit).Count;

            string body =
                "Listener: " + (SwitchBackContext.IsRunning ? "running" : "NOT running") + "\r\n" +
                "Trigger: " + SwitchBackContext.TriggerDescription + "\r\n" +
                "Trigger armed: " + (SwitchBackContext.TriggerArmed ? "yes" : "NO") + "\r\n" +
                "Pipe: " + SwitchBackContext.PipeName + "\r\n" +
                "This Navisworks PID: " +
                    Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Running Revit instances: " + revitCount.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n" +
                "Config and logs:\r\n" + ABSwitchBack.Core.Paths.Root + "\r\n\r\n" +
                "Open that folder now?";

            DialogResult answer = MessageBox.Show(body, "AB SwitchBack status",
                                                  MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) ABSwitchBack.Core.Branding.OpenDataFolder();
        }

        private static void ShowAbout()
        {
            string body =
                ABSwitchBack.Core.Branding.Tagline + "\r\n\r\n" +
                SwitchBackContext.TriggerDescription + " an element to select it in Revit, " +
                "section box it and zoom to it.\r\n\r\n" +
                "by " + ABSwitchBack.Core.Branding.Author + "\r\n" +
                ABSwitchBack.Core.Branding.LinkedInUrl + "\r\n\r\n" +
                "Open the LinkedIn profile now?";

            DialogResult answer = MessageBox.Show(body,
                ABSwitchBack.Core.Branding.ProductName + " " + ABSwitchBack.Core.Branding.Version,
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (answer == DialogResult.Yes) OpenLinkedIn();
        }

        private static void OpenLinkedIn()
        {
            if (ABSwitchBack.Core.Branding.OpenLinkedIn()) return;

            MessageBox.Show(ABSwitchBack.Core.Branding.LinkedInUrl,
                            ABSwitchBack.Core.Branding.LinkedInCaption,
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
