using System;
using ABAdvTools.Setup;

namespace ABSwitchBack.Setup
{
    internal static class Program
    {
        /// <summary>See SetupArguments for the command line (/silent, /uninstall, /allusers ...).</summary>
        [STAThread]
        private static int Main(string[] args)
        {
            return SetupProgram.Run(new SwitchBackSetup(), args);
        }
    }
}
