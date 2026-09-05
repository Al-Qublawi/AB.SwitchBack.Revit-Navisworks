using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ABSwitchBack.Core.Protocol;

namespace ABSwitchBack.Core.Ipc
{
    /// <summary>
    /// Single-client-at-a-time named pipe listener running entirely on the thread pool.
    /// The host application's UI thread is never touched, so Revit/Navisworks cannot freeze.
    /// The supplied handler also runs on a background thread - it must not call the
    /// Autodesk API directly (Revit marshals via ExternalEvent instead).
    /// </summary>
    public sealed class PipeServer : IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly string _pipeName;
        private readonly Func<SwitchBackMessage, SwitchBackMessage> _handler;
        private readonly object _gate = new object();

        private CancellationTokenSource _cts;
        private Task _loop;
        private NamedPipeServerStream _current;
        private bool _disposed;

        public string PipeName { get { return _pipeName; } }
        public bool IsRunning { get { return _loop != null && !_loop.IsCompleted; } }

        public PipeServer(string pipeName, Func<SwitchBackMessage, SwitchBackMessage> handler)
        {
            if (string.IsNullOrEmpty(pipeName)) throw new ArgumentNullException("pipeName");
            if (handler == null) throw new ArgumentNullException("handler");
            _pipeName = pipeName;
            _handler = handler;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed || IsRunning) return;
                _cts = new CancellationTokenSource();
                _loop = Task.Factory.StartNew(
                    () => RunAsync(_cts.Token),
                    _cts.Token,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).Unwrap();
                Log.Info("Pipe server listening on " + _pipeName);
            }
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    lock (_gate) _current = server;

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) break;

                    HandleConnection(server);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (IOException ex)
                {
                    // Client vanished mid-conversation; keep listening.
                    if (token.IsCancellationRequested) break;
                    Log.Warn("Pipe connection dropped: " + ex.Message);
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    Log.Error("Pipe server loop error.", ex);
                    // Back off briefly so a persistent failure cannot spin the CPU.
                    try { await Task.Delay(500, token).ConfigureAwait(false); } catch { break; }
                }
                finally
                {
                    lock (_gate) { if (ReferenceEquals(_current, server)) _current = null; }
                    SafeDispose(server);
                }
            }
            Log.Info("Pipe server stopped: " + _pipeName);
        }

        private void HandleConnection(NamedPipeServerStream server)
        {
            string requestLine;
            using (var reader = new StreamReader(server, Utf8, false, 1024, true))
                requestLine = reader.ReadLine();

            if (string.IsNullOrEmpty(requestLine)) return;

            SwitchBackMessage request;
            SwitchBackMessage response;

            if (!SwitchBackMessage.TryParse(requestLine, out request))
            {
                Log.Warn("Discarded malformed message: " + Truncate(requestLine, 200));
                response = new SwitchBackMessage(SwitchBackMessageType.Error, CurrentPid.Value, 0, "Malformed message");
            }
            else
            {
                try
                {
                    response = _handler(request)
                               ?? new SwitchBackMessage(SwitchBackMessageType.Ack, CurrentPid.Value, request.ElementId, "OK");
                }
                catch (Exception ex)
                {
                    Log.Error("Message handler threw.", ex);
                    response = new SwitchBackMessage(SwitchBackMessageType.Error, CurrentPid.Value, request.ElementId, ex.Message);
                }
            }

            using (var writer = new StreamWriter(server, Utf8, 1024, true))
            {
                writer.NewLine = "\n";
                writer.WriteLine(response.Format());
                writer.Flush();
            }

            try { server.WaitForPipeDrain(); } catch { }
            try { server.Disconnect(); } catch { }
        }

        private static string Truncate(string s, int max)
        {
            return s != null && s.Length > max ? s.Substring(0, max) + "..." : s;
        }

        private static void SafeDispose(IDisposable d)
        {
            if (d == null) return;
            try { d.Dispose(); } catch { }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            try { if (_cts != null) _cts.Cancel(); } catch { }

            // Cancelling a pending WaitForConnectionAsync is only reliable once the
            // handle is closed, so drop the stream the loop is currently parked on.
            NamedPipeServerStream current;
            lock (_gate) current = _current;
            SafeDispose(current);

            try { if (_loop != null) _loop.Wait(2000); } catch { }
            try { if (_cts != null) _cts.Dispose(); } catch { }
        }

        private static class CurrentPid
        {
            internal static readonly int Value = System.Diagnostics.Process.GetCurrentProcess().Id;
        }
    }
}
