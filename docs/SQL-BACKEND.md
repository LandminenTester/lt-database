# The SQL backend (`re.sql`)

Since 0.3.0 the connector can talk to a database in two ways. Both give consumers **exactly the same exports, requests, results and
error codes**, so a consumer bundle (the helper `LtDatabaseClient.Db`, or lt-core's `LtDatabase` storage adapter) never needs to know
which one is active.

| | `http` (default) | `sql` |
|---|---|---|
| Path | bundle → HTTPS → [lt-database-api](https://github.com/LandminenTester/lt-database-api) → database | bundle → host's `re.sql` provider → database |
| Needs | a running API, a key, an allowed host in `[Networking]` | a BlackICE host with SDK **1.3.0** or newer and a `[Database.<alias>]` entry in the bundle's `config.toml` |
| Engines | PostgreSQL, MySQL/MariaDB, MongoDB | PostgreSQL, SQLite |
| Authentication | HMAC-signed requests and answers | none on the wire; access is limited by `allowedNamespaces` and by the database account behind the alias |
| Write-behind | optional, configured on the API | not available; every write is immediate |
| Typical use | shared database, several game servers, MySQL or MongoDB | one game server, no extra service to run, local development, tests |

Pick **one** backend per bundle installation. Mixed operation per namespace is not supported.

## Selecting the backend

`vfs/database.json`:

```json
{
  "backend": "sql",
  "sqlAlias": "players",
  "sqlDialect": "auto",
  "destructiveMode": "deny",
  "allowedNamespaces": ["mygame"],
  "timeoutMs": 10000,
  "maxConcurrent": 8
}
```

| Field | Default | Meaning |
|---|---|---|
| `backend` | `"http"` | `"http"` or `"sql"`. A file without this field keeps working as before. |
| `sqlAlias` | required for `sql` | Name of a `[Database.<alias>]` entry of the bundle's `config.toml` (letters, digits, `_`, `-`, at most 64 characters). Host, user, password or file path are declared there and are never visible to the bundle code. |
| `sqlDialect` | `"auto"` | `"auto"` (asks the engine: `sqlite_version()` answers on SQLite, anything else is treated as PostgreSQL), `"postgres"` or `"sqlite"`. Set it explicitly if detection is not wanted. |
| `destructiveMode` | `"deny"` | What `schema.sync` may do with tables and columns that disappeared from the declaration: `"deny"`, `"quarantine"` or `"apply"`, see [Schema sync](#schema-sync). |
| `allowedNamespaces`, `timeoutMs`, `maxConcurrent` | as for `http` | Same meaning. `timeoutMs` becomes the SQL command timeout, rounded up to whole seconds and capped at 30 (the limit of `re.sql`). |

With `"backend": "sql"` the fields `url`, `keyId`, `secretFile` and `allowInsecureHttp` are ignored, and `[Networking] allowed` is not
needed. Any invalid combination keeps the bundle loaded but answers every call with `not_configured` (see `lt-db-status`), so a wrong
file can never fall back to another backend or to a half-working one.

The connection itself belongs into `config.toml`. Its exact keys are defined by the Replay.RE documentation for `re.sql` (SDK 1.3.0);
this bundle only needs the alias name and does not read the entry. A connection test: `lt-db-health` (the answer carries
`"database": true` when `SELECT 1` worked).

`lt-db-reload` re-reads `vfs/database.json`, drops all caches and re-detects the dialect. A changed `[Database.<alias>]` entry needs the
bundle to be restarted like any other `config.toml` change.

## What is identical to the API

Everything a consumer can observe:

* Operations: `health`, `schema.sync`, `schema.describe`, `insert`, `find`, `count`, `update`, `upsert`, `delete`, `flush`,
  `mutation.execute`, `mutation.receipt`.
* Request fields, defaults and limits: filter grammar (8 levels, 128 conditions, 1000 list items, 1024 characters for text operators),
  `fields`, `sort`, `limit` (clamped to 1 to 1000), `offset` (0 to 1,000,000), `set` and `inc`, `all`, upsert `keys` that must be the
  primary key or a unique index, at most 1000 rows per insert, `dryRun`, `idempotencyKey`.
* Result bodies: `{"rows":[…],"hasMore":…}`, `{"inserted":n,"ids":[…]}`, `{"affected":n}`, `{"count":n}`, the schema sync result with its plan,
  `schema.describe`, the receipt object.
* Error codes and HTTP-like statuses (`400 invalid_filter`, `404 unknown_table`, `409 constraint_violation`, `422 idempotency_conflict`, …).
  Driver messages are never returned (they can contain row values); only a stable code and a fixed text.
* Identifier rules, the physical names (`<namespace>__<table>`, `ix_<table>_<index>`), the bookkeeping tables (`_ldb_schema`,
  `_ldb_mutation_receipts`) and the **schema hash**. The hash, the stored definition and the receipt fingerprint are byte-identical with
  those of lt-database-api; unit tests compare them with vectors produced by the API's own code. In practice an existing PostgreSQL
  database that was created through the API can be taken over by the SQL backend, and back.
* Idempotency: the same `idempotencyKey` with the same payload returns the first result, with another payload `422 idempotency_conflict`.
  The cache lives in the bundle's memory (10 minutes, 5000 entries) and is cleared by `lt-db-reload`, a restart or a crash. The API keeps
  its cache in the API process in the same way; neither is a durable guarantee, which is why the connector never retries a write
  automatically. Use [mutation receipts](#mutation-receipts) where exactly-once matters.

## What is different (and why)

| Topic | Behavior with `sql` |
|---|---|
| Authentication, signing, replay protection, rate limits | Not present: there is no network hop to protect. Isolation rests on `allowedNamespaces` and on the **database account behind the alias**, which should only be allowed to touch what the game needs (including `CREATE`/`ALTER` for schema sync and, on PostgreSQL, `pg_advisory_xact_lock`). |
| Per-key permissions | The API can forbid unfiltered writes per key. In `sql` mode `"all": true` is always allowed, as every bundle that can call the connector is trusted to the same degree. |
| Write-behind | Not implemented. `durability` is ignored, `flush` answers `{"flushed":0,"deadLettered":0,"remaining":0,"halted":false,"enabled":false}` and `Db.IsQueued` is always `false`. |
| `destructiveMode` default | `deny` here, because there is no separate operator who configured the API. Set `quarantine` or `apply` deliberately. |
| `health` | `{"status":"ok","provider":"postgres"|"sqlite","database":true|false,"writeBuffer":{…disabled…}}`; `provider` is `unknown` before the first connection when the dialect is `auto`. |
| Engines | PostgreSQL and SQLite only. MySQL, MariaDB and MongoDB stay with the `http` backend. `mutation.*` is available on both engines. |
| Time and number formats | Same JSON. SQLite stores `decimal` as `NUMERIC` and returns it as a number without trailing zeros (`12.5` instead of `12.50`). |
| Request size | Each operation runs on its own connection (opened and closed per call); a result set is read completely into memory, bounded by the 1000-row limit. |

### Types

| Declared type | PostgreSQL | SQLite |
|---|---|---|
| `int32` | `INTEGER` | `INTEGER` |
| `int64` | `BIGINT` | `INTEGER` |
| `decimal(p,s)` | `NUMERIC(p,s)` | `NUMERIC` (binary floating point beyond about 15 digits) |
| `float64` | `DOUBLE PRECISION` | `REAL` |
| `bool` | `BOOLEAN` | `INTEGER` 0/1 |
| `string(n)` | `VARCHAR(n)` | `TEXT`, length **enforced by the bundle** |
| `text` | `TEXT` | `TEXT` |
| `json` | `JSONB` | `TEXT` (compact JSON) |
| `datetime` | `TIMESTAMPTZ` | `TEXT`, ISO 8601 UTC with six decimals, e.g. `2026-10-28T10:00:00.000000Z` |
| `date` | `DATE` | `TEXT` `yyyy-MM-dd` |
| `uuid` | `UUID` | `TEXT`, lower case |
| `bytes(n)` | `BYTEA` | `BLOB` |

The host's `re.sql` binds only primitive values. The connector therefore sends dates, uuids, decimals and JSON as text and lets
PostgreSQL convert them with an explicit `CAST(… AS …)`; it never depends on how a driver would map a .NET type.
`datetime` defaults of `"now"` become `CURRENT_TIMESTAMP` (PostgreSQL) or a UTC `strftime` expression (SQLite).

## Schema sync

`schema.sync` behaves like the API: the declaration is validated and normalized, hashed, diffed against the stored definition, and the
plan is applied in a fixed order (renames, index drops, added columns, altered columns, new tables, new indexes, dropped columns, dropped
tables). It is **declarative and repeatable**: an unchanged declaration returns `status: "unchanged"`. Objects that should exist but are
missing in the database are repaired (reported in the plan with the note `repair: …`).

* The whole sync runs in **one transaction** on both engines (their DDL is transactional), so a failure leaves nothing half applied.
  A second sync of the same namespace waits on a lock: a PostgreSQL advisory lock, or SQLite's single write lock.
* `dryRun: true` returns the plan without a transaction and without changes (`planned`, `unchanged` or `denied`).
* **PostgreSQL** alters columns in place (`ALTER COLUMN … TYPE … USING`, nullability, defaults).
* **SQLite** cannot alter a column. When a column's type, nullability or default changes, the table is **rebuilt** inside the transaction:
  a new table is created, the data is copied, the old table dropped, the new one renamed, the indexes recreated. Columns that are only
  pending removal are carried along (nullable) so `quarantine` still keeps their data. A rebuild fails, and rolls everything back, if
  existing rows violate the new definition (for example a `NOT NULL` column that contains `NULL`).
* New `NOT NULL` columns without a default get a neutral value for existing rows (`0`, `''`, …). PostgreSQL removes that default again;
  SQLite cannot, so the neutral default stays in the table definition. The bundle still demands a value for every new row because it
  checks the declared schema, not the table.
* Removed tables and columns are **destructive**. `deny` refuses the sync (`409 destructive_denied`, the dry run lists the plan);
  `quarantine` renames them to `_dropped_<unix time>_<name>` (columns are made nullable so they cannot block inserts); `apply` drops them.
  Shortening a string column below existing data is refused (`409 data_does_not_fit`), changing the primary key is refused
  (`409 unsupported_change`), and so is switching auto-increment on or off.
* SQLite has no real `VARCHAR(n)`; `string` lengths are enforced on every write by the bundle, not by the engine.

## Mutation receipts

`mutation.execute` runs one `Upsert`, `Update` or `Delete` and records a permanent receipt **in the same transaction**; `mutation.receipt`
reads it back later without executing anything. The contract is the one of lt-database-api and of lt-core's recovery code:

1. The request is validated strictly: only the five expected text members, strict UTF-8 JSON without duplicate keys (the command root is
   also compared case-insensitively), at most 8192 bytes, depth 16, `all` absent or `false`, the namespace equal to the receipt's, and the
   fingerprint equal to the SHA-256 of the length-framed `effectId`, namespace, operation and JSON.
2. A unique reservation `(namespace, effectId)` is inserted first. A concurrent request for the same effect waits until the first
   transaction ends; the same effect with the same fingerprint then returns the stored receipt, with another operation or fingerprint
   `409 receipt_conflict`.
3. A savepoint is set, the mutation runs, and it may affect **at most one row** (`409 multiple_rows` otherwise). A constraint or value
   failure, or any 4xx condition, rolls back to the savepoint and is recorded as a durable `failed` receipt with its code; `no_match`
   marks an update or delete that found nothing.
4. The receipt is finalized and the transaction commits. If the commit fails or its answer is lost, the error is propagated unchanged:
   only a later `mutation.receipt` lookup can tell whether the mutation happened. That is the point of the contract.

Engine notes: PostgreSQL uses `READ COMMITTED`, `SELECT … FOR UPDATE` on the target row and on the receipt. SQLite has a single writer; the
reservation takes its write lock, and a second writer waits (or fails with `conflict_retry`, depending on the host's busy timeout).
MySQL and MongoDB receipts are not available here (and MongoDB receipts are unsupported by the API as well).

## Operations and safety notes

* The first call after start (or `lt-db-reload`) creates `_ldb_schema` and `_ldb_mutation_receipts` with `CREATE TABLE IF NOT EXISTS`;
  the database account therefore needs `CREATE` once.
* Never run the SQL backend and an API against the same database at the same time unless both use the same namespaces on purpose: they
  share the bookkeeping tables and would see each other's schema.
* Statements are produced only by the SDK-free classes in `sh/Pure`; values always travel as bound parameters, and `NULL` is written as a
  literal. Column and table names come exclusively from the stored schema and pass the identifier rules.
* The log never contains payloads, values or driver messages, only the operation, the exception type and the mapped code.
* `lt-db-status` shows `backend=sql alias=… dialect=… destructive=…` and the last mapped error code.

## Error mapping

| Condition | Code | Status |
|---|---|---|
| Unique, primary key, not-null, foreign-key or check violation | `constraint_violation` | 409 |
| Deadlock, serialization failure, locked database | `conflict_retry` | 409 |
| Timeout | `database_timeout` | 504 |
| Bad text, range or length rejected by the engine | `invalid_value` | 400 |
| Anything else | `database_error` | 500 |

The classification looks at the driver's exception type and message text on the server only; because the host exposes no SQLSTATE,
an unusual message can fall back to `database_error`. The code never contains driver text.

## Verification status

What has been verified automatically (`tests/Bundle.Tests`, 100+ tests):

* the schema hash, canonical JSON, diff plan and receipt fingerprint against vectors produced by the real lt-database-api code;
* every generated statement for both dialects (filters, paging, inserts, updates, upserts, deletes, DDL, rebuild);
* the generated SQL executed against a **real SQLite engine** (types, filters, paging, upsert, constraint failures, rebuild with data and
  index restoration, savepoint semantics).

Verified on a real BlackICE server (Docker, `re.sql` host): the 38-step smoke bundle in `examples/smoke` passes 38/38 with `"backend": "sql"` on a
SQLite alias and 38/38 on PostgreSQL 17 (parameter binding, transactions across awaited commands, schema sync and rebuilds, type mapping).
SQLite note: the host's sandbox refuses `pragma_table_info`, so the connector reads column names from `SELECT * … LIMIT 0`.

**Not yet covered by the smoke bundle:** `mutation.execute` / `mutation.receipt` (unit-tested and run on a real SQLite engine, but no
runtime scenario in a BlackICE host yet), `CommandTimeout` behavior and concurrent writers. Keep `mutation.*` consumers on the `http`
backend until a runtime scenario exists.
