using System.Text;
using LtDatabase;

namespace LtDatabase.Server;

/// <summary>
/// Settings read from the bundle's private vfs/ folder (never streamed to players):
///   database.json    {"url":"https://db.example.com","keyId":"gameserver","secretFile":"database.secret","allowedNamespaces":["mygame"]}
///   database.secret  the key secret, one line, at least 32 characters
/// Nothing sensitive is compiled into the bundle.
/// </summary>
internal sealed class DatabaseConfig
{
    public const string ConfigFile = "database.json";
    public const int MinSecretBytes = 32;

    public string Url { get; private init; } = "";
    public string KeyId { get; private init; } = "";
    public byte[] Secret { get; private init; } = [];
    public uint TimeoutMs { get; private init; } = 15000;
    public int MaxConcurrent { get; private init; } = 8;
    public IReadOnlyList<string> AllowedNamespaces { get; private init; } = [];

    public bool AllowsNamespace(string ns) => AllowedNamespaces.Contains("*") || AllowedNamespaces.Contains(ns);

    public static DatabaseConfig? Load(out string error)
    {
        error = "";
        if (!File.Exists(ConfigFile)) { error = $"vfs/{ConfigFile} is missing. Copy database.example.json and fill it in."; return null; }
        string text;
        try { text = File.ReadAllText(ConfigFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = $"vfs/{ConfigFile} cannot be read."; return null; }
        if (!MiniJson.TryParse(text, out var parsed) || MiniJson.AsObject(parsed) is not { } root) { error = $"vfs/{ConfigFile} is not valid JSON."; return null; }

        var url = (MiniJson.GetString(root, "url") ?? "").TrimEnd('/');
        var keyId = MiniJson.GetString(root, "keyId") ?? "";
        var secretFile = MiniJson.GetString(root, "secretFile") ?? "database.secret";
        var allowInsecure = MiniJson.GetBool(root, "allowInsecureHttp") ?? false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) { error = "\"url\" must be an http(s) address."; return null; }
        var local = uri.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
        if (uri.Scheme == "http" && !local && !allowInsecure) { error = "\"url\" uses plain http to a remote host. Use https (or set allowInsecureHttp for a trusted network)."; return null; }
        if (keyId.Length is 0 or > 64) { error = "\"keyId\" is missing or too long."; return null; }
        if (secretFile.Contains("..") || Path.IsPathRooted(secretFile)) { error = "\"secretFile\" must be a file inside the vfs folder."; return null; }
        if (!File.Exists(secretFile)) { error = $"vfs/{secretFile} is missing."; return null; }

        var secret = Encoding.UTF8.GetBytes(File.ReadAllText(secretFile).Trim());
        if (secret.Length < MinSecretBytes) { error = $"The key secret is too short. Use at least {MinSecretBytes} random characters."; return null; }

        var namespaces = new List<string>();
        if (root.TryGetValue("allowedNamespaces", out var list) && list is List<object?> items)
            foreach (var item in items) if (item is string ns && (ns == "*" || IsIdentifier(ns))) namespaces.Add(ns);
        if (namespaces.Count == 0) { error = "\"allowedNamespaces\" must list at least one namespace (or \"*\")."; return null; }

        return new DatabaseConfig
        {
            Url = url,
            KeyId = keyId,
            Secret = secret,
            TimeoutMs = (uint)Math.Clamp(MiniJson.GetLong(root, "timeoutMs") ?? 15000, 1000, 120000),
            MaxConcurrent = (int)Math.Clamp(MiniJson.GetLong(root, "maxConcurrent") ?? 8, 1, 64),
            AllowedNamespaces = namespaces,
        };
    }

    /// <summary>Same rule as the API: lower-case words joined by single underscores, 24 characters at most.</summary>
    public static bool IsIdentifier(string value)
    {
        if (value.Length is 0 or > 24 || !char.IsAsciiLetterLower(value[0])) return false;
        var previousUnderscore = false;
        foreach (var c in value)
        {
            if (c == '_') { if (previousUnderscore) return false; previousUnderscore = true; continue; }
            previousUnderscore = false;
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)) return false;
        }
        return !previousUnderscore;
    }
}
