using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using ABSwitchBack.Core.Protocol;

namespace ABSwitchBack.Core.Ipc
{
    /// <summary>
    /// Fire-one-message-and-read-one-reply client. Always call this from a background
    /// thread: Connect() blocks for up to the timeout.
    /// </summary>
    public static class PipeClient
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>
        /// Sends a message and returns the reply, or null if the endpoint could not be
        /// reached. <paramref name="error"/> carries a user-presentable reason on failure.
        /// </summary>
        public static SwitchBackMessage Send(string pipeName, SwitchBackMessage message, int timeoutMs, out string error)
        {
            error = null;
            try
            {
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    client.Connect(timeoutMs);
                    try { client.ReadMode = PipeTransmissionMode.Byte; } catch { }

                    using (var writer = new StreamWriter(client, Utf8, 1024, true))
                    {
                        writer.NewLine = "\n";
                        writer.WriteLine(message.Format());
                        writer.Flush();
                    }

                    string replyLine;
                    using (var reader = new StreamReader(client, Utf8, false, 1024, true))
                        replyLine = reader.ReadLine();

                    if (string.IsNullOrEmpty(replyLine))
                    {
                        error = "The destination closed the connection without replying.";
                        return null;
                    }

                    SwitchBackMessage reply;
                    if (!SwitchBackMessage.TryParse(replyLine, out reply))
                    {
                        error = "The destination sent an unrecognised reply.";
                        return null;
                    }
                    return reply;
                }
            }
            catch (TimeoutException)
            {
                error = "Timed out connecting to the destination. It may have closed, or SwitchBack is not loaded there.";
            }
            catch (IOException ex)
            {
                error = "Connection lost: " + ex.Message;
            }
            catch (UnauthorizedAccessException)
            {
                error = "Access denied on the pipe. Both applications must run as the same Windows user.";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return null;
        }

        /// <summary>Cheap liveness probe used by the instance picker's Test button.</summary>
        public static bool Ping(string pipeName, int timeoutMs, out string error)
        {
            var msg = new SwitchBackMessage(SwitchBackMessageType.Ping, Process.GetCurrentProcess().Id, 0, string.Empty);
            var reply = Send(pipeName, msg, timeoutMs, out error);
            if (reply == null) return false;
            if (reply.Type == SwitchBackMessageType.Pong) return true;
            error = reply.Type == SwitchBackMessageType.Error ? reply.Payload : "Unexpected reply: " + reply.Type;
            return false;
        }
    }
}
