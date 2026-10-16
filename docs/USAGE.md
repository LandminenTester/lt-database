# Use the database from a C# bundle

This guide is for the **server project of your own BlackICE bundle**. `lt_database` is a connector: it sends signed requests to `lt-database-api`, which talks to the database. The panel is a separate, optional resource.

## Before writing code

1. Complete [installation](INSTALL.md), including a successful `lt-db-health` check.
2. Start `lt_database` before your consumer bundle. A **consumer** is simply a bundle that calls the connector.
3. Add your namespace to `allowedNamespaces` in the connector and `AllowedNamespaces` for the API key. The example uses `documentation_demo`.
4. Include the helper and shared types in your server project as shown in the installation guide. Do not put secrets or database calls in a client project.

## What the short names mean

The library exposes three short builder classes. They create JSON text; they do not execute database calls. You can give them readable names with standard C# `using` aliases:

```csharp
using LtDatabase;                         // DbResult and the shared data types
using Database = LtDatabaseClient.Db;     // Sends a request and receives its result
using Schema = LtDatabase.S;              // S means schema: tables, columns, indexes
using Filters = LtDatabase.F;             // F means filters: which rows to match
using Query = LtDatabase.Q;               // Q builds row values and sort instructions
```

These are aliases, not additional packages. `Filters.Eq(...)` calls exactly the same method as `F.Eq(...)`. The API reference uses the original short class names so you can find them in the source.

| Term | Meaning | Example |
| --- | --- | --- |
| Namespace (`ns`) | A named group of tables, allowed by the connector and API key | `documentation_demo` |
| Schema | Definition of tables, columns, types and indexes | `preferences` has a `player_id` text column |
| Primary key | Column(s) uniquely identifying a row | `player_id` |
| Row | One saved record, represented as a JSON object | One player's preferences |
| Filter | A condition selecting rows | `Filters.Eq("player_id", "sample_player")` |
| Callback (`done`) | Function called with the result, possibly later | `result => { ... }` |
| Upsert | Insert a row if its unique key is absent; otherwise update it | Save preferences using `player_id` |
| `setJson` / `set` | Replace a column's value | Volume becomes 40 |
| `incJson` / `inc` | **Increment**: add a number to its current value | Visits 0 + 1 becomes 1 |
| `immediate: true` | Request a live write even when write-behind is enabled | Save before reading it back |

## Follow one complete example

Use [the player-preferences walkthrough](EXAMPLES.md). It contains the complete compiled source, explains each step, and shows the expected output. It is a database demonstration, not an automatically installed player settings feature. You decide where real player events call your own save/load code.

The flow is: **declare schema → save one sample → change it → read it → remove the sample**. Each next operation starts inside the previous successful callback, so no operation assumes the previous one has already finished.

## How asynchronous results work

`Database.Find(...)` returns a ticket immediately. The helper collects the response and invokes your callback. A rejected call can invoke the callback immediately, so do not depend on it always running later. Code after the method call is not ordered after the database operation.

Always check `result.State` **before** calling `Database.Rows` or `Database.Number`. These convenience methods return empty/zero on errors; that must not be mistaken for a successful query with no matches.

```csharp
Database.Find("documentation_demo", "preferences", result =>
{
    if (result.State != "ok")
    {
        Console.WriteLine($"Load failed ({result.Code}): {Database.ErrorMessage(result)}");
        return;
    }
    var rows = Database.Rows(result);
    Console.WriteLine($"Found {rows.Count} preference row(s).");
}, limit: 10);
```

This fragment belongs inside a method of your server bundle, after schema synchronization succeeds. It uses the aliases above and the walkthrough's table.

## Readable filters and sorting

| Method | English meaning | Match |
| --- | --- | --- |
| `Eq` | Equal | Value equals the argument |
| `Ne` | Not equal | Value differs from the argument |
| `Gt` / `Gte` | Greater than / greater than or equal | `visits > 5` / `visits >= 5` |
| `Lt` / `Lte` | Less than / less than or equal | `visits < 5` / `visits <= 5` |
| `In` / `NotIn` | In / not in a list | Value is among the supplied choices |
| `And` / `Or` / `Not` | Combine / invert conditions | Both conditions, either condition, or the opposite |
| `IsNull` | No value | Nullable column is null |
| `StartsWith` / `Contains` | Text search | Prefix or contained text |

```csharp
var frequentPlayers = Filters.And(
    Filters.Gte("visits", 5),
    Filters.Lte("music_volume", 40));
var ordering = Query.Sort(("visits", true), ("player_id", false));
```

`true` means descending (largest first); `false` means ascending. The second sort column makes ordering deterministic when visit counts tie. Pass these strings as `filterJson` and `sortJson` to `Database.Find`. Set `limit` to bound the result; use `offset` and `Database.HasMore` to request later pages. Offset pagination can shift if other requests change rows during the scan.

## Live and queued writes

Live writes are the default. With the API's optional write-behind mode, `State == "ok"` can instead mean **accepted into the journal**, not already stored in the database. Check `Database.IsQueued(result)`. A queued receipt has no final affected-row count or generated IDs, and reads do not include pending changes.

For a save that must be visible before a read, pass `immediate: true` as in the walkthrough. For a scheduled batch, keep normal writes and call `Database.Flush` when appropriate. A successful flush request still needs its `halted`, `remaining` and `deadLettered` fields checked; do not treat the HTTP success alone as proof that every queued change was applied. See [write-behind](https://github.com/LandminenTester/lt-database-api/blob/main/docs/WRITE-BEHIND.md).

## Retry carefully

`inc` is not safe to repeat blindly: retrying `+1` as a new operation can count twice. The connector retries insert/upsert using the key generated for that call; calling the helper again creates a **new** key. For a stable application-level operation identity, use `Database.Call` with an explicit `idempotencyKey` and follow the HTTP reference. A sequence of separate calls is not a transaction and can stop partway through.

Use authoritative player identifiers in real code. Do not accept a namespace, table or raw JSON filter directly from untrusted player input. Keep secrets on the server. See [reference](REFERENCE.md) for every method and error code, [configuration](CONFIGURATION.md) for settings, and [operations](OPERATIONS.md) for diagnosis.
