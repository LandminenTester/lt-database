// Link this source into a consumer's SERVER project and reference ../lt_database/sh/sh.csproj:
//   <ProjectReference Include="../../lt_database/sh/sh.csproj" />
//   <Compile Include="../../lt_database/import/LtDatabase.Server.cs" Link="LtDatabase.Server.cs" />
// Then, in App.Start():  Db.Sync("mygame", schema, r => Console.WriteLine(r.State));
using LtDatabase;

namespace LtDatabaseClient;

/// <summary>
/// Calls the lt_database bundle. Every method returns at once; the callback runs later with the result.
/// Check <c>result.State == "ok"</c>, then read <c>result.Json</c> (see <see cref="Db.Rows"/> for query results).
/// </summary>
public static class Db
{
    /// <summary>Folder name of the connector bundle.</summary>
    public static string Bundle = "lt_database";

    public static DbTicket Call(string operation, string json, Action<DbResult> done)
    {
        DbTicket ticket;
        try { ticket = re.Exports.Call<DbTicket, DbCall>(Bundle, "Call", new(operation, json)); }
        catch (Exception)
        {
            done(new DbResult("", "error", 0, "bundle_unavailable", JsonText.Object(("error", new JsonText.Raw(JsonText.Object(("code", "bundle_unavailable"), ("message", "The lt_database bundle is not running.")))))));
            return new DbTicket("", false, "bundle_unavailable");
        }
        if (!ticket.Accepted)
        {
            var code = ticket.Error.Contains(':') ? ticket.Error[..ticket.Error.IndexOf(':')] : "rejected";
            done(new DbResult("", "error", 0, code, JsonText.Object(("error", new JsonText.Raw(JsonText.Object(("code", code), ("message", ticket.Error)))))));
            return ticket;
        }
        void Poll()
        {
            var r = re.Exports.Call<DbResult, DbResultQuery>(Bundle, "TakeResult", new(ticket.Id));
            if (r.State == "pending") Futures.Scheduler.Push(50, Poll);
            else done(r);
        }
        Futures.Scheduler.Push(50, Poll);
        return ticket;
    }

    // ---- schema

    /// <summary>Declare the tables of a namespace. Run it at start-up; it only changes what differs. Build the schema with <see cref="S"/>.</summary>
    public static DbTicket Sync(string ns, string schemaJson, Action<DbResult> done, bool dryRun = false) =>
        Call("schema.sync", JsonText.Object(("namespace", ns), ("schema", new JsonText.Raw(schemaJson)), ("dryRun", dryRun)), done);

    public static DbTicket Describe(string ns, Action<DbResult> done) => Call("schema.describe", JsonText.Object(("namespace", ns)), done);

    // ---- data

    /// <summary>Rows are JSON objects, see <see cref="Q.Row"/>. A fresh idempotency key makes retries after a timeout safe.</summary>
    public static DbTicket Insert(string ns, string table, string[] rowsJson, Action<DbResult> done, bool immediate = false) =>
        Call("insert", Write(JsonText.Object(("namespace", ns), ("table", table), ("rows", new JsonText.Raw("[" + string.Join(",", rowsJson) + "]")), ("idempotencyKey", Ids.Next())), immediate), done);

    public static DbTicket Insert(string ns, string table, string rowJson, Action<DbResult> done, bool immediate = false) => Insert(ns, table, [rowJson], done, immediate);

    public static DbTicket Find(string ns, string table, Action<DbResult> done, string? filterJson = null, string[]? fields = null, string? sortJson = null, int? limit = null, int? offset = null)
    {
        var parts = new List<(string, object?)> { ("namespace", ns), ("table", table) };
        if (filterJson is not null) parts.Add(("filter", new JsonText.Raw(filterJson)));
        if (fields is not null) parts.Add(("fields", new JsonText.Raw(JsonText.Array(fields))));
        if (sortJson is not null) parts.Add(("sort", new JsonText.Raw(sortJson)));
        if (limit is not null) parts.Add(("limit", limit));
        if (offset is not null) parts.Add(("offset", offset));
        return Call("find", JsonText.Object([.. parts]), done);
    }

    public static DbTicket Count(string ns, string table, Action<DbResult> done, string? filterJson = null) =>
        Call("count", filterJson is null ? JsonText.Object(("namespace", ns), ("table", table)) : JsonText.Object(("namespace", ns), ("table", table), ("filter", new JsonText.Raw(filterJson))), done);

    /// <summary>Change rows that match <paramref name="filterJson"/>. <paramref name="setJson"/> replaces values, <paramref name="incJson"/> adds to numbers.</summary>
    public static DbTicket Update(string ns, string table, string filterJson, Action<DbResult> done, string? setJson = null, string? incJson = null, bool immediate = false)
    {
        var parts = new List<(string, object?)> { ("namespace", ns), ("table", table), ("filter", new JsonText.Raw(filterJson)) };
        if (setJson is not null) parts.Add(("set", new JsonText.Raw(setJson)));
        if (incJson is not null) parts.Add(("inc", new JsonText.Raw(incJson)));
        return Call("update", Write(JsonText.Object([.. parts]), immediate), done);
    }

    /// <summary>Insert the row, or update the existing row with the same key. <paramref name="keys"/> must be the primary key or a unique index.</summary>
    public static DbTicket Upsert(string ns, string table, string rowJson, string[] keys, Action<DbResult> done, bool immediate = false) =>
        Call("upsert", Write(JsonText.Object(("namespace", ns), ("table", table), ("row", new JsonText.Raw(rowJson)), ("keys", new JsonText.Raw(JsonText.Array(keys))), ("idempotencyKey", Ids.Next())), immediate), done);

    public static DbTicket Delete(string ns, string table, string filterJson, Action<DbResult> done, bool immediate = false) =>
        Call("delete", Write(JsonText.Object(("namespace", ns), ("table", table), ("filter", new JsonText.Raw(filterJson))), immediate), done);

    /// <summary>
    /// Applies the namespace's queued writes now. Only meaningful when the API owner turned on write-behind; otherwise it does nothing.
    /// The result is {"flushed":n,"deadLettered":m,"remaining":k,"halted":false,"enabled":true}.
    /// </summary>
    public static DbTicket Flush(string ns, Action<DbResult> done) => Call("flush", JsonText.Object(("namespace", ns)), done);

    static string Write(string payloadJson, bool immediate) => immediate ? Q.Immediate(payloadJson) : payloadJson;

    public static DbTicket Health(Action<DbResult> done) => Call("health", "{}", done);

    // ---- reading results

    /// <summary>The rows of a successful <c>Find</c> result. Each row maps column names to long, double, bool, string, list, map or null.</summary>
    public static List<Dictionary<string, object?>> Rows(DbResult result)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (result.State != "ok" || !MiniJson.TryParse(result.Json, out var parsed)) return rows;
        if (MiniJson.AsObject(parsed)?.GetValueOrDefault("rows") is List<object?> list)
            foreach (var item in list) if (MiniJson.AsObject(item) is { } row) rows.Add(row);
        return rows;
    }

    /// <summary>
    /// True if the API only queued the write (write-behind mode). A queued write has no generated ids and no affected count yet,
    /// and is visible to reads after the next flush.
    /// </summary>
    public static bool IsQueued(DbResult result) =>
        result.State == "ok" && MiniJson.TryParse(result.Json, out var parsed) && MiniJson.GetBool(MiniJson.AsObject(parsed), "queued") == true;

    /// <summary>True if more rows exist than the limit allowed.</summary>
    public static bool HasMore(DbResult result) =>
        result.State == "ok" && MiniJson.TryParse(result.Json, out var parsed) && MiniJson.GetBool(MiniJson.AsObject(parsed), "hasMore") == true;

    /// <summary>The number from a <c>Count</c> result, or the affected rows of an update/upsert/delete, or the inserted rows of an insert.</summary>
    public static long Number(DbResult result)
    {
        if (result.State != "ok" || !MiniJson.TryParse(result.Json, out var parsed)) return 0;
        var o = MiniJson.AsObject(parsed);
        return MiniJson.GetLong(o, "count") ?? MiniJson.GetLong(o, "affected") ?? MiniJson.GetLong(o, "inserted") ?? 0;
    }

    /// <summary>Generated primary keys of an insert, in row order.</summary>
    public static List<object?> InsertedIds(DbResult result) =>
        result.State == "ok" && MiniJson.TryParse(result.Json, out var parsed) && MiniJson.AsObject(parsed)?.GetValueOrDefault("ids") is List<object?> ids ? ids : [];

    public static string ErrorMessage(DbResult result)
    {
        if (result.State == "ok") return "";
        if (MiniJson.TryParse(result.Json, out var parsed) && MiniJson.AsObject(MiniJson.AsObject(parsed)?.GetValueOrDefault("error")) is { } e && MiniJson.GetString(e, "message") is { } m) return m;
        return result.Code;
    }
}
