# Changelog

## 0.1.0

Initial public lt-database release: signed BlackICE database connector, schema/query builders, consumer helper and
compiled recipes. Supports PostgreSQL, MySQL and MongoDB through lt-database-api. The helper includes `Db.IsQueued`,
`Db.Flush` and `immediate: true` for the API's optional write-behind mode.

Includes installation, configuration, usage, reference, operations, security and build guides; a documented 38-step
runtime smoke bundle; CI that compiles all three WASM projects; and an allow-listed ZIP packager with SHA-256 checksums.

The default installed folder remains `lt_database` and the namespaces remain `LtDatabase` / `LtDatabaseClient`
for compatibility with existing consumers. Set `Db.Bundle` when installing under another folder name.
