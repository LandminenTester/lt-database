using System.Globalization;
using System.Text;

namespace LtDatabase;

/// <summary>
/// A small JSON reader and writer without reflection, so it is safe under trimming and AOT in the sandbox.
/// Objects become Dictionary&lt;string, object?&gt;, arrays List&lt;object?&gt;, numbers long or double.
/// </summary>
public static class MiniJson
{
    const int MaxDepth = 32;

    public static object? Parse(string text)
    {
        var p = new Parser(text);
        var value = p.Value(0);
        p.SkipSpace();
        if (!p.End) throw new FormatException("Unexpected data after the JSON value.");
        return value;
    }

    public static bool TryParse(string text, out object? value)
    {
        try { value = Parse(text); return true; }
        catch (FormatException) { value = null; return false; }
    }

    public static Dictionary<string, object?>? AsObject(object? v) => v as Dictionary<string, object?>;

    public static string? GetString(Dictionary<string, object?>? o, string key) => o is not null && o.TryGetValue(key, out var v) ? v as string : null;
    public static long? GetLong(Dictionary<string, object?>? o, string key) =>
        o is not null && o.TryGetValue(key, out var v) ? v switch { long l => l, double d when d == Math.Floor(d) => (long)d, _ => null } : null;
    public static bool? GetBool(Dictionary<string, object?>? o, string key) => o is not null && o.TryGetValue(key, out var v) ? v as bool? : null;

    sealed class Parser(string s)
    {
        int i;
        public bool End => i >= s.Length;
        public void SkipSpace() { while (i < s.Length && s[i] is ' ' or '\t' or '\n' or '\r') i++; }

        public object? Value(int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON is nested too deeply.");
            SkipSpace();
            if (End) throw new FormatException("Unexpected end of JSON.");
            switch (s[i])
            {
                case '{': return Obj(depth);
                case '[': return Arr(depth);
                case '"': return Str();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return null;
                default: return Num();
            }
        }

        void Expect(string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("Invalid JSON literal.");
            i += word.Length;
        }

        object Obj(int depth)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            i++;
            SkipSpace();
            if (i < s.Length && s[i] == '}') { i++; return result; }
            while (true)
            {
                SkipSpace();
                if (End || s[i] != '"') throw new FormatException("Expected a property name.");
                var key = Str();
                SkipSpace();
                if (End || s[i] != ':') throw new FormatException("Expected ':'.");
                i++;
                result[key] = Value(depth + 1);
                SkipSpace();
                if (End) throw new FormatException("Unterminated object.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new FormatException("Expected ',' or '}'.");
            }
        }

        object Arr(int depth)
        {
            var result = new List<object?>();
            i++;
            SkipSpace();
            if (i < s.Length && s[i] == ']') { i++; return result; }
            while (true)
            {
                result.Add(Value(depth + 1));
                SkipSpace();
                if (End) throw new FormatException("Unterminated array.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new FormatException("Expected ',' or ']'.");
            }
        }

        string Str()
        {
            var sb = new StringBuilder();
            i++;
            while (true)
            {
                if (End) throw new FormatException("Unterminated string.");
                var c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (End) throw new FormatException("Unterminated escape.");
                var e = s[i++];
                switch (e)
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
                        if (i + 4 > s.Length || !ushort.TryParse(s.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) throw new FormatException("Invalid \\u escape.");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: throw new FormatException("Invalid escape.");
                }
            }
        }

        object Num()
        {
            var start = i;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] is '-' or '+' or '.' or 'e' or 'E')) i++;
            var token = s.AsSpan(start, i - start);
            if (token.Length == 0) throw new FormatException("Unexpected character in JSON.");
            if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            throw new FormatException("Invalid number.");
        }
    }
}

/// <summary>Builds JSON text. Everything that goes into a request is escaped here.</summary>
public static class JsonText
{
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Wrapper for text that already is valid JSON (a filter, a nested object).</summary>
    public readonly record struct Raw(string Json);

    public static string Value(object? v) => v switch
    {
        null => "null",
        Raw r => r.Json,
        string s => Quote(s),
        bool b => b ? "true" : "false",
        int n => n.ToString(CultureInfo.InvariantCulture),
        long n => n.ToString(CultureInfo.InvariantCulture),
        short n => n.ToString(CultureInfo.InvariantCulture),
        byte n => n.ToString(CultureInfo.InvariantCulture),
        uint n => n.ToString(CultureInfo.InvariantCulture),
        ulong n => n.ToString(CultureInfo.InvariantCulture),
        double d when double.IsFinite(d) => d.ToString("R", CultureInfo.InvariantCulture),
        float f when float.IsFinite(f) => f.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateTime t => Quote(t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset t => Quote(t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        DateOnly d => Quote(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        Guid g => Quote(g.ToString()),
        byte[] bytes => Quote(Convert.ToBase64String(bytes)),
        _ => throw new ArgumentException($"A value of type {v.GetType().Name} cannot be written as JSON."),
    };

    public static string Object(params (string Key, object? Value)[] pairs)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (key, value) in pairs)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Quote(key)).Append(':').Append(Value(value));
        }
        return sb.Append('}').ToString();
    }

    public static string Array(IEnumerable<object?> items) => "[" + string.Join(",", items.Select(Value)) + "]";
}
