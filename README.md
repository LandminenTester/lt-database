# lt_database

A database connector for [Replay.RE BlackICE](https://docs.replay.re) servers. Other **server** bundles call its exports to
declare tables and to read, write, update and delete data in **PostgreSQL, MySQL or MongoDB**. The bundle itself never touches
a database: it talks to the standalone [lt-database-api](https://github.com/LandminenTester/lt-database-api) over HTTPS, signing
every request and checking every answer.

```
your server bundle ──re.Exports──▶ lt_database ──HTTPS, HMAC-signed──▶ lt-database-api ──▶ PostgreSQL | MySQL | MongoDB
```

Why a separate API: the BlackICE sandbox can only open HTTP connections to hosts listed in the bundle's manifest, and the
runtime offers no database driver or writable database file. The API turns "a signed JSON request" into the right SQL or
MongoDB command.

## What you get

* **Server only.** The bundle has no client component and registers no network events, so players have nothing to call.
* A small set of exports (below) plus a helper file that makes them pleasant to use (`import/LtDatabase.Server.cs`).
* **Declarative tables.** Describe tables, columns, types, lengths, defaults and indexes in C#. The first call creates them,
  later calls change exactly what differs (add, alter, rename, drop). One bundle owns one namespace, so the database always
  follows what the bundle declares.
* **Injection-proof queries.** Filters are built with `F.Eq(...)`, `F.And(...)`; values are always bound parameters.
* **Safe by default:** HMAC request and response signing, replay protection, per-namespace allow-list, size limits, a circuit
  breaker, no secret ever sent to a player or stored in the manifest.

## Set up

### 1. Run the API

Follow the [lt-database-api README](https://github.com/LandminenTester/lt-database-api#quick-start): start it with your
database, create a key for this game server and restrict it to the namespaces your bundles use and to the server's address.

### 2. Install the bundle

Build with `dotnet build sv/sv.csproj -c Release` (or through the Server Panel). The result is `dist/config.toml` and
`dist/sv.wasm`.

Edit `config.toml` so the connector may reach your API and nothing else:

```toml
[Networking]
allowed = ["db.example.com"]
```

### 3. Configure the connector

The bundle reads two files from its private `vfs/` folder on the game server (`data/ugc/lt_database/vfs/`). That folder is
not sent to players. Create them while the server is stopped, or use `lt-db-reload` afterwards:

`vfs/database.json`

```json
{
  "url": "https://db.example.com",
  "keyId": "gameserver",
  "secretFile": "database.secret",
  "allowedNamespaces": ["mygame", "scores_demo"],
  "timeoutMs": 15000,
  "maxConcurrent": 8
}
```

`vfs/database.secret`: the key's secret on one line (32+ random characters, the same value the API has for `gameserver`).

**Protect that folder.** The file is plain text on the game server's disk; anyone who can read it can act as that key.
Give it operating-system permissions for the game server account only, and restrict the key in the API to the game server's
address (`AllowedCidrs`) and your namespaces. The connector refuses plain `http://` to remote hosts, secrets under 32
characters and secrets outside the `vfs` folder.

### 4. Use it from your bundle

Link the helper and reference the connector's shared project in your server `.csproj`:

```xml
<ProjectReference Include="../lt_database/sh/sh.csproj" />
<Compile Include="../lt_database/import/LtDatabase.Server.cs" Link="LtDatabase.Server.cs" />
```

A complete example bundle is in [`examples/consumer`](examples/consumer). In short:

```csharp
using LtDatabase;
using LtDatabaseClient;

public static class App
{
    public static void Start()
    {
        var schema = S.Schema(S.Table("players", ["id"],
            [S.Int64("id", autoIncrement: true), S.Text("name", 32, unique: true), S.Int32("score", @default: 0)]));

        Db.Sync("mygame", schema, r =>
        {
            if (r.State != "ok") { Console.WriteLine(Db.ErrorMessage(r)); return; }
            Db.Upsert("mygame", "players", Q.Row(("name", "alice"), ("score", 10)), ["name"], _ => { });
        });
    }
}
```

The connector bundle has to be running when your bundle starts (start `lt_database` first); otherwise the first call
returns `bundle_unavailable`.

## Exports

All exports are synchronous, as the runtime requires. Real work happens in the background, so you get a ticket and ask for the
result. The helper does the polling for you.

| Export | Input | Output |
|---|---|---|
| `Call` | `DbCall(Operation, Json)` | `DbTicket(Id, Accepted, Error)` |
| `TakeResult` | `DbResultQuery(Id)` | `DbResult(Id, State, Status, Code, Json)` |

`Operation` is `health`, `schema.sync`, `schema.describe`, `insert`, `find`, `update`, `upsert`, `delete` or `count`, and
`Json` is the payload documented in the API's [API.md](https://github.com/LandminenTester/lt-database-api/blob/main/docs/API.md).
`State` is `pending`, `ok`, `error` or `missing` (unknown or already collected id). A result is kept for two minutes and handed
out once.

### Helper reference (`LtDatabaseClient.Db`)

| Call | Does |
|---|---|
| `Db.Sync(ns, schemaJson, done, dryRun)` | Create or update the tables of a namespace. With `dryRun` only reports the plan |
| `Db.Describe(ns, done)` | The stored schema |
| `Db.Insert(ns, table, rowJson or rowsJson[], done)` | Insert rows; ids in `Db.InsertedIds(result)`. Sends an idempotency key, so the connector may retry safely |
| `Db.Find(ns, table, done, filterJson, fields, sortJson, limit, offset)` | Query; rows with `Db.Rows(result)`, `Db.HasMore(result)` |
| `Db.Count(ns, table, done, filterJson)` | Count; `Db.Number(result)` |
| `Db.Update(ns, table, filterJson, done, setJson, incJson)` | Change matching rows; affected rows with `Db.Number(result)` |
| `Db.Upsert(ns, table, rowJson, keys, done)` | Insert or update by primary key / unique index |
| `Db.Delete(ns, table, filterJson, done)` | Delete matching rows |
| `Db.Health(done)` | Checks key, signature and database |
| `Db.ErrorMessage(result)` | Readable message of a failed result |

Builders (all return JSON text and escape every value):

* Filters `F`: `Eq Ne Gt Gte Lt Lte In NotIn IsNull StartsWith Contains And Or Not`
* Rows and sorting `Q`: `Q.Row(("col", value), …)`, `Q.Sort(("col", descending), …)`
* Schema `S`: `S.Schema`, `S.Table`, `S.Index`, `S.Int32 Int64 Text LongText Bool Decimal Double Json DateTime Date Uuid Bytes`, or `S.Column(...)` for every option

Column types, defaults, renames (`renamedFrom:`) and what counts as destructive: see the API's
[SCHEMA.md](https://github.com/LandminenTester/lt-database-api/blob/main/docs/SCHEMA.md). The server operator decides whether removed
columns and tables are dropped, quarantined (renamed to `_dropped_*`) or refused.

### Result codes

On failure `result.Code` is one of: `not_configured`, `unknown_operation`, `invalid_request`, `invalid_identifier`,
`namespace_not_allowed`, `payload_too_large`, `busy`, `unavailable` (circuit breaker open), `network`, `unauthorized`,
`rate_limited`, `bad_signature` (the answer was not signed with your secret and was discarded), `bad_response`, `internal`,
or any code from the API (`unknown_table`, `constraint_violation`, `destructive_denied`, …).

## Console commands

| Command | Does |
|---|---|
| `lt-db-status` | Shows the target, key id, allowed namespaces, queue length, circuit state and clock offset (never the secret) |
| `lt-db-health` | Sends an authenticated health check and prints the outcome |
| `lt-db-reload` | Re-reads `vfs/database.json` and the secret |

## How a call is protected

1. The namespace is checked against `allowedNamespaces` before anything leaves the server.
2. The payload is wrapped in an envelope with key id, timestamp, a unique nonce and an HMAC-SHA256 over the exact payload bytes.
3. The answer must carry an HMAC over the status, **your** nonce and its body. Anything else is discarded as `bad_signature`.
4. If the game server's clock is off, the connector asks the API for its time (`GET /v1/health`) and corrects its timestamps.
5. Reads and idempotent operations are retried up to three times on network errors; writes without an idempotency key are not.
   After five failures in a row calls fail fast for 15 seconds.

HMAC, SHA-256 and the JSON handling are small managed implementations in [`sh/Pure`](sh/Pure), because the sandbox does not
document platform cryptography. They are tested against `System.Security.Cryptography`, RFC 4231 and the API's published
test vectors.

## Limits and honest status

* **Shared secret.** Any bundle running on the same game server can call the connector's exports, and the connector cannot tell
  callers apart. Separation between bundles is by namespace and relies on you trusting the bundles you install. Use one key per
  game server.
* **Not run inside a BlackICE server yet.** The bundle compiles to WASM (`dist/sv.wasm`, 0 warnings), its protocol code is covered by
  37 tests on a normal .NET runtime, and the API is covered by cross-engine integration tests. What has *not* been exercised is the
  real sandbox: the behaviour of `re.net.Http` against your API, file access in `vfs/`, and calls between bundles. The code follows
  the SDK 1.1.0 signatures and docs.replay.re; please run `lt-db-health` first and report anything unexpected.
* No transactions across calls, and no foreign keys yet.
* Results are kept in memory; a restart of the bundle loses pending results (the database changes themselves are not lost).

## Development

```bash
dotnet build sv/sv.csproj -c Release      # dist/sv.wasm + dist/config.toml
cd tests/Bundle.Tests && dotnet test      # crypto, JSON, signing and builder tests on a desktop runtime
dotnet build examples/consumer/consumer.csproj -c Release
```

Built from the BlackICE Server Panel template (`guest-csharp-template`, MIT, see NOTICE.md).
