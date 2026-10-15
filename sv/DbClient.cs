using LtDatabase;

namespace LtDatabase.Server;

/// <summary>
/// Sends signed requests to lt-database-api and keeps the results until the caller collects them.
/// Exports are synchronous, HTTP is not, so every call returns a ticket that is polled with TakeResult.
/// </summary>
internal static class DbClient
{
    sealed record Operation(string Name, string Path, bool RetrySafe);

    static readonly Dictionary<string, Operation> Operations = new[]
    {
        new Operation("health", "/v1/health", true),
        new Operation("schema.sync", "/v1/schema/sync", true),     // declarative: running it twice is harmless
        new Operation("schema.describe", "/v1/schema/describe", true),
        new Operation("find", "/v1/data/find", true),
        new Operation("count", "/v1/data/count", true),
        new Operation("delete", "/v1/data/delete", true),           // same filter, same outcome
        new Operation("insert", "/v1/data/insert", false),          // retried only with an idempotencyKey
        new Operation("upsert", "/v1/data/upsert", false),
        new Operation("update", "/v1/data/update", false),
    }.ToDictionary(o => o.Name);

    sealed record Job(string Id, Operation Op, string Payload, bool Retry);

    const int MaxPending = 512, MaxPayloadChars = 1_000_000, CircuitThreshold = 5;
    const long CircuitOpenSeconds = 15, ClockMaxAgeSeconds = 600, ResultKeepMs = 120_000;

    static DatabaseConfig? config;
    static string configError = "Not loaded yet.";
    static readonly Dictionary<string, DbResult> Results = new();
    static readonly Queue<Job> Waiting = new();
    static int inFlight, sequence, failures;
    static long clockOffset, clockSyncedAt, circuitOpenUntil;

    public static bool Configured => config is not null;
    public static string ConfigError => configError;
    public static string Summary =>
        config is null ? $"not configured: {configError}"
        : $"url={config.Url} key={config.KeyId} namespaces=[{string.Join(",", config.AllowedNamespaces)}] pending={Results.Count(r => r.Value.State == "pending")} queued={Waiting.Count} inflight={inFlight} circuit={(NowUnix() < circuitOpenUntil ? "open" : "closed")} clockOffset={clockOffset}s";

    public static void Reload()
    {
        config = DatabaseConfig.Load(out configError);
        failures = 0; circuitOpenUntil = 0; clockSyncedAt = 0;
        Console.WriteLine(config is null ? $"[lt_database] NOT CONFIGURED: {configError}" : $"[lt_database] ready -> {config.Url} as '{config.KeyId}'");
    }

    static long LocalUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static long NowUnix() => LocalUnix() + clockOffset;

    // ---------------------------------------------------------------- exports

    public static DbTicket Call(DbCall call)
    {
        if (config is null) return Reject("not_configured", configError);
        if (call is null || !Operations.TryGetValue(call.Operation ?? "", out var op)) return Reject("unknown_operation", "Unknown operation.");
        var json = call.Json ?? "";
        if (json.Length > MaxPayloadChars) return Reject("payload_too_large", "The request is too large.");
        if (Results.Count + Waiting.Count >= MaxPending) return Reject("busy", "Too many database calls are waiting.");
        if (NowUnix() < circuitOpenUntil) return Reject("unavailable", "The database API is not answering. Retrying shortly.");

        // Look into the payload once: the namespace must be allowed here, and an idempotencyKey makes retries safe.
        var root = MiniJson.TryParse(json, out var parsed) ? MiniJson.AsObject(parsed) : null;
        if (root is null) return Reject("invalid_request", "The request is not a JSON object.");
        if (op.Name != "health")
        {
            var ns = MiniJson.GetString(root, "namespace");
            if (ns is null || !DatabaseConfig.IsIdentifier(ns)) return Reject("invalid_identifier", "The request needs a valid namespace.");
            if (!config.AllowsNamespace(ns)) return Reject("namespace_not_allowed", $"This server does not allow the namespace '{ns}'.");
        }

        var id = "db-" + ++sequence;
        var retry = op.RetrySafe || MiniJson.GetString(root, "idempotencyKey") is { Length: > 0 };
        Results[id] = new DbResult(id, "pending", 0, "", "");
        Waiting.Enqueue(new Job(id, op, json, retry));
        // Never keep a result forever, even if the caller forgets to collect it.
        Futures.Scheduler.Push((ulong)(ResultKeepMs + config.TimeoutMs * 4), () => Results.Remove(id));
        Pump();
        return new DbTicket(id, true, "");
    }

    public static DbResult Take(DbResultQuery query)
    {
        if (query is null || !Results.TryGetValue(query.Id ?? "", out var result)) return new DbResult(query?.Id ?? "", "missing", 0, "", "");
        if (result.State == "pending") return result;
        Results.Remove(result.Id);
        return result;
    }

    static DbTicket Reject(string code, string message) => new("", false, code + ": " + message);

    // ---------------------------------------------------------------- execution

    static void Pump()
    {
        if (config is null) return;
        while (inFlight < config.MaxConcurrent && Waiting.TryDequeue(out var job))
        {
            inFlight++;
            var current = job;
            Future.Spawn(() => Execute(current));
        }
    }

    static async Future Execute(Job job)
    {
        try
        {
            var cfg = config;
            if (cfg is null) { Finish(job.Id, "error", 0, "not_configured", Error("not_configured", configError)); return; }
            if (LocalUnix() - clockSyncedAt > ClockMaxAgeSeconds) await SyncClock(cfg);

            var attempts = job.Retry ? 3 : 1;
            var resynced = false;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var nonce = Ids.Next();
                var body = Wire.BuildRequest(cfg.Secret, job.Op.Path, cfg.KeyId, NowUnix(), nonce, job.Payload);
                var response = await re.net.Http.Post(cfg.Url + job.Op.Path, body, ["Content-Type: application/json", "Accept: application/json"], cfg.TimeoutMs);

                if (response.Error != 0 || response.Status == 0)
                {
                    RecordFailure();
                    if (attempt + 1 < attempts) { await Future.Sleep(300UL << attempt); continue; }
                    Finish(job.Id, "error", 0, "network", Error("network", "The database API could not be reached."));
                    return;
                }

                var status = (int)response.Status;
                var answer = Wire.ReadResponse(response.Text(), cfg.Secret, nonce, status);
                if (answer.Authentic)
                {
                    failures = 0;
                    if (status < 400) Finish(job.Id, "ok", status, "", answer.Payload);
                    else Finish(job.Id, "error", status, ErrorCode(answer.Payload, answer.Code), answer.Payload);
                    return;
                }

                // Not signed by the API: never trust the content, only decide whether to try again.
                if (status == 401 && !resynced) { resynced = true; await SyncClock(cfg); attempts = Math.Max(attempts, attempt + 2); continue; }
                if (status is 502 or 503 or 504 && attempt + 1 < attempts) { RecordFailure(); await Future.Sleep(300UL << attempt); continue; }
                if (status >= 500 || answer.Code == "bad_signature") RecordFailure();
                var code = answer.Code is "unauthorized" or "rate_limited" or "bad_signature" or "bad_response" ? answer.Code : status == 401 ? "unauthorized" : status == 429 ? "rate_limited" : "bad_response";
                Finish(job.Id, "error", status, code, Error(code, code == "bad_signature" ? "The answer was not signed with the shared secret. It was discarded." : "The database API refused or could not process the request."));
                return;
            }
        }
        catch (Exception ex)
        {
            // Details stay in the server log; the caller only learns that the call failed.
            Console.WriteLine($"[lt_database] call {job.Id} failed: {ex.GetType().Name}");
            Finish(job.Id, "error", 0, "internal", Error("internal", "The database call failed unexpectedly."));
        }
        finally
        {
            inFlight--;
            Pump();
        }
    }

    static void Finish(string id, string state, int status, string code, string json)
    {
        if (!Results.ContainsKey(id)) return; // expired while waiting
        Results[id] = new DbResult(id, state, status, code, json);
    }

    static void RecordFailure()
    {
        if (++failures >= CircuitThreshold)
        {
            circuitOpenUntil = NowUnix() + CircuitOpenSeconds;
            failures = 0;
            Console.WriteLine($"[lt_database] the database API failed {CircuitThreshold} times in a row; pausing calls for {CircuitOpenSeconds}s");
        }
    }

    static string Error(string code, string message) => JsonText.Object(("error", new JsonText.Raw(JsonText.Object(("code", code), ("message", message)))));

    static string ErrorCode(string payload, string fallback)
    {
        if (MiniJson.TryParse(payload, out var parsed) && MiniJson.AsObject(parsed) is { } root
            && MiniJson.AsObject(root.GetValueOrDefault("error")) is { } error && MiniJson.GetString(error, "code") is { Length: > 0 } code) return code;
        return fallback.Length > 0 ? fallback : "error";
    }

    /// <summary>Asks the API for its time so a game server with a wrong clock still signs acceptable timestamps.</summary>
    static async Future SyncClock(DatabaseConfig cfg)
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
