# Security

This page describes what the connector protects, how, and where the protection ends. The API's
[security design](https://github.com/LandminenTester/lt-database-api/blob/main/docs/SECURITY.md) covers the server side and the full
protocol.

## Trust model

* The **game server operator** trusts the bundles they install. Any server bundle can call the connector's exports; the connector
  cannot tell callers apart (BlackICE exports carry no caller identity).
* **Players are not trusted and cannot reach the connector at all**: it has no client component and registers no network events, so
  there is no message a client could send to it.
* The **API** is trusted to speak for the database, and proves it by signing every answer.
* The **network** between game server and API is not trusted.

## What is protected how

| Threat | Defence |
|---|---|
| A player reaching the database | No client component, no net events; the connector exists only in the server realm |
| A bundle using another bundle's data | `allowedNamespaces` in `vfs/database.json` and the API key's own namespace list; physical table names carry the namespace |
| Somebody forging requests to the API | Every request carries an HMAC-SHA256 over its exact bytes, a timestamp and a one-time nonce; the API rejects bad, old and replayed requests |
| Somebody forging answers (man in the middle, wrong host) | Every answer must carry an HMAC over its status, **your** nonce and its body; otherwise it is discarded as `bad_signature`, so the TLS certificate does not have to be pinned (the sandbox cannot pin) |
| Reading the traffic | HTTPS is required for remote hosts (`http://` only to loopback or with an explicit `allowInsecureHttp`) |
| The sandbox calling arbitrary hosts | The manifest's `[Networking] allowed` list; redirects are checked against it |
| Injection | The helper builds JSON with escaped values; the API accepts only a fixed filter grammar and binds every value as a parameter. There is no way to send raw SQL or a raw MongoDB operator |
| A runaway bundle flooding the API | 512 waiting calls, `maxConcurrent` in flight, 1 MiB per payload, a circuit breaker, plus the API's per-key rate limit |
| Duplicate writes after a timeout | Idempotency keys on inserts and upserts; the API returns the first result |
| Leaking the secret to players | The secret lives only in the private `vfs/` folder, which is not streamed to clients; nothing sensitive is compiled into the WASM |
| Information in logs | Calls are logged with id and error type only. Payloads, values and the secret are never logged |

## What it does not protect against

* **A malicious bundle on the same game server.** It can call the connector for any allowed namespace. Install only bundles you
  trust, and keep `allowedNamespaces` as narrow as practical. For hard isolation run separate game servers with separate keys.
* **Anyone who can read the `vfs` folder.** The secret is a plain file on the game server's disk. Use file permissions, keep the
  folder out of backups that others can read, and restrict the API key to the game server's address (`AllowedCidrs`) so a stolen
  secret is useless from elsewhere. Rotate the secret if in doubt ([Operations](OPERATIONS.md#rotating-the-secret)).
* **A compromised API or database.** Data integrity ends there.
* **The sandbox's own guarantees.** The connector relies on the documented `re.net.Http` allow-list and on the private `vfs/` folder.

## Cryptography notes

HMAC-SHA-256, SHA-256, base64url and the JSON handling are small managed implementations in [`sh/Pure`](../sh/Pure), because the
sandbox does not document platform cryptography. They are tested against `System.Security.Cryptography` for every input length
around the block boundaries, against RFC 4231 test cases, and against the API's published signing vectors. Nonces are unique per
request (counter, clock and random bytes mixed); they do not need to be secret. If the game server's clock is wrong, the connector
corrects its timestamps from the API's `GET /v1/health`.

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's private vulnerability reporting on this repository (Security → Report a
vulnerability) with the version, a description and steps to reproduce, without secrets. Problems in the API belong to the
[lt-database-api](https://github.com/LandminenTester/lt-database-api) repository.
