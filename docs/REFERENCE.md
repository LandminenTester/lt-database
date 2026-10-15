# Reference

## Exports

The bundle registers two server-realm exports (`re.Exports`). They are synchronous, as the runtime requires; real work happens in the
background, so a call returns a ticket and the result is collected with a second export. The helper `LtDatabaseClient.Db` does both for you.

| Export | Input | Output |
|---|---|---|
| `Call` | `DbCall(string Operation, string Json)` | `DbTicket(string Id, bool Accepted, string Error)` |
| `TakeResult` | `DbResultQuery(string Id)` | `DbResult(string Id, string State, int Status, string Code, string Json)` |

All four are MessagePack records in the shared project (`sh/Contracts.cs`).

* `DbTicket.Accepted == false` means nothing was queued; `Error` reads `"<code>: <message>"` (see [error codes](#error-codes)).
* `TakeResult` returns `State = "pending"` until the answer is ready, then hands out the result **once** (it is removed). A result that
  nobody collects is dropped after about two minutes plus four times the timeout. An unknown or collected id gives `State = "missing"`.

Calling the exports yourself (without the helper):

```csharp
var ticket = re.Exports.Call<DbTicket, DbCall>("lt_database", "Call", new("count", "{\"namespace\":\"mygame\",\"table\":\"players\"}"));
// ... later, for example from Futures.Scheduler.Push(50, Poll):
var result = re.Exports.Call<DbResult, DbResultQuery>("lt_database", "TakeResult", new(ticket.Id));
```

The first argument is the folder name of the connector bundle.

## Operations

`DbCall.Operation` is one of the names below; `Json` is the payload of the API operation
([API reference](https://github.com/LandminenTester/lt-database-api/blob/main/docs/API.md)). Every payload except `health` must carry
`"namespace"`, which must be allowed in `vfs/database.json`.

| Operation | API endpoint | Retried by the connector |
|---|---|---|
| `health` | `POST /v1/health` | yes |
| `schema.sync` | `POST /v1/schema/sync` | yes (declarative) |
| `schema.describe` | `POST /v1/schema/describe` | yes |
| `find` | `POST /v1/data/find` | yes |
| `count` | `POST /v1/data/count` | yes |
| `delete` | `POST /v1/data/delete` | yes |
| `flush` | `POST /v1/data/flush` | yes |
| `insert`, `upsert`, `update` | `POST /v1/data/…` | only if the payload carries an `idempotencyKey` (the helper adds one for insert and upsert) |

Retries (up to three attempts, 300 ms and 600 ms apart) happen for network errors and for unsigned `502/503/504` answers. A `401`
triggers one clock re-sync and one more attempt. Authentic error answers from the API are never retried.

Limits: a payload may have at most 1,000,000 characters; at most 512 calls may be waiting at once (`busy` beyond that);
`maxConcurrent` requests run in parallel.

## `LtDatabaseClient.Db` (helper)

Link `import/LtDatabase.Server.cs` into your bundle. All methods return a `DbTicket` and call `done` once.

| Method | Purpose |
|---|---|
| `Call(operation, json, done)` | Any operation with a raw payload |
| `Sync(ns, schemaJson, done, dryRun = false)` | Create/update the tables of a namespace |
| `Describe(ns, done)` | The stored schema |
| `Insert(ns, table, rowJson \| rowsJson[], done, immediate = false)` | Insert rows (idempotency key added) |
| `Find(ns, table, done, filterJson, fields, sortJson, limit, offset)` | Query |
| `Count(ns, table, done, filterJson)` | Count |
| `Update(ns, table, filterJson, done, setJson, incJson, immediate = false)` | Change matching rows |
| `Upsert(ns, table, rowJson, keys, done, immediate = false)` | Insert or update by key (idempotency key added) |
| `Delete(ns, table, filterJson, done, immediate = false)` | Delete matching rows |
| `Flush(ns, done)` | Apply queued writes now (write-behind) |
| `Health(done)` | Signed health check |
| `Rows(result)` | Rows of a successful find |
| `HasMore(result)` | More rows than `limit`? |
| `Number(result)` | Count / affected rows / inserted rows |
| `InsertedIds(result)` | Generated primary keys of an insert |
| `IsQueued(result)` | The API only queued the write (write-behind) |
| `ErrorMessage(result)` | Readable message of a failed result |
| `Bundle` (field) | Folder name of the connector, default `"lt_database"` |

If the connector bundle is not running, `done` receives an error result with code `bundle_unavailable` instead of an exception.

## Builders (`LtDatabase`, shared project)

All builders return JSON text and escape every value.

**Filters `F`:** `Eq Ne Gt Gte Lt Lte In NotIn IsNull StartsWith Contains And Or Not`.

**Rows and sorting `Q`:** `Q.Row(("column", value), …)` for rows, `set` and `inc` objects; `Q.Sort(("column", descending), …)`;
`Q.Immediate(payloadJson)` marks a payload for immediate execution.

**Schema `S`:** `Schema(tables…)`, `Table(name, primaryKey[], columns[], indexes[]?, renamedFrom?)`, `Index(name, unique, columns…)`,
`Int32 Int64 Text LongText Bool Decimal Double Json DateTime Date Uuid Bytes`, and `Column(name, type, length, precision, scale,
nullable, unique, autoIncrement, @default, renamedFrom)` for every option.

**Others:** `JsonText` (writing JSON: `Quote`, `Value`, `Object`, `Array`, `Raw`), `MiniJson` (reading JSON without reflection: `Parse`,
`TryParse`, `AsObject`, `GetString`, `GetLong`, `GetBool`; objects become `Dictionary<string,object?>`, arrays `List<object?>`, numbers
`long` or `double`), `Sha256` (`Hash`, `Hmac`), `Wire` (envelope signing and verification), `Ids.Next()`.

## Result states and codes

`DbResult.State`: `ok`, `error`, `pending`, `missing`.

### Error codes

Generated by the connector (before or instead of an API answer):

| Code | Meaning |
|---|---|
| `not_configured` | `vfs/database.json` or the secret is missing or invalid; see `lt-db-status` |
| `unknown_operation` | The operation name is not one of the list above |
| `invalid_request` | The payload is not a JSON object |
| `invalid_identifier` | Missing or invalid `namespace` |
| `namespace_not_allowed` | The namespace is not in `allowedNamespaces` |
| `payload_too_large` | More than 1,000,000 characters |
| `busy` | More than 512 calls waiting |
| `unavailable` | Circuit breaker open: five transport failures in a row pause calls for 15 s |
| `network` | The API could not be reached (timeout, refused, host not allowed) |
| `unauthorized` | The API did not accept the key (wrong secret, expired timestamp, replay, address not allowed) |
| `rate_limited` | The API's rate limit or lock-out |
| `bad_signature` | The answer was not signed with your secret and was discarded |
| `bad_response` | The answer was not a valid envelope or its signed status differed |
| `internal` | Unexpected failure inside the connector (details in the log) |
| `bundle_unavailable` | Helper only: the connector bundle is not running |

From the API (signed, trustworthy): `invalid_request`, `invalid_identifier`, `invalid_schema`, `invalid_filter`, `invalid_sort`,
`unknown_column`, `unknown_table`, `namespace_unknown`, `missing_value`, `invalid_value`, `value_too_long`, `filter_required`,
`forbidden`, `constraint_violation`, `destructive_denied`, `data_does_not_fit`, `unsupported_change`, `sync_in_progress`,
`conflict_retry`, `too_many_rows`, `payload_too_large`, `idempotency_conflict`, `write_buffer_full`, `write_buffer_unavailable`,
`database_error`, `database_timeout`, `internal_error`, `timeout`. Meanings: the API's
[error table](https://github.com/LandminenTester/lt-database-api/blob/main/docs/API.md#errors).

## Console commands

| Command | Does |
|---|---|
| `lt-db-status` | Target, key id, allowed namespaces, queue and in-flight counts, circuit state, clock offset (never the secret) |
| `lt-db-health` | Sends an authenticated health check and prints the outcome |
| `lt-db-reload` | Re-reads `vfs/database.json` and the secret, resets the circuit breaker and re-syncs the clock |

## The wire format in one paragraph

Each call becomes `POST {url}/v1/...` with the body `{"v":1,"kid":…,"ts":…,"nonce":…,"payload":"<json string>","sig":…}` where
`sig` is `base64url(HMAC-SHA256(secret, "v1\n<path>\n<kid>\n<ts>\n<nonce>\n<payload>"))`. The answer carries
`sig = HMAC(secret, "v1resp\n<your nonce>\n<status>\n<payload>")`, which the connector verifies before it believes anything.
Specification and test vectors: the API's [SECURITY.md](https://github.com/LandminenTester/lt-database-api/blob/main/docs/SECURITY.md).
