# Choosing a backend and settings

lt_database can reach a database in two ways: through [lt-database-api](https://github.com/LandminenTester/lt-database-api) over HTTPS
(`"backend": "http"`, the default) or directly through the host's `re.sql` provider (`"backend": "sql"`, since 0.3.0, see
[SQL backend](SQL-BACKEND.md)). Consumers see the same exports, requests, results and error codes either way, so this is an operations
decision, not a code decision. This page helps you take it.

## Quick decision

| Your situation | Use | Why |
|---|---|---|
| One game server, you want as little to run as possible | `sql` + SQLite | No extra service, no key, no network rule; the database is a file under `data/persistent/<bundle>/` |
| One game server, you want a real database server, backups, other tools reading the data | `sql` + PostgreSQL | Same code path, but the data lives in PostgreSQL |
| Several game servers must share the same data (economy, characters, bans) | `http` (API in front of one database), or `sql` + one shared PostgreSQL | The API gives per-server keys and namespaces; with `sql` every server needs database credentials |
| You use MySQL, MariaDB or MongoDB | `http` | `re.sql` supports PostgreSQL and SQLite only |
| Many small writes per tick, you want them batched | `http` with write-behind | Write-behind exists only in the API |
| An effect must happen exactly once (a purchase, a payout, a stock change) | either, with `mutation.execute` | Both engines record the receipt in the same transaction as the change |
| Local development, CI, a test server | `sql` + SQLite with `destructiveMode: "apply"` | Fast to set up and throw away |
| The game server host is not trusted with database credentials | `http` | The bundle holds only an API key limited to its namespaces and to the server's address |
| Moving between the two on the same PostgreSQL database | either, one at a time | Schema hash and receipt fingerprint are identical; never run both against the same namespaces at once |

## The backends side by side

| | `http` + lt-database-api | `sql` (`re.sql`) |
|---|---|---|
| Moving parts | game server, API service, database | game server, database (SQLite: only the game server) |
| Engines | PostgreSQL, MySQL/MariaDB, MongoDB | PostgreSQL, SQLite |
| Where secrets live | API key secret in the bundle's `vfs/`, database credentials only at the API | database credentials in the bundle's `config.toml` (`${VAR}` from the environment); the bundle code never sees them |
| Protection on the wire | HMAC-signed requests and answers, replay protection, per-key rate limit | none needed on SQLite; on PostgreSQL protect the connection yourself (TLS in `conninfo`) |
| Access control | per-key permissions and namespaces at the API, plus `allowedNamespaces` in the bundle | `allowedNamespaces` plus the privileges of the database account behind the alias |
| Write-behind | optional | not available, every write is immediate |
| Parallelism | up to `maxConcurrent` calls at once | one call at a time per bundle, see [Concurrency](SQL-BACKEND.md#concurrency) |
| Latency | one HTTPS round trip plus database time | database time only |
| Exactly-once receipts | relational engines of the API (not MongoDB) | PostgreSQL, SQLite |

## Use cases in detail

### 1. A single server with the least effort: SQLite

`vfs/database.json`:

```json
{ "backend": "sql", "sqlAlias": "game", "destructiveMode": "quarantine", "allowedNamespaces": ["mygame"] }
```

`config.toml` of the bundle:

```toml
[Database.game]
type = "sqlite"
name = "game.db"
```

Good for: roleplay or minigame servers with a few hundred players, preferences, statistics, small inventories.
Pros: nothing else to install, back up by copying the file, no network. Cons: SQLite has a single writer (a second writer may be answered
with `conflict_retry`, retry it); only this server can use the file; no tool outside the host reads it comfortably.
Keep `destructiveMode` at `quarantine` or `deny` once real data exists.

### 2. A single server on PostgreSQL

```toml
[Database.game]
type = "postgres"
conninfo = "host=${DB_HOST} port=5432 dbname=game user=ugc password=${DB_PASSWORD}"
```

Create a dedicated database account. It needs `CREATE` once (bookkeeping tables) and DDL rights in its schema if you use `schema.sync`.
Good for: servers that already run PostgreSQL, data that other tools (dashboards, web panels, backups) should read, a later move to
several servers. Add `sslmode=require` to `conninfo` when the database is not on the same machine.

### 3. Several game servers sharing data

Preferred: run lt-database-api once in front of the database and give every game server its own key, limited to the namespaces and the
address that server needs (`"backend": "http"`). A compromised server can then only touch its own namespaces, and you can rotate or revoke one key.

Possible: point every server's `sql` alias at the same PostgreSQL database. The database arbitrates between the servers (advisory lock for
`schema.sync`, the unique receipt reservation for `mutation.execute`), but each server holds database credentials and there is no
per-server permission layer. Use this only between servers you trust equally, and let exactly one server (or a deploy step) run
`schema.sync` with `destructiveMode` above `deny`.

### 4. MySQL, MariaDB or MongoDB

Stay on `http`. The API translates the same schema and query language to these engines. Atomic mutation receipts are not available for MongoDB.

### 5. Bursts of small writes

A tick loop that writes many tiny rows (positions, counters) is a poor fit for any backend: every call is a database round trip, and
with `sql` the calls queue behind each other. Options, best first: keep the state in memory and write on events or at intervals; batch
rows into one `insert` (up to 1000 rows); use the `http` backend with write-behind so the API journals and applies the writes later
(`durability` per call, `immediate: true` when the answer must be a real result).

### 6. Exactly-once effects: mutation receipts

When a retry after a lost answer must not apply an effect twice (a shop purchase, a payout), use `mutation.execute` with a stable
`effectId`. The change and its permanent receipt commit in one transaction; a repeated call returns the stored receipt and a lost commit
answer is resolved by `mutation.receipt`. The command may touch **at most one row**. lt-core's recovery code relies on this contract.
Use `idempotencyKey` for ordinary writes where losing the cache on a restart is acceptable (it lives in memory for ten minutes).

### 7. Development, tests and CI

`sql` + SQLite with `"destructiveMode": "apply"` gives a throwaway database in seconds; delete the file to reset. Run the smoke bundles
([`examples/smoke`](../examples/smoke/README.md)) against your target engine before going live: `db-smoke` for the operations and
`db-smoke-mutation` for receipts.

### 8. Switching later

Because the schema hash and the receipt fingerprint are byte-identical, an existing PostgreSQL database created through the API can be
taken over by the `sql` backend and back. Stop the other side first, change `backend` in `database.json`, run `lt-db-reload`, check
`lt-db-status`, and restart the consumer bundles.

## Settings and what they change

| Setting (`vfs/database.json`) | Values | Effect, and when to change it |
|---|---|---|
| `backend` | `http`, `sql` | Selects the path. A file without it stays `http`. An invalid combination answers every call with `not_configured` and never falls back. |
| `sqlAlias` | name from `config.toml` | Which `[Database.<alias>]` the connector uses. One alias per bundle installation. |
| `sqlDialect` | `auto`, `postgres`, `sqlite` | `auto` asks the engine on the first call. Fix it explicitly if you want the first call to need no detection or a proxy hides the engine. |
| `destructiveMode` | `deny`, `quarantine`, `apply` | What `schema.sync` does with tables and columns you removed from the declaration. `deny` (default): refuse and show the plan, the safe choice for production. `quarantine`: rename to `_dropped_<time>_<name>` so data can be recovered, good while a schema is still moving. `apply`: drop for real, for tests and throwaway databases. |
| `allowedNamespaces` | list or `"*"` | Which namespaces the connector sends. Give each consumer family its own namespace and list only those; `"*"` is for development. |
| `timeoutMs` | 1000 to 120000 | `http`: time one request may take. `sql`: SQL command timeout, rounded up to whole seconds, capped at 30. Raise it for large `schema.sync` rebuilds, lower it to fail fast. |
| `maxConcurrent` | 1 to 64 | `http`: calls in flight (raise for many independent callers and a fast API). `sql`: no effect above 1, calls run one at a time. |
| `url`, `keyId`, `secretFile`, `allowInsecureHttp` | | `http` only; ignored with `sql`. Plain `http://` to a remote host only inside a network you fully trust. |

Per call, independent of the backend:

| Option | Effect |
|---|---|
| `idempotencyKey` | The same key with the same payload returns the first result (10 minutes, in memory); another payload gives `422 idempotency_conflict`. |
| `dryRun` (`schema.sync`) | Shows the plan without applying it. Run it before every production deployment. |
| `durability` / `immediate` | Write-behind only (`http`). Ignored with `sql`, where every write is immediate. |
| `limit`, `offset`, `fields`, `sort` | Keep `limit` small and filter on indexed columns; with `sql` a slow query delays the calls queued behind it. |

## Common mistakes

* Running the `sql` backend and an API against the same database and namespaces at the same time: they share the bookkeeping tables.
* Leaving `destructiveMode` at `apply` after development.
* Expecting write-behind or parallel calls with `sql`.
* Using one namespace (or `"*"`) for everything: you lose the ability to separate bundles.
* Treating `idempotencyKey` as durable. After a restart the cache is gone; use mutation receipts where it matters.
* Putting database passwords into `database.json` or the repository: use `${VAR}` in `conninfo` and the environment of the server.
