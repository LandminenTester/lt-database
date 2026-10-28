using System.Text;

namespace LtDatabase;

/// <summary>
/// Translates lt-database-api requests (already parsed by <see cref="MiniJson"/>) into parameterized SQL. Column names only ever
/// come from the table model, values only ever travel as bind parameters.
/// </summary>
public sealed class SqlStatements(SqlDialect dialect)
{
    const int MaxDepth = 8, MaxNodes = 128, MaxListItems = 1000, MaxTextLength = 1024;

    static readonly HashSet<string> Ops = ["eq", "ne", "gt", "gte", "lt", "lte", "in", "nin", "isNull", "startsWith", "contains"];

    // ------------------------------------------------------------------ filter

    /// <summary>Returns null for a missing or empty filter ("match everything").</summary>
    public string? Where(object? filter, SqlTable table, SqlParams ps)
    {
        if (filter is null) return null;
        if (filter is not Dictionary<string, object?> o) throw Bad("invalid_filter", "A filter must be an object.");
        if (o.Count == 0) return null;
        var nodes = 0;
        return Node(o, 1, ref nodes, table, ps);
    }

    string Node(object? e, int depth, ref int nodes, SqlTable table, SqlParams ps)
    {
        if (depth > MaxDepth) throw Bad("invalid_filter", $"Filters may be nested at most {MaxDepth} levels deep.");
        if (++nodes > MaxNodes) throw Bad("invalid_filter", $"Filters may contain at most {MaxNodes} conditions.");
        if (e is not Dictionary<string, object?> o) throw Bad("invalid_filter", "A filter condition must be an object.");

        if (o.Count == 1)
        {
            var (key, value) = o.First();
            if (key is "and" or "or")
            {
                if (value is not List<object?> list || list.Count == 0) throw Bad("invalid_filter", $"'{key}' needs a non-empty list.");
                var parts = new List<string>();
                foreach (var item in list) parts.Add(Node(item, depth + 1, ref nodes, table, ps));
                return "(" + string.Join(key == "and" ? " AND " : " OR ", parts) + ")";
            }
            if (key == "not") return "(NOT " + Node(value, depth + 1, ref nodes, table, ps) + ")";
        }

        string? field = null, op = null; object? val = null; var hasValue = false;
        foreach (var (key, value) in o)
        {
            switch (key)
            {
                case "field": field = value as string; break;
                case "op": op = value as string; break;
                case "value": val = value; hasValue = true; break;
                default: throw Bad("invalid_filter", $"Unknown filter key '{key}'.");
            }
        }
        if (field is null || op is null) throw Bad("invalid_filter", "A condition needs 'field' and 'op'.");
        if (!Ops.Contains(op)) throw Bad("invalid_filter", $"Unknown filter operator '{op}'.");
        return Compare(Column(table, field), op, val, hasValue, ps);
    }

    string Compare(SqlColumn col, string op, object? value, bool hasValue, SqlParams ps)
    {
        var q = dialect.Quote(col.Name);
        switch (op)
        {
            case "isNull":
                if (hasValue && value is not bool) throw Bad("invalid_filter", "'isNull' takes true or false.");
                return !hasValue || (bool)value! ? $"{q} IS NULL" : $"{q} IS NOT NULL";
            case "in" or "nin":
                if (value is not List<object?> items) throw Bad("invalid_filter", $"'{op}' needs a list.");
                if (items.Count is 0 or > MaxListItems) throw Bad("invalid_filter", $"'{op}' needs between 1 and {MaxListItems} values.");
                var exprs = items.Select(i =>
                {
                    RequireScalar(i);
                    var v = SqlValues.FromJson(col, i, dialect) ?? throw Bad("invalid_filter", "Lists cannot contain null. Use isNull.");
                    return dialect.ParamExpr(col, ps.Add(v));
                }).ToList();
                return $"{q} {(op == "in" ? "IN" : "NOT IN")} ({string.Join(", ", exprs)})";
            case "startsWith" or "contains":
                if (value is not string text || text.Length is 0 or > MaxTextLength) throw Bad("invalid_filter", $"'{op}' needs a text between 1 and {MaxTextLength} characters.");
                if (col.Type is not (ColumnTypes.String or ColumnTypes.Text)) throw Bad("invalid_filter", $"'{op}' only works on text columns.");
                var escaped = text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                return $"{q} LIKE {ps.Add(op == "startsWith" ? escaped + "%" : "%" + escaped + "%")} {dialect.LikeEscape}";
        }

        if (!hasValue) throw Bad("invalid_filter", $"'{op}' needs a value.");
        RequireScalar(value);
        var driver = SqlValues.FromJson(col, value, dialect);
        if (driver is null)
        {
            if (op == "eq") return $"{q} IS NULL";
            if (op == "ne") return $"{q} IS NOT NULL";
            throw Bad("invalid_filter", $"'{op}' cannot compare with null.");
        }
        var sql = op switch { "eq" => "=", "ne" => "<>", "gt" => ">", "gte" => ">=", "lt" => "<", "lte" => "<=", _ => throw Bad("invalid_filter", "Unsupported operator.") };
        return $"{q} {sql} {dialect.ParamExpr(col, ps.Add(driver))}";
    }

    static void RequireScalar(object? v)
    {
        if (v is Dictionary<string, object?> or List<object?>) throw Bad("invalid_filter", "Filter values must be text, numbers, booleans or null.");
    }

    // ------------------------------------------------------------------ order

    public string OrderBy(SqlTable table, object? sort)
    {
        var parts = new List<string>();
        if (sort is not null)
        {
            if (sort is not List<object?> list) throw Bad("invalid_sort", "'sort' must be a list.");
            foreach (var item in list)
            {
                if (item is not Dictionary<string, object?> s || MiniJson.GetString(s, "field") is not { } field) throw Bad("invalid_sort", "Each sort entry needs a 'field'.");
                var col = Column(table, field);
                var dir = (MiniJson.GetString(s, "dir") ?? "asc").ToLowerInvariant();
                if (dir is not ("asc" or "desc")) throw Bad("invalid_sort", "Sort direction must be asc or desc.");
                if (col.Type == ColumnTypes.Json) throw Bad("invalid_sort", "json columns cannot be sorted.");
                parts.Add($"{dialect.Quote(col.Name)} {dir.ToUpperInvariant()}");
            }
        }
        // A stable order makes paging reliable.
        if (parts.Count == 0) parts.AddRange(table.PrimaryKey.Select(dialect.Quote));
        return parts.Count == 0 ? "" : " ORDER BY " + string.Join(", ", parts);
    }

    // ------------------------------------------------------------------ statements

    /// <summary>SELECT with one extra row so the caller can tell whether more rows exist. Limit = effective page size.</summary>
    public (SqlStatement Statement, IReadOnlyList<SqlColumn> Selected, int Limit) Find(string physicalTable, SqlTable table, Dictionary<string, object?> req, int maxRows)
    {
        var selected = SelectedColumns(table, req.GetValueOrDefault("fields"));
        // Like the API: an out-of-range limit is clamped, an unusable offset is refused.
        var limit = Math.Clamp(req.TryGetValue("limit", out var l) && l is not null ? WholeNumber(l, "limit") : maxRows, 1, maxRows);
        var offset = req.TryGetValue("offset", out var o) && o is not null ? WholeNumber(o, "offset") : 0;
        if (offset is < 0 or > 1_000_000) throw Bad("invalid_request", "Offset is out of range.");
        var ps = new SqlParams();
        var sb = new StringBuilder("SELECT ").Append(string.Join(", ", selected.Select(c => dialect.Quote(c.Name))))
            .Append(" FROM ").Append(dialect.Quote(physicalTable));
        if (Where(req.GetValueOrDefault("filter"), table, ps) is { } where) sb.Append(" WHERE ").Append(where);
        sb.Append(OrderBy(table, req.GetValueOrDefault("sort")));
        sb.Append(" LIMIT ").Append(limit + 1).Append(" OFFSET ").Append(offset);
        return (new SqlStatement(sb.ToString(), ps.Values), selected, limit);
    }

    public SqlStatement Count(string physicalTable, SqlTable table, Dictionary<string, object?> req)
    {
        var ps = new SqlParams();
        var sb = new StringBuilder("SELECT COUNT(*) FROM ").Append(dialect.Quote(physicalTable));
        if (Where(req.GetValueOrDefault("filter"), table, ps) is { } where) sb.Append(" WHERE ").Append(where);
        return new SqlStatement(sb.ToString(), ps.Values);
    }

    /// <summary>One INSERT per row. When the table has a single primary key column it is returned so the caller can report ids.</summary>
    public IReadOnlyList<SqlStatement> Insert(string physicalTable, SqlTable table, object? rows, int maxRows)
    {
        if (rows is not List<object?> list || list.Count == 0) throw Bad("invalid_request", "Provide at least one row.");
        if (list.Count > maxRows) throw new SqlRequestException(413, "too_many_rows", $"At most {maxRows} rows can be inserted per request.");
        var result = new List<SqlStatement>();
        foreach (var row in list) result.Add(InsertRow(physicalTable, table, row));
        return result;
    }

    SqlStatement InsertRow(string physicalTable, SqlTable table, object? row)
    {
        var values = Row(table, row, requireAll: true);
        var ps = new SqlParams();
        var returning = table.PrimaryKey.Count == 1 ? $" RETURNING {dialect.Quote(table.PrimaryKey[0])}" : "";
        if (values.Count == 0)
            return new SqlStatement($"INSERT INTO {dialect.Quote(physicalTable)} DEFAULT VALUES{returning}", ps.Values);
        var cols = values.Select(v => dialect.Quote(v.Column.Name));
        var exprs = values.Select(v => dialect.ParamExpr(v.Column, ps.Add(v.Value)));
        return new SqlStatement($"INSERT INTO {dialect.Quote(physicalTable)} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", exprs)}){returning}", ps.Values);
    }

    public SqlStatement Update(string physicalTable, SqlTable table, Dictionary<string, object?> req)
    {
        var ps = new SqlParams();
        var assignments = new List<string>();
        if (req.GetValueOrDefault("set") is { } set)
        {
            if (set is not Dictionary<string, object?> setObj) throw Bad("invalid_request", "'set' must be an object.");
            foreach (var (name, value) in setObj)
            {
                var col = Writable(table, name);
                var driver = SqlValues.FromJson(col, value, dialect);
                if (driver is null && !col.Nullable) throw Bad("invalid_value", $"Column '{col.Name}' cannot be null.");
                assignments.Add($"{dialect.Quote(col.Name)} = {dialect.ParamExpr(col, ps.Add(driver))}");
            }
        }
        if (req.GetValueOrDefault("inc") is { } inc)
        {
            if (inc is not Dictionary<string, object?> incObj) throw Bad("invalid_request", "'inc' must be an object.");
            foreach (var (name, value) in incObj)
            {
                var col = Writable(table, name);
                if (col.Type is not (ColumnTypes.Int32 or ColumnTypes.Int64 or ColumnTypes.Decimal or ColumnTypes.Float64))
                    throw Bad("invalid_request", $"Column '{col.Name}' is not numeric, so it cannot be incremented.");
                if (value is not (long or double)) throw Bad("invalid_value", "Increments must be numbers.");
                var q = dialect.Quote(col.Name);
                assignments.Add($"{q} = {q} + {ps.Add(SqlValues.FromJson(col, value, dialect))}");
            }
        }
        if (assignments.Count == 0) throw Bad("invalid_request", "Provide 'set' or 'inc' with at least one column.");

        var where = Where(req.GetValueOrDefault("filter"), table, ps);
        if (where is null && MiniJson.GetBool(req, "all") != true) throw Bad("filter_required", "An update needs a filter, or all=true where the key allows it.");
        var sql = $"UPDATE {dialect.Quote(physicalTable)} SET {string.Join(", ", assignments)}" + (where is null ? "" : " WHERE " + where);
        return new SqlStatement(sql, ps.Values);
    }

    public SqlStatement Delete(string physicalTable, SqlTable table, Dictionary<string, object?> req)
    {
        var ps = new SqlParams();
        var where = Where(req.GetValueOrDefault("filter"), table, ps);
        if (where is null && MiniJson.GetBool(req, "all") != true) throw Bad("filter_required", "A delete needs a filter, or all=true where the key allows it.");
        return new SqlStatement($"DELETE FROM {dialect.Quote(physicalTable)}" + (where is null ? "" : " WHERE " + where), ps.Values);
    }

    /// <summary>INSERT ... ON CONFLICT on exactly the primary key or a unique index. Non-key columns of the row are updated.</summary>
    public SqlStatement Upsert(string physicalTable, SqlTable table, Dictionary<string, object?> req)
    {
        if (req.GetValueOrDefault("keys") is not List<object?> rawKeys || rawKeys.Count == 0) throw Bad("invalid_request", "Provide the key columns of the upsert.");
        var keys = rawKeys.Select(k => k as string ?? throw Bad("invalid_request", "Key columns must be text.")).Distinct().ToList();
        foreach (var k in keys) Column(table, k);
        var isKey = table.PrimaryKey.ToHashSet().SetEquals(keys) || (table.UniqueKeys ?? []).Any(u => u.ToHashSet().SetEquals(keys));
        if (!isKey) throw Bad("invalid_request", "The keys must match the primary key or a unique index exactly.");
        var values = Row(table, req.GetValueOrDefault("row"), requireAll: true);
        foreach (var k in keys)
            if (!values.Any(v => v.Column.Name == k)) throw Bad("missing_value", $"The row needs a value for key column '{k}'.");
        var ps = new SqlParams();
        var exprs = values.Select(v => dialect.ParamExpr(v.Column, ps.Add(v.Value))).ToList();
        var update = values.Select(v => v.Column.Name).Where(n => !keys.Contains(n) && !table.PrimaryKey.Contains(n)).ToList();
        return new SqlStatement(dialect.UpsertSql(physicalTable, [.. values.Select(v => v.Column.Name)], exprs, keys, update), ps.Values);
    }

    // ------------------------------------------------------------------ helpers

    List<(SqlColumn Column, object? Value)> Row(SqlTable table, object? row, bool requireAll)
    {
        if (row is not Dictionary<string, object?> o) throw Bad("invalid_request", "Each row must be an object.");
        var result = new List<(SqlColumn, object?)>();
        foreach (var (name, value) in o)
        {
            var col = Column(table, name);
            var driver = SqlValues.FromJson(col, value, dialect);
            if (driver is null && !col.Nullable) throw Bad("invalid_value", $"Column '{col.Name}' cannot be null.");
            result.Add((col, driver));
        }
        if (requireAll)
            foreach (var c in table.Columns.Where(x => !x.Nullable && !x.HasDefault && !x.AutoIncrement && !result.Any(r => r.Item1.Name == x.Name)))
                throw Bad("missing_value", $"Column '{c.Name}' needs a value.");
        return result;
    }

    static IReadOnlyList<SqlColumn> SelectedColumns(SqlTable table, object? fields)
    {
        if (fields is null) return table.Columns;
        if (fields is not List<object?> list) throw Bad("invalid_request", "'fields' must be a list.");
        if (list.Count == 0) return table.Columns;
        return list.Select(f => f is string s ? Column(table, s) : throw Bad("invalid_request", "'fields' must contain column names.")).DistinctBy(c => c.Name).ToList();
    }

    static int WholeNumber(object v, string name) =>
        v is long l && l is >= int.MinValue and <= int.MaxValue ? (int)l : throw Bad("invalid_request", $"'{name}' must be a whole number.");

    public static SqlColumn Column(SqlTable table, string name) =>
        table.Columns.FirstOrDefault(c => c.Name == name) ?? throw Bad("unknown_column", $"Table '{table.Name}' has no column '{name}'.");

    static SqlColumn Writable(SqlTable table, string name)
    {
        var c = Column(table, name);
        if (table.PrimaryKey.Contains(c.Name)) throw Bad("invalid_request", $"Primary key column '{c.Name}' cannot be changed.");
        return c;
    }

    static SqlRequestException Bad(string code, string message) => new(400, code, message);
}
