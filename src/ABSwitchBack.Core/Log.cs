using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ABSwitchBack.Core
{
    /// <summary>
    /// Minimal thread-safe file logger. One log file per host process so that two
    /// Revit instances never fight over the same handle.
    /// </summary>
    public static class Log
    {
        private const long MaxBytes = 1024 * 1024; // roll at 1 MB
        private static readonly object Gate = new object();
        private static string _file;
        private static string _role = "App";

        public static void Init(string role)
        {
            _role = role ?? "App";
            Paths.EnsureCreated();
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                _file = Path.Combine(Paths.LogsDir, _role + "-" + pid.ToString(CultureInfo.InvariantCulture) + ".log");
                Roll();
            }
            catch { _file = null; }
        }

        public static void Info(string message) { Write("INFO ", message); }
        public static void Warn(string message) { Write("WARN ", message); }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", ex == null ? message : message + " :: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
        }

        private static void Write(string level, string message)
        {
            if (_file == null) return;
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                              + " [" + level + "] " + message + Environment.NewLine;
                lock (Gate) File.AppendAllText(_file, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never throw into the host.
            }
        }

        private static void Roll()
        {
            try
            {
                if (!File.Exists(_file)) return;
                var fi = new FileInfo(_file);
                if (fi.Length < MaxBytes) return;
                string old = _file + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(_file, old);
            }
            catch { }
        }
    }
}
