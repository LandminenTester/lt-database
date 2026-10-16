# Configuration

The connector has no code-level configuration. Everything is in the manifest and two files in its private `vfs/` folder.
After changing `vfs/database.json` or the secret, run `lt-db-reload` in the server console; no restart is needed. A change
to `config.toml` needs the bundle to be restarted (`restart lt_database`).

## Manifest: `config.toml`

```toml
[General]
description = "…"
author = "…"
version = "0.2.1"

[Scripting]
server_blob = "sv.wasm"
enable_vfs = true

[Networking]
allowed = ["db.example.com"]

[Meta]
config_version = "alpha_playtest"
```

| Key | Meaning |
|---|---|
| `Scripting.server_blob` | The server component. There is deliberately no `client_blob`. |
| `Scripting.enable_vfs` | Mounts the bundle's private `vfs/` folder. **Required**: the connector reads its settings from there. |
| `Networking.allowed` | The only hosts the connector may contact. Entries: `host`, `host:port`, `scheme://host`, `scheme://host:port`; exact match, no wildcards; redirects are checked too. A request to an unlisted host fails and is logged. |

## `vfs/database.json`

```json
{
  "url": "https://db.example.com",
  "keyId": "gameserver",
  "secretFile": "database.secret",
  "allowedNamespaces": ["mygame", "otherbundle"],
  "timeoutMs": 15000,
  "maxConcurrent": 8,
  "allowInsecureHttp": false
}
```

| Field | Default | Meaning |
|---|---|---|
| `url` | required | Base address of lt-database-api, without a path. `https://` is required except for `localhost`, `127.0.0.1` and `::1`. |
| `keyId` | required | The API key's id, at most 64 characters. |
| `secretFile` | `database.secret` | File inside `vfs/` that holds the secret. Absolute paths and `..` are refused. |
| `allowedNamespaces` | required | Namespaces the connector will send. At least one entry, or `"*"` for all. Each entry follows the identifier rule below. |
| `timeoutMs` | `15000` | Time one request may take (1000 to 120000). |
| `maxConcurrent` | `8` | Requests in flight at once (1 to 64); more calls wait in a queue. |
| `allowInsecureHttp` | `false` | Allows plain `http://` to a remote host. Only for a network you fully trust. |

If the file is missing or invalid, the bundle stays loaded but answers every call with `not_configured` and logs why
(`lt-db-status` shows the reason).

### Namespaces and identifiers

A namespace belongs to one bundle (or one feature) and owns a set of tables. Names (namespace, table, column, index) are lower-case
words joined by single underscores: `^[a-z][a-z0-9]*(_[a-z0-9]+)*$`; namespaces at most 24 characters, tables 36, columns 48.
`player_prefs` is fine; `Player`, `a__b`, `1st` and `a-b` are not.

`allowedNamespaces` is a safety net on the game server (a typo or an unknown bundle cannot reach the database). The API key's own
`AllowedNamespaces` is the real authority; both must allow a namespace.

## `vfs/database.secret`

One line, at least 32 characters (`openssl rand -base64 48`). Surrounding whitespace is ignored. It must equal the secret of the
key on the API side.

Give the `vfs` folder operating-system permissions so that only the game server account (and administrators) can read it.

## Several environments

The connector talks to one API at a time. To use a different database in test and production, keep one `database.json` per
environment (different `url` and `keyId`, different secret) and let your deployment copy the right one. To switch a running server
between databases (for example during a migration), change `url` and run `lt-db-reload`; bundles that already synced
their schema keep their state, so re-declare schemas by restarting them, or create the schema through the API first.

## Which settings live where

| What | Where |
|---|---|
| Which hosts the sandbox may contact | `config.toml` → `Networking.allowed` |
| Where the API is, which key, which namespaces | `vfs/database.json` |
| The secret | `vfs/database.secret` |
| Database credentials | Never here. Only the API knows them. |
| Which namespaces the key may use, rate limits, source address, destructive schema changes, write-behind | The API's configuration ([lt-database-api docs](https://github.com/LandminenTester/lt-database-api/blob/main/docs/DEPLOYMENT.md)) |
