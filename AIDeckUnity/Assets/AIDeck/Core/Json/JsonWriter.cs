using System.Text;

namespace AIDeck.Core.Json
{
    /// <summary>Serializes a <see cref="JsonValue"/> back to text.</summary>
    public static class JsonWriter
    {
        public static string Write(JsonValue value, bool indented = true)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value ?? JsonValue.Null, indented, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, JsonValue value, bool indented, int depth)
        {
            switch (value.Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(value.RawBool ? "true" : "false");
                    break;
                case JsonKind.Number:
                    sb.Append(JsonValue.FormatNumber(value.RawNumber));
                    break;
                case JsonKind.String:
                    WriteString(sb, value.RawString);
                    break;
                case JsonKind.Array:
                    WriteArray(sb, value, indented, depth);
                    break;
                case JsonKind.Object:
                    WriteObject(sb, value, indented, depth);
                    break;
            }
        }

        private static void WriteArray(StringBuilder sb, JsonValue value, bool indented, int depth)
        {
            if (value.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append('[');
            var first = true;
            foreach (var item in value)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                NewLine(sb, indented, depth + 1);
                WriteValue(sb, item, indented, depth + 1);
            }

            NewLine(sb, indented, depth);
            sb.Append(']');
        }

        private static void WriteObject(StringBuilder sb, JsonValue value, bool indented, int depth)
        {
            if (value.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');
            var first = true;
            foreach (var key in value.Keys)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                NewLine(sb, indented, depth + 1);
                WriteString(sb, key);
                sb.Append(':');
                if (indented)
                {
                    sb.Append(' ');
                }

                WriteValue(sb, value[key], indented, depth + 1);
            }

            NewLine(sb, indented, depth);
            sb.Append('}');
        }

        private static void NewLine(StringBuilder sb, bool indented, int depth)
        {
            if (!indented)
            {
                return;
            }

            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                foreach (var c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20 || c == 0x7f)
                            {
                                sb.Append("\\u").Append(((int)c).ToString("x4"));
                            }
                            else
                            {
                                sb.Append(c);
                            }

                            break;
                    }
                }
            }

            sb.Append('"');
        }
    }
}
