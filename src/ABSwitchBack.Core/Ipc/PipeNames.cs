using System.Diagnostics;
using System.Globalization;

namespace ABSwitchBack.Core.Ipc
{
    /// <summary>Every process gets its own endpoint, so N Revits and N Navisworks coexist.</summary>
    public static class PipeNames
    {
        public const string RoleRevit = "Revit";
        public const string RoleNavisworks = "Navisworks";

        public static string For(string role, int pid)
        {
            return "ABSwitchBack." + role + "." + pid.ToString(CultureInfo.InvariantCulture);
        }

        public static string ForCurrentProcess(string role)
        {
            return For(role, Process.GetCurrentProcess().Id);
        }
    }
}
