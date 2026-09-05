using System;
using System.IO;

namespace ABSwitchBack.Core
{
    /// <summary>
    /// All on-disk locations used by SwitchBack. Everything lives under
    /// %LOCALAPPDATA%\ABSwitchBack so no elevated rights are ever needed at runtime.
    /// </summary>
    public static class Paths
    {
        public const string ProductFolder = "ABSwitchBack";

        public static string Root
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, ProductFolder);
            }
        }

        public static string LogsDir { get { return Path.Combine(Root, "logs"); } }

        /// <summary>One small text file per live Revit/Navisworks process (the discovery directory).</summary>
        public static string InstancesDir { get { return Path.Combine(Root, "instances"); } }

        public static string ConfigFile { get { return Path.Combine(Root, "config.txt"); } }

        /// <summary>Creates the folder tree. Safe to call repeatedly and from any thread.</summary>
        public static void EnsureCreated()
        {
            try
            {
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(LogsDir);
                Directory.CreateDirectory(InstancesDir);
            }
            catch
            {
                // Never let a disk problem take down the host application.
            }
        }
    }
}
