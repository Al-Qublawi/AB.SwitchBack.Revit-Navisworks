using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using Autodesk.Navisworks.Api.Plugins;
using ABAdvTools;
using ABAdvTools.Navisworks;

namespace ABSwitchBack.Navisworks
{
    /// <summary>
    /// EventWatcherPlugin is the only plugin kind Navisworks loads automatically at
    /// startup, which is what the background listener and the selection tracking need -
    /// and what joining the AB Adv Tools suite needs too: the shared ribbon tab is merged
    /// and the release check runs from here.
    /// </summary>
    [Plugin("ABSwitchBackWatcher", "ABSB",
        DisplayName = "AB SwitchBack",
        ToolTip = "Sends the clicked element to Revit")]
    public class SwitchBackWatcher : EventWatcherPlugin
    {
        public override void OnLoaded()
        {
            SwitchBackContext.Start();
            NavisworksAdvTools.Start(Product);
        }

        public override void OnUnloading()
        {
            NavisworksAdvTools.Stop();
            SwitchBackContext.Stop();
        }

        /// <summary>
        /// AB SwitchBack as the suite knows it. The existing "Check for updates" setting in the
        /// SwitchBack settings dialog still switches the automatic check off.
        /// </summary>
        internal static AdvToolsProduct Product
        {
            get
            {
                var product = new AdvToolsProduct(
                    "SwitchBack",
                    ABSwitchBack.Core.Branding.ProductName,
                    ABSwitchBack.Core.Branding.Tagline,
                    AdvToolsHost.Navisworks,
                    ABSwitchBack.Core.Branding.GitHubRepository,
                    typeof(SwitchBackWatcher).Assembly)
                {
                    AutomaticChecksAllowed = delegate { return ABSwitchBack.Core.SwitchBackConfig.Load().CheckForUpdates; },
                    UpdateSettingsHint = "SwitchBack Settings",

                    // What SwitchBack's own About said before it joined the suite.
                    Details = delegate
                    {
                        return SwitchBackContext.TriggerDescription + " an element to select it in Revit, " +
                               "section box it and zoom to it." + Environment.NewLine +
                               "Config and logs: " + ABSwitchBack.Core.Paths.Root;
                    }
                };
                product.AddAction("Open the settings and log folder", delegate { ABSwitchBack.Core.Branding.OpenDataFolder(); });
                return product;
            }
        }
    }

    /// <summary>
    /// SwitchBack's panel on the shared "AB Adv Tools" ribbon tab.
    ///
    /// The layout lives in ABSwitchBack.xaml, which Navisworks reads as a loose file in a
    /// locale subfolder beside the plugin DLL, and the button images come from the Icon /
    /// LargeIcon names below, resolved against the Images folder next to the DLL.
    ///
    /// The tab id, and the About / Check for Updates / LinkedIn commands, are the suite's
    /// (NavisworksAdvTools); every AB plugin declares the same ones, and the kit merges
    /// their tabs into one at startup.
    /// </summary>
    [Plugin("ABSwitchBack.Ribbon", "ABSB",
        DisplayName = "AB SwitchBack",
        ToolTip = "Send the clicked element to Revit")]
    [RibbonLayout("ABSwitchBack.xaml")]
    [RibbonTab(NavisworksAdvTools.RibbonTabId, DisplayName = NavisworksAdvTools.RibbonTabTitle, LoadForCanExecute = true)]
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
    [Command(NavisworksAdvTools.AboutCommandId,
        DisplayName = NavisworksAdvTools.AboutText,
        Icon = NavisworksAdvTools.AboutIcon, LargeIcon = NavisworksAdvTools.AboutLargeIcon,
        ToolTip = NavisworksAdvTools.AboutToolTip)]
    [Command(NavisworksAdvTools.UpdatesCommandId,
        DisplayName = NavisworksAdvTools.UpdatesText,
        Icon = NavisworksAdvTools.UpdatesIcon, LargeIcon = NavisworksAdvTools.UpdatesLargeIcon,
        ToolTip = NavisworksAdvTools.UpdatesToolTip)]
    [Command(NavisworksAdvTools.LinkedInCommandId,
        DisplayName = NavisworksAdvTools.LinkedInText,
        Icon = NavisworksAdvTools.LinkedInIcon, LargeIcon = NavisworksAdvTools.LinkedInLargeIcon,
        ToolTip = NavisworksAdvTools.LinkedInToolTip)]
    public sealed class SwitchBackRibbonPlugin : CommandHandlerPlugin
    {
        public override int ExecuteCommand(string commandId, params string[] parameters)
        {
            try
            {
                if (NavisworksAdvTools.TryExecuteSharedCommand(commandId)) return 0;

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

            DialogResult answer = MessageBox.Show(body,
                ABSwitchBack.Core.Branding.ProductName + " " + ABSwitchBack.Core.Branding.Version + " status",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) ABSwitchBack.Core.Branding.OpenDataFolder();
        }
    }
}
