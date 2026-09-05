using System;
using System.Globalization;

namespace ABSwitchBack.Core.Protocol
{
    public enum SwitchBackMessageType
    {
        Unknown = 0,
        Ping,     // "are you alive?"  -> Pong
        Pong,
        Select,   // "focus this Revit ElementId" -> Ack / Error
        Ack,
        Error
    }

    /// <summary>
    /// One message = one UTF-8 line:
    ///     ABSB1|Type|SourcePid|ElementId|Payload
    /// Payload is escaped so it can never contain a separator or newline.
    /// Deliberately trivial: no serializer, no schema, no versioning ceremony
    /// beyond the leading magic token.
    /// </summary>
    public sealed class SwitchBackMessage
    {
        public const string Magic = "ABSB1";
        private const char Sep = '|';

        public SwitchBackMessageType Type { get; set; }
        public int SourcePid { get; set; }
        public long ElementId { get; set; }
        public string Payload { get; set; }

        public SwitchBackMessage() { Payload = string.Empty; }

        public SwitchBackMessage(SwitchBackMessageType type, int sourcePid, long elementId, string payload)
        {
            Type = type;
            SourcePid = sourcePid;
            ElementId = elementId;
            Payload = payload ?? string.Empty;
        }

        public string Format()
        {
            return Magic + Sep
                 + Type.ToString() + Sep
                 + SourcePid.ToString(CultureInfo.InvariantCulture) + Sep
                 + ElementId.ToString(CultureInfo.InvariantCulture) + Sep
                 + Escape(Payload);
        }

        public static bool TryParse(string line, out SwitchBackMessage message)
        {
            message = null;
            if (string.IsNullOrEmpty(line)) return false;

            string[] parts = line.Split(Sep);
            if (parts.Length < 5) return false;
            if (!string.Equals(parts[0], Magic, StringComparison.Ordinal)) return false;

            SwitchBackMessageType type;
            if (!TryParseType(parts[1], out type)) return false;

            int pid;
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);

            long id;
            long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out id);

            message = new SwitchBackMessage(type, pid, id, Unescape(parts[4]));
            return true;
        }

        private static bool TryParseType(string s, out SwitchBackMessageType type)
        {
            // Enum.TryParse<T> is unavailable in some older profiles; this is explicit and allocation-free.
            switch (s)
            {
                case "Ping": type = SwitchBackMessageType.Ping; return true;
                case "Pong": type = SwitchBackMessageType.Pong; return true;
                case "Select": type = SwitchBackMessageType.Select; return true;
                case "Ack": type = SwitchBackMessageType.Ack; return true;
                case "Error": type = SwitchBackMessageType.Error; return true;
                default: type = SwitchBackMessageType.Unknown; return false;
            }
        }

        // Control characters written as code points so this file contains no
        // backslash literals at all - the escaping scheme stays unambiguous.
        private const char Esc = (char)92;   // backslash
        private const char CR  = (char)13;
        private const char LF  = (char)10;

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == Esc) { sb.Append(Esc); sb.Append(Esc); }
                else if (c == Sep) { sb.Append(Esc); sb.Append('p'); }
                else if (c == CR) { sb.Append(Esc); sb.Append('r'); }
                else if (c == LF) { sb.Append(Esc); sb.Append('n'); }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != Esc || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char n = s[++i];
                if (n == 'p') sb.Append(Sep);
                else if (n == 'n') sb.Append(LF);
                else if (n == 'r') sb.Append(CR);
                else if (n == Esc) sb.Append(Esc);
                else { sb.Append(Esc); sb.Append(n); }
            }
            return sb.ToString();
        }

        public override string ToString() { return Format(); }
    }
}
