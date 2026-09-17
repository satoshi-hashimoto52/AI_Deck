using System;
using System.Globalization;
using System.Text;

namespace AIDeck.Core.Json
{
    /// <summary>
    /// Recursive-descent JSON reader. <see cref="TryParse"/> never throws — callers
    /// that load a possibly corrupt file on disk use it and fall back to defaults.
    /// </summary>
    public static class JsonParser
    {
        /// <summary>Nesting limit, so a hand-edited or truncated file cannot blow the stack.</summary>
        public const int MaxDepth = 64;

        public static bool TryParse(string text, out JsonValue value, out string error)
        {
            value = JsonValue.Null;
            error = null;

            if (string.IsNullOrEmpty(text))
            {
                error = "empty document";
                return false;
            }

            try
            {
                var index = 0;
                var result = ParseValue(text, ref index, 0);
                SkipWhitespace(text, ref index);
                if (index != text.Length)
                {
                    error = $"trailing content at offset {index}";
                    return false;
                }

                value = result;
                return true;
            }
            catch (JsonParseException ex)
            {
                error = ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static JsonValue Parse(string text)
        {
            if (!TryParse(text, out var value, out var error))
            {
                throw new JsonParseException(error);
            }

            return value;
        }

        private static JsonValue ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new JsonParseException($"nesting deeper than {MaxDepth}");
            }

            SkipWhitespace(s, ref i);
            if (i >= s.Length)
            {
                throw new JsonParseException("unexpected end of document");
            }

            var c = s[i];
            switch (c)
            {
                case '{':
                    return ParseObject(s, ref i, depth);
                case '[':
                    return ParseArray(s, ref i, depth);
                case '"':
                    return JsonValue.String(ParseString(s, ref i));
                case 't':
                    Expect(s, ref i, "true");
                    return JsonValue.Bool(true);
                case 'f':
                    Expect(s, ref i, "false");
                    return JsonValue.Bool(false);
                case 'n':
                    Expect(s, ref i, "null");
                    return JsonValue.Null;
                default:
                    return JsonValue.Number(ParseNumber(s, ref i));
            }
        }

        private static JsonValue ParseObject(string s, ref int i, int depth)
        {
            var obj = JsonValue.NewObject();
            i++; // '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return obj;
            }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"')
                {
                    throw new JsonParseException($"expected member name at offset {i}");
                }

                var key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':')
                {
                    throw new JsonParseException($"expected ':' at offset {i}");
                }

                i++;
                obj.Set(key, ParseValue(s, ref i, depth + 1));
                SkipWhitespace(s, ref i);
                if (i >= s.Length)
                {
                    throw new JsonParseException("unterminated object");
                }

                if (s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (s[i] == '}')
                {
                    i++;
                    return obj;
                }

                throw new JsonParseException($"expected ',' or '}}' at offset {i}");
            }
        }

        private static JsonValue ParseArray(string s, ref int i, int depth)
        {
            var arr = JsonValue.NewArray();
            i++; // '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return arr;
            }

            while (true)
            {
                arr.Add(ParseValue(s, ref i, depth + 1));
                SkipWhitespace(s, ref i);
                if (i >= s.Length)
                {
                    throw new JsonParseException("unterminated array");
                }

                if (s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (s[i] == ']')
                {
                    i++;
                    return arr;
                }

                throw new JsonParseException($"expected ',' or ']' at offset {i}");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length)
                {
                    throw new JsonParseException("unterminated string");
                }

                var c = s[i];
                if (c == '"')
                {
                    i++;
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                i++;
                if (i >= s.Length)
                {
                    throw new JsonParseException("unterminated escape sequence");
                }

                var esc = s[i++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length)
                        {
                            throw new JsonParseException("truncated \\u escape");
                        }

                        var hex = s.Substring(i, 4);
                        if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            throw new JsonParseException($"bad \\u escape '{hex}'");
                        }

                        sb.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw new JsonParseException($"unknown escape '\\{esc}'");
                }
            }
        }

        private static double ParseNumber(string s, ref int i)
        {
            var start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            {
                i++;
            }

            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' ||
                                    ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E'))))
            {
                i++;
            }

            var token = s.Substring(start, i - start);
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                throw new JsonParseException($"bad number '{token}' at offset {start}");
            }

            return d;
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
            {
                throw new JsonParseException($"expected '{literal}' at offset {i}");
            }

            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                var c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                {
                    i++;
                    continue;
                }

                break;
            }
        }
    }

    public sealed class JsonParseException : Exception
    {
        public JsonParseException(string message) : base(message)
        {
        }
    }
}
