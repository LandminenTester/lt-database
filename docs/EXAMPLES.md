# Player preferences: a complete database example

## What this example teaches

You will create a table, save one record, change two fields, read the record and delete that same sample. These are create, read, update and delete operations, commonly abbreviated **CRUD**.

This is deliberately a **disposable server-side demo**. It runs once when the example bundle starts. It does not listen for player events, ban players, manage a leaderboard or install a gameplay feature. For a real preferences feature, call equivalent save/load methods from your own server-side player event handling and remove the sample cleanup step.

## Requirements and scope

- The connector is installed and `lt-db-health` succeeds.
- The connector and API key both allow `documentation_demo`.
- You are using a development database. The demo creates `preferences` in that namespace and overwrites/removes the row whose `player_id` is `sample_player`. Do not use that row for real data.
- Use a source checkout to build the example, with .NET 10 and the bundle build prerequisites from [Build](BUILD.md).

Run from the `lt-database` source checkout:

```sh
dotnet build examples/consumer/consumer.csproj -c Release
```

The generated file is `examples/consumer/bin/Release/net10.0/wasi-wasm/native/consumer.wasm`. To run it:

1. Create `<server>/data/ugc/preferences_demo/`.
2. Copy that `consumer.wasm` into the new folder.
3. Copy `examples/consumer/config.toml` into the same folder **last**. It names `consumer.wasm` as `server_blob`; do not reuse the connector manifest.
4. In the BlackICE server console run:

```text
start lt_database
start preferences_demo
```

The consumer is separate from the connector: never replace the connector's `sv.wasm` with it. To run the demo again use `restart preferences_demo`.

## Data model

| Column | Type | Purpose |
| --- | --- | --- |
| `player_id` | Text, up to 64 characters, primary key | Identifies one sample/player |
| `music_volume` | 32-bit integer | Example numeric preference; demo changes 25 to 40 |
| `visits` | 32-bit integer | Example counter; demo changes 0 to 1 |

## The five steps

1. **Create the schema.** `Database.Sync` declares the table. Wait for success before using it. Repeating the unchanged schema declaration does not recreate the table.
2. **Save.** `Upsert` uses the unique `player_id` key. For this disposable sample, it resets volume to 25 and visits to 0 on every run.
3. **Change.** `setJson` replaces volume with 40; `incJson` adds 1 to visits. An increment is not idempotent: running the change twice adds twice.
4. **Read.** `Find` selects the sample player. A failed request is logged separately from a successful query that found no row.
5. **Clean up.** `Delete` filters on the fixed sample key and removes only that row. It leaves the table in place.

All writes use `immediate: true` so this demonstration also works when write-behind is enabled. Every callback checks errors and stops the sequence on failure. The five calls are not a transaction; a failure can leave the sample row behind, and you can rerun the demo to reset it.

## Complete compiled source

The following file is compiled by the example project in CI. `Schema`, `Filters`, `Query` and `Database` are readable C# aliases for the library's `S`, `F`, `Q` and `Db` classes.

```csharp
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
```

The example project's entry point is:

```csharp
public static class App
{
    public static void Start() => Examples.PlayerPreferences.Run();
}
```

## Expected result

```text
[preferences demo] volume=40, visits=1
[preferences demo] Removed 1 sample row(s). Demo complete.
```

If setup fails, check namespace permissions, connector startup order and the API key. The log includes the failed step and error code. A successful run ends with no sample row; this is intentional cleanup, not lost data.

## Adapt it to a real bundle

Use your own namespace, table and stable player identifier. Declare the schema at startup, then save on the server event that changes a preference and load when it is needed. Keep a clear distinction between request failure and missing data. Decide whether a queued save is acceptable before telling the player it was persisted. See [usage](USAGE.md) for result handling, filters and write-behind semantics.
