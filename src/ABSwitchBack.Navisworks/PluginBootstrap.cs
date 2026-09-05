using System;
using System.IO;
using System.Reflection;

namespace ABSwitchBack.Navisworks
{
    /// <summary>
    /// Navisworks probes for assemblies next to Roamer.exe, not inside the plugin folder,
    /// so ABSwitchBack.Core.dll would fail to load without help.
    ///
    /// This type deliberately references nothing from Core: it is triggered from a static
    /// constructor so the resolver is registered before any Core type has to be resolved.
    /// </summary>
    internal static class PluginBootstrap
    {
        private static readonly object Gate = new object();
        private static bool _registered;
        private static string _pluginDirectory;

        public static void EnsureAssemblyResolver()
        {
            lock (Gate)
            {
                if (_registered) return;
                _registered = true;

                try
                {
                    _pluginDirectory = Path.GetDirectoryName(typeof(PluginBootstrap).Assembly.Location);
                    AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                }
                catch
                {
                    // If this fails there is nothing useful we can do; Navisworks will
                    // report the load error itself.
                }
            }
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                if (string.IsNullOrEmpty(_pluginDirectory)) return null;

                string simpleName = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(simpleName)) return null;

                // Only ever resolve our own assemblies out of the plugin folder.
                if (!simpleName.StartsWith("ABSwitchBack", StringComparison.OrdinalIgnoreCase)) return null;

                string candidate = Path.Combine(_pluginDirectory, simpleName + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The Navisworks release this plugin was compiled against, e.g. "2027".</summary>
        public static string BuildVersion
        {
            get
            {
                try
                {
                    Assembly self = typeof(PluginBootstrap).Assembly;
                    foreach (AssemblyMetadataAttribute attribute in
                             self.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                    {
                        if (string.Equals(attribute.Key, "NavisVersion", StringComparison.OrdinalIgnoreCase))
                            return attribute.Value;
                    }
                }
                catch { }
                return null;
            }
        }
    }
}
