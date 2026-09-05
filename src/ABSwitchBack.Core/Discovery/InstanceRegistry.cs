using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ABSwitchBack.Core.Ipc;

namespace ABSwitchBack.Core.Discovery
{
    /// <summary>
    /// Discovery via a folder of tiny advert files, one per live process. Chosen over
    /// enumerating \.\pipe\ because it also carries the document name and app version,
    /// so the picker can be populated instantly with no network round-trips.
    /// Liveness is decided by the OS process table, not by the heartbeat timestamp.
    /// </summary>
    public sealed class InstanceRegistry : IDisposable
    {
        private const string Extension = ".inst";
        private static readonly object FileGate = new object();

        private readonly InstanceInfo _self;
        private readonly string _selfPath;
        private Timer _heartbeat;
        private bool _disposed;

        public InstanceInfo Self { get { return _self; } }

        public InstanceRegistry(string role, string appName, string version)
        {
            Paths.EnsureCreated();

            Process p = Process.GetCurrentProcess();
            _self = new InstanceInfo
            {
                Role = role,
                Pid = p.Id,
                ProcessName = SafeProcessName(p),
                AppName = appName,
                Version = version,
                Document = string.Empty,
                PipeName = PipeNames.For(role, p.Id),
                UpdatedUtc = DateTime.UtcNow
            };

            _selfPath = Path.Combine(Paths.InstancesDir,
                role + "." + _self.Pid.ToString(CultureInfo.InvariantCulture) + Extension);

            Publish();

            // Refresh the timestamp periodically so stale adverts are obvious to a human
            // reading the folder. Correctness never depends on it.
            _heartbeat = new Timer(_ => Publish(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        /// <summary>Call when the active document changes so the picker shows the right name.</summary>
        public void UpdateDocument(string documentName)
        {
            if (_disposed) return;
            string incoming = documentName ?? string.Empty;
            if (string.Equals(_self.Document, incoming, StringComparison.Ordinal)) return;
            _self.Document = incoming;
            Publish();
        }

        private void Publish()
        {
            if (_disposed) return;
            try
            {
                _self.UpdatedUtc = DateTime.UtcNow;
                lock (FileGate)
                {
                    Paths.EnsureCreated();
                    File.WriteAllText(_selfPath, _self.ToFileContent(), Encoding.UTF8);
                }
            }
            catch (Exception ex) { Log.Error("Could not publish instance advert.", ex); }
        }

        /// <summary>
        /// Lists live instances for a role, pruning adverts whose process is gone.
        /// Pass a role of null to list everything.
        /// </summary>
        public static List<InstanceInfo> List(string role)
        {
            var result = new List<InstanceInfo>();
            try
            {
                Paths.EnsureCreated();
                string[] files = Directory.GetFiles(Paths.InstancesDir, "*" + Extension);

                foreach (string file in files)
                {
                    InstanceInfo info = null;
                    try { info = InstanceInfo.FromLines(File.ReadAllLines(file)); }
                    catch { }

                    if (info == null || info.Pid <= 0) { TryDelete(file); continue; }
                    if (!IsAlive(info)) { TryDelete(file); continue; }
                    if (role != null && !string.Equals(info.Role, role, StringComparison.OrdinalIgnoreCase)) continue;

                    result.Add(info);
                }
            }
            catch (Exception ex) { Log.Error("Could not list instances.", ex); }

            result.Sort((a, b) =>
            {
                int c = string.Compare(a.Version, b.Version, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Pid.CompareTo(b.Pid);
            });
            return result;
        }

        /// <summary>
        /// True when the advertised PID still exists AND still belongs to the same
        /// executable - Windows recycles PIDs, and focusing a random process would be worse
        /// than doing nothing.
        /// </summary>
        public static bool IsAlive(InstanceInfo info)
        {
            if (info == null || info.Pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(info.Pid))
                {
                    if (p.HasExited) return false;
                    if (string.IsNullOrEmpty(info.ProcessName)) return true;
                    return string.Equals(SafeProcessName(p), info.ProcessName, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (ArgumentException) { return false; }   // no such process
            catch (InvalidOperationException) { return false; }
            catch { return true; }                        // access denied: assume alive
        }

        private static string SafeProcessName(Process p)
        {
            try { return p.ProcessName; } catch { return string.Empty; }
        }

        private static void TryDelete(string file)
        {
            try { File.Delete(file); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_heartbeat != null) _heartbeat.Dispose(); } catch { }
            _heartbeat = null;
            try { lock (FileGate) if (File.Exists(_selfPath)) File.Delete(_selfPath); } catch { }
        }
    }
}
