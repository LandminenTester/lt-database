# Usage and examples

All code on this page compiles: see [`examples/consumer/Recipes.cs`](../examples/consumer/Recipes.cs). Add the project reference and
the helper as shown in [Install](INSTALL.md#6-use-it) and put these two lines at the top of your file:

```csharp
using LtDatabase;          // F (filters), Q (rows, sorting), S (schema), MiniJson, JsonText
using LtDatabaseClient;    // Db
```

## How a call works

1. You call a `Db.*` method with a callback. It returns at once.
2. The connector checks the namespace, signs the request and sends it to the API in the background.
3. When the answer is verified, your callback runs with a `DbResult`.

```csharp
Db.Count("mygame", "players", result =>
{
    if (result.State != "ok") { Console.WriteLine(Db.ErrorMessage(result)); return; }
    Console.WriteLine($"{Db.Number(result)} players");
});
```

Never wait for a result in a loop. Do the next step inside the callback (see the chains in the recipes). Callbacks run on the
game server's normal script thread, like any other scheduled work.

### The result

| Member | Meaning |
|---|---|
| `State` | `"ok"`, `"error"` (or `"pending"` / `"missing"`, which the helper never hands to your callback) |
| `Status` | The API's HTTP status; `0` when the API was not reached |
| `Code` | Short error code, empty on success: `unknown_table`, `constraint_violation`, `network`, … ([Reference](REFERENCE.md#error-codes)) |
| `Json` | The result as JSON text on success, `{"error":{"code","message"}}` on failure |

Reading it: `Db.Rows(r)` (rows of a find), `Db.HasMore(r)`, `Db.Number(r)` (count, affected rows or inserted rows),
`Db.InsertedIds(r)`, `Db.IsQueued(r)`, `Db.ErrorMessage(r)`. For anything else parse `r.Json` with `MiniJson.Parse`.

## Declaring tables

```csharp
var schema = S.Schema(
    S.Table("players", ["id"],
    [
        S.Int64("id", autoIncrement: true),
        S.Text("name", 32, unique: true),
        S.Int32("score", @default: 0),
        S.LongText("bio", nullable: true),
        S.Bool("banned", @default: false),
        S.Json("meta"),
        S.DateTime("created", defaultNow: true),
    ],
    [S.Index("by_score", false, "score")]));

Db.Sync("mygame", schema, r => Console.WriteLine(r.State == "ok" ? "schema ready" : Db.ErrorMessage(r)));
```

Call `Db.Sync` once at start-up of your bundle, with the **complete** schema. The first run creates everything; later runs change
only what differs. Running it again with the same schema does nothing (`"status":"unchanged"` in the result).

* **Add** a column: add it to the list. Give it a `@default` or make it `nullable` if the table already has rows.
* **Change** a column (longer text, another default): change it in the list. Shortening a text column below stored data is refused.
* **Rename** a column or table: keep the data by naming the old name: `S.LongText("about", nullable: true, renamedFrom: "bio")`.
* **Remove** a column or table: delete it from the declaration. Depending on the API owner's setting it is dropped, renamed to
  `_dropped_…` (quarantine) or refused. Preview first with `Db.Sync(ns, schema, r => …, dryRun: true)`; the plan lists every
  step with `"destructive": true/false`.
* **Primary keys** cannot change. Create a new table and copy the data.

Column types: `Int32 Int64 Decimal Double Bool Text` (string with a length) `LongText Json DateTime Date Uuid Bytes`; or `S.Column(...)` with
every option. See the API's [schema guide](https://github.com/LandminenTester/lt-database-api/blob/main/docs/SCHEMA.md) for the type
mapping per engine and for exactly what sync does.

## Writing data

```csharp
// insert one or many rows; the ids of generated keys come back
Db.Insert("mygame", "players", Q.Row(("name", "alice"), ("score", 10)), r =>
    Console.WriteLine("new id: " + Db.InsertedIds(r).FirstOrDefault()));

Db.Insert("mygame", "players", [Q.Row(("name", "bob")), Q.Row(("name", "carol"))], r => Console.WriteLine(Db.Number(r) + " rows"));

// change rows: set replaces values, inc adds to numbers
Db.Update("mygame", "players", F.Eq("name", "alice"), r => { },
    setJson: Q.Row(("bio", "hello")), incJson: Q.Row(("score", 5)));

// insert or update by key (the key must be the primary key or a unique index)
Db.Upsert("mygame", "players", Q.Row(("name", "dave"), ("score", 99)), ["name"], r => { });

// delete
Db.Delete("mygame", "players", F.Eq("name", "bob"), r => Console.WriteLine(Db.Number(r) + " deleted"));
```

Values can be `string`, `bool`, `int`, `long`, `double`, `decimal`, `DateTime` (sent as UTC), `DateOnly`, `Guid`, `byte[]` (base64), `null`,
or `new JsonText.Raw("{...}")` for JSON text that already exists (for `Json` columns).

* `Update` and `Delete` **need a filter**. They never touch every row by accident.
* `Insert` and `Upsert` send an idempotency key, so the connector may safely repeat them after a network error. Your callback still
  runs once.
* Unique values that already exist give `constraint_violation` (HTTP 409); missing required columns give `missing_value`.

## Reading data

```csharp
Db.Find("mygame", "players", r =>
{
    foreach (var row in Db.Rows(r))
        Console.WriteLine($"{row["name"]}: {row["score"]}");
},
filterJson: F.And(F.Gte("score", 10), F.Not(F.Eq("banned", true))),
fields: ["name", "score"],
sortJson: Q.Sort(("score", true), ("name", false)),   // true = descending
limit: 20, offset: 0);
```

Row values come back as `long`, `double`, `bool`, `string`, `null`, or (for `Json` columns) a dictionary or list.
Use `Convert.ToInt64(row["score"])` rather than a cast. Datetimes are ISO 8601 strings in UTC, dates `yyyy-MM-dd`, uuids lower-case
strings, bytes base64.

### Filters

```csharp
F.Eq("name", "alice")           F.Ne("state", "done")
F.Gt("score", 10)   F.Gte(...)  F.Lt(...)   F.Lte(...)
F.In("name", "a", "b", "c")     F.NotIn("name", "x", "y")
F.IsNull("bio")                 F.IsNull("bio", false)       // IS NOT NULL
F.StartsWith("name", "al")      F.Contains("name", "ic")     // text columns, case sensitive, wildcards are escaped
F.And(f1, f2, ...)              F.Or(f1, f2, ...)            F.Not(f)
```

Every value is sent as data, never spliced into a query, so user-provided text is safe to use directly. Unknown columns, operators
that do not fit a type and malformed filters fail with `invalid_filter` or `unknown_column`. `Ne` and the comparisons do not match
rows where the column is NULL (as in SQL).

### Paging

`Find` returns at most the API's `MaxRows` (default 1000). Use `limit`/`offset` and `Db.HasMore(r)`:

```csharp
Paging.ForEachPage("mygame", "players", "name", 100,
    rows => { foreach (var row in rows) Console.WriteLine(row["name"]); return true; },
    () => Console.WriteLine("done"));
```

Without `sortJson` the primary key order is used, so paging is stable. Offsets above 1,000,000 are refused.

### Counting

```csharp
Db.Count("mygame", "players", r => Console.WriteLine(Db.Number(r)), F.Gt("score", 100));
```

## Recipes

### A leaderboard

```csharp
Leaderboard.Setup(ok => { /* ... */ });                  // declares the table
Leaderboard.Submit("alice", 4200, error => { });         // counts the game, raises "best" if higher
Leaderboard.Top(10, list => { foreach (var (player, best) in list) Console.WriteLine($"{player} {best}"); });
```

See `Leaderboard` in `Recipes.cs`: an upsert that only sets the key creates the row without touching an existing one, an `inc`
counts the game, and a filtered `set` (`best < score`) raises the best score. Each step is safe to repeat.

### Per-player settings as a JSON document

```csharp
PlayerSettings.Save(playerKey, "{\"x\":120,\"y\":80,\"collapsed\":false}", ok => { });
PlayerSettings.Load(playerKey, doc => Console.WriteLine(doc?["x"]));
```

Use a stable key (a hash of the player's identity, not the slot number). Chat settings can be stored this way.

### Time-limited bans

```csharp
Bans.Ban("alice", "cheating", TimeSpan.FromDays(7), ok => { });
Bans.IsBanned("alice", banned => { /* ... */ });
Bans.Cleanup(removed => Console.WriteLine(removed + " expired"));
```

Compares a `DateTime` column with filters (`F.Gt("until", DateTime.UtcNow)`).

### Handling errors

```csharp
Db.Find("mygame", "players", r =>
{
    if (r.State == "ok") { /* use it */ return; }
    Errors.Report("load players", r);              // logs code, status and message
    if (Errors.IsTemporary(r)) { /* try again later */ }
});
```

Temporary: `network`, `unavailable` (circuit breaker), `busy`, `rate_limited`, `database_timeout`, `conflict_retry`, `write_buffer_full`.
Everything else is a mistake in the request, the schema or the configuration and will not fix itself.

## Write-behind (when the API owner turned it on)

You do not have to change anything. If the owner buffers your namespace, `Insert`, `Update`, `Upsert` and `Delete` answer
`{"queued":true,...}` immediately and the API applies them later, in order, in batches. Consequences:

* A queued insert returns **no ids** and a queued update/delete **no affected count**: `Db.InsertedIds(r)` is empty, `Db.Number(r)` is 0.
  Use keys you generate yourself (a `uuid` column or a natural key) when you must refer to a new row.
* Reads see only what has been applied, unless the owner enabled `FlushBeforeRead`.
* Mistakes that can never succeed are still rejected at once with the same codes; only database decisions (a duplicate unique
  value) can fail later, and are then dead-lettered on the API side.

```csharp
Db.Update("mygame", "stats", F.Eq("name", "kills"), r =>
    Console.WriteLine(Buffered.Describe(r)), incJson: Q.Row(("value", 1)));        // "queued, will be applied at the next flush"

// must be in the database before the player is told: send this one live
Db.Update("mygame", "wallets", F.And(F.Eq("player", p), F.Gte("coins", 50)), r => { },
    incJson: Q.Row(("coins", -50)), immediate: true);

// end of a round: apply everything still queued for the namespace
Db.Flush("mygame", r => Console.WriteLine(r.Json));     // {"flushed":12,"deadLettered":0,"remaining":0,...}
```

`Db.IsQueued(r)` tells a queued answer from a live one. More: the API's
[write-behind guide](https://github.com/LandminenTester/lt-database-api/blob/main/docs/WRITE-BEHIND.md).

## Using it from a console command

```csharp
re.Con.RegisterCommand("top", _ => Leaderboard.Top(5, list =>
{
    foreach (var (player, best) in list) Console.WriteLine($"{player}: {best}");
}));
```

Output of `Console.WriteLine` appears in the server log tagged with your bundle's name.

## Good habits

* Declare the schema **once**, at start-up, and wait for its callback before the first query (a small `ready` flag).
* Do not query in a hot loop. One `Find` per player per session beats one per tick; cache what you read.
* Keep one namespace per bundle, named after it.
* Generate keys yourself when you need them immediately, and make writes repeatable (`Upsert`, `set` instead of `inc` where possible).
* Never put secrets, tokens or full personal data in tables you do not need. Store a hash of the player's identity, not the identity.
* Always handle `State != "ok"`; the database can be unreachable.
