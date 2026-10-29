using LtDatabase;

namespace LtDatabase.Server;

/// <summary>
/// Sends signed requests to lt-database-api (HMAC envelope, clock sync, retries, circuit breaker).
/// This is the original connector behavior, moved here unchanged so that other backends can sit next to it.
/// </summary>
internal sealed class HttpBackend : IDbBackend
{
    const int CircuitThreshold = 5;
    const long CircuitOpenSeconds = 15, ClockMaxAgeSeconds = 600;

    int failures;
    long clockOffset, clockSyncedAt, circuitOpenUntil;

    public string Name => "http";
    public bool Unavailable => NowUnix() < circuitOpenUntil;

    public string Describe(DatabaseConfig config) =>
        $"backend=http url={config.Url} key={config.KeyId} circuit={(Unavailable ? "open" : "closed")} clockOffset={clockOffset}s";

    public int MaxParallel(DatabaseConfig config) => config.MaxConcurrent;

    long LocalUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    long NowUnix() => LocalUnix() + clockOffset;

    public async Future<BackendResult> Execute(DatabaseConfig cfg, BackendCall call)
    {
        if (LocalUnix() - clockSyncedAt > ClockMaxAgeSeconds) await SyncClock(cfg);

        var attempts = call.Retry ? 3 : 1;
        var resynced = false;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var nonce = Ids.Next();
            var body = Wire.BuildRequest(cfg.Secret, call.Path, cfg.KeyId, NowUnix(), nonce, call.Payload);
            var response = await re.net.Http.Post(cfg.Url + call.Path, body, ["Content-Type: application/json", "Accept: application/json"], cfg.TimeoutMs);

            if (response.Error != 0 || response.Status == 0)
            {
                RecordFailure();
                if (attempt + 1 < attempts) { await Future.Sleep(300UL << attempt); continue; }
                return BackendResult.Fail(0, "network", "The database API could not be reached.");
            }

            var status = (int)response.Status;
            var answer = Wire.ReadResponse(response.Text(), cfg.Secret, nonce, status);
            if (answer.Authentic)
            {
                failures = 0;
                if (status < 400) return BackendResult.Ok(status, answer.Payload);
                return BackendResult.FailRaw(status, ErrorCode(answer.Payload, answer.Code), answer.Payload);
            }

            // Not signed by the API: never trust the content, only decide whether to try again.
            if (status == 401 && !resynced) { resynced = true; await SyncClock(cfg); attempts = Math.Max(attempts, attempt + 2); continue; }
            if (status is 502 or 503 or 504 && attempt + 1 < attempts) { RecordFailure(); await Future.Sleep(300UL << attempt); continue; }
            if (status >= 500 || answer.Code == "bad_signature") RecordFailure();
            var code = answer.Code is "unauthorized" or "rate_limited" or "bad_signature" or "bad_response" ? answer.Code : status == 401 ? "unauthorized" : status == 429 ? "rate_limited" : "bad_response";
            return BackendResult.Fail(status, code, code == "bad_signature" ? "The answer was not signed with the shared secret. It was discarded." : "The database API refused or could not process the request.");
        }
        return BackendResult.Fail(0, "network", "The database API could not be reached.");
    }

    void RecordFailure()
    {
        if (++failures >= CircuitThreshold)
        {
            circuitOpenUntil = NowUnix() + CircuitOpenSeconds;
            failures = 0;
            Console.WriteLine($"[lt_database] the database API failed {CircuitThreshold} times in a row; pausing calls for {CircuitOpenSeconds}s");
        }
    }

    static string ErrorCode(string payload, string fallback)
    {
        if (MiniJson.TryParse(payload, out var parsed) && MiniJson.AsObject(parsed) is { } root
            && MiniJson.AsObject(root.GetValueOrDefault("error")) is { } error && MiniJson.GetString(error, "code") is { Length: > 0 } code) return code;
        return fallback.Length > 0 ? fallback : "error";
    }

    /// <summary>Asks the API for its time so a game server with a wrong clock still signs acceptable timestamps.</summary>
    async Future SyncClock(DatabaseConfig cfg)
    {
        try
        {
            var before = LocalUnix();
            var response = await re.net.Http.Get(cfg.Url + "/v1/health", null, Math.Min(cfg.TimeoutMs, 5000u));
            if (!response.Ok) return;
            if (MiniJson.TryParse(response.Text(), out var parsed) && MiniJson.GetLong(MiniJson.AsObject(parsed), "time") is { } server)
            {
                clockOffset = server - (before + LocalUnix()) / 2;
                clockSyncedAt = LocalUnix();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[lt_database] clock sync failed: {ex.GetType().Name}");
        }
    }
}
