using LtDatabase;

namespace LtDatabase.Server;

/// <summary>
/// Talks to a database directly through the host's re.sql provider (Postgres or SQLite). The connection itself is declared
/// as an alias in the bundle's config.toml ([Database.&lt;alias&gt;]), so no host, password or file path is ever visible here.
/// Requires a BlackICE host with SDK 1.3.0 or newer.
/// </summary>
/// <remarks>
/// Same operations, request bodies, result bodies and error codes as lt-database-api, so consumers (lt-core's adapter
/// included) cannot tell the backends apart. All SQL is produced by the SDK-free classes in sh/Pure, which have unit tests.
/// What differs from the HTTP API is documented in docs/SQL-BACKEND.md.
/// </remarks>
internal sealed partial class SqlBackend : IDbBackend
{
    const int MaxRows = 1000, MaxInsertRows = 1000, MaxTables = 64;
    const long SchemaCacheMs = 20_000, IdempotencyKeepMs = 600_000;
    const int MaxIdempotencyEntries = 5_000;

    sealed record StoredSchema(SchemaDefinition? Schema, string? Hash, string? Updated);
    sealed record Cached(StoredSchema Stored, long LoadedAt);
    sealed record IdempotentResult(string PayloadHash, string Json, long Expires);

    readonly Dictionary<string, Cached> schemaCache = new();
    readonly Dictionary<string, IdempotentResult> idempotency = new();
    SqlDialect? dialect;
    bool initialized;
    string lastError = "";

    public string Name => "sql";
    public bool Unavailable => false;

    public string Describe(DatabaseConfig config) =>
        $"backend=sql alias={config.SqlAlias} dialect={dialect?.Name ?? "unknown"} destructive={config.DestructiveMode}{(lastError.Length > 0 ? " lastError=" + lastError : "")}";

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // re.sql caps CommandTimeout at 30 seconds and treats 0 as "default 5".
    static int TimeoutSeconds(DatabaseConfig cfg) => (int)Math.Clamp((cfg.TimeoutMs + 999) / 1000, 1, 30);

    // ------------------------------------------------------------------ entry point

    public async Future<BackendResult> Execute(DatabaseConfig cfg, BackendCall call)
    {
        try
        {
            var root = MiniJson.TryParse(call.Payload, out var parsed) ? MiniJson.AsObject(parsed) : null;
            if (root is null) return BackendResult.Fail(400, "invalid_request", "Payload is not valid for this operation.");

            var key = WritesData(call.Operation) ? MiniJson.GetString(root, "idempotencyKey") : null;
            var cacheKey = key is null ? null : call.Operation + "|" + key;
            var hash = key is null ? "" : Convert.ToHexString(Sha256.Hash(System.Text.Encoding.UTF8.GetBytes(call.Payload)));
            if (cacheKey is not null && idempotency.TryGetValue(cacheKey, out var hit) && hit.Expires > NowMs())
            {
                if (hit.PayloadHash != hash) return BackendResult.Fail(422, "idempotency_conflict", "This idempotency key was already used with a different request.");
                return BackendResult.Ok(200, hit.Json);
            }

            var json = await Dispatch(cfg, call.Operation, call.Payload, root);
            if (cacheKey is not null) RememberIdempotent(cacheKey, hash, json);
            lastError = "";
            return BackendResult.Ok(200, json);
        }
        catch (SqlRequestException ex)
        {
            return BackendResult.Fail(ex.Status, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            // Driver messages can carry row values or connection details, so only the type is logged and nothing is returned.
            var mapped = SqlErrors.Classify(ex.GetType().Name, ex.Message);
            lastError = mapped.Code;
            Console.WriteLine($"[lt_database] sql {call.Operation} failed: {ex.GetType().Name} -> {mapped.Code}");
            return BackendResult.Fail(mapped.Status, mapped.Code, mapped.Message);
        }
    }

    static bool WritesData(string op) => op is "schema.sync" or "insert" or "update" or "upsert" or "delete" or "flush" or "mutation.execute";

    void RememberIdempotent(string cacheKey, string hash, string json)
    {
        var now = NowMs();
        if (idempotency.Count >= MaxIdempotencyEntries)
            foreach (var expired in idempotency.Where(p => p.Value.Expires < now).Select(p => p.Key).ToList()) idempotency.Remove(expired);
        if (idempotency.Count < MaxIdempotencyEntries) idempotency[cacheKey] = new IdempotentResult(hash, json, now + IdempotencyKeepMs);
    }

    async Future<string> Dispatch(DatabaseConfig cfg, string op, string payload, Dictionary<string, object?> req)
    {
        switch (op)
        {
            case "health": return await Health(cfg);
            case "mutation.execute": return await ExecuteReceipted(cfg, payload);
            case "mutation.receipt": return await ReadReceipt(cfg, payload);
        }

        var ns = SqlSchema.Namespace(MiniJson.GetString(req, "namespace"));
        switch (op)
        {
            case "flush": return JsonText.Object(("flushed", 0), ("deadLettered", 0), ("remaining", 0), ("halted", false), ("enabled", false));
            case "schema.sync": return await SchemaSync(cfg, ns, req);
            case "schema.describe": return await SchemaDescribe(cfg, ns);
            case "insert": return await InsertRows(cfg, ns, req);
            case "find": return await FindRows(cfg, ns, req);
            case "count": return await CountRows(cfg, ns, req);
            case "update": return await UpdateRows(cfg, ns, req);
            case "upsert": return await UpsertRow(cfg, ns, req);
            case "delete": return await DeleteRows(cfg, ns, req);
            default: throw new SqlRequestException(501, "unsupported_operation", $"The sql backend does not implement '{op}'.");
        }
    }

    // ------------------------------------------------------------------ connection helpers

    async Future<re.sql.SqlConnection> Open(DatabaseConfig cfg)
    {
        var db = new re.sql.SqlConnection(re.sql.SqlConnection.For(cfg.SqlAlias));
        await db.OpenAsync();
        if (dialect is null) dialect = await Detect(cfg, db);
        if (!initialized)
        {
            // The two bookkeeping tables are created on first use. IF NOT EXISTS makes this safe to repeat.
            await Run(cfg, db, null, dialect.MetaTableSql, []);
            await Run(cfg, db, null, dialect.ReceiptTableSql, []);
            initialized = true;
        }
        return db;
    }

    async Future<SqlDialect> Detect(DatabaseConfig cfg, re.sql.SqlConnection db)
    {
        if (cfg.SqlDialect == "postgres") return SqlDialect.Postgres;
        if (cfg.SqlDialect == "sqlite") return SqlDialect.Sqlite;
        try
        {
            await Scalar(cfg, db, null, "SELECT sqlite_version()", []);
            return SqlDialect.Sqlite;
        }
        catch (Exception)
        {
            return SqlDialect.Postgres;
        }
    }

    static re.sql.SqlCommand Command(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction? tx, string sql, IReadOnlyList<object?> parameters)
    {
        var cmd = new re.sql.SqlCommand(db);
        cmd.CommandText = sql;
        cmd.CommandTimeout = TimeoutSeconds(cfg);
        if (tx is not null) cmd.Transaction = tx;
        foreach (var p in parameters) cmd.Parameters.AddWithValue(p!);
        return cmd;
    }

    static async Future<int> Run(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction? tx, string sql, IReadOnlyList<object?> parameters)
    {
        await using var cmd = Command(cfg, db, tx, sql, parameters);
        return await cmd.ExecuteNonQueryAsync();
    }

    static async Future<object?> Scalar(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction? tx, string sql, IReadOnlyList<object?> parameters)
    {
        await using var cmd = Command(cfg, db, tx, sql, parameters);
        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>Reads every row into memory. Result sizes are bounded by MaxRows, schema lookups and name lists are small.</summary>
    static async Future<List<object?[]>> Query(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction? tx, string sql, IReadOnlyList<object?> parameters)
    {
        var rows = new List<object?[]>();
        await using var cmd = Command(cfg, db, tx, sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    // ------------------------------------------------------------------ health

    async Future<string> Health(DatabaseConfig cfg)
    {
        var reachable = true;
        try
        {
            await using var db = await Open(cfg);
            await Scalar(cfg, db, null, "SELECT 1", []);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[lt_database] sql health failed: {ex.GetType().Name}");
            reachable = false;
        }
        return JsonText.Object(("status", "ok"), ("provider", dialect?.Name ?? (cfg.SqlDialect == "auto" ? "unknown" : cfg.SqlDialect)), ("database", reachable),
            ("writeBuffer", new JsonText.Raw(JsonText.Object(("enabled", false), ("pending", 0), ("oldestAgeSeconds", 0), ("halted", false)))));
    }

    // ------------------------------------------------------------------ stored schema

    async Future<StoredSchema> LoadStored(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction? tx, string ns)
    {
        var rows = await Query(cfg, db, tx, $"SELECT hash, definition, updated_at FROM {dialect!.Quote("_ldb_schema")} WHERE ns = $1", [ns]);
        if (rows.Count == 0) return new StoredSchema(null, null, null);
        return new StoredSchema(SqlSchema.ParseStored(rows[0][1]!.ToString()!), rows[0][0]!.ToString(), rows[0][2]?.ToString());
    }

    async Future<(SchemaTable Table, string Physical)> Resolve(DatabaseConfig cfg, string ns, string? tableName)
    {
        var table = SqlSchema.Table(tableName);
        if (schemaCache.TryGetValue(ns, out var hit) && NowMs() - hit.LoadedAt < SchemaCacheMs && hit.Stored.Schema is not null)
            return (TableOf(hit.Stored.Schema, table), SqlSchema.Physical(ns, table));
        await using var db = await Open(cfg);
        var stored = await LoadStored(cfg, db, null, ns);
        if (stored.Schema is null) throw new SqlRequestException(404, "namespace_unknown", "This namespace has no schema yet. Run a schema sync first.");
        schemaCache[ns] = new Cached(stored, NowMs());
        return (TableOf(stored.Schema, table), SqlSchema.Physical(ns, table));
    }

    static SchemaTable TableOf(SchemaDefinition schema, string table) =>
        schema.Tables.FirstOrDefault(t => t.Name == table) ?? throw new SqlRequestException(404, "unknown_table", $"The namespace has no table '{table}'.");

    async Future<string> SchemaDescribe(DatabaseConfig cfg, string ns)
    {
        await using var db = await Open(cfg);
        var stored = await LoadStored(cfg, db, null, ns);
        // Members that are null are left out, as the API does.
        var pairs = new List<(string, object?)> { ("namespace", ns) };
        if (stored.Hash is not null) pairs.Add(("hash", stored.Hash));
        if (stored.Schema is not null) pairs.Add(("schema", new JsonText.Raw(SqlSchema.ToCanonicalJson(stored.Schema))));
        if (stored.Updated is not null) pairs.Add(("updatedAt", UpdatedAt(stored.Updated)));
        return JsonText.Object([.. pairs]);
    }

    static string UpdatedAt(string stored) =>
        DateTime.TryParse(stored, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            : stored;

    // ------------------------------------------------------------------ data

    async Future<string> InsertRows(DatabaseConfig cfg, string ns, Dictionary<string, object?> req)
    {
        var (table, physical) = await Resolve(cfg, ns, MiniJson.GetString(req, "table"));
        var query = table.ToQuery();
        var statements = new SqlStatements(await DialectOf(cfg)).Insert(physical, query, req.GetValueOrDefault("rows"), MaxInsertRows);
        var key = table.PrimaryKey.Count == 1 ? query.Columns.First(c => c.Name == table.PrimaryKey[0]) : null;
        var ids = new List<object?>();
        await using var db = await Open(cfg);
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            foreach (var st in statements)
            {
                if (key is null) { await Run(cfg, db, tx, st.Sql, st.Parameters); ids.Add(null); }
                else ids.Add(SqlValues.ToJson(key, await Scalar(cfg, db, tx, st.Sql, st.Parameters)));
            }
            await tx.CommitAsync();
        }
        catch (Exception)
        {
            await tx.RollbackAsync();
            throw;
        }
        return JsonText.Object(("inserted", statements.Count), ("ids", new JsonText.Raw(JsonText.Array(ids))));
    }

    async Future<string> FindRows(DatabaseConfig cfg, string ns, Dictionary<string, object?> req)
    {
        var (table, physical) = await Resolve(cfg, ns, MiniJson.GetString(req, "table"));
        var d = await DialectOf(cfg);
        var (st, selected, limit) = new SqlStatements(d).Find(physical, table.ToQuery(), req, MaxRows);
        await using var db = await Open(cfg);
        var raw = await Query(cfg, db, null, st.Sql, st.Parameters);
        var rows = new List<object?>();
        foreach (var r in raw.Take(limit))
        {
            var pairs = new (string, object?)[selected.Count];
            for (var i = 0; i < pairs.Length; i++) pairs[i] = (selected[i].Name, d.FromDriver(selected[i], r[i]));
            rows.Add(new JsonText.Raw(JsonText.Object(pairs)));
        }
        return JsonText.Object(("rows", new JsonText.Raw(JsonText.Array(rows))), ("hasMore", raw.Count > limit));
    }

    async Future<string> CountRows(DatabaseConfig cfg, string ns, Dictionary<string, object?> req)
    {
        var (table, physical) = await Resolve(cfg, ns, MiniJson.GetString(req, "table"));
        var st = new SqlStatements(await DialectOf(cfg)).Count(physical, table.ToQuery(), req);
        await using var db = await Open(cfg);
        var count = Convert.ToInt64(await Scalar(cfg, db, null, st.Sql, st.Parameters), System.Globalization.CultureInfo.InvariantCulture);
        return JsonText.Object(("count", count));
    }

    async Future<string> UpdateRows(DatabaseConfig cfg, string ns, Dictionary<string, object?> req)
    {
        var (table, physical) = await Resolve(cfg, ns, MiniJson.GetString(req, "table"));
        var st = new SqlStatements(await DialectOf(cfg)).Update(physical, table.ToQuery(), req);
        await using var db = await Open(cfg);
        return JsonText.Object(("affected", (long)await Run(cfg, db, null, st.Sql, st.Parameters)));
    }

    async Future<string> UpsertRow(DatabaseConfig cfg, string ns, Dictionary<string, object?> req)
    {
        var (table, physical) = await Resolve(cfg, ns, MiniJson.GetString(req, "table"));
        var st = new SqlStatements(await DialectOf(cfg)).Upsert(physical, table.ToQuery(), req);
        await using var db = await Open(cfg);
        await Run(cfg, db, null, st.Sql, st.Parameters);
        return JsonText.Object(("affected", 1L));
    }

    async Future<string> DeleteRows(DatabaseConfig cfg, string ns, Dictionary<string, object?> req)
    {
        var (table, physical) = await Resolve(cfg, ns, MiniJson.GetString(req, "table"));
        var st = new SqlStatements(await DialectOf(cfg)).Delete(physical, table.ToQuery(), req);
        await using var db = await Open(cfg);
        return JsonText.Object(("affected", (long)await Run(cfg, db, null, st.Sql, st.Parameters)));
    }

    /// <summary>The dialect is known once a connection was opened; Resolve (which precedes every data call) guarantees that.</summary>
    async Future<SqlDialect> DialectOf(DatabaseConfig cfg)
    {
        if (dialect is null) { await using var db = await Open(cfg); }
        return dialect!;
    }
}
