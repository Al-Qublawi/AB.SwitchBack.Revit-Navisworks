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

        /// <summary>Delay after the click before reading Navisworks' selection, in milliseconds.</summary>
        public int ClickDelayMs { get { return Clamp(GetInt("ClickDelayMs", 150), 30, 2000); } set { Set("ClickDelayMs", value); } }

        /// <summary>Master switch for the click hook in Navisworks.</summary>
        public bool EnableClickHook { get { return GetBool("EnableClickHook", true); } set { Set("EnableClickHook", value); } }

        /// <summary>
        /// Modifier that triggers a switch back: Ctrl (default), CtrlShift or Alt.
        /// Ctrl+Shift is intercepted by Navisworks itself and expands the pick to the whole
        /// model file, so it is not recommended.
        /// </summary>
        public string Trigger { get { return GetString("Trigger", "Ctrl"); } set { Set("Trigger", value); } }

        /// <summary>Named-pipe connect/response timeout, in milliseconds.</summary>
        public int PipeTimeoutMs { get { return Clamp(GetInt("PipeTimeoutMs", 3000), 500, 30000); } set { Set("PipeTimeoutMs", value); } }

        /// <summary>Last Revit process chosen as a target from Navisworks (a hint; re-prompted if dead).</summary>
        public int RevitTargetPid { get { return GetInt("RevitTargetPid", 0); } set { Set("RevitTargetPid", value); } }

        /// <summary>Last Navisworks process chosen as a target from Revit.</summary>
        public int NavisTargetPid { get { return GetInt("NavisTargetPid", 0); } set { Set("NavisTargetPid", value); } }

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
            return cfg;
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
                sb.AppendLine("# Delay after the click before reading the Navisworks selection.");
                sb.AppendLine("ClickDelayMs=150");
                sb.AppendLine("# Set to false to disable the click hook in Navisworks.");
                sb.AppendLine("EnableClickHook=true");
                sb.AppendLine("# Trigger gesture: Ctrl, CtrlShift or Alt (plus left click).");
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
                string existing = File.ReadAllText(Paths.ConfigFile);
                var additions = new StringBuilder();

                if (existing.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    additions.AppendLine();
                    additions.AppendLine("# Trigger gesture: Ctrl, CtrlShift or Alt (plus left click).");
                    additions.AppendLine("# Ctrl+Shift is intercepted by Navisworks and selects the whole file - avoid it.");
                    additions.AppendLine("Trigger=Ctrl");
                }

                if (existing.IndexOf("CreateViewIfMissing", StringComparison.OrdinalIgnoreCase) < 0)
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
