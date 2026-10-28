using Microsoft.Data.Sqlite;

namespace Bundle.Tests;

/// <summary>Runs the generated SQL against a real in-memory SQLite database, the engine behind re.sql's sqlite alias.</summary>
public class SqliteIntegrationTests : IDisposable
{
    readonly SqliteConnection db = new("Data Source=:memory:");
    readonly SqlStatements statements = new(SqlDialect.Sqlite);

    public SqliteIntegrationTests() => db.Open();
    public void Dispose() => db.Dispose();

    const string Schema = """
    {"tables":[{"name":"players","primaryKey":["id"],"columns":[
      {"name":"id","type":"int64","autoIncrement":true},
      {"name":"name","type":"string","length":16,"unique":true},
      {"name":"coins","type":"int32","default":5},
      {"name":"active","type":"bool","default":true},
      {"name":"bal","type":"decimal","precision":12,"scale":2,"nullable":true},
      {"name":"meta","type":"json","nullable":true},
      {"name":"seen","type":"datetime","default":"now"},
      {"name":"born","type":"date","nullable":true},
      {"name":"uid","type":"uuid","nullable":true},
      {"name":"raw","type":"bytes","length":8,"nullable":true}],
     "indexes":[{"name":"by_coins","columns":["coins"]}]}]}
    """;

    SchemaTable Table()
    {
        var table = SqlSchema.Normalize(SqlSchema.Parse(MiniJson.Parse(Schema))).Tables[0];
        foreach (var sql in new[] { SqlDialect.Sqlite.MetaTableSql, SqlDialect.Sqlite.CreateTableSql("lt__players", table) }) Exec(sql);
        foreach (var ix in table.Indexes) Exec(SqlDialect.Sqlite.CreateIndexSql("lt__players", SqlSchema.IndexName("lt__players", ix.Name), ix));
        return table;
    }

    SqliteCommand Command(SqlStatement st)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = st.Sql;
        for (var i = 0; i < st.Parameters.Count; i++) cmd.Parameters.AddWithValue("$" + (i + 1), st.Parameters[i]);
        return cmd;
    }

    int Exec(string sql) { using var c = db.CreateCommand(); c.CommandText = sql; return c.ExecuteNonQuery(); }
    int Exec(SqlStatement st) { using var c = Command(st); return c.ExecuteNonQuery(); }

    List<string> Names(string sql, string value)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.Parameters.AddWithValue("$1", value);
        using var r = c.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    static Dictionary<string, object?> Req(string json) => (Dictionary<string, object?>)MiniJson.Parse(json)!;

    string Find(SchemaTable table, string request)
    {
        var (st, selected, limit) = statements.Find("lt__players", table.ToQuery(), Req(request), 1000);
        using var c = Command(st);
        using var r = c.ExecuteReader();
        var rows = new List<object?>();
        while (r.Read() && rows.Count < limit)
        {
            var pairs = new (string, object?)[selected.Count];
            for (var i = 0; i < pairs.Length; i++) pairs[i] = (selected[i].Name, SqlDialect.Sqlite.FromDriver(selected[i], r.IsDBNull(i) ? null : r.GetValue(i)));
            rows.Add(new JsonText.Raw(JsonText.Object(pairs)));
        }
        return JsonText.Array(rows);
    }

    string NameList(SchemaTable table, string request) =>
        "[" + string.Join(",", MiniJson.Parse(Find(table, request)) is List<object?> l ? l.Select(r => JsonText.Quote((string)((Dictionary<string, object?>)r!)["name"]!)) : []) + "]";

    [Fact]
    public void Insert_returns_generated_ids_and_find_round_trips_every_type()
    {
        var table = Table();
        var insert = statements.Insert("lt__players", table.ToQuery(),
            MiniJson.Parse("""[{"name":"ann","bal":"12.50","meta":{"a":[1,2]},"born":"2026-10-28","uid":"123E4567-E89B-12D3-A456-426614174000","raw":"AQID","active":false}]"""), 10).Single();
        using (var c = Command(insert)) Assert.Equal(1L, c.ExecuteScalar());
        var json = Find(table, """{"filter":{"field":"name","op":"eq","value":"ann"}}""");
        Assert.StartsWith("[{\"id\":1,\"name\":\"ann\",\"coins\":5,\"active\":false,\"bal\":12.5,\"meta\":{\"a\":[1,2]},\"seen\":\"", json);
        Assert.Contains("\"born\":\"2026-10-28\",\"uid\":\"123e4567-e89b-12d3-a456-426614174000\",\"raw\":\"AQID\"}]", json);
        Assert.Contains("Z\"", json);
    }

    [Fact]
    public void Filters_sort_paging_and_counts_work()
    {
        var table = Table();
        foreach (var (name, coins) in new[] { ("amy", 10), ("bob", 20), ("cat", 30), ("dan%", 40) })
            Exec(statements.Insert("lt__players", table.ToQuery(), MiniJson.Parse($"[{{\"name\":\"{name}\",\"coins\":{coins}}}]"), 1).Single());

        Assert.Equal("[\"cat\",\"bob\"]", NameList(table, """{"fields":["name"],"filter":{"and":[{"field":"coins","op":"gte","value":20},{"not":{"field":"name","op":"eq","value":"dan%"}}]},"sort":[{"field":"coins","dir":"desc"}]}"""));
        Assert.Equal("[\"dan%\"]", NameList(table, """{"fields":["name"],"filter":{"field":"name","op":"contains","value":"%"}}"""));
        Assert.Equal("[\"bob\",\"cat\"]", NameList(table, """{"fields":["name"],"limit":2,"offset":1}"""));
        Assert.Equal("[\"amy\",\"dan%\"]", NameList(table, """{"fields":["name"],"filter":{"field":"name","op":"in","value":["amy","dan%"]}}"""));
        Assert.Equal("[\"bob\",\"cat\"]", NameList(table, """{"fields":["name"],"filter":{"or":[{"field":"name","op":"startsWith","value":"b"},{"field":"coins","op":"eq","value":30}]}}"""));
        using var count = Command(statements.Count("lt__players", table.ToQuery(), Req("""{"filter":{"field":"bal","op":"isNull"}}""")));
        Assert.Equal(4L, count.ExecuteScalar());
    }

    [Fact]
    public void Update_inc_upsert_and_delete()
    {
        var table = Table();
        Exec(statements.Insert("lt__players", table.ToQuery(), MiniJson.Parse("""[{"name":"ann"}]"""), 1).Single());
        Assert.Equal(1, Exec(statements.Update("lt__players", table.ToQuery(), Req("""{"filter":{"field":"name","op":"eq","value":"ann"},"set":{"bal":null,"meta":{"k":true}},"inc":{"coins":3}}"""))));
        Assert.Contains("\"coins\":8", Find(table, "{}"));
        Assert.Contains("\"meta\":{\"k\":true}", Find(table, "{}"));

        Assert.Equal(1, Exec(statements.Upsert("lt__players", table.ToQuery(), Req("""{"keys":["name"],"row":{"name":"ann","coins":99}}"""))));
        Assert.Contains("\"coins\":99", Find(table, "{}"));
        Exec(statements.Upsert("lt__players", table.ToQuery(), Req("""{"keys":["name"],"row":{"name":"zed","coins":1}}""")));
        Assert.Equal(2, MiniJson.Parse(Find(table, "{}")) is List<object?> l ? l.Count : 0);

        Assert.Equal(1, Exec(statements.Delete("lt__players", table.ToQuery(), Req("""{"filter":{"field":"name","op":"eq","value":"zed"}}"""))));
        Assert.Equal(1, Exec(statements.Delete("lt__players", table.ToQuery(), Req("""{"all":true}"""))));
    }

    [Fact]
    public void Constraint_failures_are_classified_without_leaking_values()
    {
        var table = Table();
        var insert = statements.Insert("lt__players", table.ToQuery(), MiniJson.Parse("""[{"name":"ann"}]"""), 1).Single();
        Exec(insert);
        var ex = Assert.ThrowsAny<Exception>(() => Exec(insert));
        var mapped = SqlErrors.Classify(ex.GetType().Name, ex.Message);
        Assert.Equal("constraint_violation", mapped.Code);
        Assert.DoesNotContain("ann", mapped.Message);
    }

    [Fact]
    public void Rebuild_keeps_the_data_changes_the_column_and_restores_indexes()
    {
        var table = Table();
        Exec(statements.Insert("lt__players", table.ToQuery(), MiniJson.Parse("""[{"name":"ann","coins":7}]"""), 1).Single());
        var desired = SqlSchema.Normalize(SqlSchema.Parse(MiniJson.Parse("""{"tables":[{"name":"players","primaryKey":["id"],"columns":[{"name":"id","type":"int64","autoIncrement":true},{"name":"name","type":"string","length":16,"unique":true},{"name":"coins","type":"int64","nullable":true},{"name":"extra","type":"int32","nullable":true}],"indexes":[{"name":"by_coins","columns":["coins"]}]}]}"""))).Tables[0];
        var existing = Names(SqlDialect.Sqlite.ColumnNamesSql, "lt__players").ToHashSet();
        var removed = table.Columns.Where(c => c.Name == "bal").ToList();
        foreach (var sql in SqlSchema.RebuildStatements(SqlDialect.Sqlite, "lt__players", desired, existing, removed)) Exec(sql);

        Assert.Equal(["id", "name", "coins", "extra", "bal"], Names(SqlDialect.Sqlite.ColumnNamesSql, "lt__players"));
        var indexes = Names(SqlDialect.Sqlite.IndexNamesSql, "lt__players");
        Assert.Contains("ix_lt__players_by_coins", indexes);
        Assert.Contains("ix_lt__players_uq_name", indexes);
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT id, name, coins FROM lt__players";
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal((1L, "ann", 7L), (r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
        }
        // The id still generates itself after the rebuild.
        using var next = Command(statements.Insert("lt__players", desired.ToQuery(), MiniJson.Parse("""[{"name":"bob"}]"""), 1).Single());
        Assert.Equal(2L, next.ExecuteScalar());
    }

    [Fact]
    public void Existence_queries_work()
    {
        Table();
        var exists = SqlDialect.Sqlite.TableExistsSql.Replace("SELECT 1", "SELECT name");
        Assert.Equal(["lt__players"], Names(exists, "lt__players"));
        Assert.Empty(Names(exists, "nope"));
        Assert.Contains("coins", Names(SqlDialect.Sqlite.ColumnNamesSql, "lt__players"));
    }

    [Fact]
    public void Add_column_not_null_without_default_works_on_a_table_with_rows()
    {
        var table = Table();
        Exec(statements.Insert("lt__players", table.ToQuery(), MiniJson.Parse("""[{"name":"ann"}]"""), 1).Single());
        var col = SqlSchema.Normalize(SqlSchema.Parse(MiniJson.Parse("""{"tables":[{"name":"t","columns":[{"name":"lvl","type":"int32"}]}]}"""))).Tables[0].Columns[0];
        foreach (var sql in SqlDialect.Sqlite.AddColumnSql("lt__players", col)) Exec(sql);
        Assert.Contains("lvl", Names(SqlDialect.Sqlite.ColumnNamesSql, "lt__players"));
    }

    [Fact]
    public void Savepoint_rollback_discards_the_business_change_but_keeps_the_receipt()
    {
        var table = Table();
        Exec(SqlDialect.Sqlite.ReceiptTableSql);
        Exec(statements.Insert("lt__players", table.ToQuery(), MiniJson.Parse("""[{"name":"ann"}]"""), 1).Single());
        using var tx = db.BeginTransaction();
        Exec("INSERT INTO \"_ldb_mutation_receipts\" (ns, effect_id, operation, fingerprint, state, code, result_json) VALUES ('lt','e','Update','f','pending','owner','{}')");
        Exec("SAVEPOINT ldb_mutation");
        Exec("UPDATE lt__players SET coins = 100");
        Exec("ROLLBACK TO SAVEPOINT ldb_mutation");
        Exec("UPDATE \"_ldb_mutation_receipts\" SET state = 'failed', code = 'no_match' WHERE ns = 'lt' AND effect_id = 'e' AND state = 'pending'");
        tx.Commit();
        Assert.Contains("\"coins\":5", Find(table, "{}"));
        Assert.Single(Names("SELECT state FROM \"_ldb_mutation_receipts\" WHERE state = 'failed' AND ns <> $1", "other"));
    }
}
