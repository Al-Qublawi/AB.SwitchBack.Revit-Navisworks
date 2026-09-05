using System;
using System.Runtime.InteropServices;

namespace ABSwitchBack.Core.Interop
{
    /// <summary>
    /// Windows will not let an inactive process steal focus. The sender therefore calls
    /// <see cref="AllowForProcess"/> to hand its foreground right to the destination,
    /// and the destination then calls <see cref="BringToFront"/> successfully.
    /// </summary>
    public static class WindowFocus
    {
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        /// <summary>Grant the target process permission to take the foreground. Call before sending.</summary>
        public static void AllowForProcess(int pid)
        {
            try { if (pid > 0) AllowSetForegroundWindow(pid); } catch { }
        }

        /// <summary>Restore and focus a window. Called by the receiving application.</summary>
        public static void BringToFront(IntPtr hWnd)
        {
            try
            {
                if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;
                if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
            }
            catch { }
        }
    }
}
