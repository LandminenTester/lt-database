using System.Text;

namespace LtDatabase;

/// <summary>
/// The signed envelope of lt-database-api (https://github.com/LandminenTester/lt-database-api, docs/SECURITY.md).
/// Pure functions only, so they are tested on a normal .NET runtime against the API's published test vectors.
/// </summary>
public static class Wire
{
    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string Mac(byte[] secret, string message) => Base64Url(Sha256.Hmac(secret, Encoding.UTF8.GetBytes(message)));

    public static string SignRequest(byte[] secret, string path, string kid, long ts, string nonce, string payload) =>
        Mac(secret, $"v1\n{path}\n{kid}\n{ts}\n{nonce}\n{payload}");

    public static string SignResponse(byte[] secret, string requestNonce, int status, string payload) =>
        Mac(secret, $"v1resp\n{requestNonce}\n{status}\n{payload}");

    /// <summary>Compares without stopping at the first difference.</summary>
    public static bool ConstantTimeEquals(string a, string b)
    {
        var diff = a.Length ^ b.Length;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    public static string Envelope(string kid, long ts, string nonce, string payload, string sig) =>
        JsonText.Object(("v", 1), ("kid", kid), ("ts", ts), ("nonce", nonce), ("payload", payload), ("sig", sig));

    public static string BuildRequest(byte[] secret, string path, string kid, long ts, string nonce, string payload) =>
        Envelope(kid, ts, nonce, payload, SignRequest(secret, path, kid, ts, nonce, payload));

    public readonly record struct Response(bool Authentic, int Status, string Payload, string Code, string Problem);

    /// <summary>
    /// Reads the API's answer. Only a response whose signature matches is "authentic"; everything else must be treated
    /// as an untrusted failure (the API itself answers unauthenticated errors without a signature).
    /// </summary>
    public static Response ReadResponse(string body, byte[] secret, string requestNonce, int httpStatus)
    {
        if (!MiniJson.TryParse(body, out var parsed) || MiniJson.AsObject(parsed) is not { } root)
            return new Response(false, httpStatus, "", "bad_response", "The answer is not a valid envelope.");
        var payload = MiniJson.GetString(root, "payload");
        var status = (int?)MiniJson.GetLong(root, "status");
        var sig = MiniJson.GetString(root, "sig");
        var code = MiniJson.GetString(root, "code") ?? "";
        if (payload is null || status is null)
            return new Response(false, httpStatus, "", "bad_response", "The answer has no payload or status.");
        if (sig is null)
            return new Response(false, httpStatus, "", code.Length > 0 ? code : "unauthenticated", "The answer is not signed.");
        var expected = SignResponse(secret, requestNonce, status.Value, payload);
        if (!ConstantTimeEquals(expected, sig))
            return new Response(false, httpStatus, "", "bad_signature", "The answer signature does not match. It was not produced by the API.");
        if (status.Value != httpStatus)
            return new Response(false, httpStatus, "", "bad_response", "The signed status differs from the HTTP status.");
        return new Response(true, status.Value, payload, code, "");
    }
}
