using System;
using System.Diagnostics;
using System.IO;

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

        /// <summary>The project page, opened by Help when no user guide is installed beside the add-in.</summary>
        public const string ProjectUrl = "https://github.com/Al-Qublawi/" + GitHubRepository;

#if STORE_BUILD
        /// <summary>
        /// True in the Autodesk Marketplace build (-p:StoreBuild=true). The store forbids an app
        /// carrying its own update mechanism, so that build never contacts GitHub.
        /// </summary>
        public const bool IsStoreBuild = true;

        /// <summary>The repository the release check asks; none in a store build.</summary>
        public const string ReleaseRepository = null;
#else
        public const bool IsStoreBuild = false;
        public const string ReleaseRepository = GitHubRepository;
#endif

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

        /// <summary>
        /// A page of the user guide shipped with the add-in, or null. The Marketplace bundle keeps it in
        /// Contents\Resources\Help, two folders above each host's binaries (Contents\Revit\2027,
        /// Contents\Navisworks\v24); a Help folder beside the binaries is accepted too.
        /// </summary>
        public static string ShippedHelpPage(string name)
        {
            try
            {
                string folder = Path.GetDirectoryName(typeof(Branding).Assembly.Location) ?? string.Empty;
                foreach (string candidate in new[]
                {
                    Path.Combine(folder, "Help", name),
                    Path.Combine(folder, "..", "..", "Resources", "Help", name),
                    Path.Combine(folder, "..", "Resources", "Help", name)
                })
                {
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            catch { }
            return null;
        }

        /// <summary>The user guide (F1 help), or null when none is installed.</summary>
        public static string HelpFile { get { return ShippedHelpPage("index.html"); } }

        /// <summary>Opens the installed user guide, or the project page when none is installed.</summary>
        public static bool OpenHelp()
        {
            string target = HelpFile ?? ProjectUrl;
            try
            {
                var info = new ProcessStartInfo(target);
                info.UseShellExecute = true;
                Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open help (" + target + "): " + ex.Message);
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
