namespace Bundle.Tests;

/// <summary>
/// Golden vectors in this file were produced by running the real lt-database-api code (Ldb.Core: SchemaValidator, SchemaHasher,
/// SchemaDiffer, MutationReceiptValidation) on the same inputs. They prove that both backends agree byte for byte.
/// </summary>
public class SqlSchemaTests
{
    const string Declared = """
    {"tables":[
     {"name":"players","primaryKey":["id"],"columns":[
       {"name":"id","type":"int64","autoIncrement":true},
       {"name":"name","type":"string","length":32,"unique":true},
       {"name":"coins","type":"int32","default":5},
       {"name":"balance","type":"decimal","precision":12,"scale":2,"nullable":true},
       {"name":"motto","type":"text","default":"it's <ok> & +1 é","nullable":true},
       {"name":"seen","type":"datetime","default":"now"},
       {"name":"meta","type":"json","nullable":true},
       {"name":"active","type":"bool","default":true}],
      "indexes":[{"name":"by_coins","columns":["coins","name"]}]},
     {"name":"audit_log","columns":[{"name":"at","type":"datetime"},{"name":"msg","type":"text"}]}
    ]}
    """;

    const string Changed = """{"tables":[{"name":"players","primaryKey":["id"],"columns":[{"name":"id","type":"int64","autoIncrement":true},{"name":"nick","type":"string","length":16,"renamedFrom":"name"},{"name":"coins","type":"int64","default":5},{"name":"extra","type":"int32","nullable":true}],"indexes":[{"name":"by_coins","columns":["coins"],"unique":true}]},{"name":"gamers","renamedFrom":"audit_log","columns":[{"name":"at","type":"datetime"},{"name":"msg","type":"text"}]}]}""";

    static SchemaDefinition Norm(string json) => SqlSchema.Normalize(SqlSchema.Parse(MiniJson.Parse(json)));

    [Fact]
    public void Canonical_json_and_hash_match_the_api_byte_for_byte()
    {
        var n = Norm(Declared);
        Assert.Equal("{\"tables\":[{\"name\":\"audit_log\",\"columns\":[{\"name\":\"at\",\"type\":\"datetime\",\"nullable\":false,\"unique\":false,\"autoIncrement\":false},{\"name\":\"msg\",\"type\":\"text\",\"nullable\":false,\"unique\":false,\"autoIncrement\":false}],\"primaryKey\":[],\"indexes\":[]},{\"name\":\"players\",\"columns\":[{\"name\":\"active\",\"type\":\"bool\",\"nullable\":false,\"default\":true,\"unique\":false,\"autoIncrement\":false},{\"name\":\"balance\",\"type\":\"decimal\",\"precision\":12,\"scale\":2,\"nullable\":true,\"unique\":false,\"autoIncrement\":false},{\"name\":\"coins\",\"type\":\"int32\",\"nullable\":false,\"default\":5,\"unique\":false,\"autoIncrement\":false},{\"name\":\"id\",\"type\":\"int64\",\"nullable\":false,\"unique\":false,\"autoIncrement\":true},{\"name\":\"meta\",\"type\":\"json\",\"nullable\":true,\"unique\":false,\"autoIncrement\":false},{\"name\":\"motto\",\"type\":\"text\",\"nullable\":true,\"default\":\"it\\u0027s \\u003Cok\\u003E \\u0026 \\u002B1 \\u00E9\",\"unique\":false,\"autoIncrement\":false},{\"name\":\"name\",\"type\":\"string\",\"length\":32,\"nullable\":false,\"unique\":false,\"autoIncrement\":false},{\"name\":\"seen\",\"type\":\"datetime\",\"nullable\":false,\"default\":\"now\",\"unique\":false,\"autoIncrement\":false}],\"primaryKey\":[\"id\"],\"indexes\":[{\"name\":\"by_coins\",\"columns\":[\"coins\",\"name\"],\"unique\":false},{\"name\":\"uq_name\",\"columns\":[\"name\"],\"unique\":true}]}]}",
            SqlSchema.ToCanonicalJson(n));
        Assert.Equal("367a30aeb6516c3f149be595f04a5b456f0fbfba87738bb606d5e4b84e8a9885", SqlSchema.Hash(n));
    }

    [Fact]
    public void Declaration_order_and_rename_hints_do_not_change_the_hash()
    {
        var one = Norm("""{"tables":[{"name":"b","columns":[{"name":"x","type":"int32"},{"name":"y","type":"text"}]},{"name":"a","columns":[{"name":"z","type":"bool"}]}]}""");
        var two = Norm("""{"tables":[{"name":"a","columns":[{"name":"z","type":"bool"}]},{"name":"b","renamedFrom":"old","columns":[{"name":"y","type":"text"},{"name":"x","type":"int32","renamedFrom":"w"}]}]}""");
        Assert.Equal(SqlSchema.Hash(one), SqlSchema.Hash(two));
    }

    [Fact]
    public void The_stored_definition_round_trips()
    {
        var n = Norm(Declared);
        var again = SqlSchema.ParseStored(SqlSchema.ToCanonicalJson(n));
        Assert.Equal(SqlSchema.Hash(n), SqlSchema.Hash(again));
    }

    [Fact]
    public void Plan_matches_the_api_plan()
    {
        var ops = SqlSchema.Diff(Norm(Declared), Norm(Changed));
        Assert.Equal("[{\"kind\":\"renameTable\",\"table\":\"audit_log\",\"newName\":\"gamers\",\"destructive\":false,\"note\":\"rename table audit_log to gamers\"},{\"kind\":\"renameColumn\",\"table\":\"players\",\"column\":\"name\",\"newName\":\"nick\",\"destructive\":false,\"note\":\"rename column name to nick\"},{\"kind\":\"dropIndex\",\"table\":\"players\",\"index\":\"by_coins\",\"destructive\":false},{\"kind\":\"dropIndex\",\"table\":\"players\",\"index\":\"uq_name\",\"destructive\":false},{\"kind\":\"addColumn\",\"table\":\"players\",\"column\":\"extra\",\"destructive\":false},{\"kind\":\"alterColumn\",\"table\":\"players\",\"column\":\"nick\",\"destructive\":true},{\"kind\":\"alterColumn\",\"table\":\"players\",\"column\":\"coins\",\"destructive\":false},{\"kind\":\"createIndex\",\"table\":\"players\",\"index\":\"by_coins\",\"destructive\":false},{\"kind\":\"dropColumn\",\"table\":\"players\",\"column\":\"balance\",\"destructive\":true,\"note\":\"column removed from the schema\"},{\"kind\":\"dropColumn\",\"table\":\"players\",\"column\":\"motto\",\"destructive\":true,\"note\":\"column removed from the schema\"},{\"kind\":\"dropColumn\",\"table\":\"players\",\"column\":\"seen\",\"destructive\":true,\"note\":\"column removed from the schema\"},{\"kind\":\"dropColumn\",\"table\":\"players\",\"column\":\"meta\",\"destructive\":true,\"note\":\"column removed from the schema\"},{\"kind\":\"dropColumn\",\"table\":\"players\",\"column\":\"active\",\"destructive\":true,\"note\":\"column removed from the schema\"}]",
            "[" + string.Join(",", ops.Select(o => o.ToJson())) + "]");
    }

    [Fact]
    public void First_sync_creates_every_table_and_unchanged_schema_has_an_empty_plan()
    {
        var n = Norm(Declared);
        var first = SqlSchema.Diff(null, n);
        Assert.Equal(2, first.Count(o => o.Kind == SchemaOpKind.CreateTable));
        Assert.Equal(2, first.Count(o => o.Kind == SchemaOpKind.CreateIndex));
        Assert.Empty(SqlSchema.Diff(n, Norm(Declared)));
    }

    [Fact]
    public void Primary_key_changes_are_refused()
    {
        var a = Norm("""{"tables":[{"name":"t","primaryKey":["a"],"columns":[{"name":"a","type":"int32"},{"name":"b","type":"int32"}]}]}""");
        var b = Norm("""{"tables":[{"name":"t","primaryKey":["b"],"columns":[{"name":"a","type":"int32"},{"name":"b","type":"int32"}]}]}""");
        Assert.Equal("unsupported_change", Assert.Throws<SqlRequestException>(() => SqlSchema.Diff(a, b)).Code);
    }

    [Theory]
    [InlineData("""{"tables":[{"name":"Bad","columns":[{"name":"a","type":"int32"}]}]}""")]
    [InlineData("""{"tables":[{"name":"t","columns":[]}]}""")]
    [InlineData("""{"tables":[{"name":"t","columns":[{"name":"a","type":"nope"}]}]}""")]
    [InlineData("""{"tables":[{"name":"t","primaryKey":["a"],"columns":[{"name":"a","type":"int32","nullable":true}]}]}""")]
    [InlineData("""{"tables":[{"name":"t","primaryKey":["a"],"columns":[{"name":"a","type":"text"}]}]}""")]
    [InlineData("""{"tables":[{"name":"t","columns":[{"name":"a","type":"string","length":4001}]}]}""")]
    [InlineData("""{"tables":[{"name":"t","columns":[{"name":"a","type":"int32","default":"x"}]}]}""")]
    [InlineData("""{"tables":[{"name":"t","columns":[{"name":"a","type":"int32","autoIncrement":true}]}]}""")]
    [InlineData("""{"tables":[{"name":"t","columns":[{"name":"a","type":"int32"}],"indexes":[{"name":"i","columns":["zzz"]}]}]}""")]
    [InlineData("""{"tables":[{"name":"t","columns":[{"name":"a","type":"int32"}]},{"name":"t","columns":[{"name":"a","type":"int32"}]}]}""")]
    public void Invalid_schemas_are_refused_like_the_api(string json)
    {
        var ex = Assert.Throws<SqlRequestException>(() => Norm(json));
        Assert.Contains(ex.Code, new[] { "invalid_schema", "invalid_identifier" });
    }

    [Fact]
    public void Names_follow_the_api_rules()
    {
        Assert.True(SqlSchema.IsName("player_data2", 36));
        Assert.False(SqlSchema.IsName("double__underscore", 36));
        Assert.False(SqlSchema.IsName("_lead", 36));
        Assert.False(SqlSchema.IsName("trail_", 36));
        Assert.False(SqlSchema.IsName("Upper", 36));
        Assert.False(SqlSchema.IsName(new string('a', 25), 24));
        Assert.Equal("lt__players", SqlSchema.Physical("lt", "players"));
        Assert.StartsWith("ix_", SqlSchema.IndexName(SqlSchema.Physical("lt", new string('x', 36)), new string('y', 48)));
        Assert.True(SqlSchema.IndexName(SqlSchema.Physical("lt", new string('x', 36)), new string('y', 48)).Length <= 60);
    }
}

public class SqlDdlTests
{
    static SchemaTable Table(string json) =>
        SqlSchema.Normalize(SqlSchema.Parse(MiniJson.Parse("{\"tables\":[" + json + "]}"))).Tables[0];

    const string Players = """{"name":"players","primaryKey":["id"],"columns":[{"name":"id","type":"int64","autoIncrement":true},{"name":"name","type":"string","length":32},{"name":"coins","type":"int32","default":5},{"name":"ok","type":"bool","default":true},{"name":"seen","type":"datetime","default":"now"},{"name":"bal","type":"decimal","precision":12,"scale":2,"nullable":true},{"name":"meta","type":"json","nullable":true}]}""";

    [Fact]
    public void Postgres_create_table_matches_the_api()
    {
        Assert.Equal(
            "CREATE TABLE \"lt__players\" (\"id\" BIGINT GENERATED BY DEFAULT AS IDENTITY NOT NULL, \"name\" VARCHAR(32) NOT NULL, \"coins\" INTEGER NOT NULL DEFAULT 5, \"ok\" BOOLEAN NOT NULL DEFAULT TRUE, \"seen\" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP, \"bal\" NUMERIC(12,2) NULL, \"meta\" JSONB NULL, PRIMARY KEY (\"id\"))",
            SqlDialect.Postgres.CreateTableSql("lt__players", Table(Players)));
    }

    [Fact]
    public void Sqlite_create_table_uses_an_inline_autoincrement_key()
    {
        Assert.Equal(
            "CREATE TABLE \"lt__players\" (\"id\" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, \"name\" TEXT NOT NULL, \"coins\" INTEGER NOT NULL DEFAULT 5, \"ok\" INTEGER NOT NULL DEFAULT 1, \"seen\" TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f000Z', 'now')), \"bal\" NUMERIC NULL, \"meta\" TEXT NULL)",
            SqlDialect.Sqlite.CreateTableSql("lt__players", Table(Players)));
        Assert.Equal("CREATE TABLE \"t\" (\"a\" INTEGER NOT NULL, \"b\" INTEGER NOT NULL, PRIMARY KEY (\"a\", \"b\"))",
            SqlDialect.Sqlite.CreateTableSql("t", Table("""{"name":"t","primaryKey":["a","b"],"columns":[{"name":"a","type":"int32"},{"name":"b","type":"int32"}]}""")));
    }

    [Fact]
    public void Defaults_are_escaped()
    {
        var t = Table("""{"name":"t","columns":[{"name":"s","type":"string","length":20,"default":"it's"},{"name":"u","type":"uuid","default":"123E4567-E89B-12D3-A456-426614174000"}]}""");
        Assert.Contains("DEFAULT 'it''s'", SqlDialect.Postgres.CreateTableSql("t", t));
        Assert.Contains("DEFAULT '123E4567-E89B-12D3-A456-426614174000'::uuid", SqlDialect.Postgres.CreateTableSql("t", t));
        Assert.Contains("DEFAULT '123e4567-e89b-12d3-a456-426614174000'", SqlDialect.Sqlite.CreateTableSql("t", t));
    }

    [Fact]
    public void Postgres_alters_in_place_and_sqlite_rebuilds()
    {
        var old = Table("""{"name":"t","columns":[{"name":"c","type":"int32"}]}""").Columns[0];
        var nw = Table("""{"name":"t","columns":[{"name":"c","type":"int64","nullable":true}]}""").Columns[0];
        Assert.Equal(
            ["ALTER TABLE \"p\" ALTER COLUMN \"c\" DROP DEFAULT", "ALTER TABLE \"p\" ALTER COLUMN \"c\" TYPE BIGINT USING \"c\"::BIGINT", "ALTER TABLE \"p\" ALTER COLUMN \"c\" DROP NOT NULL"],
            SqlDialect.Postgres.AlterColumnSql("p", old, nw));
        Assert.False(SqlDialect.Sqlite.AltersInPlace);
        Assert.Throws<NotSupportedException>(() => SqlDialect.Sqlite.AlterColumnSql("p", old, nw).ToList());
    }

    [Fact]
    public void Add_column_without_default_gets_a_neutral_value_for_existing_rows()
    {
        var c = Table("""{"name":"t","columns":[{"name":"n","type":"int32"}]}""").Columns[0];
        Assert.Equal(
            ["ALTER TABLE \"p\" ADD COLUMN \"n\" INTEGER NOT NULL DEFAULT 0", "ALTER TABLE \"p\" ALTER COLUMN \"n\" DROP DEFAULT"],
            SqlDialect.Postgres.AddColumnSql("p", c));
        Assert.Equal(["ALTER TABLE \"p\" ADD COLUMN \"n\" INTEGER NOT NULL DEFAULT 0"], SqlDialect.Sqlite.AddColumnSql("p", c));
    }

    [Fact]
    public void Sqlite_rebuild_copies_shared_columns_and_keeps_removed_ones_nullable()
    {
        var desired = Table("""{"name":"t","primaryKey":["id"],"columns":[{"name":"id","type":"int64"},{"name":"nick","type":"string","length":16}],"indexes":[{"name":"by_nick","columns":["nick"],"unique":true}]}""");
        var removed = Table("""{"name":"t","columns":[{"name":"old","type":"int32"}]}""").Columns[0];
        var sql = SqlSchema.RebuildStatements(SqlDialect.Sqlite, "lt__t", desired, new HashSet<string> { "id", "nick", "old" }, [removed]).ToList();
        Assert.Equal(
        [
            "DROP TABLE IF EXISTS \"lt__t__rebuild\"",
            "CREATE TABLE \"lt__t__rebuild\" (\"id\" INTEGER NOT NULL, \"nick\" TEXT NOT NULL, \"old\" INTEGER NULL, PRIMARY KEY (\"id\"))",
            "INSERT INTO \"lt__t__rebuild\" (\"id\", \"nick\", \"old\") SELECT \"id\", \"nick\", \"old\" FROM \"lt__t\"",
            "DROP TABLE IF EXISTS \"lt__t\"",
            "ALTER TABLE \"lt__t__rebuild\" RENAME TO \"lt__t\"",
            "CREATE UNIQUE INDEX \"ix_lt__t_by_nick\" ON \"lt__t\" (\"nick\")",
        ], sql);
        // A column that does not exist yet (just added) is simply not copied.
        var fresh = SqlSchema.RebuildStatements(SqlDialect.Sqlite, "lt__t", desired, new HashSet<string> { "id" }, []).ToList();
        Assert.Contains("INSERT INTO \"lt__t__rebuild\" (\"id\") SELECT \"id\" FROM \"lt__t\"", fresh);
    }

    [Fact]
    public void Upsert_syntax_per_engine()
    {
        var pg = SqlDialect.Postgres.UpsertSql("t", ["a", "b"], ["$1", "$2"], ["a"], ["b"]);
        Assert.Equal("INSERT INTO \"t\" (\"a\", \"b\") VALUES ($1, $2) ON CONFLICT (\"a\") DO UPDATE SET \"b\" = EXCLUDED.\"b\"", pg);
        Assert.Equal("INSERT INTO \"t\" (\"a\", \"b\") VALUES ($1, $2) ON CONFLICT (\"a\") DO UPDATE SET \"b\" = excluded.\"b\"", SqlDialect.Sqlite.UpsertSql("t", ["a", "b"], ["$1", "$2"], ["a"], ["b"]));
        Assert.EndsWith("DO NOTHING", SqlDialect.Sqlite.UpsertSql("t", ["a"], ["$1"], ["a"], []));
    }
}

public class SqlUpsertAndValueTests
{
    static readonly SqlTable Table = new("lt__kv",
        [new SqlColumn("k", ColumnTypes.String, 20), new SqlColumn("v", ColumnTypes.Int32, HasDefault: true), new SqlColumn("email", ColumnTypes.String, 40, true)],
        ["k"], [["email"]]);

    static Dictionary<string, object?> Req(string json) => (Dictionary<string, object?>)MiniJson.Parse(json)!;

    [Fact]
    public void Upsert_updates_the_non_key_columns()
    {
        var st = new SqlStatements(SqlDialect.Postgres).Upsert("lt__kv", Table, Req("""{"keys":["k"],"row":{"k":"a","v":2,"email":null}}"""));
        Assert.Equal("INSERT INTO \"lt__kv\" (\"k\", \"v\", \"email\") VALUES ($1, $2, NULL) ON CONFLICT (\"k\") DO UPDATE SET \"v\" = EXCLUDED.\"v\", \"email\" = EXCLUDED.\"email\"", st.Sql);
        Assert.Equal(new object?[] { "a", 2L }, st.Parameters);
    }

    [Fact]
    public void Upsert_accepts_a_unique_index_and_refuses_other_keys()
    {
        var s = new SqlStatements(SqlDialect.Sqlite);
        Assert.Contains("ON CONFLICT (\"email\")", s.Upsert("t", Table, Req("""{"keys":["email"],"row":{"k":"a","email":"x@y"}}""")).Sql);
        Assert.Equal("invalid_request", Assert.Throws<SqlRequestException>(() => s.Upsert("t", Table, Req("""{"keys":["v"],"row":{"k":"a","v":1}}"""))).Code);
        Assert.Equal("missing_value", Assert.Throws<SqlRequestException>(() => s.Upsert("t", Table, Req("""{"keys":["k"],"row":{"v":1}}"""))).Code);
        Assert.Equal("invalid_request", Assert.Throws<SqlRequestException>(() => s.Upsert("t", Table, Req("""{"row":{"k":"a"}}"""))).Code);
    }

    [Fact]
    public void Postgres_binds_primitives_and_lets_the_server_convert()
    {
        var table = new SqlTable("t",
        [
            new SqlColumn("id", ColumnTypes.Uuid), new SqlColumn("at", ColumnTypes.DateTime), new SqlColumn("d", ColumnTypes.Date),
            new SqlColumn("m", ColumnTypes.Decimal), new SqlColumn("b", ColumnTypes.Bytes, 8, true), new SqlColumn("j", ColumnTypes.Json, Nullable: true),
        ], ["id"]);
        var st = new SqlStatements(SqlDialect.Postgres).Insert("t", table, MiniJson.Parse("""[{"id":"123e4567-e89b-12d3-a456-426614174000","at":"2026-10-28T10:00:00+02:00","d":"2026-10-28","m":"12.50","b":"AQID","j":[1,{"a":null}]}]"""), 10).Single();
        Assert.Equal("INSERT INTO \"t\" (\"id\", \"at\", \"d\", \"m\", \"b\", \"j\") VALUES (CAST($1 AS uuid), CAST($2 AS timestamptz), CAST($3 AS date), CAST($4 AS numeric), $5, CAST($6 AS jsonb)) RETURNING \"id\"", st.Sql);
        Assert.Equal("123e4567-e89b-12d3-a456-426614174000", st.Parameters[0]);
        Assert.Equal("2026-10-28T08:00:00.000000Z", st.Parameters[1]);
        Assert.Equal("2026-10-28", st.Parameters[2]);
        Assert.Equal("12.50", st.Parameters[3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, st.Parameters[4]);
        Assert.Equal("[1,{\"a\":null}]", st.Parameters[5]);
    }

    [Fact]
    public void Values_read_back_tolerate_the_hosts_type_mapping()
    {
        var at = new SqlColumn("at", ColumnTypes.DateTime);
        Assert.Equal("\"2026-10-28T08:00:00.0000000Z\"", JsonText.Value(SqlValues.ToJson(at, new DateTime(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc))));
        Assert.Equal("\"2026-10-28T08:00:00.0000000Z\"", JsonText.Value(SqlValues.ToJson(at, "2026-10-28 08:00:00+00")));
        Assert.Equal("\"2026-10-28\"", JsonText.Value(SqlValues.ToJson(new SqlColumn("d", ColumnTypes.Date), "2026-10-28T00:00:00")));
        Assert.Equal("\"abc\"", JsonText.Value(SqlValues.ToJson(new SqlColumn("u", ColumnTypes.Uuid), "ABC")));
        Assert.Equal("12.5", JsonText.Value(SqlValues.ToJson(new SqlColumn("m", ColumnTypes.Decimal), "12.5")));
        Assert.Equal("true", JsonText.Value(SqlValues.ToJson(new SqlColumn("b", ColumnTypes.Bool), "t")));
        Assert.Equal("\"AQID\"", JsonText.Value(SqlValues.ToJson(new SqlColumn("b", ColumnTypes.Bytes), new byte[] { 1, 2, 3 })));
        Assert.Equal("{\"a\":1}", JsonText.Value(SqlValues.ToJson(new SqlColumn("j", ColumnTypes.Json), "{\"a\":1}")));
        Assert.Null(SqlValues.ToJson(at, DBNull.Value));
    }
}

public class MutationReceiptTests
{
    const string Command = "{\"table\":\"players\",\"filter\":{\"field\":\"id\",\"op\":\"eq\",\"value\":1},\"set\":{\"name\":\"é\"}}";

    static MutationReceiptRequest Request(string json = Command, string op = "Update", string? fingerprint = null) =>
        new("lt", "eff-1", op, fingerprint ?? MutationReceipts.Fingerprint("eff-1", "lt", op, json), json);

    [Fact]
    public void Fingerprint_matches_the_api()
    {
        Assert.Equal("b17872a0e9aa23675dfe6a9b091952ae81bcd8ef8c059c0e2c97113eae9ac78b", MutationReceipts.Fingerprint("eff-1", "lt", "Update", Command));
    }

    [Fact]
    public void A_valid_request_passes()
    {
        MutationReceipts.Validate(Request());
    }

    [Fact]
    public void A_wrong_fingerprint_is_a_conflict()
    {
        var ex = Assert.Throws<SqlRequestException>(() => MutationReceipts.Validate(Request(fingerprint: new string('0', 64))));
        Assert.Equal((409, "receipt_conflict"), (ex.Status, ex.Code));
    }

    [Theory]
    [InlineData("{\"table\":\"p\",\"table\":\"q\"}")]                          // duplicate key
    [InlineData("{\"table\":\"p\",\"Table\":\"q\"}")]                          // duplicate key, case-insensitive at the command root
    [InlineData("{\"table\":\"p\",\"all\":true}")]                             // unfiltered
    [InlineData("{\"table\":\"p\",\"namespace\":\"other\"}")]                  // foreign namespace
    [InlineData("[1]")]                                                        // not an object
    [InlineData("")]
    public void Unsafe_commands_are_refused(string json)
    {
        var ex = Assert.Throws<SqlRequestException>(() => MutationReceipts.Validate(Request(json, fingerprint: new string('a', 64))));
        Assert.Equal("invalid_request", ex.Code);
    }

    [Fact]
    public void Limits_and_identity_rules()
    {
        Assert.Throws<SqlRequestException>(() => MutationReceipts.Validate(Request("{\"table\":\"" + new string('x', 8200) + "\"}", fingerprint: new string('a', 64))));
        Assert.Throws<SqlRequestException>(() => MutationReceipts.ValidateIdentity("lt", "bad id", "Update", new string('a', 64)));
        Assert.Throws<SqlRequestException>(() => MutationReceipts.ValidateIdentity("lt", new string('e', 129), "Update", new string('a', 64)));
        Assert.Throws<SqlRequestException>(() => MutationReceipts.ValidateIdentity("lt", "e", "Insert", new string('a', 64)));
        Assert.Throws<SqlRequestException>(() => MutationReceipts.ValidateIdentity("lt", "e", "Update", new string('A', 64)));
        MutationReceipts.ValidateIdentity("lt", "a.b:c_d-9", "Delete", new string('f', 64));
    }

    [Fact]
    public void Envelope_must_contain_exactly_the_expected_text_members()
    {
        var ok = MutationReceipts.ParseEnvelope("{\"namespace\":\"lt\",\"effectId\":\"e\"}", "namespace", "effectId");
        Assert.Equal("lt", ok["namespace"]);
        Assert.Throws<SqlRequestException>(() => MutationReceipts.ParseEnvelope("{\"namespace\":\"lt\"}", "namespace", "effectId"));
        Assert.Throws<SqlRequestException>(() => MutationReceipts.ParseEnvelope("{\"namespace\":\"lt\",\"effectId\":1}", "namespace", "effectId"));
        Assert.Throws<SqlRequestException>(() => MutationReceipts.ParseEnvelope("{\"namespace\":\"lt\",\"namespace\":\"x\"}", "namespace", "effectId"));
        Assert.Throws<SqlRequestException>(() => MutationReceipts.ParseEnvelope("{\"namespace\":\"lt\",\"effectId\":\"e\",\"x\":\"y\"}", "namespace", "effectId"));
    }

    [Fact]
    public void Receipt_json_has_the_api_shape()
    {
        var r = new MutationReceipt("e", "lt", "Update", new string('a', 64), "succeeded", "", "{\"affected\":1}");
        Assert.Equal("{\"effectId\":\"e\",\"namespace\":\"lt\",\"operation\":\"Update\",\"fingerprint\":\"" + new string('a', 64) + "\",\"state\":\"succeeded\",\"code\":\"\",\"json\":\"{\\\"affected\\\":1}\"}", r.ToJson());
    }

    [Theory]
    [InlineData("duplicate key value violates unique constraint \"x\"", "constraint_violation", 409)]
    [InlineData("UNIQUE constraint failed: t.a", "constraint_violation", 409)]
    [InlineData("database is locked", "conflict_retry", 409)]
    [InlineData("canceling statement due to statement timeout", "database_timeout", 504)]
    [InlineData("invalid input syntax for type uuid", "invalid_value", 400)]
    [InlineData("something unexpected with secret value 42", "database_error", 500)]
    public void Driver_errors_map_to_stable_codes_without_leaking_the_message(string message, string code, int status)
    {
        var e = SqlErrors.Classify("SqlException", message);
        Assert.Equal((status, code), (e.Status, e.Code));
        Assert.DoesNotContain("42", e.Message);
    }
}
