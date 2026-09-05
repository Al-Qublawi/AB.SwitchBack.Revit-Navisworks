using System;
using System.Runtime.InteropServices;

namespace ABSwitchBack.Navisworks
{
    /// <summary>Which modifier combination triggers a switch back.</summary>
    internal enum ClickTrigger
    {
        /// <summary>Ctrl + left click. Navisworks treats this as "toggle in selection".</summary>
        Ctrl,
        /// <summary>
        /// Ctrl + Shift + left click. NOT recommended: Navisworks intercepts this
        /// combination and expands the pick to the whole model file.
        /// </summary>
        CtrlShift,
        /// <summary>Alt + left click.</summary>
        Alt
    }

    /// <summary>
    /// Reads the modifier keys for the configured trigger.
    ///
    /// This replaced a WH_MOUSE_LL global mouse hook. That hook forced Windows to route
    /// every mouse event in the system - including the flood of moves during an orbit -
    /// through the Navisworks UI thread's message queue before the input could proceed.
    /// Whenever that thread was busy rendering, all mouse input serialised behind it and
    /// the whole application felt sluggish. Nothing here runs on the input path: the
    /// modifier state is read once per selection change, and only then.
    /// </summary>
    internal static class TriggerGesture
    {
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;   // Alt

        private const short Pressed = unchecked((short)0x8000);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public static ClickTrigger Parse(string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                string normalised = value.Replace(" ", string.Empty).Replace("+", string.Empty).Trim();
                if (normalised.Equals("ctrlshift", StringComparison.OrdinalIgnoreCase)) return ClickTrigger.CtrlShift;
                if (normalised.Equals("alt", StringComparison.OrdinalIgnoreCase)) return ClickTrigger.Alt;
            }
            return ClickTrigger.Ctrl;
        }

        public static string Describe(ClickTrigger trigger)
        {
            switch (trigger)
            {
                case ClickTrigger.CtrlShift: return "Ctrl+Shift+Left Click";
                case ClickTrigger.Alt: return "Alt+Left Click";
                default: return "Ctrl+Left Click";
            }
        }

        /// <summary>
        /// Exact match, so Ctrl mode does not also fire on Ctrl+Shift and the two gestures
        /// can never be confused.
        /// </summary>
        public static bool ModifiersHeld(ClickTrigger trigger)
        {
            try
            {
                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & Pressed) != 0;
                bool shift = (GetAsyncKeyState(VK_SHIFT) & Pressed) != 0;
                bool alt = (GetAsyncKeyState(VK_MENU) & Pressed) != 0;

                bool needCtrl = trigger == ClickTrigger.Ctrl || trigger == ClickTrigger.CtrlShift;
                bool needShift = trigger == ClickTrigger.CtrlShift;
                bool needAlt = trigger == ClickTrigger.Alt;

                return ctrl == needCtrl && shift == needShift && alt == needAlt;
            }
            catch
            {
                return false;
            }
        }
    }
}
