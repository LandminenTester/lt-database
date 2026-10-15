using LtDatabase;
using LtDatabaseClient;

// Recipes used in docs/USAGE.md. They are compiled with the example bundle, so they stay correct when the helper changes.
// None of this runs unless you call it from App.Start().
namespace Recipes;

/// <summary>A leaderboard: one row per player, best score kept, top list on demand.</summary>
public static class Leaderboard
{
    const string Ns = "scores_demo";

    public static string Schema() => S.Schema(S.Table("scores", ["player"],
        [S.Text("player", 64), S.Int32("best", @default: 0), S.Int32("games", @default: 0), S.DateTime("updated", defaultNow: true)],
        [S.Index("by_best", false, "best")]));

    public static void Setup(Action<bool> ready) =>
        Db.Sync(Ns, Schema(), r => ready(r.State == "ok"));

    /// <summary>Counts a game and raises the best score if this one is better.</summary>
    public static void Submit(string player, int score, Action<string> done)
    {
        // 1. make sure the row exists (an upsert that only sets the key leaves existing values alone)
        Db.Upsert(Ns, "scores", Q.Row(("player", player)), ["player"], created =>
        {
            if (created.State != "ok") { done(Db.ErrorMessage(created)); return; }
            // 2. count the game
            Db.Update(Ns, "scores", F.Eq("player", player), counted =>
            {
                // 3. raise best only when this score is higher; the filter does the comparison, so it is safe to call twice
                Db.Update(Ns, "scores", F.And(F.Eq("player", player), F.Lt("best", score)), _ => done(""), setJson: Q.Row(("best", score)));
            }, incJson: Q.Row(("games", 1)));
        });
    }

    public static void Top(int count, Action<List<(string Player, long Best)>> done) =>
        Db.Find(Ns, "scores", r =>
        {
            var list = new List<(string, long)>();
            foreach (var row in Db.Rows(r)) list.Add(((string)row["player"]!, Convert.ToInt64(row["best"])));
            done(list);
        }, sortJson: Q.Sort(("best", true), ("player", false)), limit: count);
}

/// <summary>Per-player settings stored as a JSON document in one column.</summary>
public static class PlayerSettings
{
    const string Ns = "settings_demo";

    public static string Schema() => S.Schema(S.Table("settings", ["player"], [S.Text("player", 64), S.Json("data", nullable: true)]));

    public static void Save(string player, string jsonDocument, Action<bool> done) =>
        Db.Upsert(Ns, "settings", Q.Row(("player", player), ("data", new JsonText.Raw(jsonDocument))), ["player"], r => done(r.State == "ok"));

    /// <summary>Returns the stored document, or null if the player has none yet.</summary>
    public static void Load(string player, Action<Dictionary<string, object?>?> done) =>
        Db.Find(Ns, "settings", r =>
        {
            var rows = Db.Rows(r);
            done(rows.Count == 0 ? null : MiniJson.AsObject(rows[0]["data"]));
        }, filterJson: F.Eq("player", player), limit: 1);
}

/// <summary>Time-limited bans: filter on a datetime column.</summary>
public static class Bans
{
    const string Ns = "bans_demo";

    public static string Schema() => S.Schema(S.Table("bans", ["id"],
        [S.Int64("id", autoIncrement: true), S.Text("player", 64), S.Text("reason", 200, nullable: true), S.DateTime("until")],
        [S.Index("by_player", false, "player")]));

    public static void Ban(string player, string reason, TimeSpan duration, Action<bool> done) =>
        Db.Insert(Ns, "bans", Q.Row(("player", player), ("reason", reason), ("until", DateTime.UtcNow + duration)), r => done(r.State == "ok"));

    /// <summary>A player is banned while a row with a future "until" exists.</summary>
    public static void IsBanned(string player, Action<bool> done) =>
        Db.Count(Ns, "bans", r => done(r.State == "ok" && Db.Number(r) > 0), F.And(F.Eq("player", player), F.Gt("until", DateTime.UtcNow)));

    public static void Cleanup(Action<long> done) =>
        Db.Delete(Ns, "bans", F.Lt("until", DateTime.UtcNow), r => done(Db.Number(r)));
}

/// <summary>Reads a whole table page by page.</summary>
public static class Paging
{
    public static void ForEachPage(string ns, string table, string sortColumn, int pageSize, Func<List<Dictionary<string, object?>>, bool> handle, Action done, int offset = 0) =>
        Db.Find(ns, table, r =>
        {
            if (r.State != "ok") { done(); return; }
            var keepGoing = handle(Db.Rows(r));
            if (keepGoing && Db.HasMore(r)) ForEachPage(ns, table, sortColumn, pageSize, handle, done, offset + pageSize);
            else done();
        }, sortJson: Q.Sort((sortColumn, false)), limit: pageSize, offset: offset);
}

/// <summary>What to do with a failed result.</summary>
public static class Errors
{
    /// <summary>True for failures that are worth repeating later; false for mistakes in the request or in the configuration.</summary>
    public static bool IsTemporary(DbResult r) => r.State == "error" && r.Code is "network" or "unavailable" or "busy" or "rate_limited" or "database_timeout" or "conflict_retry" or "write_buffer_full";

    public static void Report(string what, DbResult r)
    {
        if (r.State == "ok") return;
        Console.WriteLine($"[{what}] failed ({r.Code}, http {r.Status}): {Db.ErrorMessage(r)}{(IsTemporary(r) ? " - will be worth retrying" : "")}");
    }
}

/// <summary>Working with the API's optional write-behind mode.</summary>
public static class Buffered
{
    /// <summary>Counts an event. May be queued by the API; the result is not needed.</summary>
    public static void CountEvent(string ns, string table, string key) =>
        Db.Update(ns, table, F.Eq("name", key), r => { if (r.State != "ok") Errors.Report("count", r); }, incJson: Q.Row(("value", 1)));

    /// <summary>A write that must be in the database before the player is told about it: send it live even in a buffered namespace.</summary>
    public static void Purchase(string ns, string player, int price, Action<bool> done) =>
        Db.Update(ns, "wallets", F.And(F.Eq("player", player), F.Gte("coins", price)), r => done(r.State == "ok" && Db.Number(r) == 1),
            incJson: Q.Row(("coins", -price)), immediate: true);

    /// <summary>At the end of a round, apply everything that is still queued, then continue.</summary>
    public static void EndOfRound(string ns, Action next) =>
        Db.Flush(ns, r => { if (r.State != "ok") Errors.Report("flush", r); next(); });

    /// <summary>Tells a live result from a queued one. A queued insert has no ids yet.</summary>
    public static string Describe(DbResult r) =>
        Db.IsQueued(r) ? "queued, will be applied at the next flush" : $"applied ({Db.Number(r)} row(s))";
}
