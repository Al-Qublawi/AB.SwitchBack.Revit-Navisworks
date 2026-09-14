using System;
using System.Collections.Generic;
using System.IO;
using ABAdvTools.Setup;

namespace ABSwitchBack.Setup
{
    /// <summary>
    /// What AB SwitchBack installs, and how its earlier releases are recognised.
    ///
    /// Layout - unchanged from the 1.x MSI, so nothing a user relies on moves:
    ///   Revit       Addins\&lt;year&gt;\ABSwitchBack.addin + ABSwitchBack\  - all users by default,
    ///               or the current user
    ///   Navisworks  &lt;install folder&gt;\Plugins\ABSwitchBack\  - Navisworks has no per-user
    ///               plugin folder, so this half always needs administrator rights
    ///
    /// Earlier versions it recognises:
    ///   - the 1.x Windows Installer package, by its UpgradeCode, removed through msiexec
    ///   - files copied by build\install.ps1 (Revit per user, Navisworks plugin folders)
    ///   - an install made by this installer, through its Apps and Features entry
    /// </summary>
    internal sealed class SwitchBackSetup : SetupProduct
    {
        /// <summary>UpgradeCode of the WiX package that shipped 1.0 - 1.2. Never changes.</summary>
        private const string LegacyMsiUpgradeCode = "EC8C6EF4-D80D-43D8-AAFA-81653D9BA6FD";

        private const string NavisworksPluginFolder = "ABSwitchBack";

        private readonly RevitAddin _revit = new RevitAddin
        {
            ManifestFileName = "ABSwitchBack.addin",
            FolderName = "ABSwitchBack",
            AssemblyFileName = "ABSwitchBack.Revit.dll",
            AddInName = "AB SwitchBack",
            FullClassName = "ABSwitchBack.Revit.App",
            // Revit keys the registration off this id. It must never change.
            AddInId = "b6342c28-4b89-4417-bb34-17d8f02664e3",
            VendorId = "ABSB",
            VendorDescription = "AB SwitchBack - Navisworks to Revit switch back"
        };

        public override string Id { get { return "SwitchBack"; } }
        public override string Name { get { return "AB SwitchBack"; } }
        public override string GitHubRepository { get { return "AB.SwitchBack.Revit-Navisworks"; } }
        public override InstallScope DefaultScope { get { return InstallScope.AllUsers; } }

        public override string[] BlockingProcesses
        {
            get { return new[] { AutodeskLocator.RevitProcess, AutodeskLocator.NavisworksProcess }; }
        }

        public override string Description
        {
            get
            {
                return "Ctrl+click an element in Navisworks and the paired Revit session selects it, section " +
                       "boxes it and zooms to it. Install it on both sides.";
            }
        }

        public override string HostSummary
        {
            get
            {
                return "Autodesk Revit " + Payload.YearRange("Revit") + " and Navisworks " + Payload.YearRange("Navisworks");
            }
        }

        public override string NextSteps
        {
            get
            {
                return "Start Revit and Navisworks, then Ctrl+click an element in Navisworks. " +
                       "SwitchBack's buttons are on the AB Adv Tools tab in both.";
            }
        }

        // ------------------------------------------------------------ targets

        public override List<HostTarget> FindTargets(SetupContext ctx)
        {
            var targets = new List<HostTarget>();

            foreach (HostTarget revit in AutodeskLocator.FindRevit(2019, 2035))
            {
                revit.PayloadAvailable = Payload.Has(RevitAddin.PayloadName(revit.Year));
                if (!revit.PayloadAvailable) revit.Note = "this installer has no build for " + revit.Year;
                targets.Add(revit);
            }

            // The 1.x MSI installed the Revit add-in for every built year unconditionally - Revit reads
            // only its own year folder, so one package served a team on mixed versions, including a
            // Revit installed afterwards. Keep that. (Navisworks needs its install folder, so it is
            // only ever installed where Navisworks already is, as before.)
            AddReleasesNotInstalled(targets, HostKind.Revit, "Revit", null);

            foreach (HostTarget navisworks in AutodeskLocator.FindNavisworks(2020, 2035))
            {
                navisworks.PayloadAvailable = Payload.Has(NavisworksPayload(navisworks.Year));
                if (!navisworks.PayloadAvailable) navisworks.Note = "this installer has no build for " + navisworks.Year;
                targets.Add(navisworks);
            }

            return targets;
        }

        public override string ElevationReason(InstallScope scope, IList<HostTarget> targets)
        {
            if (scope == InstallScope.AllUsers) return "install for all users of this computer";

            foreach (HostTarget target in targets)
            {
                if (target.Host == HostKind.Navisworks)
                    return "install the Navisworks plugin into the Navisworks program folder";
            }
            return null;
        }

        // ------------------------------------------------------------ install / remove

        public override void Install(SetupContext ctx, InstallScope scope, IList<HostTarget> targets)
        {
            foreach (HostTarget target in targets)
            {
                if (target.Host == HostKind.Revit)
                {
                    _revit.Install(ctx, scope, target.Year);
                    continue;
                }

                // The DLL must sit in a folder with its own name, or Navisworks will not load it.
                string folder = PluginFolder(ctx, target);
                string locked = FileOps.FirstLockedFile(folder);
                if (locked != null)
                    throw new InvalidOperationException("'" + locked + "' is in use. Close Navisworks and run setup again.");

                int files = Payload.Extract(NavisworksPayload(target.Year), folder, true);
                ctx.Log("   " + target.DisplayName + "  ->  " + folder + "  (" + files + " files)");
            }
        }

        public override void RemoveFiles(SetupContext ctx, InstallScope scope)
        {
            _revit.Remove(ctx, scope);
            RemoveNavisworksPlugins(ctx);
        }

        // ------------------------------------------------------------ earlier versions

        public override List<ExistingInstall> FindExistingInstalls(SetupContext ctx)
        {
            var found = new List<ExistingInstall>();
            List<string> plugins = ExistingPluginFolders(ctx);

            bool msiFound = false;
            if (!ctx.IsSandbox)
            {
                foreach (MsiProduct msi in WindowsInstaller.FindByUpgradeCode(LegacyMsiUpgradeCode))
                {
                    msiFound = true;
                    ExistingInstall install = MsiInstall(msi, delegate (SetupContext c)
                    {
                        // Leftovers only: Windows Installer removes what it installed.
                        _revit.Remove(c, InstallScope.AllUsers);
                        RemoveNavisworksPlugins(c);
                    });
                    AddLocations(install, ctx, InstallScope.AllUsers, plugins);
                    found.Add(install);
                }
            }

            bool anyRegistered = false;
            foreach (InstallScope scope in new[] { InstallScope.AllUsers, InstallScope.CurrentUser })
            {
                ExistingInstall registered = RegisteredInstallFor(ctx, scope);
                if (registered == null) continue;

                anyRegistered = true;
                // Its Navisworks half is in Program Files whatever the scope.
                registered.NeedsElevation = registered.NeedsElevation || plugins.Count > 0;
                AddLocations(registered, ctx, scope, plugins);
                found.Add(registered);
            }

            if (msiFound || anyRegistered)
            {
                // Everything else on disk belongs to one of those, except a per-user Revit copy
                // from install.ps1, which neither the MSI nor an all-users install would own.
                if (!IsRegistered(ctx, InstallScope.CurrentUser)) AddLooseCurrentUser(found, ctx);
                return found;
            }

            List<int> machineYears = _revit.InstalledYears(ctx, InstallScope.AllUsers);
            if (machineYears.Count > 0 || plugins.Count > 0)
            {
                var files = new ExistingInstall
                {
                    Key = "files-allusers",
                    ProductName = Name,
                    Version = HighestVersion(ctx, InstallScope.AllUsers, plugins),
                    InstalledBy = "Copied files",
                    Scope = InstallScope.AllUsers,
                    NeedsElevation = true,
                    Remove = delegate (SetupContext c)
                    {
                        _revit.Remove(c, InstallScope.AllUsers);
                        RemoveNavisworksPlugins(c);
                    }
                };
                AddLocations(files, ctx, InstallScope.AllUsers, plugins);
                found.Add(files);
            }

            AddLooseCurrentUser(found, ctx);
            return found;
        }

        private void AddLooseCurrentUser(List<ExistingInstall> found, SetupContext ctx)
        {
            List<int> years = _revit.InstalledYears(ctx, InstallScope.CurrentUser);
            if (years.Count == 0) return;

            var files = new ExistingInstall
            {
                Key = "files-currentuser",
                ProductName = Name,
                Version = _revit.InstalledVersion(ctx, InstallScope.CurrentUser),
                InstalledBy = "Copied files (install.ps1)",
                Scope = InstallScope.CurrentUser,
                NeedsElevation = false,
                Remove = delegate (SetupContext c) { _revit.Remove(c, InstallScope.CurrentUser); }
            };
            AddLocations(files, ctx, InstallScope.CurrentUser, new List<string>());
            found.Add(files);
        }

        private bool IsRegistered(SetupContext ctx, InstallScope scope)
        {
            return UninstallEntry.Read(ctx, this, scope) != null;
        }

        private void AddLocations(ExistingInstall install, SetupContext ctx, InstallScope scope, List<string> plugins)
        {
            foreach (int year in _revit.InstalledYears(ctx, scope))
                install.Locations.Add(_revit.BinaryFolder(ctx, scope, year));
            install.Locations.AddRange(plugins);
        }

        private string HighestVersion(SetupContext ctx, InstallScope scope, List<string> plugins)
        {
            var files = new List<string>();
            foreach (int year in _revit.InstalledYears(ctx, scope))
                files.Add(Path.Combine(_revit.BinaryFolder(ctx, scope, year), _revit.AssemblyFileName));
            foreach (string plugin in plugins)
                files.Add(Path.Combine(plugin, NavisworksPluginFolder + ".dll"));
            return FileOps.HighestVersion(files);
        }

        // ------------------------------------------------------------ Navisworks plugin folders

        private static string NavisworksPayload(int year)
        {
            return "Navisworks" + year;
        }

        private static string PluginFolder(SetupContext ctx, HostTarget navisworks)
        {
            return ctx.Redirect(Path.Combine(navisworks.InstallDirectory, "Plugins", NavisworksPluginFolder));
        }

        private static List<string> ExistingPluginFolders(SetupContext ctx)
        {
            var folders = new List<string>();
            foreach (HostTarget navisworks in AutodeskLocator.FindNavisworks(2020, 2035))
            {
                string folder = PluginFolder(ctx, navisworks);
                if (Directory.Exists(folder)) folders.Add(folder);
            }
            return folders;
        }

        private static void RemoveNavisworksPlugins(SetupContext ctx)
        {
            foreach (string folder in ExistingPluginFolders(ctx))
                FileOps.RemoveDirectory(ctx, folder);
        }
    }
}
