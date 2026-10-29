using LtDatabase;

namespace LtDatabase.Server;

internal sealed partial class SqlBackend
{
    // ------------------------------------------------------------------ schema sync

    async Future<string> SchemaSync(DatabaseConfig cfg, string ns, Dictionary<string, object?> req)
    {
        var desired = SqlSchema.Normalize(SqlSchema.Parse(req.GetValueOrDefault("schema")), MaxTables);
        var dryRun = MiniJson.GetBool(req, "dryRun") ?? false;
        var hash = SqlSchema.Hash(desired);
        var mode = cfg.DestructiveMode;

        await using var db = await Open(cfg);
        // Structure changes are transactional on both engines, so a failed sync leaves nothing half applied.
        re.sql.SqlTransaction? tx = dryRun ? null : await db.BeginTransactionAsync();
        try
        {
            if (tx is not null) await LockNamespace(cfg, db, tx, ns);
            var stored = await LoadStored(cfg, db, tx, ns);
            var ops = SqlSchema.Diff(stored.Schema, desired);
            await AddRepairOps(cfg, db, tx, ns, desired, ops);
            ops = [.. ops.OrderBy(o => (int)o.Kind)];

            if (ops.Count == 0 && stored.Hash == hash)
            {
                if (tx is not null) await tx.RollbackAsync();
                return SyncJson(ns, hash, false, dryRun, "unchanged", ops);
            }

            var destructive = ops.Count(o => o.Destructive);
            if (mode == "deny" && destructive > 0)
            {
                if (dryRun) return SyncJson(ns, hash, true, true, "denied", ops);
                throw new SqlRequestException(409, "destructive_denied", $"The change contains {destructive} destructive operation(s) and this server denies them. Run a dry run to see the plan.");
            }
            if (dryRun) return SyncJson(ns, hash, true, true, "planned", ops);

            var state = new ApplyState();
            foreach (var op in ops) await Apply(cfg, db, tx!, ns, op, ops, mode, state);
            foreach (var table in state.NeedsRelax.Where(t => !state.Rebuilt.Contains(t)))
                await Rebuild(cfg, db, tx!, ns, state.Desired[table], ops.Where(o => o.Kind == SchemaOpKind.DropColumn && o.Table == table).Select(o => Parked(o, state)), state);
            await SaveStored(cfg, db, tx!, ns, hash, desired);
            await tx!.CommitAsync();
            schemaCache.Remove(ns);
            return SyncJson(ns, hash, true, false, "applied", ops);
        }
        catch (Exception)
        {
            if (tx is not null) await tx.RollbackAsync();
            throw;
        }
    }

    static string SyncJson(string ns, string hash, bool changed, bool dryRun, string status, List<SchemaOp> plan) =>
        JsonText.Object(("namespace", ns), ("hash", hash), ("changed", changed), ("dryRun", dryRun), ("status", status),
            ("plan", new JsonText.Raw("[" + string.Join(",", plan.Select(o => o.ToJson())) + "]")));

    sealed class ApplyState
    {
        public HashSet<string> Rebuilt { get; } = [];
        public HashSet<string> NeedsRelax { get; } = [];
        public Dictionary<string, SchemaTable> Desired { get; } = new();
        public Dictionary<(string Table, string Column), string> Parked { get; } = new();
    }

    /// <summary>Column of a quarantined drop under its parked name, nullable so it can no longer block inserts.</summary>
    static SchemaColumn Parked(SchemaOp op, ApplyState state) =>
        op.OldColumnDef! with { Name = state.Parked.GetValueOrDefault((op.Table, op.Column!), op.Column!), Nullable = true };

    async Future LockNamespace(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction tx, string ns)
    {
        // Two concurrent syncs of one namespace must not interleave. Postgres has an advisory lock released at transaction end,
        // SQLite takes its single write lock with the first write statement (which changes nothing).
        if (dialect!.Name == "postgres") await Scalar(cfg, db, tx, "SELECT pg_advisory_xact_lock(hashtext($1))", ["ldb:" + ns]);
        else await Run(cfg, db, tx, $"UPDATE {dialect.Quote("_ldb_schema")} SET hash = hash WHERE ns = $1", [ns]);
    }

    async Future SaveStored(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction tx, string ns, string hash, SchemaDefinition schema)
    {
        var ps = new SqlParams();
        var values = new[] { ps.Add(ns), ps.Add(hash), ps.Add(SqlSchema.ToCanonicalJson(schema)), ps.Add(DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)) };
        var sql = dialect!.UpsertSql("_ldb_schema", ["ns", "hash", "definition", "updated_at"], values, ["ns"], ["hash", "definition", "updated_at"]);
        await Run(cfg, db, tx, sql, ps.Values);
    }

    // ------------------------------------------------------------------ repair

    async Future<bool> TableExists(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction? tx, string physical) =>
        (await Query(cfg, db, tx, dialect!.TableExistsSql, [physical])).Count > 0;

    async Future<HashSet<string>> NameSet(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction? tx, string sql, string physical) =>
        [.. (await Query(cfg, db, tx, sql, [physical])).Select(r => r[0]!.ToString()!)];

    /// <summary>
    /// Column names of a table. SQLite's host sandbox does not authorize pragma_table_info, so the names come from the
    /// header of an empty SELECT instead; PostgreSQL uses information_schema.
    /// </summary>
    async Future<HashSet<string>> ColumnSet(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction? tx, string physical)
    {
        if (dialect!.Name != "sqlite") return await NameSet(cfg, db, tx, dialect.ColumnNamesSql, physical);
        var names = new HashSet<string>();
        await using var cmd = Command(cfg, db, tx, $"SELECT * FROM {dialect.Quote(physical)} LIMIT 0", []);
        await using var reader = await cmd.ExecuteReaderAsync();
        for (var i = 0; i < reader.FieldCount; i++) names.Add(reader.GetName(i));
        return names;
    }

    /// <summary>Compares the stored plan with reality: objects that should exist but were removed or never created are added to the plan.</summary>
    async Future AddRepairOps(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction? tx, string ns, SchemaDefinition desired, List<SchemaOp> ops)
    {
        foreach (var t in desired.Tables)
        {
            if (ops.Any(o => (o.Kind == SchemaOpKind.CreateTable && o.Table == t.Name) || (o.Kind == SchemaOpKind.RenameTable && o.NewName == t.Name))) continue;
            var phys = SqlSchema.Physical(ns, t.Name);
            if (!await TableExists(cfg, db, tx, phys))
            {
                ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateTable, Table = t.Name, TableDef = t, Note = "repair: table is missing" });
                foreach (var ix in t.Indexes) ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateIndex, Table = t.Name, Index = ix.Name, IndexDef = ix, Note = "repair" });
                continue;
            }
            var columns = await ColumnSet(cfg, db, tx, phys);
            foreach (var col in t.Columns.Where(x => !columns.Contains(x.Name)))
            {
                if (ops.Any(o => (o.Kind == SchemaOpKind.AddColumn && o.Column == col.Name && o.Table == t.Name) || (o.Kind == SchemaOpKind.RenameColumn && o.NewName == col.Name && o.Table == t.Name))) continue;
                ops.Add(new SchemaOp { Kind = SchemaOpKind.AddColumn, Table = t.Name, Column = col.Name, ColumnDef = col, Note = "repair: column is missing" });
            }
            var indexes = await NameSet(cfg, db, tx, dialect!.IndexNamesSql, phys);
            foreach (var ix in t.Indexes.Where(x => !indexes.Contains(SqlSchema.IndexName(phys, x.Name))))
            {
                if (ops.Any(o => o.Kind == SchemaOpKind.CreateIndex && o.Index == ix.Name && o.Table == t.Name)) continue;
                ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateIndex, Table = t.Name, Index = ix.Name, IndexDef = ix, Note = "repair: index is missing" });
            }
        }
    }

    // ------------------------------------------------------------------ apply

    async Future Apply(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction tx, string ns, SchemaOp op, List<SchemaOp> all, string mode, ApplyState state)
    {
        var d = dialect!;
        var phys = SqlSchema.Physical(ns, op.Table);
        if (op.TableDef is { } td && op.Kind is SchemaOpKind.AlterColumn or SchemaOpKind.DropColumn) state.Desired[op.Table] = td;

        switch (op.Kind)
        {
            case SchemaOpKind.RenameTable:
                var to = SqlSchema.Physical(ns, op.NewName!);
                if (!await TableExists(cfg, db, tx, phys) && await TableExists(cfg, db, tx, to)) break;
                await Run(cfg, db, tx, d.RenameTableSql(phys, to), []);
                break;
            case SchemaOpKind.RenameColumn:
                var cols = await ColumnSet(cfg, db, tx, phys);
                if (!cols.Contains(op.Column!) && cols.Contains(op.NewName!)) break;
                await Run(cfg, db, tx, d.RenameColumnSql(phys, op.Column!, op.NewName!), []);
                break;
            case SchemaOpKind.DropIndex:
                var ixName = SqlSchema.IndexName(phys, op.Index!);
                if ((await NameSet(cfg, db, tx, d.IndexNamesSql, phys)).Contains(ixName)) await Run(cfg, db, tx, d.DropIndexSql(phys, ixName), []);
                break;
            case SchemaOpKind.AddColumn:
                if ((await ColumnSet(cfg, db, tx, phys)).Contains(op.Column!)) break;
                foreach (var sql in d.AddColumnSql(phys, op.ColumnDef!)) await Run(cfg, db, tx, sql, []);
                break;
            case SchemaOpKind.AlterColumn:
                await Alter(cfg, db, tx, ns, phys, op, all, state);
                break;
            case SchemaOpKind.CreateTable:
                if (await TableExists(cfg, db, tx, phys)) break;
                await Run(cfg, db, tx, d.CreateTableSql(phys, op.TableDef!), []);
                break;
            case SchemaOpKind.CreateIndex:
                var newIx = SqlSchema.IndexName(phys, op.Index!);
                if ((await NameSet(cfg, db, tx, d.IndexNamesSql, phys)).Contains(newIx)) break;
                await Run(cfg, db, tx, d.CreateIndexSql(phys, newIx, op.IndexDef!), []);
                break;
            case SchemaOpKind.DropColumn:
                if (!(await ColumnSet(cfg, db, tx, phys)).Contains(op.Column!)) break;
                if (mode == "quarantine")
                {
                    var parked = Park(op.Column!, 63);
                    state.Parked[(op.Table, op.Column!)] = parked;
                    await Run(cfg, db, tx, d.RenameColumnSql(phys, op.Column!, parked), []);
                    if (op.OldColumnDef is { Nullable: false })
                    {
                        // Postgres makes the column nullable in place; SQLite rebuilds the table once after all operations.
                        if (d.AltersInPlace) await Run(cfg, db, tx, $"ALTER TABLE {d.Quote(phys)} ALTER COLUMN {d.Quote(parked)} DROP NOT NULL", []);
                        else state.NeedsRelax.Add(op.Table);
                    }
                    op.Note = $"quarantined as {parked}";
                }
                else await Run(cfg, db, tx, $"ALTER TABLE {d.Quote(phys)} DROP COLUMN {d.Quote(op.Column!)}", []);
                break;
            case SchemaOpKind.DropTable:
                if (mode == "quarantine")
                {
                    if (!await TableExists(cfg, db, tx, phys)) break;
                    var parked = Park(phys, 63);
                    await Run(cfg, db, tx, d.RenameTableSql(phys, parked), []);
                    op.Note = $"quarantined as {parked}";
                }
                else await Run(cfg, db, tx, d.DropTableSql(phys), []);
                break;
        }
    }

    static string Park(string name, int max)
    {
        var full = $"_dropped_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_" + name;
        return full.Length <= max ? full : full[..max];
    }

    async Future Alter(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction tx, string ns, string phys, SchemaOp op, List<SchemaOp> all, ApplyState state)
    {
        var d = dialect!;
        var old = op.OldColumnDef!; var nw = op.ColumnDef!;
        if (old.AutoIncrement != nw.AutoIncrement)
            throw new SqlRequestException(409, "unsupported_change", $"Column '{op.Table}.{nw.Name}': switching auto-increment on or off is not supported.");
        if (old.Type == ColumnTypes.String && nw.Type == ColumnTypes.String && nw.Length < old.Length)
        {
            var rows = await Query(cfg, db, tx, $"SELECT COUNT(*) FROM {d.Quote(phys)} WHERE {d.CharLengthFunction}({d.Quote(nw.Name)}) > $1", [(long)nw.Length!.Value]);
            if (Convert.ToInt64(rows[0][0], System.Globalization.CultureInfo.InvariantCulture) > 0)
                throw new SqlRequestException(409, "data_does_not_fit", $"Column '{op.Table}.{nw.Name}' holds values longer than {nw.Length}. Shorten them first.");
        }
        if (d.AltersInPlace)
        {
            foreach (var sql in d.AlterColumnSql(phys, old, nw)) await Run(cfg, db, tx, sql, []);
            return;
        }
        if (state.Rebuilt.Contains(op.Table)) return; // one rebuild already carries every column change of this table
        var carry = all.Where(o => o.Kind == SchemaOpKind.DropColumn && o.Table == op.Table).Select(o => o.OldColumnDef!);
        await Rebuild(cfg, db, tx, ns, op.TableDef!, carry, state);
    }

    async Future Rebuild(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction tx, string ns, SchemaTable desired, IEnumerable<SchemaColumn> carry, ApplyState state)
    {
        var phys = SqlSchema.Physical(ns, desired.Name);
        var existing = await ColumnSet(cfg, db, tx, phys);
        foreach (var sql in SqlSchema.RebuildStatements(dialect, phys, desired, existing, carry)) await Run(cfg, db, tx, sql, []);
        state.Rebuilt.Add(desired.Name);
    }
}
