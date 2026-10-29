# Runtime smoke test

This server-only bundle registers two commands:

* `db-smoke <label> [connector-bundle]` runs 38 steps: health, schema creation and migration, all CRUD operations, filters, sorting,
  paging, constraints, namespace isolation and final cleanup.
* `db-smoke-mutation <label> [connector-bundle]` runs 29 steps for atomic mutation receipts (`mutation.execute`, `mutation.receipt`):
  replay, receipt conflicts, lookups, `no_match`, `multiple_rows`, durable failed receipts, rejected envelopes and two parallel executions.

The optional second argument names the connector bundle to test (default `lt_database`), so a `sql` and an `http` installation can sit
side by side. A label that contains `sql` makes the test expect the `sql` behavior where it differs from the API (an unfiltered delete is
refused with `filter_required`; the API refuses it with `forbidden` when the key has no permission).

Use a disposable database. The test **drops and recreates tables in `smoke_test`**. Do not store real data in that namespace.

## Build and install

From the repository root, with .NET 10:

```sh
dotnet build examples/smoke/sv/sv.csproj -c Release
```

Copy `examples/smoke/sv/bin/Release/net10.0/wasi-wasm/native/sv.wasm` into
`<server>/data/ugc/db_smoke/`, then copy `examples/smoke/data/config.toml` there **last** so the server discovers a complete
bundle. Do not use the connector's root `dist/sv.wasm`: that is the connector, not this test.

Install and configure the connector first using [Install](../../docs/INSTALL.md). Allow `smoke_test` in both its
`vfs/database.json` and the API key. Keep `smoke_denied` disallowed in the connector (the test checks this).
The test uses the default connector folder `lt_database`; if yours differs, set `Db.Bundle` in `sv/App.cs` before building.

## Run

In the game server console:

```text
start lt_database
start db_smoke
db-smoke postgres
```

Expected final line: `[db-smoke] RESULT postgres: 38/38 passed`. For receipts run `db-smoke-mutation postgres` and expect
`29/29 passed`. Mutation effect IDs carry a per-run prefix because receipts are permanent.

With the `sql` backend (`"backend": "sql"` and a `[Database.<alias>]` entry in the connector's `config.toml`) use a label with `sql`,
for example `db-smoke sql-sqlite lt_database_sqltest`. Both scenarios were run this way on a SQLite alias and on PostgreSQL 17.
Run separately against PostgreSQL, MySQL and MongoDB by changing the connector's API URL and running `lt-db-reload`.
The label is only a log label; it does not select a database. See [local Docker setup](../../docs/INSTALL.md#local-test-with-docker).

This scenario expects **live writes** because it checks generated IDs, affected counts and immediate reads. Disable
write-behind for `smoke_test`. The API's automated tests cover buffered writes; a passing live smoke test alone does not
prove buffered behavior inside BlackICE.

Read the first FAIL line and the API logs when diagnosing failures. After testing, stop `db_smoke`, remove its disposable
namespace permissions, and remove the test bundle if no longer needed. Never publish your actual VFS secret.
