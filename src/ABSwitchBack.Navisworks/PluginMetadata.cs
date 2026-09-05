using System;
using System.Reflection;

namespace ABSwitchBack.Navisworks
{
    /// <summary>
    /// Assembly-level facts about this plugin.
    ///
    /// There is deliberately no AssemblyResolve handler here any more. Core is compiled
    /// into this assembly (see the .csproj), so the plugin has no dependency to resolve
    /// beyond the Navisworks API itself. A resolver could not have helped in any case:
    /// Navisworks scans the assembly for [Plugin] types before any of our code runs, so
    /// a handler registered from a static constructor is always too late.
    /// </summary>
    internal static class PluginMetadata
    {
        /// <summary>The Navisworks release this plugin was compiled against, e.g. "2027".</summary>
        public static string BuildVersion
        {
            get
            {
                try
                {
                    Assembly self = typeof(PluginMetadata).Assembly;
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
