using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace ABSwitchBack.Core
{
    /// <summary>What a check found. Everything is optional; nothing here is ever required.</summary>
    public sealed class UpdateInfo
    {
        public string LatestVersion { get; set; }
        public string DownloadUrl { get; set; }
        public bool IsNewer { get; set; }

        /// <summary>True when this version has not been shown to the user yet.</summary>
        public bool ShouldNotify { get; set; }
    }

    /// <summary>
    /// Asks GitHub whether a newer release exists.
    ///
    /// Design rules, because this runs inside someone's Revit or Navisworks:
    ///   - never on the UI thread, never blocking startup
    ///   - fails silently: no network, a proxy, a firewall or a rate limit are all normal
    ///   - at most one request a day, and one notice per new version, so it cannot nag
    ///   - a single anonymous GET to the public releases API. Nothing is uploaded, no
    ///     telemetry, no identifiers. Switchable off in Settings.
    /// </summary>
    public static class UpdateCheck
    {
        public const string RepositoryUrl = "https://github.com/Al-Qublawi/AB.SwitchBack.Revit-Navisworks";
        private const string LatestReleaseApi =
            "https://api.github.com/repos/Al-Qublawi/AB.SwitchBack.Revit-Navisworks/releases/latest";

        private static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(24);
        private static readonly object Gate = new object();

        private static string StateFile { get { return Path.Combine(Paths.Root, "update-state.txt"); } }

        /// <summary>
        /// Call from a background thread. Returns null when the check is disabled, throttled,
        /// or anything at all goes wrong.
        /// </summary>
        public static UpdateInfo Check()
        {
            try
            {
                SwitchBackConfig cfg = SwitchBackConfig.Load();
                if (!cfg.CheckForUpdates) return null;

                string lastCheck, lastSeen, notified;
                ReadState(out lastCheck, out lastSeen, out notified);

                // Throttle, but still report a version already known from an earlier check.
                if (!DueForCheck(lastCheck))
                    return Build(lastSeen, notified);

                string tag = FetchLatestTag();
                WriteState(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                           string.IsNullOrEmpty(tag) ? lastSeen : tag,
                           notified);

                return Build(string.IsNullOrEmpty(tag) ? lastSeen : tag, notified);
            }
            catch (Exception ex)
            {
                Log.Warn("Update check failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Records that the user has been told about this version, so it is shown once.</summary>
        public static void MarkNotified(string version)
        {
            try
            {
                string lastCheck, lastSeen, notified;
                ReadState(out lastCheck, out lastSeen, out notified);
                WriteState(lastCheck, lastSeen, version);
            }
            catch { }
        }

        public static bool OpenReleasesPage()
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(RepositoryUrl + "/releases/latest");
                info.UseShellExecute = true;
                System.Diagnostics.Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open the releases page: " + ex.Message);
                return false;
            }
        }

        private static UpdateInfo Build(string latestTag, string notified)
        {
            if (string.IsNullOrEmpty(latestTag)) return null;

            string latest = Normalise(latestTag);
            bool newer = Compare(latest, Branding.Version) > 0;

            return new UpdateInfo
            {
                LatestVersion = latest,
                DownloadUrl = RepositoryUrl + "/releases/latest",
                IsNewer = newer,
                ShouldNotify = newer && !string.Equals(latest, Normalise(notified), StringComparison.OrdinalIgnoreCase)
            };
        }

        private static bool DueForCheck(string lastCheck)
        {
            if (string.IsNullOrEmpty(lastCheck)) return true;

            DateTime when;
            if (!DateTime.TryParse(lastCheck, CultureInfo.InvariantCulture,
                                   DateTimeStyles.RoundtripKind, out when)) return true;

            return DateTime.UtcNow - when >= MinimumInterval;
        }

        /// <summary>
        /// Reads tag_name out of the releases API response. Hand-parsed on purpose: adding a
        /// JSON library to an add-in that loads beside other vendors' add-ins is a known way
        /// to cause assembly-version conflicts, and one field does not justify the risk.
        /// </summary>
        internal static string FetchLatestTag()
        {
            try
            {
                // .NET Framework defaults can still negotiate TLS 1.0, which GitHub refuses.
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
                catch { }

                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);

                    // GitHub rejects requests with no User-Agent.
                    client.DefaultRequestHeaders.Add("User-Agent",
                        "ABSwitchBack/" + Branding.Version);
                    client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

                    string json = client.GetStringAsync(LatestReleaseApi).GetAwaiter().GetResult();
                    return ExtractJsonString(json, "tag_name");
                }
            }
            catch (Exception ex)
            {
                // Offline, proxied, firewalled or rate limited are all ordinary. Not an error.
                Log.Info("Update check could not reach GitHub: " + ex.Message);
                return null;
            }
        }

        /// <summary>Pulls one top-level string field out of a JSON document.</summary>
        internal static string ExtractJsonString(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return null;

            string needle = "\"" + field + "\"";
            int at = json.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return null;

            int colon = json.IndexOf(':', at + needle.Length);
            if (colon < 0) return null;

            int open = json.IndexOf('"', colon + 1);
            if (open < 0) return null;

            var value = new StringBuilder();
            for (int i = open + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"') return value.ToString();

                // Skip escapes rather than decoding them: a version tag has none.
                if (c == (char)92)
                {
                    i++;
                    continue;
                }
                value.Append(c);
            }
            return null;
        }

        /// <summary>"v1.2.0" and "1.2.0" are the same thing.</summary>
        internal static string Normalise(string version)
        {
            if (string.IsNullOrEmpty(version)) return string.Empty;
            string v = version.Trim();
            if (v.Length > 0 && (v[0] == 'v' || v[0] == 'V')) v = v.Substring(1);
            return v;
        }

        /// <summary>
        /// Numeric comparison, part by part. Returns &gt;0 when left is newer.
        /// "1.10.0" must beat "1.9.0", which a string comparison would get wrong.
        /// </summary>
        internal static int Compare(string left, string right)
        {
            string[] a = Normalise(left).Split('.');
            string[] b = Normalise(right).Split('.');
            int parts = Math.Max(a.Length, b.Length);

            for (int i = 0; i < parts; i++)
            {
                int x = PartAt(a, i);
                int y = PartAt(b, i);
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        private static int PartAt(string[] parts, int index)
        {
            if (index >= parts.Length) return 0;

            // Tolerate "1.2.0-beta" by reading the leading digits only.
            var digits = new StringBuilder();
            foreach (char c in parts[index])
            {
                if (c < '0' || c > '9') break;
                digits.Append(c);
            }

            int value;
            return int.TryParse(digits.ToString(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static void ReadState(out string lastCheck, out string lastSeen, out string notified)
        {
            lastCheck = null; lastSeen = null; notified = null;
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(StateFile)) return;
                    foreach (string raw in File.ReadAllLines(StateFile))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = raw.Substring(0, eq).Trim();
                        string value = raw.Substring(eq + 1).Trim();

                        if (key.Equals("LastCheckUtc", StringComparison.OrdinalIgnoreCase)) lastCheck = value;
                        else if (key.Equals("LastSeenVersion", StringComparison.OrdinalIgnoreCase)) lastSeen = value;
                        else if (key.Equals("NotifiedVersion", StringComparison.OrdinalIgnoreCase)) notified = value;
                    }
                }
            }
            catch { }
        }

        private static void WriteState(string lastCheck, string lastSeen, string notified)
        {
            try
            {
                Paths.EnsureCreated();
                var sb = new StringBuilder();
                sb.AppendLine("# Written by AB SwitchBack. Safe to delete.");
                sb.AppendLine("LastCheckUtc=" + (lastCheck ?? string.Empty));
                sb.AppendLine("LastSeenVersion=" + (lastSeen ?? string.Empty));
                sb.AppendLine("NotifiedVersion=" + (notified ?? string.Empty));

                lock (Gate) File.WriteAllText(StateFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
