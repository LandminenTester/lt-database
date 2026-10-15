# Runtime smoke test

This server-only bundle registers `db-smoke <label>`. It exercises 38 steps: signed health, schema creation and migration,
all CRUD operations, filters, sorting, paging, constraints, namespace isolation and final cleanup.

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

Expected final line: `[db-smoke] RESULT postgres: 38/38 passed`.
Run separately against PostgreSQL, MySQL and MongoDB by changing the connector's API URL and running `lt-db-reload`.
The label is only a log label; it does not select a database. See [local Docker setup](../../docs/INSTALL.md#local-test-with-docker).

This scenario expects **live writes** because it checks generated IDs, affected counts and immediate reads. Disable
write-behind for `smoke_test`. The API's automated tests cover buffered writes; a passing live smoke test alone does not
prove buffered behavior inside BlackICE.

Read the first FAIL line and the API logs when diagnosing failures. After testing, stop `db_smoke`, remove its disposable
namespace permissions, and remove the test bundle if no longer needed. Never publish your actual VFS secret.
