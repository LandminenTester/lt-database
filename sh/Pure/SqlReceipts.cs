using System.Globalization;
using System.Text;

namespace LtDatabase;

public sealed record MutationReceipt(string EffectId, string Namespace, string Operation, string Fingerprint, string State, string Code, string Json)
{
    /// <summary>Wire form, identical to the API's receipt object.</summary>
    public string ToJson() => JsonText.Object(("effectId", EffectId), ("namespace", Namespace), ("operation", Operation),
        ("fingerprint", Fingerprint), ("state", State), ("code", Code), ("json", Json));
}

public sealed record MutationReceiptRequest(string Namespace, string EffectId, string Operation, string Fingerprint, string Json);

/// <summary>
/// The receipt contract of lt-database-api (docs/API.md, mutation.execute / mutation.receipt): same limits, same strictness,
/// same fingerprint, so lt-core's recovery code cannot tell the backends apart.
/// </summary>
public static class MutationReceipts
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public const int MaxJsonBytes = 8192, MaxEnvelopeBytes = 65536, MaxDepth = 16;

    /// <summary>The request body has exactly these string members and nothing else.</summary>
    public static Dictionary<string, object?> ParseEnvelope(string payload, params string[] fields)
    {
        try
        {
            if (StrictUtf8.GetByteCount(payload) > MaxEnvelopeBytes) throw new FormatException();
            if (MiniJson.Parse(payload, strict: true) is not Dictionary<string, object?> root || root.Count != fields.Length) throw new FormatException();
            foreach (var (name, value) in root)
            {
                if (!fields.Contains(name, StringComparer.Ordinal) || value is not string text) throw new FormatException();
                StrictUtf8.GetByteCount(name);
                StrictUtf8.GetByteCount(text);
            }
            return root;
        }
        catch (Exception ex) when (ex is FormatException or EncoderFallbackException or ArgumentException)
        {
            throw new SqlRequestException(400, "invalid_request", "The mutation receipt envelope is invalid.");
        }
    }

    public static void ValidateIdentity(string ns, string effectId, string operation, string fingerprint)
    {
        SqlSchema.Namespace(ns);
        if (string.IsNullOrEmpty(effectId) || effectId.Length > 128 ||
            effectId.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or ':')))
            throw Bad("The effect ID must contain 1 to 128 ASCII letters, digits, underscores, hyphens, dots or colons.");
        if (operation is not ("Upsert" or "Update" or "Delete")) throw Bad("Unsupported receipt operation.");
        if (fingerprint is null || fingerprint.Length != 64 || fingerprint.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Bad("The fingerprint must be a lowercase SHA-256 digest.");
    }

    public static void Validate(MutationReceiptRequest r)
    {
        ValidateIdentity(r.Namespace, r.EffectId, r.Operation, r.Fingerprint);
        if (string.IsNullOrEmpty(r.Json) || r.Json.Length > MaxJsonBytes) throw Bad("Mutation JSON exceeds the 8192 byte limit.");
        try
        {
            if (StrictUtf8.GetByteCount(r.Json) > MaxJsonBytes) throw Bad("Mutation JSON exceeds the 8192 byte limit.");
            var root = MiniJson.Parse(r.Json, strict: true) as Dictionary<string, object?> ?? throw Bad("Mutation JSON must be an object.");
            if (Depth(root) > MaxDepth) throw new FormatException();
            CheckStrings(root);
            // The command root is also compared case-insensitively, because the receipt belongs to exactly one namespace and never to "all".
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in root)
            {
                if (!names.Add(name)) throw new FormatException();
                if (name.Equals("namespace", StringComparison.OrdinalIgnoreCase) && (value is not string given || given != r.Namespace))
                    throw Bad("The mutation namespace must match the receipt namespace.");
                if (name.Equals("all", StringComparison.OrdinalIgnoreCase) && value is not false)
                    throw Bad("Atomic mutations do not allow all=true.");
            }
        }
        catch (Exception ex) when (ex is FormatException or EncoderFallbackException or ArgumentException)
        {
            throw Bad("Mutation JSON must be valid strict UTF-8 JSON without duplicate keys.");
        }
        if (r.Fingerprint != Fingerprint(r.EffectId, r.Namespace, r.Operation, r.Json))
            throw new SqlRequestException(409, "receipt_conflict", "The fingerprint does not match the original mutation.");
    }

    static void CheckStrings(object? v)
    {
        switch (v)
        {
            case string s: StrictUtf8.GetByteCount(s); break;
            case Dictionary<string, object?> o: foreach (var (k, x) in o) { StrictUtf8.GetByteCount(k); CheckStrings(x); } break;
            case List<object?> a: foreach (var x in a) CheckStrings(x); break;
        }
    }

    static int Depth(object? v) => v switch
    {
        Dictionary<string, object?> o => 1 + (o.Count == 0 ? 0 : o.Values.Max(Depth)),
        List<object?> a => 1 + (a.Count == 0 ? 0 : a.Max(Depth)),
        _ => 0,
    };

    /// <summary>Lengths are original UTF-16 code units, exactly as in the Core StorageCommand fingerprint.</summary>
    public static string Fingerprint(string effectId, string ns, string operation, string json)
    {
        var framed = new StringBuilder();
        foreach (var value in new[] { effectId, ns, operation, json })
            framed.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        return Convert.ToHexString(Sha256.Hash(StrictUtf8.GetBytes(framed.ToString()))).ToLowerInvariant();
    }

    static SqlRequestException Bad(string message) => new(400, "invalid_request", message);
}

/// <summary>Maps database driver failures to the stable error codes of the API without ever exposing driver messages (they can contain row values).</summary>
public static class SqlErrors
{
    public static SqlRequestException Classify(string exceptionType, string message)
    {
        var m = message.ToLowerInvariant();
        if (m.Contains("unique") || m.Contains("duplicate") || m.Contains("constraint") || m.Contains("violates") || m.Contains("not null") || m.Contains("foreign key"))
            return new SqlRequestException(409, "constraint_violation", "A constraint rejected the operation (duplicate value, missing value or broken reference).");
        if (m.Contains("deadlock") || m.Contains("could not serialize") || m.Contains("database is locked") || m.Contains("busy"))
            return new SqlRequestException(409, "conflict_retry", "The operation conflicted with another one. Try again.");
        if (m.Contains("timeout") || m.Contains("timed out") || exceptionType.Contains("Timeout", StringComparison.Ordinal))
            return new SqlRequestException(504, "database_timeout", "The database did not answer in time.");
        if (m.Contains("invalid input syntax") || m.Contains("out of range") || m.Contains("value too long") || m.Contains("datatype mismatch"))
            return new SqlRequestException(400, "invalid_value", "The database rejected a value.");
        return new SqlRequestException(500, "database_error", "The database could not complete the operation.");
    }
}
