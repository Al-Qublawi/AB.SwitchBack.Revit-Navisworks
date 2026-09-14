using System;
using System.Diagnostics;

namespace ABSwitchBack.Core
{
    /// <summary>Author and product identity, in one place so nothing drifts.</summary>
    public static class Branding
    {
        public const string Author = "Abdullah Lotfy";
        public const string LinkedInUrl = "https://www.linkedin.com/in/abdullahalqublawi/";
        public const string LinkedInCaption = "Abdullah Lotfy - LinkedIn";
        public const string ProductName = "AB SwitchBack";
        public const string Tagline = "Navisworks to Revit switch back";

        /// <summary>Where releases are published, and where installed copies look for new ones.</summary>
        public const string GitHubRepository = "AB.SwitchBack.Revit-Navisworks";

        /// <summary>
        /// Read from the compiled assembly (Directory.Build.props), never a hand-kept constant:
        /// a constant that drifts from the build makes every update check misreport.
        /// </summary>
        public static string Version
        {
            get
            {
                try
                {
                    System.Version v = typeof(Branding).Assembly.GetName().Version;
                    return v == null ? "0.0.0" : v.ToString(3);
                }
                catch
                {
                    return "0.0.0";
                }
            }
        }

        public static string AboutLine
        {
            get { return ProductName + " " + Version + "  -  " + Author; }
        }

        /// <summary>
        /// Opens the author's LinkedIn profile in the default browser.
        ///
        /// UseShellExecute must be set explicitly: it defaults to false on .NET
        /// (Revit 2025+), where passing a URL to Process.Start then throws Win32Exception.
        /// On .NET Framework the default is true, so this is correct on both.
        /// </summary>
        public static bool OpenLinkedIn()
        {
            try
            {
                var info = new ProcessStartInfo(LinkedInUrl);
                info.UseShellExecute = true;
                Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open LinkedIn: " + ex.Message);
                return false;
            }
        }

        /// <summary>Opens the settings and log folder in Explorer.</summary>
        public static bool OpenDataFolder()
        {
            try
            {
                Paths.EnsureCreated();
                var info = new ProcessStartInfo("explorer.exe", "\"" + Paths.Root + "\"");
                info.UseShellExecute = true;
                Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open the data folder: " + ex.Message);
                return false;
            }
        }
    }
}
