namespace Bundle.Tests;

public class SqlStatementTests
{
    static readonly SqlTable Players = new("lt__players",
    [
        new SqlColumn("id", ColumnTypes.Int64, AutoIncrement: true),
        new SqlColumn("name", ColumnTypes.String, Length: 8),
        new SqlColumn("coins", ColumnTypes.Int32, HasDefault: true),
        new SqlColumn("active", ColumnTypes.Bool, HasDefault: true),
        new SqlColumn("meta", ColumnTypes.Json, Nullable: true),
        new SqlColumn("seen", ColumnTypes.DateTime, Nullable: true),
    ], ["id"]);

    static Dictionary<string, object?> Req(string json) => (Dictionary<string, object?>)MiniJson.Parse(json)!;

    static SqlRequestException Fails(Action a) => Assert.Throws<SqlRequestException>(a);

    [Fact]
    public void Filter_builds_positional_parameters_and_never_inlines_values()
    {
        var s = new SqlStatements(SqlDialect.Postgres);
        var ps = new SqlParams();
        var where = s.Where(MiniJson.Parse("""{"and":[{"field":"name","op":"eq","value":"x';--"},{"or":[{"field":"coins","op":"gte","value":5},{"not":{"field":"active","op":"eq","value":true}}]}]}"""), Players, ps);
        Assert.Equal("(\"name\" = $1 AND (\"coins\" >= $2 OR (NOT \"active\" = $3)))", where);
        Assert.Equal(new object?[] { "x';--", 5L, true }, ps.Values);
    }

    [Fact]
    public void Filter_handles_null_in_like_and_json()
    {
        var s = new SqlStatements(SqlDialect.Postgres);
        var ps = new SqlParams();
        Assert.Equal("\"meta\" IS NULL", s.Where(MiniJson.Parse("""{"field":"meta","op":"eq","value":null}"""), Players, ps));
        Assert.Equal("\"name\" IN ($1, $2)", s.Where(MiniJson.Parse("""{"field":"name","op":"in","value":["a","b"]}"""), Players, ps));
        Assert.Equal("\"name\" LIKE $3 ESCAPE '\\'", s.Where(MiniJson.Parse("""{"field":"name","op":"startsWith","value":"50%_"}"""), Players, ps));
        Assert.Equal("50\\%\\_%", ps.Values[2]);
        Assert.Null(s.Where(MiniJson.Parse("{}"), Players, new SqlParams()));
    }

    [Fact]
    public void Filter_rejects_bad_input()
    {
        var s = new SqlStatements(SqlDialect.Postgres);
        string Where(string json) => s.Where(MiniJson.Parse(json), Players, new SqlParams()) ?? "";
        Assert.Equal("unknown_column", Fails(() => Where("""{"field":"nope","op":"eq","value":1}""")).Code);
        Assert.Equal("invalid_filter", Fails(() => Where("""{"field":"name","op":"drop","value":1}""")).Code);
        Assert.Equal("invalid_filter", Fails(() => Where("""{"field":"coins","op":"contains","value":"1"}""")).Code);
        Assert.Equal("invalid_filter", Fails(() => Where("""{"field":"name","op":"gt","value":null}""")).Code);
        Assert.Equal("invalid_filter", Fails(() => Where("""{"field":"name","op":"in","value":[]}""")).Code);
        Assert.Equal("invalid_filter", Fails(() => Where("""{"field":"name","op":"eq","value":{"a":1}}""")).Code);
        Assert.Equal("invalid_filter", Fails(() => Where("""{"field":"name","op":"eq","value":"a","sql":"1"}""")).Code);
        Assert.Equal("invalid_value", Fails(() => Where("""{"field":"coins","op":"eq","value":"text"}""")).Code);
    }

    [Fact]
    public void Filter_limits_depth_and_node_count()
    {
        var s = new SqlStatements(SqlDialect.Postgres);
        var deep = """{"field":"coins","op":"eq","value":1}""";
        for (var i = 0; i < 8; i++) deep = "{\"not\":" + deep + "}";
        Assert.Equal("invalid_filter", Fails(() => s.Where(MiniJson.Parse(deep), Players, new SqlParams())).Code);
        var wide = "{\"and\":[" + string.Join(",", Enumerable.Repeat("""{"field":"coins","op":"eq","value":1}""", 129)) + "]}";
        Assert.Equal("invalid_filter", Fails(() => s.Where(MiniJson.Parse(wide), Players, new SqlParams())).Code);
    }

    [Fact]
    public void Find_orders_by_primary_key_and_fetches_one_extra_row()
    {
        var s = new SqlStatements(SqlDialect.Postgres);
        var (st, selected, limit) = s.Find("lt__players", Players, Req("""{"fields":["name","id"],"filter":{"field":"id","op":"gt","value":3},"limit":10,"offset":20}"""), 100);
        Assert.Equal("SELECT \"name\", \"id\" FROM \"lt__players\" WHERE \"id\" > $1 ORDER BY \"id\" LIMIT 11 OFFSET 20", st.Sql);
        Assert.Equal(new object?[] { 3L }, st.Parameters);
        Assert.Equal(["name", "id"], selected.Select(c => c.Name));
        Assert.Equal(10, limit);
        Assert.Equal(100, s.Find("lt__players", Players, Req("{}"), 100).Limit);
    }

    [Fact]
    public void Find_validates_sort_and_limit()
    {
        var s = new SqlStatements(SqlDialect.Postgres);
        var st = s.Find("lt__players", Players, Req("""{"sort":[{"field":"coins","dir":"DESC"},{"field":"name"}]}"""), 100).Statement;
        Assert.Contains("ORDER BY \"coins\" DESC, \"name\" ASC", st.Sql);
        Assert.Equal("invalid_sort", Fails(() => s.Find("t", Players, Req("""{"sort":[{"field":"meta"}]}"""), 100)).Code);
        Assert.Equal("invalid_sort", Fails(() => s.Find("t", Players, Req("""{"sort":[{"field":"name","dir":"sideways"}]}"""), 100)).Code);
        Assert.Equal(100, s.Find("t", Players, Req("""{"limit":1001}"""), 100).Limit);
        Assert.Equal(1, s.Find("t", Players, Req("""{"limit":0}"""), 100).Limit);
        Assert.Equal("invalid_request", Fails(() => s.Find("t", Players, Req("""{"offset":-1}"""), 100)).Code);
    }

    [Fact]
    public void Insert_requires_columns_and_casts_json_on_postgres()
    {
        var s = new SqlStatements(SqlDialect.Postgres);
        var st = s.Insert("lt__players", Players, MiniJson.Parse("""[{"name":"Ann","meta":{"a":[1,2]}}]"""), 100).Single();
        Assert.Equal("INSERT INTO \"lt__players\" (\"name\", \"meta\") VALUES ($1, CAST($2 AS jsonb)) RETURNING \"id\"", st.Sql);
        Assert.Equal(new object?[] { "Ann", "{\"a\":[1,2]}" }, st.Parameters);
        Assert.Equal("missing_value", Fails(() => s.Insert("t", Players, MiniJson.Parse("""[{"coins":1}]"""), 100)).Code);
        Assert.Equal("value_too_long", Fails(() => s.Insert("t", Players, MiniJson.Parse("""[{"name":"123456789"}]"""), 100)).Code);
        Assert.Equal("invalid_value", Fails(() => s.Insert("t", Players, MiniJson.Parse("""[{"name":null}]"""), 100)).Code);
        Assert.Equal("too_many_rows", Fails(() => s.Insert("t", Players, MiniJson.Parse("""[{"name":"a"},{"name":"b"}]"""), 1)).Code);
    }

    [Fact]
    public void Sqlite_stores_booleans_dates_and_integers_in_its_own_forms()
    {
        var s = new SqlStatements(SqlDialect.Sqlite);
        var st = s.Insert("lt__players", Players, MiniJson.Parse("""[{"name":"Ann","active":true,"coins":7,"seen":"2026-10-28T10:00:00Z","meta":{"k":1}}]"""), 100).Single();
        Assert.Equal("INSERT INTO \"lt__players\" (\"name\", \"active\", \"coins\", \"seen\", \"meta\") VALUES ($1, $2, $3, $4, $5) RETURNING \"id\"", st.Sql);
        Assert.Equal(new object?[] { "Ann", 1L, 7L, "2026-10-28T10:00:00.000000Z", "{\"k\":1}" }, st.Parameters);
    }

    [Fact]
    public void Update_needs_filter_or_all_and_protects_the_primary_key()
    {
        var s = new SqlStatements(SqlDialect.Postgres);
        var st = s.Update("lt__players", Players, Req("""{"filter":{"field":"id","op":"eq","value":1},"set":{"name":"Bob"},"inc":{"coins":5}}"""));
        Assert.Equal("UPDATE \"lt__players\" SET \"name\" = $1, \"coins\" = \"coins\" + $2 WHERE \"id\" = $3", st.Sql);
        Assert.Equal(new object?[] { "Bob", 5L, 1L }, st.Parameters);
        Assert.Equal("filter_required", Fails(() => s.Update("t", Players, Req("""{"set":{"name":"Bob"}}"""))).Code);
        Assert.Equal("UPDATE \"t\" SET \"name\" = $1", s.Update("t", Players, Req("""{"all":true,"set":{"name":"Bob"}}""")).Sql);
        Assert.Equal("invalid_request", Fails(() => s.Update("t", Players, Req("""{"all":true,"set":{"id":2}}"""))).Code);
        Assert.Equal("invalid_request", Fails(() => s.Update("t", Players, Req("""{"all":true,"inc":{"name":1}}"""))).Code);
        Assert.Equal("invalid_request", Fails(() => s.Update("t", Players, Req("""{"all":true}"""))).Code);
    }

    [Fact]
    public void Delete_and_count()
    {
        var s = new SqlStatements(SqlDialect.Postgres);
        Assert.Equal("DELETE FROM \"t\" WHERE \"id\" = $1", s.Delete("t", Players, Req("""{"filter":{"field":"id","op":"eq","value":1}}""")).Sql);
        Assert.Equal("DELETE FROM \"t\"", s.Delete("t", Players, Req("""{"all":true}""")).Sql);
        Assert.Equal("filter_required", Fails(() => s.Delete("t", Players, Req("{}"))).Code);
        Assert.Equal("SELECT COUNT(*) FROM \"t\"", s.Count("t", Players, Req("{}")).Sql);
    }

    [Fact]
    public void Values_round_trip_to_json_text()
    {
        var meta = Players.Columns.Single(c => c.Name == "meta");
        Assert.Equal("{\"a\":1}", JsonText.Value(SqlDialect.Postgres.FromDriver(meta, "{\"a\":1}")));
        var seen = Players.Columns.Single(c => c.Name == "seen");
        Assert.Equal("\"2026-10-28T10:00:00.0000000Z\"", JsonText.Value(SqlDialect.Sqlite.FromDriver(seen, "2026-10-28T10:00:00.000000Z")));
        var active = Players.Columns.Single(c => c.Name == "active");
        Assert.Equal("true", JsonText.Value(SqlDialect.Sqlite.FromDriver(active, 1L)));
    }
}
