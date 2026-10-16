# lt_database

A database connector for [Replay.RE BlackICE](https://docs.replay.re) servers. Your server bundles call its exports to declare
tables and to read, write, update and delete data in **PostgreSQL, MySQL/MariaDB or MongoDB**. The bundle itself never touches a
database: it talks to the standalone [lt-database-api](https://github.com/LandminenTester/lt-database-api) over HTTPS, signing
every request and verifying every answer.

```
your server bundle ──re.Exports──▶ lt_database (this bundle) ──HTTPS, HMAC-signed──▶ lt-database-api ──▶ PostgreSQL | MySQL | MongoDB
```

The BlackICE sandbox can only open HTTP connections to hosts listed in a bundle's manifest and has no database driver or
writable database file. This bundle plus the API is the bridge.

Install the bundle as `lt_database`. Its public C# contracts use `LtDatabase`, and its consumer helper uses
`LtDatabaseClient`. The console commands are `lt-db-status`, `lt-db-health` and `lt-db-reload`.

## What you get

* **Server only.** No client component and no network events: players have nothing to call.
* **Declarative tables.** Describe tables, columns, types, lengths, defaults and indexes in C#. The first call creates them, later
  calls change exactly what differs (add, alter, rename, drop). A bundle owns a *namespace*, and the database follows what the
  bundle declares.
* **Injection-proof queries.** Filters are built with `F.Eq(...)`, `F.And(...)`; every value reaches the database as a bound parameter.
* **Safe by default.** HMAC request and response signing, replay protection, a namespace allow-list, size limits, a circuit
  breaker, and a secret that never leaves the server.
* **Optional write-behind.** If the API owner turns it on, writes are queued and applied later in batches. The helper handles it
  (`Db.IsQueued`, `Db.Flush`, `immediate: true`).
* One helper file and a few builders, documented with examples.

## Five-minute start

1. Run [lt-database-api](https://github.com/LandminenTester/lt-database-api) with your database and create a key for your server.
2. Download `lt_database-<version>.zip` from the [releases](../../releases) (or [build it](docs/BUILD.md)) and unpack it as
   `data/ugc/lt_database/` on the game server. Set your API host in its `config.toml` under `[Networking]`.
3. Create `vfs/database.json` and `vfs/database.secret` next to it, and start the bundle: [Install](docs/INSTALL.md).
4. Use it from your own server bundle:

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
            Db.Upsert("mygame", "players", Q.Row(("name", "alice"), ("score", 10)), ["name"], _ =>
                Db.Find("mygame", "players", top =>
                {
                    foreach (var row in Db.Rows(top)) Console.WriteLine($"{row["name"]}: {row["score"]}");
                }, sortJson: Q.Sort(("score", true)), limit: 10));
        });
    }
}
```

## Documentation

| Guide | What it covers |
|---|---|
| [Install](docs/INSTALL.md) | Prerequisites, installing the bundle, the two `vfs` files, starting it, checking it, a local Docker test setup |
| [Configuration](docs/CONFIGURATION.md) | Every setting of `database.json`, the manifest, namespaces, secrets, several environments |
| [Usage and examples](docs/USAGE.md) | Concepts, every helper call, filters, paging, counters, per-player data, error handling, write-behind, recipes |
| [Reference](docs/REFERENCE.md) | Exports and their types, operations and payloads, result states and codes, builders |
| [Operations](docs/OPERATIONS.md) | Console commands, rotating the secret, upgrades, backups, monitoring, performance, troubleshooting |
| [Security](docs/SECURITY.md) | Trust model, what is protected how, limits of the design |
| [Build and contribute](docs/BUILD.md) | Toolchain, building the WASM bundle, tests, releases |

Examples that compile: [`examples/consumer`](examples/consumer) (a small consumer bundle) and
[`examples/smoke`](examples/smoke) (`db-smoke`, a 38-step test of every function that you can run on your own server).

## Status

* Compiles to WASM with 0 warnings; the protocol code (crypto, JSON, signing, builders) has 39 tests on a normal .NET runtime,
  checked against `System.Security.Cryptography`, RFC 4231 and the API's published test vectors.
* **Tested in a real BlackICE server** against PostgreSQL 17, MySQL 8.4 and MongoDB 8 through the API: all 38 steps of
  `db-smoke` pass on each, and a real consumer stores per-player settings through it.
* Not tested: several game servers at once, TLS between bundle and API (plain loopback HTTP was used), key rotation in a running
  server, MariaDB, write-behind from inside a game server (the API side is tested, the helper side by unit tests).

## License

MIT, see [LICENSE](LICENSE). Built from the BlackICE Server Panel template, see [NOTICE.md](NOTICE.md).

For a beginner-friendly introduction, read [C# usage](docs/USAGE.md) and the [complete preferences walkthrough](docs/EXAMPLES.md). Public English/German guides: [BlackICE documentation](https://blackice.landminentester.de/).
