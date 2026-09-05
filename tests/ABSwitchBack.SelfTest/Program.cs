using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ABSwitchBack.Core;
using ABSwitchBack.Core.Discovery;
using ABSwitchBack.Core.Ipc;
using ABSwitchBack.Core.Protocol;

namespace ABSwitchBack.SelfTest
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Log.Init("SelfTest");

            // Lets the settings dialog be opened outside Revit and Navisworks, for a quick
            // look at the layout without launching a 2 GB host application.
            if (args != null && Array.IndexOf(args, "--settings") >= 0)
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                Console.WriteLine("Saved: " + ABSwitchBack.Core.UI.SettingsForm.Show(null));
                return 0;
            }

            Section("1. Protocol round-trip");
            TestProtocol();

            Section("2. Named pipe request/response");
            TestSinglePipe();

            Section("3. Multiple concurrent instances");
            TestMultipleInstances();

            Section("4. Failure handling");
            TestFailureModes();

            Section("5. Concurrency under load");
            TestConcurrency();

            Section("6. Instance discovery and liveness");
            TestDiscovery();

            Section("7. Element id text parsing");
            TestElementIdParsing();

            Section("8. Trigger gesture configuration");
            TestTriggerParsing();

            Console.WriteLine();
            Console.WriteLine(new string('=', 62));
            Console.WriteLine("  PASSED: " + _passed + "   FAILED: " + _failed);
            Console.WriteLine(new string('=', 62));
            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ tests

        private static void TestProtocol()
        {
            // Ordinary message.
            RoundTrip(new SwitchBackMessage(SwitchBackMessageType.Select, 1234, 987654321L, "hello"));

            // Payloads containing the field separator and newlines must survive intact:
            // a Revit document title really can contain a pipe character.
            RoundTrip(new SwitchBackMessage(SwitchBackMessageType.Pong, 42, 0, "Tower|A - Level 3"));
            RoundTrip(new SwitchBackMessage(SwitchBackMessageType.Error, 7, 5, "line one\r\nline two"));
            RoundTrip(new SwitchBackMessage(SwitchBackMessageType.Ack, 9, 1, @"C:\Models\Block A\file.rvt"));
            RoundTrip(new SwitchBackMessage(SwitchBackMessageType.Ping, 0, 0, string.Empty));

            // 64-bit ids: Revit 2024+ can exceed int range.
            RoundTrip(new SwitchBackMessage(SwitchBackMessageType.Select, 1, 5_000_000_000L, "big"));

            // Garbage must be rejected, not misparsed.
            SwitchBackMessage parsed;
            Check("rejects empty line", !SwitchBackMessage.TryParse("", out parsed));
            Check("rejects wrong magic", !SwitchBackMessage.TryParse("NOPE|Ping|1|0|", out parsed));
            Check("rejects short line", !SwitchBackMessage.TryParse("ABSB1|Ping|1", out parsed));
            Check("rejects unknown type", !SwitchBackMessage.TryParse("ABSB1|Explode|1|0|", out parsed));
        }

        private static void RoundTrip(SwitchBackMessage original)
        {
            string wire = original.Format();

            Check("wire form is a single line: " + Describe(original),
                  wire.IndexOf('\n') < 0 && wire.IndexOf('\r') < 0);

            SwitchBackMessage parsed;
            if (!SwitchBackMessage.TryParse(wire, out parsed))
            {
                Check("parses back: " + Describe(original), false);
                return;
            }

            Check("round-trips: " + Describe(original),
                  parsed.Type == original.Type &&
                  parsed.SourcePid == original.SourcePid &&
                  parsed.ElementId == original.ElementId &&
                  parsed.Payload == original.Payload);
        }

        private static void TestSinglePipe()
        {
            string pipe = "ABSwitchBack.Test." + Guid.NewGuid().ToString("N");

            using (var server = new PipeServer(pipe, request =>
                new SwitchBackMessage(SwitchBackMessageType.Pong, 999, request.ElementId, "from server")))
            {
                server.Start();
                Thread.Sleep(200);

                Check("server reports running", server.IsRunning);

                string error;
                bool ok = PipeClient.Ping(pipe, 3000, out error);
                Check("ping succeeds (" + (error ?? "no error") + ")", ok);

                var reply = PipeClient.Send(pipe,
                    new SwitchBackMessage(SwitchBackMessageType.Select, 1, 424242L, string.Empty),
                    3000, out error);

                Check("select is answered", reply != null);
                if (reply != null)
                {
                    Check("element id survives the wire", reply.ElementId == 424242L);
                    Check("payload survives the wire", reply.Payload == "from server");
                }

                // The listener must serve more than one conversation.
                for (int i = 0; i < 5; i++)
                {
                    var r = PipeClient.Send(pipe,
                        new SwitchBackMessage(SwitchBackMessageType.Select, 1, 100 + i, string.Empty),
                        3000, out error);
                    if (r == null || r.ElementId != 100 + i)
                    {
                        Check("sequential request " + i, false);
                        return;
                    }
                }
                Check("serves sequential requests", true);
            }
        }

        private static void TestMultipleInstances()
        {
            // Two pretend Revits and two pretend Navisworks, all in one process,
            // each on its own endpoint - the real multi-instance scenario.
            var endpoints = new List<Tuple<string, string, PipeServer>>();

            try
            {
                foreach (var spec in new[]
                {
                    Tuple.Create("Revit", 11001), Tuple.Create("Revit", 11002),
                    Tuple.Create("Navisworks", 22001), Tuple.Create("Navisworks", 22002)
                })
                {
                    string role = spec.Item1;
                    int pid = spec.Item2;
                    string name = PipeNames.For(role, pid);
                    string identity = role + ":" + pid;

                    var server = new PipeServer(name, req =>
                        new SwitchBackMessage(SwitchBackMessageType.Pong, pid, req.ElementId, identity));
                    server.Start();
                    endpoints.Add(Tuple.Create(name, identity, server));
                }

                Thread.Sleep(300);
                Check("four endpoints have distinct names",
                      endpoints.Select(e => e.Item1).Distinct().Count() == 4);

                // Every message must reach exactly the endpoint it was addressed to.
                bool allCorrect = true;
                foreach (var endpoint in endpoints)
                {
                    string error;
                    var reply = PipeClient.Send(endpoint.Item1,
                        new SwitchBackMessage(SwitchBackMessageType.Select, 1, 7L, string.Empty),
                        3000, out error);

                    if (reply == null || reply.Payload != endpoint.Item2)
                    {
                        Console.WriteLine("      misrouted: expected " + endpoint.Item2 +
                                          ", got " + (reply == null ? "no reply" : reply.Payload));
                        allCorrect = false;
                    }
                }
                Check("messages route to the correct instance", allCorrect);
            }
            finally
            {
                foreach (var endpoint in endpoints) endpoint.Item3.Dispose();
            }
        }

        private static void TestFailureModes()
        {
            string error;

            // A destination that was closed must fail fast, not hang the caller.
            var stopwatch = Stopwatch.StartNew();
            var reply = PipeClient.Send("ABSwitchBack.Revit.999999",
                new SwitchBackMessage(SwitchBackMessageType.Select, 1, 1L, string.Empty),
                1000, out error);
            stopwatch.Stop();

            Check("closed destination returns null", reply == null);
            Check("closed destination reports a reason", !string.IsNullOrEmpty(error));
            Check("closed destination fails fast (" + stopwatch.ElapsedMilliseconds + " ms)",
                  stopwatch.ElapsedMilliseconds < 3000);

            // A handler that throws must produce an Error reply, not kill the listener.
            string pipe = "ABSwitchBack.Test." + Guid.NewGuid().ToString("N");
            using (var server = new PipeServer(pipe, req =>
            {
                if (req.ElementId == 13) throw new InvalidOperationException("boom");
                return new SwitchBackMessage(SwitchBackMessageType.Ack, 1, req.ElementId, "ok");
            }))
            {
                server.Start();
                Thread.Sleep(200);

                var bad = PipeClient.Send(pipe,
                    new SwitchBackMessage(SwitchBackMessageType.Select, 1, 13L, string.Empty), 3000, out error);
                Check("throwing handler yields an Error reply",
                      bad != null && bad.Type == SwitchBackMessageType.Error);

                var good = PipeClient.Send(pipe,
                    new SwitchBackMessage(SwitchBackMessageType.Select, 1, 14L, string.Empty), 3000, out error);
                Check("listener survives a handler exception",
                      good != null && good.Type == SwitchBackMessageType.Ack);

                // Raw garbage down the pipe must not take the listener down either.
                WriteRawLine(pipe, "this is not a switchback message");
                var after = PipeClient.Send(pipe,
                    new SwitchBackMessage(SwitchBackMessageType.Select, 1, 15L, string.Empty), 3000, out error);
                Check("listener survives malformed input",
                      after != null && after.Type == SwitchBackMessageType.Ack);
            }
        }

        private static void TestConcurrency()
        {
            string pipe = "ABSwitchBack.Test." + Guid.NewGuid().ToString("N");
            int handled = 0;

            using (var server = new PipeServer(pipe, req =>
            {
                Interlocked.Increment(ref handled);
                return new SwitchBackMessage(SwitchBackMessageType.Ack, 1, req.ElementId, "ok");
            }))
            {
                server.Start();
                Thread.Sleep(200);

                const int total = 25;
                int succeeded = 0;

                // The listener accepts one connection at a time by design; clients must
                // queue rather than fail. This is the burst a jumpy user generates.
                Parallel.For(0, total, i =>
                {
                    string error;
                    var reply = PipeClient.Send(pipe,
                        new SwitchBackMessage(SwitchBackMessageType.Select, 1, i, string.Empty),
                        5000, out error);
                    if (reply != null && reply.ElementId == i) Interlocked.Increment(ref succeeded);
                });

                Check("all " + total + " concurrent sends succeeded (" + succeeded + "/" + total + ")",
                      succeeded == total);
                Check("server handled every request", handled == total);
            }
        }

        private static void TestDiscovery()
        {
            Paths.EnsureCreated();

            using (var registry = new InstanceRegistry("Revit", "Autodesk Revit", "2024"))
            {
                registry.UpdateDocument("Tower A - Structure.rvt");

                List<InstanceInfo> found = InstanceRegistry.List("Revit");
                InstanceInfo self = found.FirstOrDefault(i => i.Pid == Process.GetCurrentProcess().Id);

                Check("own advert is discoverable", self != null);
                if (self != null)
                {
                    Check("document name is advertised", self.Document == "Tower A - Structure.rvt");
                    Check("version is advertised", self.Version == "2024");
                    Check("pipe name matches the convention",
                          self.PipeName == PipeNames.For("Revit", Process.GetCurrentProcess().Id));
                    Check("display name is human readable",
                          self.DisplayName.Contains("Tower A") && self.DisplayName.Contains("2024"));
                }

                Check("role filter excludes other roles",
                      InstanceRegistry.List("Navisworks").All(i => i.Role == "Navisworks"));

                // A dead process must be pruned rather than offered as a destination.
                string deadFile = Path.Combine(Paths.InstancesDir, "Revit.999999.inst");
                File.WriteAllText(deadFile, string.Join(Environment.NewLine, new[]
                {
                    "role=Revit", "pid=999999", "process=Revit", "app=Autodesk Revit",
                    "version=2024", "document=Ghost.rvt",
                    "pipe=" + PipeNames.For("Revit", 999999),
                    "updated=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                }));

                List<InstanceInfo> afterDead = InstanceRegistry.List("Revit");
                Check("dead instance is not listed", afterDead.All(i => i.Pid != 999999));
                Check("dead advert file is deleted", !File.Exists(deadFile));

                // PID reuse: right pid, wrong executable, must not be trusted.
                string reusedFile = Path.Combine(Paths.InstancesDir, "Revit.reuse.inst");
                File.WriteAllText(reusedFile, string.Join(Environment.NewLine, new[]
                {
                    "role=Revit", "pid=" + Process.GetCurrentProcess().Id,
                    "process=SomeOtherApp", "app=Autodesk Revit", "version=2024",
                    "document=Recycled.rvt", "pipe=x",
                    "updated=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                }));

                List<InstanceInfo> afterReuse = InstanceRegistry.List("Revit");
                Check("pid reuse is rejected by process name",
                      afterReuse.All(i => i.Document != "Recycled.rvt"));

                try { File.Delete(reusedFile); } catch { }
            }

            // Dispose must withdraw the advert so a closed host stops being offered.
            Check("advert is withdrawn on dispose",
                  InstanceRegistry.List("Revit").All(i => i.Pid != Process.GetCurrentProcess().Id));
        }

        private static void TestElementIdParsing()
        {
            MethodInfo parse = LoadParser();
            if (parse == null)
            {
                Console.WriteLine("      SKIPPED - could not load the Navisworks plugin assembly.");
                return;
            }

            // value, expected success, expected id
            var cases = new[]
            {
                Tuple.Create("123456", true, 123456L),
                Tuple.Create("  987654  ", true, 987654L),
                Tuple.Create("Element ID: 55512", true, 55512L),
                Tuple.Create("8388608", true, 8388608L),
                Tuple.Create("0", false, 0L),
                Tuple.Create("-5", false, 0L),
                Tuple.Create("", false, 0L),
                Tuple.Create("Basic Wall", false, 0L),
                Tuple.Create("d3f1a2b4-1111-2222-3333-444455556666", false, 0L),
                Tuple.Create("12 and 34", false, 0L)
            };

            bool allOk = true;
            foreach (var testCase in cases)
            {
                object[] parameters = { testCase.Item1, 0L };
                bool ok = (bool)parse.Invoke(null, parameters);
                long value = (long)parameters[1];

                bool correct = ok == testCase.Item2 && (!ok || value == testCase.Item3);
                if (!correct)
                {
                    Console.WriteLine("      wrong for '" + testCase.Item1 + "': ok=" + ok + " value=" + value);
                    allOk = false;
                }
            }
            Check("element id text parsing handles all " + cases.Length + " cases", allOk);
        }

        private static void TestTriggerParsing()
        {
            // input, expected canonical form. Anything unrecognised must fall back to Ctrl,
            // so a typo in config.txt can never silently disable the modifier or select the
            // Ctrl+Shift gesture that Navisworks reserves.
            var cases = new[]
            {
                Tuple.Create("Ctrl", "Ctrl"),
                Tuple.Create("ctrl", "Ctrl"),
                Tuple.Create("CONTROL", "Ctrl"),
                Tuple.Create("CtrlShift", "Ctrl+Shift"),
                Tuple.Create("Ctrl+Shift", "Ctrl+Shift"),
                Tuple.Create("ctrl shift", "Ctrl+Shift"),
                Tuple.Create("shift+ctrl", "Ctrl+Shift"),
                Tuple.Create("Alt", "Alt"),
                Tuple.Create("Ctrl+Alt", "Ctrl+Alt"),
                Tuple.Create("Shift+Alt", "Shift+Alt"),
                Tuple.Create("Ctrl+Shift+Alt", "Ctrl+Shift+Alt"),
                Tuple.Create("None", "None"),
                Tuple.Create("none", "None"),
                Tuple.Create("nonsense", "Ctrl"),
                Tuple.Create("Ctrl+banana", "Ctrl"),
                Tuple.Create("", "Ctrl"),
                Tuple.Create((string)null, "Ctrl")
            };

            bool allOk = true;
            foreach (var testCase in cases)
            {
                string actual = TriggerGesture.Format(TriggerGesture.Parse(testCase.Item1));
                if (actual != testCase.Item2)
                {
                    Console.WriteLine("      '" + (testCase.Item1 ?? "<null>") + "' gave " + actual +
                                      ", expected " + testCase.Item2);
                    allOk = false;
                }
            }
            Check("trigger parsing handles all " + cases.Length + " cases", allOk);

            // Every combination the settings dialog can produce must survive a save/load.
            bool roundTrips = true;
            for (int bits = 0; bits < 8; bits++)
            {
                var modifiers = (TriggerModifiers)bits;
                TriggerModifiers back = TriggerGesture.Parse(TriggerGesture.Format(modifiers));
                if (back != modifiers)
                {
                    Console.WriteLine("      " + modifiers + " round-tripped to " + back);
                    roundTrips = false;
                }
            }
            Check("all 8 modifier combinations round-trip through config", roundTrips);

            Check("Ctrl+Shift is flagged as reserved by Navisworks",
                  TriggerGesture.IsReservedByNavisworks(TriggerModifiers.Ctrl | TriggerModifiers.Shift));
            Check("Ctrl alone is not flagged as reserved",
                  !TriggerGesture.IsReservedByNavisworks(TriggerModifiers.Ctrl));

            Check("no-modifier gesture is described clearly",
                  TriggerGesture.Describe(TriggerModifiers.None) == "Left Click (no modifier)");
            Check("Ctrl gesture is described clearly",
                  TriggerGesture.Describe(TriggerModifiers.Ctrl) == "Ctrl+Left Click");

            // The default must be Ctrl: Ctrl+Shift is reserved by Navisworks.
            var config = new SwitchBackConfig();
            Check("default trigger is Ctrl", config.Trigger == "Ctrl");
            Check("trigger is enabled by default", config.EnableClickHook);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Loads the built Navisworks plugin and reaches its internal text parser by
        /// reflection, redirecting Autodesk assembly references to the real install.
        /// </summary>
        private static Assembly _pluginAssembly;
        private static bool _pluginLoadAttempted;

        /// <summary>
        /// Loads the built Navisworks plugin, redirecting Autodesk assembly references to
        /// the real install so its internal types can be reached by reflection.
        /// </summary>
        private static Assembly LoadPluginAssembly()
        {
            if (_pluginLoadAttempted) return _pluginAssembly;
            _pluginLoadAttempted = true;

            try
            {
                string repoRoot = FindRepoRoot();
                if (repoRoot == null) return null;

                string navisArtifacts = Path.Combine(repoRoot, "artifacts", "Navisworks");
                if (!Directory.Exists(navisArtifacts)) return null;

                string plugin = Directory
                    .GetFiles(navisArtifacts, "ABSwitchBack.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (plugin == null) return null;

                string navisDir = FindNavisworksDir();
                AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
                {
                    string simple = new AssemblyName(e.Name).Name;
                    foreach (string dir in new[] { Path.GetDirectoryName(plugin), navisDir })
                    {
                        if (string.IsNullOrEmpty(dir)) continue;
                        string candidate = Path.Combine(dir, simple + ".dll");
                        if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                    }
                    return null;
                };

                _pluginAssembly = Assembly.LoadFrom(plugin);
            }
            catch (Exception ex)
            {
                Console.WriteLine("      loader error: " + ex.Message);
            }
            return _pluginAssembly;
        }

        private static MethodInfo LoadParser()
        {
            try
            {
                Assembly assembly = LoadPluginAssembly();
                if (assembly == null) return null;

                Type type = assembly.GetType("ABSwitchBack.Navisworks.ElementIdExtractor", true);
                return type.GetMethod("TryParseIdText", BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                Console.WriteLine("      loader error: " + ex.Message);
                return null;
            }
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                // Directory.Build.props is the one marker present regardless of whether
                // the SDK generated a .sln or the newer .slnx.
                if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private static string FindNavisworksDir()
        {
            string programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (string.IsNullOrEmpty(programFiles)) return null;

            string autodesk = Path.Combine(programFiles, "Autodesk");
            if (!Directory.Exists(autodesk)) return null;

            return Directory.GetDirectories(autodesk, "Navisworks *")
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "Autodesk.Navisworks.Api.dll")));
        }

        private static void WriteRawLine(string pipeName, string text)
        {
            try
            {
                using (var client = new System.IO.Pipes.NamedPipeClientStream(
                    ".", pipeName, System.IO.Pipes.PipeDirection.InOut))
                {
                    client.Connect(2000);
                    byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
                    client.Write(bytes, 0, bytes.Length);
                    client.Flush();

                    // Drain the reply so the server completes its exchange cleanly.
                    var buffer = new byte[512];
                    try { client.Read(buffer, 0, buffer.Length); } catch { }
                }
            }
            catch { }
        }

        private static string Describe(SwitchBackMessage m)
        {
            string payload = m.Payload ?? string.Empty;
            if (payload.Length > 24) payload = payload.Substring(0, 24) + "...";
            return m.Type + "/" + m.ElementId + "/'" + payload.Replace("\r", "").Replace("\n", " ") + "'";
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine(title);
            Console.WriteLine(new string('-', title.Length));
        }

        private static void Check(string description, bool condition)
        {
            if (condition) { _passed++; Console.WriteLine("  PASS  " + description); }
            else { _failed++; Console.WriteLine("  FAIL  " + description); }
        }
    }
}
