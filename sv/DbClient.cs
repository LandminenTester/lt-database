using LtDatabase;

namespace LtDatabase.Server;

/// <summary>
/// Accepts database calls, hands them to the configured backend (lt-database-api over HTTP, or re.sql directly) and keeps
/// the results until the caller collects them. Exports are synchronous, the backends are not, so every call returns a
/// ticket that is polled with TakeResult.
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
        new Operation("delete", "/v1/data/delete", false),          // a repeated filter can match newly inserted rows
        new Operation("flush", "/v1/data/flush", true),             // applies queued writes (API write-behind mode)
        new Operation("insert", "/v1/data/insert", false),          // no automatic retry without a durable effect receipt
        new Operation("upsert", "/v1/data/upsert", false),
        new Operation("update", "/v1/data/update", false),
        new Operation("mutation.execute", "/v1/data/mutation/execute", false),
        new Operation("mutation.receipt", "/v1/data/mutation/receipt", true),
    }.ToDictionary(o => o.Name);

    sealed record Job(string Id, BackendCall Call);

    const int MaxPending = 512, MaxPayloadChars = 1_000_000;
    const long ResultKeepMs = 120_000;

    static DatabaseConfig? config;
    static IDbBackend? backend;
    static string configError = "Not loaded yet.";
    static readonly Dictionary<string, DbResult> Results = new();
    static readonly Queue<Job> Waiting = new();
    static int inFlight, sequence;

    public static bool Configured => config is not null;
    public static string ConfigError => configError;
    public static string Summary =>
        config is null || backend is null ? $"not configured: {configError}"
        : $"{backend.Describe(config)} namespaces=[{string.Join(",", config.AllowedNamespaces)}] pending={Results.Count(r => r.Value.State == "pending")} queued={Waiting.Count} inflight={inFlight}";

    public static void Reload()
    {
        config = DatabaseConfig.Load(out configError);
        // A fresh backend per reload also resets the circuit breaker and clock offset of the HTTP backend.
        backend = config is null ? null : config.Backend == DatabaseConfig.SqlBackendName ? new SqlBackend() : new HttpBackend();
        Console.WriteLine(config is null || backend is null
            ? $"[lt_database] NOT CONFIGURED: {configError}"
            : config.Backend == DatabaseConfig.SqlBackendName
                ? $"[lt_database] ready -> re.sql alias '{config.SqlAlias}'"
                : $"[lt_database] ready -> {config.Url} as '{config.KeyId}'");
    }

    // ---------------------------------------------------------------- exports

    public static DbTicket Call(DbCall call)
    {
        if (config is null || backend is null) return Reject("not_configured", configError);
        if (call is null || !Operations.TryGetValue(call.Operation ?? "", out var op)) return Reject("unknown_operation", "Unknown operation.");
        var json = call.Json ?? "";
        if (json.Length > MaxPayloadChars) return Reject("payload_too_large", "The request is too large.");
        if (Results.Count + Waiting.Count >= MaxPending) return Reject("busy", "Too many database calls are waiting.");
        if (backend.Unavailable) return Reject("unavailable", "The database is not answering. Retrying shortly.");

        // Look into the payload once: the namespace must be allowed here. An idempotencyKey alone is not durable proof
        // that a mutation was applied exactly once, so it must not enable an automatic network retry.
        var root = MiniJson.TryParse(json, out var parsed) ? MiniJson.AsObject(parsed) : null;
        if (root is null) return Reject("invalid_request", "The request is not a JSON object.");
        if (op.Name != "health")
        {
            var ns = MiniJson.GetString(root, "namespace");
            if (ns is null || !DatabaseConfig.IsIdentifier(ns)) return Reject("invalid_identifier", "The request needs a valid namespace.");
            if (!config.AllowsNamespace(ns)) return Reject("namespace_not_allowed", $"This server does not allow the namespace '{ns}'.");
        }

        var id = "db-" + ++sequence;
        Results[id] = new DbResult(id, "pending", 0, "", "");
        Waiting.Enqueue(new Job(id, new BackendCall(op.Name, op.Path, json, op.RetrySafe)));
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
        var limit = Math.Min(config.MaxConcurrent, backend?.MaxParallel(config) ?? config.MaxConcurrent);
        while (inFlight < limit && Waiting.TryDequeue(out var job))
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
            var active = backend;
            if (cfg is null || active is null) { Finish(job.Id, "error", 0, "not_configured", BackendResult.ErrorJson("not_configured", configError)); return; }
            var result = await active.Execute(cfg, job.Call);
            Finish(job.Id, result.State, result.Status, result.Code, result.Json);
        }
        catch (Exception ex)
        {
            // Details stay in the server log; the caller only learns that the call failed.
            Console.WriteLine($"[lt_database] call {job.Id} failed: {ex.GetType().Name}");
            Finish(job.Id, "error", 0, "internal", BackendResult.ErrorJson("internal", "The database call failed unexpectedly."));
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
}
