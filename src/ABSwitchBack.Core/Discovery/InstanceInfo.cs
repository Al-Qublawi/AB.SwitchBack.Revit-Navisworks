using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ABSwitchBack.Core.Discovery
{
    /// <summary>
    /// One running Revit or Navisworks process, as advertised in the discovery folder.
    /// Identity = PID + application/version + document, exactly as required.
    /// </summary>
    public sealed class InstanceInfo
    {
        public string Role { get; set; }          // "Revit" | "Navisworks"
        public int Pid { get; set; }
        public string ProcessName { get; set; }   // guards against PID reuse
        public string AppName { get; set; }       // e.g. "Autodesk Revit"
        public string Version { get; set; }       // e.g. "2024"
        public string Document { get; set; }      // active document / project name
        public string PipeName { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public InstanceInfo()
        {
            Role = ""; ProcessName = ""; AppName = ""; Version = ""; Document = ""; PipeName = "";
        }

        /// <summary>What the picker list shows for this instance.</summary>
        public string DisplayName
        {
            get
            {
                string doc = string.IsNullOrEmpty(Document) ? "(no document)" : Document;
                return AppName + " " + Version + "  -  " + doc + "  [PID " + Pid.ToString(CultureInfo.InvariantCulture) + "]";
            }
        }

        public string ToFileContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("role=" + Role);
            sb.AppendLine("pid=" + Pid.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("process=" + ProcessName);
            sb.AppendLine("app=" + AppName);
            sb.AppendLine("version=" + Version);
            sb.AppendLine("document=" + Sanitize(Document));
            sb.AppendLine("pipe=" + PipeName);
            sb.AppendLine("updated=" + UpdatedUtc.ToString("o", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static InstanceInfo FromLines(string[] lines)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines)
            {
                if (raw == null) continue;
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                map[raw.Substring(0, eq).Trim()] = raw.Substring(eq + 1).Trim();
            }

            var info = new InstanceInfo
            {
                Role = Get(map, "role"),
                ProcessName = Get(map, "process"),
                AppName = Get(map, "app"),
                Version = Get(map, "version"),
                Document = Get(map, "document"),
                PipeName = Get(map, "pipe")
            };

            int pid;
            int.TryParse(Get(map, "pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
            info.Pid = pid;

            DateTime updated;
            if (DateTime.TryParse(Get(map, "updated"), CultureInfo.InvariantCulture,
                                  DateTimeStyles.RoundtripKind, out updated))
                info.UpdatedUtc = updated;

            return info;
        }

        private static string Get(Dictionary<string, string> map, string key)
        {
            string v;
            return map.TryGetValue(key, out v) ? v : string.Empty;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
