namespace LtDatabase;

// Types that cross the bundle boundary (re.Exports). They are MessagePack records, so everything else is JSON text:
// requests and results travel as strings, which keeps the exports stable while the API grows.

/// <summary>One database call. Operation is one of: health, schema.sync, schema.describe, insert, find, update, upsert, delete, count.</summary>
[Msgpack.Gen]
public partial record DbCall(string Operation, string Json);

/// <summary>Answer to <see cref="DbCall"/>. Accepted=false means nothing was queued and Error says why.</summary>
[Msgpack.Gen]
public partial record DbTicket(string Id, bool Accepted, string Error);

[Msgpack.Gen]
public partial record DbResultQuery(string Id);

/// <summary>
/// State is "pending" (ask again), "ok", "error" or "missing" (unknown or already collected id).
/// Status is the API's HTTP status (0 when the API was not reached). Json is the result on "ok",
/// and {"error":{"code","message"}} on "error". Code is the short error code, empty on success.
/// </summary>
[Msgpack.Gen]
public partial record DbResult(string Id, string State, int Status, string Code, string Json);
