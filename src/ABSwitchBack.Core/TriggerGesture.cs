using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ABSwitchBack.Core
{
    /// <summary>Modifier keys that must be held for a click to trigger a switch back.</summary>
    [Flags]
    public enum TriggerModifiers
    {
        /// <summary>No modifier: every element you select is sent.</summary>
        None = 0,
        Ctrl = 1,
        Shift = 2,
        Alt = 4
    }

    /// <summary>
    /// Parses, formats and tests the trigger gesture. Any combination of Ctrl, Shift and
    /// Alt is allowed, so the user is not limited to a fixed list.
    ///
    /// Nothing here runs on the system input path: the modifier state is read once per
    /// selection change, and only then.
    /// </summary>
    public static class TriggerGesture
    {
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;   // Alt

        private const short Pressed = unchecked((short)0x8000);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>The gesture used when nothing valid is configured.</summary>
        public static readonly TriggerModifiers Default = TriggerModifiers.Ctrl;

        /// <summary>
        /// Accepts "Ctrl", "Ctrl+Shift", "ctrl shift", "CtrlShift", "Alt", "None" and so on.
        /// Anything unrecognised falls back to Ctrl, so a typo in config.txt can never
        /// silently select the broken Ctrl+Shift gesture or disable the modifier entirely.
        /// </summary>
        public static TriggerModifiers Parse(string value)
        {
            if (string.IsNullOrEmpty(value)) return Default;

            string text = value.Trim();
            if (text.Equals("None", StringComparison.OrdinalIgnoreCase)) return TriggerModifiers.None;

            // Tolerate "Ctrl+Shift", "Ctrl Shift", "ctrl,shift" and the older "CtrlShift".
            string collapsed = text.Replace("+", string.Empty)
                                   .Replace(",", string.Empty)
                                   .Replace(" ", string.Empty)
                                   .Replace("-", string.Empty)
                                   .ToLowerInvariant();

            var result = TriggerModifiers.None;
            int consumed = 0;

            foreach (var candidate in new[]
            {
                new KeyValuePair<string, TriggerModifiers>("control", TriggerModifiers.Ctrl),
                new KeyValuePair<string, TriggerModifiers>("ctrl", TriggerModifiers.Ctrl),
                new KeyValuePair<string, TriggerModifiers>("shift", TriggerModifiers.Shift),
                new KeyValuePair<string, TriggerModifiers>("alt", TriggerModifiers.Alt)
            })
            {
                int index = collapsed.IndexOf(candidate.Key, StringComparison.Ordinal);
                if (index < 0) continue;
                if ((result & candidate.Value) != 0) continue;

                result |= candidate.Value;
                consumed += candidate.Key.Length;
                collapsed = collapsed.Remove(index, candidate.Key.Length);
            }

            // Reject strings that were not fully understood, e.g. "banana".
            if (consumed == 0 || collapsed.Length > 0) return Default;
            return result;
        }

        /// <summary>Canonical form written to config.txt, e.g. "Ctrl+Shift".</summary>
        public static string Format(TriggerModifiers modifiers)
        {
            if (modifiers == TriggerModifiers.None) return "None";

            var parts = new List<string>(3);
            if ((modifiers & TriggerModifiers.Ctrl) != 0) parts.Add("Ctrl");
            if ((modifiers & TriggerModifiers.Shift) != 0) parts.Add("Shift");
            if ((modifiers & TriggerModifiers.Alt) != 0) parts.Add("Alt");
            return string.Join("+", parts.ToArray());
        }

        /// <summary>Human readable gesture, e.g. "Ctrl+Shift+Left Click".</summary>
        public static string Describe(TriggerModifiers modifiers)
        {
            if (modifiers == TriggerModifiers.None) return "Left Click (no modifier)";
            return Format(modifiers) + "+Left Click";
        }

        /// <summary>
        /// Exact match: Ctrl mode does not also fire on Ctrl+Shift, so two configured
        /// gestures can never be confused with one another.
        /// </summary>
        public static bool AreHeld(TriggerModifiers modifiers)
        {
            try
            {
                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & Pressed) != 0;
                bool shift = (GetAsyncKeyState(VK_SHIFT) & Pressed) != 0;
                bool alt = (GetAsyncKeyState(VK_MENU) & Pressed) != 0;

                return ctrl == ((modifiers & TriggerModifiers.Ctrl) != 0)
                    && shift == ((modifiers & TriggerModifiers.Shift) != 0)
                    && alt == ((modifiers & TriggerModifiers.Alt) != 0);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Navisworks reserves Ctrl+Shift+click and expands the pick to the whole model
        /// file, so the plugin only ever sees the file node. Warn rather than forbid.
        /// </summary>
        public static bool IsReservedByNavisworks(TriggerModifiers modifiers)
        {
            return (modifiers & TriggerModifiers.Ctrl) != 0
                && (modifiers & TriggerModifiers.Shift) != 0;
        }
    }
}
