# Changelog

## 0.2.0

The bundle identity is `lt_database`, with `LtDatabase` contracts, `LtDatabaseClient` consumer helpers and `lt-db-*`
console commands. All examples, configuration guides and package filenames use this identity.

Update the helper and shared source references in consumers together, rebuild consumers, install the connector under
`lt_database`, and start it before consumer bundles. This is a breaking source and bundle-name change; the HTTP protocol
and database namespaces are unchanged.

## 0.1.0

Initial public lt-database release: signed BlackICE database connector, schema/query builders, consumer helper and
compiled recipes. Supports PostgreSQL, MySQL and MongoDB through lt-database-api. The helper includes `Db.IsQueued`,
`Db.Flush` and `immediate: true` for the API's optional write-behind mode.

Includes installation, configuration, usage, reference, operations, security and build guides; a documented 38-step
runtime smoke bundle; CI that compiles all three WASM projects; and an allow-listed ZIP packager with SHA-256 checksums.

The installed folder is `lt_database` and the namespaces are `LtDatabase` / `LtDatabaseClient`.
