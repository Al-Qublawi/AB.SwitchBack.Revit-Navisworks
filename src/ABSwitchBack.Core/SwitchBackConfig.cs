using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ABSwitchBack.Core
{
    /// <summary>
    /// Tiny key=value configuration file at %LOCALAPPDATA%\ABSwitchBack\config.txt.
    /// Deliberately dependency-free: pulling a JSON library into a Revit add-in is a
    /// classic source of assembly-version conflicts with other vendors' add-ins.
    /// </summary>
    public sealed class SwitchBackConfig
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Padding added around the element's bounding box, in millimetres.</summary>
        public double SectionBoxMarginMm { get { return GetDouble("SectionBoxMarginMm", 1000.0); } set { Set("SectionBoxMarginMm", value); } }

        /// <summary>Whether Revit should apply a section box, or only select and zoom.</summary>
        public bool CreateSectionBox { get { return GetBool("CreateSectionBox", true); } set { Set("CreateSectionBox", value); } }

        /// <summary>
        /// Whether Revit may create a 3D view when the project has none that is usable.
        /// This is the only model write besides the section box; setting both this and
        /// CreateSectionBox to false makes the add-in strictly read-only.
        /// </summary>
        public bool CreateViewIfMissing { get { return GetBool("CreateViewIfMissing", true); } set { Set("CreateViewIfMissing", value); } }

        /// <summary>Master switch for the trigger gesture in Navisworks.</summary>
        public bool TriggerEnabled { get { return GetBool("TriggerEnabled", true); } set { Set("TriggerEnabled", value); } }

        /// <summary>
        /// Modifiers that trigger a switch back: any combination of Ctrl, Shift and Alt
        /// (e.g. "Ctrl+Alt"), or "None" to send every element you select.
        /// Ctrl+Shift is intercepted by Navisworks itself and expands the pick to the whole
        /// model file, so it is not recommended. See <see cref="TriggerGesture"/>.
        /// </summary>
        public string Trigger { get { return GetString("Trigger", "Ctrl"); } set { Set("Trigger", value); } }

        /// <summary>Named-pipe connect/response timeout, in milliseconds.</summary>
        public int PipeTimeoutMs { get { return Clamp(GetInt("PipeTimeoutMs", 3000), 500, 30000); } set { Set("PipeTimeoutMs", value); } }

        /// <summary>Last Revit process chosen as a target from Navisworks (a hint; re-prompted if dead).</summary>
        public int RevitTargetPid { get { return GetInt("RevitTargetPid", 0); } set { Set("RevitTargetPid", value); } }


        public static SwitchBackConfig Load()
        {
            var cfg = new SwitchBackConfig();
            try
            {
                if (!File.Exists(Paths.ConfigFile)) return cfg;
                foreach (string raw in File.ReadAllLines(Paths.ConfigFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    cfg._values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception ex) { Log.Error("Config load failed, using defaults.", ex); }

            cfg.MigrateLegacyKeys();
            return cfg;
        }

        /// <summary>
        /// Carries settings written by an older version over to their current names, so an
        /// upgrade never silently resets someone's choice. The old key is dropped, so the
        /// next Save writes a clean file.
        /// </summary>
        private void MigrateLegacyKeys()
        {
            // "EnableClickHook" dates from the mouse hook removed in 1.0.1. There has been
            // no hook since; the setting simply arms the trigger gesture.
            Rename("EnableClickHook", "TriggerEnabled");

            // Settings that no longer do anything. Save() rewrites whatever it loaded, so
            // without this they would linger in every config file indefinitely and invite
            // someone to tune a value that is read by nothing.
            _values.Remove("ClickDelayMs");    // the mouse hook's settle delay, gone in 1.0.1
            _values.Remove("NavisTargetPid");  // written by Revit, never read by anything
        }

        /// <summary>
        /// True when a line actually assigns this key. A substring search would be wrong:
        /// "Trigger" is a prefix of "TriggerEnabled", so it would report the wrong answer.
        /// </summary>
        internal static bool HasKey(string[] lines, string key)
        {
            if (lines == null) return false;

            foreach (string raw in lines)
            {
                if (raw == null) continue;
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void Rename(string oldKey, string newKey)
        {
            string value;
            if (!_values.TryGetValue(oldKey, out value)) return;

            _values.Remove(oldKey);
            if (!_values.ContainsKey(newKey)) _values[newKey] = value;
        }

        public void Save()
        {
            try
            {
                Paths.EnsureCreated();
                var sb = new StringBuilder();
                sb.AppendLine("# AB SwitchBack configuration");
                sb.AppendLine("# Lengths are in millimetres, times in milliseconds.");
                foreach (var kv in _values) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(Paths.ConfigFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Error("Config save failed.", ex); }
        }

        /// <summary>
        /// Writes a fully commented default file if none exists yet, and tops up an
        /// existing one with keys added by a later version so upgrades stay discoverable.
        /// </summary>
        public static void EnsureDefaultFile()
        {
            try
            {
                Paths.EnsureCreated();
                if (File.Exists(Paths.ConfigFile)) { AppendMissingKeys(); return; }
                var sb = new StringBuilder();
                sb.AppendLine("# AB SwitchBack configuration");
                sb.AppendLine("# Section box padding around the found element, in millimetres.");
                sb.AppendLine("SectionBoxMarginMm=1000");
                sb.AppendLine("# Set to false to only select + zoom without applying a section box.");
                sb.AppendLine("CreateSectionBox=true");
                sb.AppendLine("# Allow Revit to create a 3D view if the project has none that is usable.");
                sb.AppendLine("# Set this AND CreateSectionBox to false to make the add-in strictly read-only.");
                sb.AppendLine("CreateViewIfMissing=true");
                sb.AppendLine("# Set to false to disable the trigger gesture in Navisworks.");
                sb.AppendLine("TriggerEnabled=true");
                sb.AppendLine("# Trigger gesture: any combination of Ctrl, Shift and Alt plus a left click,");
                sb.AppendLine("# e.g. Ctrl+Alt. Use None to send every element you select.");
                sb.AppendLine("# Ctrl+Shift is intercepted by Navisworks and selects the whole file - avoid it.");
                sb.AppendLine("Trigger=Ctrl");
                sb.AppendLine("# Named pipe connect/response timeout.");
                sb.AppendLine("PipeTimeoutMs=3000");
                File.WriteAllText(Paths.ConfigFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>
        /// Adds keys introduced after the file was first written. Values already present
        /// are never touched, so a user's choices always survive an upgrade.
        /// </summary>
        private static void AppendMissingKeys()
        {
            try
            {
                string[] lines = File.ReadAllLines(Paths.ConfigFile);
                var additions = new StringBuilder();

                if (!HasKey(lines, "Trigger"))
                {
                    additions.AppendLine();
                    additions.AppendLine("# Trigger gesture: Ctrl, CtrlShift or Alt (plus left click).");
                    additions.AppendLine("# Ctrl+Shift is intercepted by Navisworks and selects the whole file - avoid it.");
                    additions.AppendLine("Trigger=Ctrl");
                }

                if (!HasKey(lines, "CreateViewIfMissing"))
                {
                    additions.AppendLine();
                    additions.AppendLine("# Allow Revit to create a 3D view if the project has none that is usable.");
                    additions.AppendLine("# Set this AND CreateSectionBox to false to make the add-in strictly read-only.");
                    additions.AppendLine("CreateViewIfMissing=true");
                }

                if (additions.Length > 0) File.AppendAllText(Paths.ConfigFile, additions.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not top up the config file: " + ex.Message);
            }
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private string GetString(string key, string fallback)
        {
            string s;
            if (_values.TryGetValue(key, out s) && !string.IsNullOrEmpty(s)) return s;
            return fallback;
        }

        private void Set(string key, object v) { _values[key] = Convert.ToString(v, CultureInfo.InvariantCulture); }

        private int GetInt(string key, int fallback)
        {
            string s; int v;
            if (_values.TryGetValue(key, out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        private double GetDouble(string key, double fallback)
        {
            string s; double v;
            if (_values.TryGetValue(key, out s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        private bool GetBool(string key, bool fallback)
        {
            string s;
            if (!_values.TryGetValue(key, out s)) return fallback;
            s = s.Trim();
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0" || s.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }
    }
}
