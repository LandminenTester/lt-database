# Operations

## Day to day

| Task | How |
|---|---|
| See what the connector is doing | `lt-db-status` in the server console |
| Check the whole chain | `lt-db-health` |
| Apply a changed `database.json` or secret | `lt-db-reload` |
| Apply a changed `config.toml` or a new `sv.wasm` | Replace the files, write `config.toml` last, then `restart lt_database` |
| Switch to another database or API | Change `url` (and `keyId` / secret if needed), `lt-db-reload`, restart the bundles that declared schemas |
| Find log lines | Server log, tag `script:lt_database` (and your own bundle's tag) |

## Starting and stopping

Start the connector before the bundles that use it. If a consumer starts first, its first call returns `bundle_unavailable`;
let it retry (for example schedule the schema sync again in a few seconds) or order the `start` lines in `server.cfg`.

Stopping or restarting the connector loses calls that are in flight and results nobody collected yet; the database changes that were
already acknowledged are not affected. Consumers that keep a "database ready" flag should clear it when they notice
`bundle_unavailable`.

## Rotating the secret

1. On the API, add the new secret as `Next` of the key and restart the API (both secrets are accepted).
2. On the game server, write the new secret to `vfs/database.secret` and run `lt-db-reload`.
3. Check with `lt-db-health`.
4. On the API, move the new secret to `Current` and clear `Next`.

No downtime, and old and new secrets overlap only for the time between steps 1 and 4.

## Upgrading

1. Read the release notes.
2. Build or download the new version and replace `sv.wasm` and `config.toml` (keep your `[Networking]` list and the `vfs` folder).
3. `restart lt_database`. Consumers need not restart. The wire protocol is versioned (`"v":1`); a new API stays compatible
   with older connectors within a major version.
4. Run `lt-db-health`.

Upgrade the API first when both change; it accepts older connectors.

## Backups

The connector keeps no data. Back up the database (see the API's documentation) and the `vfs` folder's `database.json` (not the
secret) if you want to restore the configuration quickly. A schema can always be recreated from the declaration in your bundle, but
data cannot.

## Monitoring

* Log lines: `ready -> …` at start and reload, `the database API failed 5 times in a row; pausing calls for 15s` when the circuit
  opens, `call db-N failed: <type>` for unexpected errors (no values, no secrets).
* `lt-db-status` shows `pending`, `queued`, `inflight`, `circuit` and `clockOffset`.
* The API side exposes metrics, an audit line per request and (with write-behind) queue depth: see its documentation.

Alert on: repeated `circuit=open`, `bad_signature` (somebody answers in the API's name, or the secret differs), a growing
`queued` number, and `unauthorized` after a deployment (wrong secret or clock).

## Performance

* One call is one HTTPS round trip plus the database time, typically some tens of milliseconds. Do not call per tick.
* `maxConcurrent` (default 8) limits parallel requests; raise it if you have many independent callers and a fast API.
* Batch: insert many rows with one call, read with `limit`, and cache what you read.
* For many small writes ask the API owner about [write-behind](https://github.com/LandminenTester/lt-database-api/blob/main/docs/WRITE-BEHIND.md).
* Keep payloads small. The API's limit is 1 MiB per request; rows per insert are limited (default 1000).

## Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| `[lt_database] NOT CONFIGURED: vfs/database.json is missing` | Create the file in the bundle's `vfs/` folder; check the folder name matches the bundle name |
| `… "url" uses plain http to a remote host` | Use https, or set `allowInsecureHttp` for a trusted network |
| `… The key secret is too short` | Use at least 32 characters in `database.secret` |
| `network` for every call | The host is not in `[Networking] allowed` of `config.toml`, the API is down, a firewall blocks it, or TLS fails. Try the URL from the game server's machine. Redirects must also target allowed hosts |
| `unauthorized` | Wrong `keyId` or secret; the game server's clock is more than 30 s off (the connector corrects this itself, but not if the API's `/v1/health` is unreachable); the key is limited to another source address; the API's lock-out is active after repeated failures |
| `bad_signature` | The secret differs from the API's, or a proxy rewrote the answer. The answer was discarded |
| `namespace_not_allowed` | Add the namespace to `allowedNamespaces` (and to the key on the API side) |
| `namespace_unknown` / `unknown_table` | The schema was not synced yet (or was removed): run `Db.Sync` first, and wait for its callback |
| `constraint_violation` | A unique value already exists or a required reference is missing |
| `missing_value` | A required column has no value and no default |
| `unavailable` | Five failures in a row; the circuit breaker pauses calls for 15 seconds. Fix the cause (see `network`) |
| `bundle_unavailable` | The connector is not running (`bundles`, `start lt_database`) or has another folder name than `Db.Bundle` |
| `start lt_database` says "Failed to find bundle" | The server has not discovered the folder: rewrite `config.toml` after `sv.wasm`, or restart the server |
| A schema change was refused (`destructive_denied`) | The API owner forbids dropping data. Use a dry run to see the plan, or ask for `Quarantine`/`Apply` |
| Results never arrive | A callback was never reached because an earlier step in your chain failed: log every `State != "ok"` |

Collect for a bug report: the output of `lt-db-status`, the error `Code` and `Status`, the log lines around the call, the API
version and database engine. Never send the secret.
