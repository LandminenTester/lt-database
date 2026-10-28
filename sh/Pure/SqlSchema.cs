using System.Globalization;
using System.Text;

namespace LtDatabase;

/// <summary>Full column definition as declared by a bundle (lt-database-api docs/SCHEMA.md). <see cref="Default"/> is raw JSON text.</summary>
public sealed record SchemaColumn(
    string Name, string Type, int? Length = null, int? Precision = null, int? Scale = null, bool Nullable = false,
    string? Default = null, bool Unique = false, bool AutoIncrement = false, string? RenamedFrom = null);

public sealed record SchemaIndex(string Name, IReadOnlyList<string> Columns, bool Unique = false);

public sealed record SchemaTable(
    string Name, IReadOnlyList<SchemaColumn> Columns, IReadOnlyList<string> PrimaryKey, IReadOnlyList<SchemaIndex> Indexes, string? RenamedFrom = null)
{
    public SqlTable ToQuery() => new(Name,
        [.. Columns.Select(c => new SqlColumn(c.Name, c.Type, c.Length, c.Nullable, c.Default is not null, c.AutoIncrement))], PrimaryKey,
        [.. Indexes.Where(i => i.Unique).Select(i => i.Columns)]);
}

public sealed record SchemaDefinition(IReadOnlyList<SchemaTable> Tables);

public enum SchemaOpKind { RenameTable, RenameColumn, DropIndex, AddColumn, AlterColumn, CreateTable, CreateIndex, DropColumn, DropTable }

public sealed class SchemaOp
{
    public SchemaOpKind Kind { get; init; }
    public string Table { get; init; } = "";
    public string? Column { get; init; }
    public string? NewName { get; init; }
    public string? Index { get; init; }
    public bool Destructive { get; init; }
    public string? Note { get; set; }
    public SchemaTable? TableDef { get; init; }
    public SchemaColumn? ColumnDef { get; init; }
    public SchemaColumn? OldColumnDef { get; init; }
    public SchemaIndex? IndexDef { get; init; }

    /// <summary>Wire form, identical to the API's plan entries (kind in camelCase, null fields omitted).</summary>
    public string ToJson() => JsonText.Object(
        new (string, object?)[]
        {
            ("kind", char.ToLowerInvariant(Kind.ToString()[0]) + Kind.ToString()[1..]),
            ("table", Table),
            ("column", Column), ("newName", NewName), ("index", Index),
            ("destructive", Destructive),
            ("note", Note),
        }.Where(p => p.Item2 is not null).ToArray());
}

/// <summary>Validates and normalizes a desired schema exactly like the API, so equal declarations always hash identically.</summary>
public static class SqlSchema
{
    public const int MaxIndexedString = 255;

    static readonly HashSet<string> Types =
    [
        ColumnTypes.Int32, ColumnTypes.Int64, ColumnTypes.Decimal, ColumnTypes.Float64, ColumnTypes.Bool, ColumnTypes.String,
        ColumnTypes.Text, ColumnTypes.Json, ColumnTypes.DateTime, ColumnTypes.Date, ColumnTypes.Uuid, ColumnTypes.Bytes,
    ];

    // ------------------------------------------------------------------ names

    public static bool IsName(string? value, int max)
    {
        if (value is null || value.Length == 0 || value.Length > max || !char.IsAsciiLetterLower(value[0])) return false;
        var underscore = false;
        foreach (var c in value)
        {
            if (c == '_') { if (underscore) return false; underscore = true; continue; }
            underscore = false;
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)) return false;
        }
        return !underscore;
    }

    public static string Namespace(string? v) => Require(v, 24, "namespace");
    public static string Table(string? v) => Require(v, 36, "table");
    public static string Column(string? v) => Require(v, 48, "column");

    static string Require(string? v, int max, string what) =>
        IsName(v, max) ? v! : throw new SqlRequestException(400, "invalid_identifier", $"The {what} name is not valid. Use lower-case letters, digits and single underscores (max {max}).");

    public static string Physical(string ns, string table) => $"{ns}__{table}";

    /// <summary>Physical index name. It carries the table name because some engines keep index names in one namespace.</summary>
    public static string IndexName(string physicalTable, string index)
    {
        var name = $"ix_{physicalTable}_{index}";
        if (name.Length <= 60) return name;
        return "ix_" + Convert.ToHexString(Sha256.Hash(Encoding.UTF8.GetBytes(name)))[..24].ToLowerInvariant();
    }

    // ------------------------------------------------------------------ parsing

    public static SchemaDefinition Parse(object? schema)
    {
        if (schema is not Dictionary<string, object?> root || root.GetValueOrDefault("tables") is not List<object?> tables)
            throw Bad("The schema needs a tables list.");
        return new SchemaDefinition([.. tables.Select(ParseTable)]);
    }

    static SchemaTable ParseTable(object? t)
    {
        if (t is not Dictionary<string, object?> o) throw Bad("A table must be an object.");
        var columns = o.GetValueOrDefault("columns") is List<object?> cs ? cs.Select(ParseColumn).ToList() : [];
        var pk = o.GetValueOrDefault("primaryKey") is List<object?> p ? p.Select(x => x as string ?? throw Bad("Primary key entries must be text.")).ToList() : [];
        var indexes = o.GetValueOrDefault("indexes") is List<object?> ix ? ix.Select(ParseIndex).ToList() : [];
        return new SchemaTable(MiniJson.GetString(o, "name") ?? "", columns, pk, indexes, MiniJson.GetString(o, "renamedFrom"));
    }

    static SchemaColumn ParseColumn(object? c)
    {
        if (c is not Dictionary<string, object?> o) throw Bad("A column must be an object.");
        int? Int(string k) => o.TryGetValue(k, out var v) && v is not null ? v is long l && l is >= int.MinValue and <= int.MaxValue ? (int)l : throw Bad($"'{k}' must be a whole number.") : null;
        string? def = o.TryGetValue("default", out var d) && d is not null ? RawJson(d) : null;
        return new SchemaColumn(MiniJson.GetString(o, "name") ?? "", MiniJson.GetString(o, "type") ?? "", Int("length"), Int("precision"), Int("scale"),
            MiniJson.GetBool(o, "nullable") ?? false, def, MiniJson.GetBool(o, "unique") ?? false, MiniJson.GetBool(o, "autoIncrement") ?? false, MiniJson.GetString(o, "renamedFrom"));
    }

    static SchemaIndex ParseIndex(object? i)
    {
        if (i is not Dictionary<string, object?> o) throw Bad("An index must be an object.");
        var cols = o.GetValueOrDefault("columns") is List<object?> l ? l.Select(x => x as string ?? throw Bad("Index columns must be text.")).ToList() : [];
        return new SchemaIndex(MiniJson.GetString(o, "name") ?? "", cols, MiniJson.GetBool(o, "unique") ?? false);
    }

    /// <summary>Serialises a parsed JSON value back to compact text (used for column defaults).</summary>
    public static string RawJson(object? v) => v switch
    {
        Dictionary<string, object?> o => "{" + string.Join(",", o.Select(p => JsonText.Quote(p.Key) + ":" + RawJson(p.Value))) + "}",
        List<object?> a => "[" + string.Join(",", a.Select(RawJson)) + "]",
        _ => JsonText.Value(v),
    };

    // ------------------------------------------------------------------ normalization

    public static SchemaDefinition Normalize(SchemaDefinition input, int maxTables = 64)
    {
        if (input.Tables.Count > maxTables) throw Bad($"A namespace may define at most {maxTables} tables.");
        var seen = new HashSet<string>();
        var tables = new List<SchemaTable>();
        foreach (var t in input.Tables)
        {
            Table(t.Name);
            if (!seen.Add(t.Name)) throw Bad($"Table '{t.Name}' is defined twice.");
            if (t.RenamedFrom is not null) Table(t.RenamedFrom);
            tables.Add(NormalizeTable(t));
        }
        return new SchemaDefinition(tables);
    }

    static SchemaTable NormalizeTable(SchemaTable t)
    {
        if (t.Columns.Count == 0) throw Bad($"Table '{t.Name}' needs at least one column.");
        if (t.Columns.Count > 256) throw Bad($"Table '{t.Name}' has more than 256 columns.");
        var columns = new List<SchemaColumn>();
        var names = new HashSet<string>();
        var indexes = new List<SchemaIndex>(t.Indexes);
        foreach (var c in t.Columns)
        {
            Column(c.Name);
            if (!names.Add(c.Name)) throw Bad($"Column '{t.Name}.{c.Name}' is defined twice.");
            if (c.RenamedFrom is not null) Column(c.RenamedFrom);
            var n = NormalizeColumn(t.Name, c);
            if (c.Unique) indexes.Add(new SchemaIndex("uq_" + c.Name, [c.Name], true));
            columns.Add(n with { Unique = false });
        }

        var pk = t.PrimaryKey;
        if (pk.Distinct().Count() != pk.Count) throw Bad($"Table '{t.Name}' lists a primary key column twice.");
        foreach (var k in pk)
        {
            var col = columns.FirstOrDefault(x => x.Name == k) ?? throw Bad($"Primary key column '{t.Name}.{k}' does not exist.");
            if (col.Nullable) throw Bad($"Primary key column '{t.Name}.{k}' cannot be nullable.");
            if (col.Type is ColumnTypes.Json or ColumnTypes.Text) throw Bad($"Primary key column '{t.Name}.{k}' has a type that cannot be indexed.");
            if (col.Type == ColumnTypes.String && col.Length > MaxIndexedString) throw Bad($"Primary key column '{t.Name}.{k}': indexed strings may be at most {MaxIndexedString} characters.");
        }
        foreach (var c in columns.Where(x => x.AutoIncrement))
            if (pk.Count != 1 || pk[0] != c.Name) throw Bad($"Auto-increment column '{t.Name}.{c.Name}' must be the only primary key column.");

        if (indexes.Count > 32) throw Bad($"Table '{t.Name}' has more than 32 indexes.");
        var indexNames = new HashSet<string>();
        var normalized = new List<SchemaIndex>();
        foreach (var ix in indexes)
        {
            Column(ix.Name);
            if (!indexNames.Add(ix.Name)) throw Bad($"Index '{t.Name}.{ix.Name}' is defined twice.");
            if (ix.Columns.Count is 0 or > 8) throw Bad($"Index '{t.Name}.{ix.Name}' needs 1 to 8 columns.");
            foreach (var cn in ix.Columns)
            {
                var col = columns.FirstOrDefault(x => x.Name == cn) ?? throw Bad($"Index '{t.Name}.{ix.Name}' uses unknown column '{cn}'.");
                if (col.Type is ColumnTypes.Json or ColumnTypes.Text || (col.Type == ColumnTypes.Bytes && col.Length is null))
                    throw Bad($"Index '{t.Name}.{ix.Name}' uses column '{cn}', whose type cannot be indexed.");
                if (col.Type == ColumnTypes.String && col.Length > MaxIndexedString)
                    throw Bad($"Index '{t.Name}.{ix.Name}': indexed strings may be at most {MaxIndexedString} characters ('{cn}' has {col.Length}).");
            }
            normalized.Add(ix with { Columns = [.. ix.Columns] });
        }
        return t with { Columns = columns, PrimaryKey = [.. pk], Indexes = normalized };
    }

    static SchemaColumn NormalizeColumn(string table, SchemaColumn c)
    {
        var where = $"{table}.{c.Name}";
        if (!Types.Contains(c.Type)) throw Bad($"Column '{where}' has unknown type '{c.Type}'.");
        int? length = null, precision = null, scale = null;
        switch (c.Type)
        {
            case ColumnTypes.String:
                length = c.Length ?? 255;
                if (length is < 1 or > 4000) throw Bad($"Column '{where}': string length must be between 1 and 4000.");
                break;
            case ColumnTypes.Bytes:
                length = c.Length;
                if (length is < 1 or > 65535) throw Bad($"Column '{where}': bytes length must be between 1 and 65535.");
                break;
            case ColumnTypes.Decimal:
                precision = c.Precision ?? 18; scale = c.Scale ?? 0;
                if (precision is < 1 or > 38) throw Bad($"Column '{where}': precision must be between 1 and 38.");
                if (scale < 0 || scale > precision) throw Bad($"Column '{where}': scale must be between 0 and the precision.");
                break;
        }
        if (c.AutoIncrement && c.Type is not (ColumnTypes.Int32 or ColumnTypes.Int64)) throw Bad($"Column '{where}': only int32 and int64 can auto-increment.");
        if (c.AutoIncrement && c.Nullable) throw Bad($"Column '{where}': an auto-increment column cannot be nullable.");
        if (c.Default is not null) ValidateDefault(where, c.Type, c.Default);
        return c with { Length = length, Precision = precision, Scale = scale };
    }

    static void ValidateDefault(string where, string type, string raw)
    {
        var ok = MiniJson.TryParse(raw, out var d) && d is not null && type switch
        {
            ColumnTypes.Int32 or ColumnTypes.Int64 => d is long,
            ColumnTypes.Decimal or ColumnTypes.Float64 => d is long or double,
            ColumnTypes.Bool => d is bool,
            ColumnTypes.String or ColumnTypes.Text => d is string s && IsSafeText(s),
            ColumnTypes.DateTime => d is string s && (s == "now" || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _)),
            ColumnTypes.Date => d is string s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            ColumnTypes.Uuid => d is string s && Guid.TryParse(s, out _),
            ColumnTypes.Json => IsSafeText(raw),
            _ => false,
        };
        if (!ok) throw Bad($"Column '{where}': the default value does not fit the column type.");
    }

    static bool IsSafeText(string value) => value.Length <= 255 && !value.Any(char.IsControl);

    // ------------------------------------------------------------------ canonical form and hash

    /// <summary>Sorted copy without rename hints. This is also the form stored as "previous schema".</summary>
    public static SchemaDefinition Canonicalize(SchemaDefinition s) => new(
        [.. s.Tables.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => t with
        {
            RenamedFrom = null,
            Columns = [.. t.Columns.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => c with { RenamedFrom = null })],
            Indexes = [.. t.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal)],
        })]);

    /// <summary>
    /// The exact JSON the API stores and hashes (camelCase, null members omitted, record member order), so a database that was
    /// created through lt-database-api can be taken over by this backend and the other way around.
    /// </summary>
    public static string ToCanonicalJson(SchemaDefinition s)
    {
        var sb = new StringBuilder("{\"tables\":[");
        var firstTable = true;
        foreach (var t in Canonicalize(s).Tables)
        {
            if (!firstTable) sb.Append(',');
            firstTable = false;
            sb.Append("{\"name\":").Append(Q(t.Name)).Append(",\"columns\":[");
            sb.Append(string.Join(",", t.Columns.Select(ColumnJson)));
            sb.Append("],\"primaryKey\":[").Append(string.Join(",", t.PrimaryKey.Select(Q))).Append("],\"indexes\":[");
            sb.Append(string.Join(",", t.Indexes.Select(i => $"{{\"name\":{Q(i.Name)},\"columns\":[{string.Join(",", i.Columns.Select(Q))}],\"unique\":{(i.Unique ? "true" : "false")}}}")));
            sb.Append("]}");
        }
        return sb.Append("]}").ToString();
    }

    static string ColumnJson(SchemaColumn c)
    {
        var sb = new StringBuilder("{\"name\":").Append(Q(c.Name)).Append(",\"type\":").Append(Q(c.Type));
        if (c.Length is { } l) sb.Append(",\"length\":").Append(l.ToString(CultureInfo.InvariantCulture));
        if (c.Precision is { } p) sb.Append(",\"precision\":").Append(p.ToString(CultureInfo.InvariantCulture));
        if (c.Scale is { } sc) sb.Append(",\"scale\":").Append(sc.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"nullable\":").Append(c.Nullable ? "true" : "false");
        if (c.Default is not null) sb.Append(",\"default\":").Append(CanonicalDefault(c.Default));
        sb.Append(",\"unique\":").Append(c.Unique ? "true" : "false");
        sb.Append(",\"autoIncrement\":").Append(c.AutoIncrement ? "true" : "false");
        return sb.Append('}').ToString();
    }

    static string CanonicalDefault(string raw) => MiniJson.TryParse(raw, out var v) && v is string s ? Q(s) : raw;

    /// <summary>JSON string quoting as System.Text.Json writes it by default: ASCII-only output, HTML-sensitive characters escaped.</summary>
    static string Q(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20 || ch > 0x7E || ch is '<' or '>' or '&' or '\'' or '+') sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public static string Hash(SchemaDefinition normalized) =>
        Convert.ToHexString(Sha256.Hash(Encoding.UTF8.GetBytes(ToCanonicalJson(normalized)))).ToLowerInvariant();

    /// <summary>Reads a stored definition (as written by this backend or by the API).</summary>
    public static SchemaDefinition ParseStored(string json) => Parse(MiniJson.Parse(json));

    // ------------------------------------------------------------------ rebuild (engines that cannot alter a column in place)

    /// <summary>
    /// Statements that replace a table by its desired definition and copy the data. Columns that exist physically but are not part of
    /// the desired schema (removed ones that are only quarantined) are carried along as nullable columns, so no data is lost.
    /// </summary>
    public static IEnumerable<string> RebuildStatements(SqlDialect dialect, string physical, SchemaTable desired, IReadOnlyCollection<string> existing, IEnumerable<SchemaColumn> carry)
    {
        var extras = carry.Where(c => existing.Contains(c.Name) && desired.Columns.All(d => d.Name != c.Name)).Select(c => c with { Nullable = true, AutoIncrement = false, Unique = false }).ToList();
        var target = desired with { Columns = [.. desired.Columns, .. extras] };
        var copy = target.Columns.Where(c => existing.Contains(c.Name)).Select(c => (c.Name, c.Name)).ToList();
        return dialect.RebuildSql(physical, target, copy);
    }

    // ------------------------------------------------------------------ diff

    /// <summary>Compares the previously applied schema with the desired one and produces an ordered plan. Renames need an explicit RenamedFrom hint.</summary>
    public static List<SchemaOp> Diff(SchemaDefinition? previous, SchemaDefinition desired)
    {
        var ops = new List<SchemaOp>();
        var prevTables = (previous?.Tables ?? []).ToDictionary(t => t.Name);
        var desiredNames = desired.Tables.Select(t => t.Name).ToHashSet();
        var matchedPrev = new HashSet<string>();

        foreach (var d in desired.Tables)
        {
            SchemaTable? p = null;
            if (prevTables.TryGetValue(d.Name, out var same)) p = same;
            else if (d.RenamedFrom is { } from && prevTables.TryGetValue(from, out var old) && !desiredNames.Contains(from) && !matchedPrev.Contains(from))
            {
                p = old;
                ops.Add(new SchemaOp { Kind = SchemaOpKind.RenameTable, Table = from, NewName = d.Name, Note = $"rename table {from} to {d.Name}" });
            }

            if (p is null)
            {
                ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateTable, Table = d.Name, TableDef = d });
                foreach (var ix in d.Indexes) ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateIndex, Table = d.Name, Index = ix.Name, IndexDef = ix });
                continue;
            }
            matchedPrev.Add(p.Name);
            DiffTable(ops, p, d);
        }

        foreach (var p in prevTables.Values.Where(p => !matchedPrev.Contains(p.Name)))
            ops.Add(new SchemaOp { Kind = SchemaOpKind.DropTable, Table = p.Name, Destructive = true, TableDef = p, Note = "table removed from the schema" });

        return [.. ops.OrderBy(o => (int)o.Kind)];
    }

    static void DiffTable(List<SchemaOp> ops, SchemaTable p, SchemaTable d)
    {
        var table = d.Name;
        if (!p.PrimaryKey.SequenceEqual(d.PrimaryKey))
            throw new SqlRequestException(409, "unsupported_change", $"Table '{table}': changing the primary key is not supported. Create a new table and migrate the data.");

        var prevCols = p.Columns.ToDictionary(c => c.Name);
        var desiredColNames = d.Columns.Select(c => c.Name).ToHashSet();
        var consumed = new HashSet<string>();

        foreach (var dc in d.Columns)
        {
            if (prevCols.TryGetValue(dc.Name, out var pc))
            {
                consumed.Add(pc.Name);
                if (!Same(pc, dc))
                    ops.Add(new SchemaOp { Kind = SchemaOpKind.AlterColumn, Table = table, Column = dc.Name, ColumnDef = dc, OldColumnDef = pc, TableDef = d, Destructive = Narrows(pc, dc) });
            }
            else if (dc.RenamedFrom is { } from && prevCols.TryGetValue(from, out var renamed) && !desiredColNames.Contains(from) && !consumed.Contains(from))
            {
                consumed.Add(from);
                ops.Add(new SchemaOp { Kind = SchemaOpKind.RenameColumn, Table = table, Column = from, NewName = dc.Name, Note = $"rename column {from} to {dc.Name}" });
                if (!Same(renamed with { Name = dc.Name }, dc))
                    ops.Add(new SchemaOp { Kind = SchemaOpKind.AlterColumn, Table = table, Column = dc.Name, ColumnDef = dc, OldColumnDef = renamed with { Name = dc.Name }, TableDef = d, Destructive = Narrows(renamed, dc) });
            }
            else ops.Add(new SchemaOp { Kind = SchemaOpKind.AddColumn, Table = table, Column = dc.Name, ColumnDef = dc });
        }
        foreach (var pc in p.Columns.Where(c => !consumed.Contains(c.Name)))
            ops.Add(new SchemaOp { Kind = SchemaOpKind.DropColumn, Table = table, Column = pc.Name, OldColumnDef = pc, TableDef = d, Destructive = true, Note = "column removed from the schema" });

        var prevIx = p.Indexes.ToDictionary(i => i.Name);
        var desiredIx = d.Indexes.ToDictionary(i => i.Name);
        foreach (var (name, ix) in desiredIx)
        {
            if (!prevIx.TryGetValue(name, out var old))
                ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateIndex, Table = table, Index = name, IndexDef = ix });
            else if (old.Unique != ix.Unique || !old.Columns.SequenceEqual(ix.Columns))
            {
                ops.Add(new SchemaOp { Kind = SchemaOpKind.DropIndex, Table = table, Index = name, IndexDef = old });
                ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateIndex, Table = table, Index = name, IndexDef = ix });
            }
        }
        foreach (var (name, ix) in prevIx.Where(x => !desiredIx.ContainsKey(x.Key)))
            ops.Add(new SchemaOp { Kind = SchemaOpKind.DropIndex, Table = table, Index = name, IndexDef = ix });
    }

    static bool Same(SchemaColumn a, SchemaColumn b) =>
        a.Type == b.Type && a.Length == b.Length && a.Precision == b.Precision && a.Scale == b.Scale
        && a.Nullable == b.Nullable && a.AutoIncrement == b.AutoIncrement && (a.Default ?? "") == (b.Default ?? "");

    /// <summary>A change that can lose or reject existing data: another type, a shorter or less precise column, or NOT NULL.</summary>
    public static bool Narrows(SchemaColumn o, SchemaColumn n)
    {
        if (o.Type != n.Type) return !(o.Type == ColumnTypes.Int32 && n.Type == ColumnTypes.Int64) && !(o.Type == ColumnTypes.String && n.Type == ColumnTypes.Text);
        if (o.Length is { } ol && n.Length is { } nl && nl < ol) return true;
        if (o.Precision is { } op && n.Precision is { } np && (np < op || n.Scale < o.Scale)) return true;
        return o.Nullable && !n.Nullable;
    }

    static SqlRequestException Bad(string message) => new(400, "invalid_schema", message);
}
