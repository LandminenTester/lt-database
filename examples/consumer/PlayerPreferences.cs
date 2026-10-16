using LtDatabase;
using Database = LtDatabaseClient.Db;
using Schema = LtDatabase.S;
using Filters = LtDatabase.F;
using Query = LtDatabase.Q;

namespace Examples;

/// <summary>A disposable server-side CRUD demonstration, not a player event integration.</summary>
public static class PlayerPreferences
{
    // Allow this namespace in both the connector and the API key before running.
    const string Namespace = "documentation_demo";
    const string Table = "preferences";
    const string Player = "sample_player";

    public static void Run()
    {
        var schema = Schema.Schema(Schema.Table(Table, ["player_id"],
            [Schema.Text("player_id", 64), Schema.Int32("music_volume"), Schema.Int32("visits")]));

        Database.Sync(Namespace, schema, result =>
        {
            if (!Succeeded("Create schema", result)) return;
            Save();
        });
    }

    static void Save()
    {
        var row = Query.Row(("player_id", Player), ("music_volume", 25), ("visits", 0));
        Database.Upsert(Namespace, Table, row, ["player_id"], result =>
        {
            if (!Succeeded("Save preferences", result)) return;
            Change();
        }, immediate: true);
    }

    static void Change()
    {
        Database.Update(Namespace, Table, Filters.Eq("player_id", Player), result =>
        {
            if (!Succeeded("Change preferences", result)) return;
            if (Database.Number(result) != 1)
            {
                Console.WriteLine("[preferences demo] Expected exactly one changed row; stopping.");
                return;
            }
            Read();
        }, setJson: Query.Row(("music_volume", 40)),
           incJson: Query.Row(("visits", 1)), immediate: true);
    }

    static void Read()
    {
        Database.Find(Namespace, Table, result =>
        {
            if (!Succeeded("Read preferences", result)) return;
            var rows = Database.Rows(result);
            if (rows.Count != 1)
            {
                Console.WriteLine("[preferences demo] Sample row not found; stopping.");
                return;
            }
            var row = rows[0];
            Console.WriteLine($"[preferences demo] volume={row["music_volume"]}, visits={row["visits"]}");
            RemoveSample();
        }, filterJson: Filters.Eq("player_id", Player), limit: 1);
    }

    static void RemoveSample()
    {
        Database.Delete(Namespace, Table, Filters.Eq("player_id", Player), result =>
        {
            if (!Succeeded("Remove sample row", result)) return;
            Console.WriteLine($"[preferences demo] Removed {Database.Number(result)} sample row(s). Demo complete.");
        }, immediate: true);
    }

    static bool Succeeded(string step, DbResult result)
    {
        if (result.State == "ok" && !Database.IsQueued(result)) return true;
        var message = Database.IsQueued(result)
            ? "Write is queued rather than applied; stopping this sequential demonstration."
            : $"{result.Code}: {Database.ErrorMessage(result)}";
        Console.WriteLine($"[preferences demo] {step} failed: {message}");
        return false;
    }
}
