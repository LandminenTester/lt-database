using LtDatabase;

namespace LtDatabase.Server;

/// <summary>One accepted call, already validated by <see cref="DbClient"/> (known operation, allowed namespace).</summary>
internal sealed record BackendCall(string Operation, string Path, string Payload, bool Retry);

/// <summary>Outcome of one call. State is "ok" or "error"; Json is the result, or {"error":{"code","message"}}.</summary>
internal sealed record BackendResult(string State, int Status, string Code, string Json)
{
    public static BackendResult Ok(int status, string json) => new("ok", status, "", json);
    public static BackendResult Fail(int status, string code, string message) => new("error", status, code, ErrorJson(code, message));
    public static BackendResult FailRaw(int status, string code, string json) => new("error", status, code, json);

    public static string ErrorJson(string code, string message) =>
        JsonText.Object(("error", new JsonText.Raw(JsonText.Object(("code", code), ("message", message)))));
}

/// <summary>
/// Where database calls are carried out. The public export contract (DbCall / DbTicket / DbResult) is identical for every
/// backend, so consumers such as lt-core never need to know which one is active.
/// </summary>
internal interface IDbBackend
{
    /// <summary>"http" or "sql", as written in vfs/database.json.</summary>
    string Name { get; }

    /// <summary>True while the backend refuses new calls (circuit breaker). Callers get an immediate "unavailable".</summary>
    bool Unavailable { get; }

    /// <summary>One-line state for lt-db-status.</summary>
    string Describe(DatabaseConfig config);

    Future<BackendResult> Execute(DatabaseConfig config, BackendCall call);
}
