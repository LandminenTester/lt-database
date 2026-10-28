using System.Globalization;

namespace LtDatabase;

/// <summary>Converts JSON values (as produced by <see cref="MiniJson"/>) to typed driver values and back. Nothing is ever concatenated into SQL.</summary>
public static class SqlValues
{
    const int MaxJsonChars = 1_000_000;

    public static object? FromJson(SqlColumn c, object? e, SqlDialect dialect)
    {
        if (e is null) return null;
        return dialect.ToDriver(c, Coerce(c, e));
    }

    static object Coerce(SqlColumn c, object e)
    {
        try
        {
            switch (c.Type)
            {
                case ColumnTypes.Int32:
                    return e is long l32 ? checked((int)l32) : e is double d32 && d32 == Math.Floor(d32) ? checked((int)d32) : throw Mismatch(c);
                case ColumnTypes.Int64:
                    return e is long l64 ? l64 : e is double d64 && d64 == Math.Floor(d64) && Math.Abs(d64) < 9e15 ? (long)d64 : throw Mismatch(c);
                case ColumnTypes.Float64:
                    return e is long lf ? (double)lf : e is double df ? df : throw Mismatch(c);
                case ColumnTypes.Decimal:
                    return e switch
                    {
                        long ld => (decimal)ld,
                        double dd => (decimal)dd,
                        string sd => decimal.Parse(sd, NumberStyles.Number, CultureInfo.InvariantCulture),
                        _ => throw Mismatch(c),
                    };
                case ColumnTypes.Bool:
                    return e is bool b ? b : throw Mismatch(c);
                case ColumnTypes.String or ColumnTypes.Text:
                    if (e is not string s) throw Mismatch(c);
                    if (s.Contains('\0')) throw new SqlRequestException(400, "invalid_value", $"Column '{c.Name}' contains an invalid character.");
                    if (c.Type == ColumnTypes.String && c.Length is { } max && s.Length > max)
                        throw new SqlRequestException(400, "value_too_long", $"Column '{c.Name}' accepts at most {max} characters.");
                    return s;
                case ColumnTypes.Json:
                    var raw = e is string js ? JsonText.Quote(js) : JsonText.Value(Reshape(e));
                    if (raw.Length > MaxJsonChars) throw new SqlRequestException(400, "value_too_long", $"Column '{c.Name}' is too large.");
                    return raw;
                case ColumnTypes.DateTime:
                    return e is string dt ? DateTime.Parse(dt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : throw Mismatch(c);
                case ColumnTypes.Date:
                    return e is string dts ? DateOnly.ParseExact(dts, "yyyy-MM-dd", CultureInfo.InvariantCulture) : throw Mismatch(c);
                case ColumnTypes.Uuid:
                    return e is string g ? Guid.Parse(g) : throw Mismatch(c);
                case ColumnTypes.Bytes:
                    if (e is not string b64) throw Mismatch(c);
                    var bytes = Convert.FromBase64String(b64);
                    if (c.Length is { } n && bytes.Length > n) throw new SqlRequestException(400, "value_too_long", $"Column '{c.Name}' accepts at most {n} bytes.");
                    return bytes;
                default: throw Mismatch(c);
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw Mismatch(c);
        }
    }

    // MiniJson objects and lists are Dictionary / List; JsonText.Value only writes scalars, so nested values are re-serialised here.
    static object? Reshape(object? v) => v switch
    {
        Dictionary<string, object?> o => new JsonText.Raw(JsonText.Object(o.Select(p => (p.Key, (object?)Reshape(p.Value))).ToArray())),
        List<object?> a => new JsonText.Raw(JsonText.Array(a.Select(Reshape))),
        _ => v,
    };

    /// <summary>Turns a value read from the database into something <see cref="JsonText.Value"/> can write. Tolerant about how the host maps a type.</summary>
    public static object? ToJson(SqlColumn c, object? v)
    {
        if (v is null or DBNull) return null;
        return c.Type switch
        {
            ColumnTypes.Int32 => Convert.ToInt32(v, CultureInfo.InvariantCulture),
            ColumnTypes.Int64 => Convert.ToInt64(v, CultureInfo.InvariantCulture),
            ColumnTypes.Float64 => Convert.ToDouble(v, CultureInfo.InvariantCulture),
            ColumnTypes.Decimal => v is string sd ? decimal.Parse(sd, NumberStyles.Number, CultureInfo.InvariantCulture) : Convert.ToDecimal(v, CultureInfo.InvariantCulture),
            ColumnTypes.Bool => v is bool b ? b : v is string sb ? sb is "t" or "true" or "1" : Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0,
            ColumnTypes.DateTime => AsUtc(v switch
            {
                DateTime d => d,
                DateTimeOffset o => o.UtcDateTime,
                _ => DateTime.Parse(v.ToString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            }).ToString("O", CultureInfo.InvariantCulture),
            ColumnTypes.Date => v switch
            {
                DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTime t => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => v.ToString()!.Length >= 10 ? v.ToString()![..10] : v.ToString(),
            },
            ColumnTypes.Uuid => v.ToString()!.ToLowerInvariant(),
            ColumnTypes.Bytes => v is byte[] bytes ? Convert.ToBase64String(bytes) : v.ToString(),
            ColumnTypes.Json => MiniJson.TryParse(v.ToString()!, out _) ? new JsonText.Raw(v.ToString()!) : v.ToString(),
            _ => v.ToString(),
        };
    }

    static DateTime AsUtc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    static SqlRequestException Mismatch(SqlColumn c) => new(400, "invalid_value", $"The value for column '{c.Name}' does not fit its type '{c.Type}'.");
}
