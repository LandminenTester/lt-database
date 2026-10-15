using LtDatabase;
using LtDatabaseClient;

// Example: a server bundle that keeps player scores in the database through lt_database.
// Its namespace "scores_demo" must be listed in the connector's vfs/database.json (allowedNamespaces)
// and in the API key's AllowedNamespaces.
public static class App
{
    const string Ns = "scores_demo";

    public static void Start()
    {
        // 1. Declare the tables once. This creates them, and on later starts changes only what differs.
        var schema = S.Schema(
            S.Table("players", ["id"],
                [S.Int64("id", autoIncrement: true), S.Text("name", 32, unique: true), S.Int32("score", @default: 0), S.DateTime("created", defaultNow: true)],
                [S.Index("by_score", false, "score")]));

        Db.Sync(Ns, schema, result =>
        {
            if (result.State != "ok") { Console.WriteLine("[scores] schema failed: " + Db.ErrorMessage(result)); return; }
            Console.WriteLine("[scores] schema ready: " + result.Json);
            Demo();
        });
    }

    static void Demo()
    {
        // 2. Save a player (an upsert, so running it twice does not fail).
        Db.Upsert(Ns, "players", Q.Row(("name", "alice"), ("score", 10)), ["name"], saved =>
        {
            if (saved.State != "ok") { Console.WriteLine("[scores] save failed: " + Db.ErrorMessage(saved)); return; }

            // 3. Add points, then read the top players.
            Db.Update(Ns, "players", F.Eq("name", "alice"), _ =>
                Db.Find(Ns, "players", top =>
                {
                    foreach (var row in Db.Rows(top)) Console.WriteLine($"[scores] {row["name"]}: {row["score"]}");
                }, sortJson: Q.Sort(("score", true)), limit: 10),
                incJson: Q.Row(("score", 5)));
        });
    }
}
