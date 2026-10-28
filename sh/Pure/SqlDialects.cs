using System.Globalization;
using System.Text;

namespace LtDatabase;

/// <summary>Everything that differs between the two engines re.sql supports. Statements use $1, $2, ... placeholders.</summary>
public abstract class SqlDialect
{
    public static readonly SqlDialect Postgres = new PostgresDialect();
    public static readonly SqlDialect Sqlite = new SqliteDialect();

    public abstract string Name { get; }

    public string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>Wraps a parameter that needs an explicit cast (jsonb on Postgres).</summary>
    public virtual string ParamExpr(SqlColumn c, string parameter) => parameter;

    /// <summary>Converts a coerced value to what the engine stores for this column type.</summary>
    public abstract object ToDriver(SqlColumn c, object value);

    /// <summary>Converts a value read back from the engine to the JSON-ready form (inverse of ToDriver).</summary>
    public virtual object? FromDriver(SqlColumn c, object? value) => SqlValues.ToJson(c, value);

    public abstract string LikeEscape { get; }

    // ------------------------------------------------------------------ DDL

    public abstract string TypeSql(SchemaColumn c);
    /// <summary>Appended after the type, before NOT NULL.</summary>
    public abstract string IdentitySql(SchemaColumn c);
    public abstract string DefaultSql(SchemaColumn c);
    public abstract IEnumerable<string> AddColumnSql(string table, SchemaColumn c);
    public abstract string RenameColumnSql(string table, string from, string to);
    public abstract string RenameTableSql(string from, string to);
    public abstract string DropIndexSql(string table, string index);

    /// <summary>True when the engine can change a column in place. SQLite cannot and rebuilds the table instead.</summary>
    public abstract bool AltersInPlace { get; }
    public abstract IEnumerable<string> AlterColumnSql(string table, SchemaColumn old, SchemaColumn nw);

    public abstract string TableExistsSql { get; }
    public abstract string ColumnNamesSql { get; }
    public abstract string IndexNamesSql { get; }
    public abstract string MetaTableSql { get; }
    public abstract string ReceiptTableSql { get; }

    /// <summary>Length check used before a string column is shortened.</summary>
    public virtual string CharLengthFunction => "CHAR_LENGTH";

    public abstract string UpsertSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> values, IReadOnlyList<string> keys, IReadOnlyList<string> updateColumns);

    public string ColumnSql(SchemaColumn c)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(c.Name)).Append(' ').Append(TypeSql(c));
        var id = c.AutoIncrement ? IdentitySql(c) : "";
        if (id.Length > 0) sb.Append(' ').Append(id);
        sb.Append(c.Nullable ? " NULL" : " NOT NULL");
        if (!c.AutoIncrement && c.Default is not null) sb.Append(" DEFAULT ").Append(DefaultSql(c));
        return sb.ToString();
    }

    public virtual string CreateTableSql(string table, SchemaTable t)
    {
        var parts = t.Columns.Select(ColumnSql).ToList();
        if (t.PrimaryKey.Count > 0) parts.Add($"PRIMARY KEY ({string.Join(", ", t.PrimaryKey.Select(Quote))})");
        return $"CREATE TABLE {Quote(table)} ({string.Join(", ", parts)})";
    }

    public string CreateIndexSql(string table, string indexName, SchemaIndex ix) =>
        $"CREATE {(ix.Unique ? "UNIQUE " : "")}INDEX {Quote(indexName)} ON {Quote(table)} ({string.Join(", ", ix.Columns.Select(Quote))})";

    public string DropTableSql(string table) => $"DROP TABLE IF EXISTS {Quote(table)}";

    /// <summary>
    /// Replaces a table by a new definition and copies the data over (used where the engine cannot alter a column in place).
    /// Every statement belongs into one transaction.
    /// </summary>
    public IEnumerable<string> RebuildSql(string physical, SchemaTable target, IReadOnlyList<(string To, string From)> copy)
    {
        var temp = physical + "__rebuild";
        yield return DropTableSql(temp);
        yield return CreateTableSql(temp, target);
        if (copy.Count > 0)
            yield return $"INSERT INTO {Quote(temp)} ({string.Join(", ", copy.Select(c => Quote(c.To)))}) SELECT {string.Join(", ", copy.Select(c => Quote(c.From)))} FROM {Quote(physical)}";
        yield return DropTableSql(physical);
        yield return RenameTableSql(temp, physical);
        foreach (var ix in target.Indexes) yield return CreateIndexSql(physical, SqlSchema.IndexName(physical, ix.Name), ix);
    }

    protected static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    protected static string Number(string raw) => raw.Contains('e', StringComparison.OrdinalIgnoreCase)
        ? double.Parse(raw, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)
        : raw;

    protected static string Text(string rawDefault) => MiniJson.Parse(rawDefault) as string ?? "";
}

sealed class PostgresDialect : SqlDialect
{
    public override string Name => "postgres";
    public override string LikeEscape => "ESCAPE '\\'";
    public override bool AltersInPlace => true;

    // The host binds plain primitives only (text, whole numbers, doubles, booleans, bytes). Everything else travels as text and the
    // server converts it, so the result does not depend on how a driver would have mapped a .NET type.
    public override string ParamExpr(SqlColumn c, string parameter) => c.Type switch
    {
        ColumnTypes.Json => $"CAST({parameter} AS jsonb)",
        ColumnTypes.DateTime => $"CAST({parameter} AS timestamptz)",
        ColumnTypes.Date => $"CAST({parameter} AS date)",
        ColumnTypes.Uuid => $"CAST({parameter} AS uuid)",
        ColumnTypes.Decimal => $"CAST({parameter} AS numeric)",
        _ => parameter,
    };

    public override object ToDriver(SqlColumn c, object value) => value switch
    {
        int i => (long)i,
        DateTime d => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        _ => value,
    };

    public override string TypeSql(SchemaColumn c) => c.Type switch
    {
        ColumnTypes.Int32 => "INTEGER",
        ColumnTypes.Int64 => "BIGINT",
        ColumnTypes.Decimal => $"NUMERIC({c.Precision},{c.Scale})",
        ColumnTypes.Float64 => "DOUBLE PRECISION",
        ColumnTypes.Bool => "BOOLEAN",
        ColumnTypes.String => $"VARCHAR({c.Length})",
        ColumnTypes.Text => "TEXT",
        ColumnTypes.Json => "JSONB",
        ColumnTypes.DateTime => "TIMESTAMPTZ",
        ColumnTypes.Date => "DATE",
        ColumnTypes.Uuid => "UUID",
        ColumnTypes.Bytes => "BYTEA",
        _ => throw new NotSupportedException(c.Type),
    };

    public override string IdentitySql(SchemaColumn c) => "GENERATED BY DEFAULT AS IDENTITY";

    public override string UpsertSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> values, IReadOnlyList<string> keys, IReadOnlyList<string> updateColumns)
    {
        var action = updateColumns.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", updateColumns.Select(c => $"{Quote(c)} = EXCLUDED.{Quote(c)}"));
        return $"INSERT INTO {Quote(table)} ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", values)}) ON CONFLICT ({string.Join(", ", keys.Select(Quote))}) {action}";
    }

    public override string TableExistsSql => "SELECT 1 FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = $1";
    public override string ColumnNamesSql => "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = $1";
    public override string IndexNamesSql => "SELECT indexname FROM pg_indexes WHERE schemaname = current_schema() AND tablename = $1";

    public override string MetaTableSql => "CREATE TABLE IF NOT EXISTS \"_ldb_schema\" (ns VARCHAR(24) NOT NULL PRIMARY KEY, hash VARCHAR(64) NOT NULL, definition TEXT NOT NULL, updated_at VARCHAR(40) NOT NULL)";

    public override string ReceiptTableSql =>
        "CREATE TABLE IF NOT EXISTS \"_ldb_mutation_receipts\" (ns VARCHAR(24) COLLATE \"C\" NOT NULL, effect_id VARCHAR(128) COLLATE \"C\" NOT NULL, operation VARCHAR(8) NOT NULL, fingerprint VARCHAR(64) NOT NULL, state VARCHAR(16) NOT NULL, code VARCHAR(64) NOT NULL, result_json TEXT NOT NULL, PRIMARY KEY (ns, effect_id))";

    public override string DefaultSql(SchemaColumn c)
    {
        var d = c.Default!;
        return c.Type switch
        {
            ColumnTypes.Int32 or ColumnTypes.Int64 or ColumnTypes.Decimal or ColumnTypes.Float64 => Number(d),
            ColumnTypes.Bool => d == "true" ? "TRUE" : "FALSE",
            ColumnTypes.DateTime => Text(d) == "now" ? "CURRENT_TIMESTAMP" : Literal(Text(d)) + "::timestamptz",
            ColumnTypes.Date => Literal(Text(d)) + "::date",
            ColumnTypes.Uuid => Literal(Text(d)) + "::uuid",
            ColumnTypes.Json => Literal(d) + "::jsonb",
            _ => Literal(Text(d)),
        };
    }

    static string Implicit(SchemaColumn c) => c.Type switch
    {
        ColumnTypes.Int32 or ColumnTypes.Int64 or ColumnTypes.Decimal or ColumnTypes.Float64 => "0",
        ColumnTypes.Bool => "FALSE",
        ColumnTypes.Json => "'null'::jsonb",
        ColumnTypes.DateTime => "CURRENT_TIMESTAMP",
        ColumnTypes.Date => "CURRENT_DATE",
        ColumnTypes.Uuid => "'00000000-0000-0000-0000-000000000000'::uuid",
        ColumnTypes.Bytes => "''::bytea",
        _ => "''",
    };

    public override IEnumerable<string> AddColumnSql(string table, SchemaColumn c)
    {
        if (c.Nullable || c.Default is not null || c.AutoIncrement)
        {
            yield return $"ALTER TABLE {Quote(table)} ADD COLUMN {ColumnSql(c)}";
            yield break;
        }
        // A NOT NULL column without default would fail on a table that already holds rows, so give existing rows a neutral value first.
        yield return $"ALTER TABLE {Quote(table)} ADD COLUMN {ColumnSql(c)} DEFAULT {Implicit(c)}";
        yield return $"ALTER TABLE {Quote(table)} ALTER COLUMN {Quote(c.Name)} DROP DEFAULT";
    }

    public override IEnumerable<string> AlterColumnSql(string table, SchemaColumn old, SchemaColumn nw)
    {
        var t = Quote(table); var c = Quote(nw.Name);
        var typeChanged = TypeSql(old) != TypeSql(nw);
        var defaultChanged = (old.Default ?? "") != (nw.Default ?? "");
        if (typeChanged || defaultChanged) yield return $"ALTER TABLE {t} ALTER COLUMN {c} DROP DEFAULT";
        if (typeChanged) yield return $"ALTER TABLE {t} ALTER COLUMN {c} TYPE {TypeSql(nw)} USING {c}::{TypeSql(nw)}";
        if ((typeChanged || defaultChanged) && nw.Default is not null) yield return $"ALTER TABLE {t} ALTER COLUMN {c} SET DEFAULT {DefaultSql(nw)}";
        if (old.Nullable != nw.Nullable) yield return $"ALTER TABLE {t} ALTER COLUMN {c} {(nw.Nullable ? "DROP" : "SET")} NOT NULL";
    }

    public override string RenameColumnSql(string table, string from, string to) => $"ALTER TABLE {Quote(table)} RENAME COLUMN {Quote(from)} TO {Quote(to)}";
    public override string RenameTableSql(string from, string to) => $"ALTER TABLE {Quote(from)} RENAME TO {Quote(to)}";
    public override string DropIndexSql(string table, string index) => $"DROP INDEX IF EXISTS {Quote(index)}";
}

sealed class SqliteDialect : SqlDialect
{
    public override string Name => "sqlite";
    public override string LikeEscape => "ESCAPE '\\'";
    public override bool AltersInPlace => false;
    public override string CharLengthFunction => "LENGTH";

    // SQLite has no boolean, date or uuid storage class: booleans are 0/1, dates ISO text, uuids lower-case text, bytes BLOB,
    // and integers always travel as Int64.
    public override object ToDriver(SqlColumn c, object value) => value switch
    {
        bool b => b ? 1L : 0L,
        int i => (long)i,
        DateTime d => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        _ => value,
    };

    public override object? FromDriver(SqlColumn c, object? value)
    {
        if (value is null or DBNull) return null;
        return c.Type switch
        {
            ColumnTypes.DateTime when value is string s => SqlValues.ToJson(c, DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)),
            ColumnTypes.Date when value is string s => s,
            _ => SqlValues.ToJson(c, value),
        };
    }

    // The declared length of string columns is enforced by the bundle (SqlValues), SQLite itself stores any length.
    public override string TypeSql(SchemaColumn c) => c.Type switch
    {
        ColumnTypes.Int32 or ColumnTypes.Int64 or ColumnTypes.Bool => "INTEGER",
        ColumnTypes.Float64 => "REAL",
        ColumnTypes.Decimal => "NUMERIC",
        ColumnTypes.Bytes => "BLOB",
        _ => "TEXT",
    };

    public override string IdentitySql(SchemaColumn c) => "PRIMARY KEY AUTOINCREMENT";

    public override string CreateTableSql(string table, SchemaTable t)
    {
        // AUTOINCREMENT is only valid on an inline "INTEGER PRIMARY KEY", so such a column carries the primary key itself.
        var inlineKey = t.Columns.Any(c => c.AutoIncrement);
        var parts = t.Columns.Select(ColumnSql).ToList();
        if (!inlineKey && t.PrimaryKey.Count > 0) parts.Add($"PRIMARY KEY ({string.Join(", ", t.PrimaryKey.Select(Quote))})");
        return $"CREATE TABLE {Quote(table)} ({string.Join(", ", parts)})";
    }

    public override string UpsertSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> values, IReadOnlyList<string> keys, IReadOnlyList<string> updateColumns)
    {
        var action = updateColumns.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", updateColumns.Select(c => $"{Quote(c)} = excluded.{Quote(c)}"));
        return $"INSERT INTO {Quote(table)} ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", values)}) ON CONFLICT ({string.Join(", ", keys.Select(Quote))}) {action}";
    }

    public override string TableExistsSql => "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $1";
    public override string ColumnNamesSql => "SELECT name FROM pragma_table_info($1)";
    public override string IndexNamesSql => "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = $1";

    public override string MetaTableSql => "CREATE TABLE IF NOT EXISTS \"_ldb_schema\" (ns TEXT NOT NULL PRIMARY KEY, hash TEXT NOT NULL, definition TEXT NOT NULL, updated_at TEXT NOT NULL)";

    public override string ReceiptTableSql =>
        "CREATE TABLE IF NOT EXISTS \"_ldb_mutation_receipts\" (ns TEXT NOT NULL, effect_id TEXT NOT NULL, operation TEXT NOT NULL, fingerprint TEXT NOT NULL, state TEXT NOT NULL, code TEXT NOT NULL, result_json TEXT NOT NULL, PRIMARY KEY (ns, effect_id))";

    public override string DefaultSql(SchemaColumn c)
    {
        var d = c.Default!;
        return c.Type switch
        {
            ColumnTypes.Int32 or ColumnTypes.Int64 or ColumnTypes.Decimal or ColumnTypes.Float64 => Number(d),
            ColumnTypes.Bool => d == "true" ? "1" : "0",
            ColumnTypes.DateTime => Text(d) == "now" ? "(strftime('%Y-%m-%dT%H:%M:%f000Z', 'now'))" : Literal(IsoDateTime(Text(d))),
            ColumnTypes.Uuid => Literal(Text(d).ToLowerInvariant()),
            ColumnTypes.Json => Literal(d),
            _ => Literal(Text(d)),
        };
    }

    static string IsoDateTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    static string Implicit(SchemaColumn c) => c.Type switch
    {
        ColumnTypes.Int32 or ColumnTypes.Int64 or ColumnTypes.Decimal or ColumnTypes.Float64 or ColumnTypes.Bool => "0",
        ColumnTypes.Json => "'null'",
        ColumnTypes.DateTime => "(strftime('%Y-%m-%dT%H:%M:%f000Z', 'now'))",
        ColumnTypes.Date => "(date('now'))",
        ColumnTypes.Uuid => "'00000000-0000-0000-0000-000000000000'",
        ColumnTypes.Bytes => "x''",
        _ => "''",
    };

    // SQLite refuses NOT NULL columns without a default on ADD COLUMN, and cannot drop a default again without a rebuild.
    // Existing rows get a neutral value, and the bundle still demands a value for every new row because it checks the declared schema.
    public override IEnumerable<string> AddColumnSql(string table, SchemaColumn c)
    {
        if (c.Nullable || c.Default is not null || c.AutoIncrement)
            yield return $"ALTER TABLE {Quote(table)} ADD COLUMN {ColumnSql(c)}";
        else
            yield return $"ALTER TABLE {Quote(table)} ADD COLUMN {ColumnSql(c)} DEFAULT {Implicit(c)}";
    }

    public override IEnumerable<string> AlterColumnSql(string table, SchemaColumn old, SchemaColumn nw) =>
        throw new NotSupportedException("SQLite alters a column by rebuilding the table.");

    public override string RenameColumnSql(string table, string from, string to) => $"ALTER TABLE {Quote(table)} RENAME COLUMN {Quote(from)} TO {Quote(to)}";
    public override string RenameTableSql(string from, string to) => $"ALTER TABLE {Quote(from)} RENAME TO {Quote(to)}";
    public override string DropIndexSql(string table, string index) => $"DROP INDEX IF EXISTS {Quote(index)}";
}
