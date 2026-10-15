using LtDatabase;
using LtDatabaseClient;

// Runtime smoke test for the lt_database connector.
//   db-smoke <label>    runs every connector function against the configured database and prints PASS/FAIL per step.
// The namespace "smoke_test" must be in vfs/database.json (allowedNamespaces) and in the API key. "smoke_denied" must NOT be.
public static class App
{
    const string Ns = "smoke_test";

    sealed record Step(string Name, Action<Action<DbResult>> Run, Func<DbResult, string?> Check);

    static List<Step> steps = [];
    static int index, passed;
    static string label = "";
    static bool running;

    public static void Start()
    {
        re.Con.RegisterCommand("db-smoke", a => Begin(a.FirstOrDefault() ?? "unnamed"));
        Console.WriteLine("[db-smoke] ready. Run: db-smoke <label>");
    }

    static void Begin(string name)
    {
        if (running) { Console.WriteLine("[db-smoke] a run is already in progress"); return; }
        label = name; steps = Build(); index = 0; passed = 0; running = true;
        Console.WriteLine($"[db-smoke] START {label} ({steps.Count} steps)");
        Next();
    }

    static void Next()
    {
        if (index >= steps.Count)
        {
            Console.WriteLine($"[db-smoke] RESULT {label}: {passed}/{steps.Count} passed");
            running = false;
            return;
        }
        var step = steps[index];
        step.Run(result =>
        {
            string? problem;
            try { problem = step.Check(result); }
            catch (Exception ex) { problem = "check threw " + ex.GetType().Name; }
            var n = (index + 1).ToString("00");
            if (problem is null) { passed++; Console.WriteLine($"[db-smoke] PASS {n} {step.Name}"); }
            else Console.WriteLine($"[db-smoke] FAIL {n} {step.Name}: {problem} (state={result.State} status={result.Status} code={result.Code})");
            index++;
            Next();
        });
    }

    // ---- checks

    static string? Ok(DbResult r) => r.State == "ok" ? null : "expected ok: " + Db.ErrorMessage(r);
    static string? Error(DbResult r, string code) => r.State == "error" && r.Code == code ? null : $"expected error '{code}'";
    static string? Both(string? a, Func<string?> b) => a ?? b();
    static string? Field(DbResult r, string key, string expected)
    {
        if (!MiniJson.TryParse(r.Json, out var parsed)) return "result is not JSON";
        var o = MiniJson.AsObject(parsed);
        var actual = o is not null && o.TryGetValue(key, out var v) ? (v?.ToString() ?? "null") : "<missing>";
        return actual == expected || (expected == "true" && actual == "True") || (expected == "false" && actual == "False") ? null : $"{key} was {actual}, expected {expected}";
    }
    static string? Rows(DbResult r, int count)
    {
        var rows = Db.Rows(r);
        return rows.Count == count ? null : $"expected {count} row(s), got {rows.Count}";
    }
    static long Num(object? v) => v is null ? long.MinValue : Convert.ToInt64(v);

    // ---- the scenario

    const string Players = "players";

    static string SchemaV1() => S.Schema(S.Table(Players, ["id"],
    [
        S.Int64("id", autoIncrement: true), S.Text("name", 32, unique: true), S.Int32("score", @default: 0),
        S.LongText("bio", nullable: true), S.Bool("banned", @default: false), S.Json("meta"), S.DateTime("created", defaultNow: true),
    ], [S.Index("by_score", false, "score")]));

    static string SchemaV2() => S.Schema(S.Table(Players, ["id"],
    [
        S.Int64("id", autoIncrement: true), S.Text("name", 64, unique: true), S.Int32("score", @default: 0),
        S.LongText("about", nullable: true, renamedFrom: "bio"), S.Json("meta"), S.DateTime("created", defaultNow: true), S.Int32("level", @default: 1),
    ], [S.Index("by_score", false, "score")]));

    static List<Step> Build() =>
    [
        new("health (signed round trip)", d => Db.Health(d), r => Both(Ok(r), () => Field(r, "database", "true"))),
        new("clean slate: sync empty schema", d => Db.Sync(Ns, S.Schema(), d), Ok),
        new("sync v1 creates the table", d => Db.Sync(Ns, SchemaV1(), d), r => Both(Ok(r), () => Field(r, "status", "applied"))),
        new("sync v1 again changes nothing", d => Db.Sync(Ns, SchemaV1(), d), r => Both(Ok(r), () => Field(r, "status", "unchanged"))),
        new("describe returns the stored schema", d => Db.Describe(Ns, d), r => Both(Ok(r), () => r.Json.Contains("\"players\"") ? null : "players missing")),
        new("insert 3 rows with generated ids", d => Db.Insert(Ns, Players,
            [Q.Row(("name", "alice"), ("score", 10)), Q.Row(("name", "bob"), ("score", 20), ("bio", "hi")), Q.Row(("name", "carol"), ("score", 30), ("banned", true), ("meta", new JsonText.Raw("{\"k\":[1,2,{\"x\":null}]}")))], d),
            r => Both(Ok(r), () => Db.Number(r) == 3 && Db.InsertedIds(r).Count == 3 ? null : "expected 3 rows and 3 ids")),
        new("insert hostile text stays literal", d => Db.Insert(Ns, Players, Q.Row(("name", "x'); DROP TABLE players;--"), ("score", 1)), d), Ok),
        new("find all, sorted by score desc", d => Db.Find(Ns, Players, d, sortJson: Q.Sort(("score", true))),
            r => Both(Ok(r), () => Both(Rows(r, 4), () => Db.Rows(r)[0]["name"] as string == "carol" ? null : "first row is not carol"))),
        new("find with and/not/gte", d => Db.Find(Ns, Players, d, filterJson: F.And(F.Gte("score", 10), F.Not(F.Eq("name", "carol")), F.Lt("score", 25))), r => Both(Ok(r), () => Rows(r, 2))),
        new("find with in/startsWith/contains", d => Db.Find(Ns, Players, d, filterJson: F.Or(F.In("name", "alice", "zed"), F.StartsWith("name", "car"), F.Contains("name", "ob"))), r => Both(Ok(r), () => Rows(r, 3))),
        new("find the hostile row by equality", d => Db.Find(Ns, Players, d, filterJson: F.Eq("name", "x'); DROP TABLE players;--")), r => Both(Ok(r), () => Rows(r, 1))),
        new("find with limit reports hasMore", d => Db.Find(Ns, Players, d, sortJson: Q.Sort(("score", false)), limit: 2), r => Both(Ok(r), () => Both(Rows(r, 2), () => Db.HasMore(r) ? null : "hasMore should be true"))),
        new("find projection returns only asked columns", d => Db.Find(Ns, Players, d, filterJson: F.Eq("name", "bob"), fields: ["name", "score"]), r => Both(Ok(r), () => Db.Rows(r)[0].Count == 2 ? null : "expected 2 columns")),
        new("count all", d => Db.Count(Ns, Players, d), r => Both(Ok(r), () => Db.Number(r) == 4 ? null : $"count was {Db.Number(r)}")),
        new("count with filter", d => Db.Count(Ns, Players, d, F.Gt("score", 5)), r => Both(Ok(r), () => Db.Number(r) == 3 ? null : $"count was {Db.Number(r)}")),
        new("update set + inc", d => Db.Update(Ns, Players, F.Eq("name", "alice"), d, setJson: Q.Row(("bio", "updated")), incJson: Q.Row(("score", 5))), r => Both(Ok(r), () => Db.Number(r) == 1 ? null : "expected 1 affected")),
        new("update is visible", d => Db.Find(Ns, Players, d, filterJson: F.Eq("name", "alice")),
            r => Both(Ok(r), () => Both(Rows(r, 1), () => Num(Db.Rows(r)[0]["score"]) == 15 && Db.Rows(r)[0]["bio"] as string == "updated" ? null : "alice was not updated"))),
        new("upsert inserts a new key", d => Db.Upsert(Ns, Players, Q.Row(("name", "dave"), ("score", 1)), ["name"], d), Ok),
        new("upsert updates the same key", d => Db.Upsert(Ns, Players, Q.Row(("name", "dave"), ("score", 99)), ["name"], d), Ok),
        new("upsert left exactly one dave", d => Db.Find(Ns, Players, d, filterJson: F.Eq("name", "dave")),
            r => Both(Ok(r), () => Both(Rows(r, 1), () => Num(Db.Rows(r)[0]["score"]) == 99 ? null : "score is not 99"))),
        new("idempotent insert (first)", d => Db.Call("insert", "{\"namespace\":\"smoke_test\",\"table\":\"players\",\"rows\":[{\"name\":\"idem\"}],\"idempotencyKey\":\"smoke-idem-1\"}", d), Ok),
        new("idempotent insert (retry writes nothing)", d => Db.Call("insert", "{\"namespace\":\"smoke_test\",\"table\":\"players\",\"rows\":[{\"name\":\"idem\"}],\"idempotencyKey\":\"smoke-idem-1\"}", d), Ok),
        new("only one idem row exists", d => Db.Count(Ns, Players, d, F.Eq("name", "idem")), r => Both(Ok(r), () => Db.Number(r) == 1 ? null : $"found {Db.Number(r)}")),
        new("duplicate unique value is a clean 409", d => Db.Insert(Ns, Players, Q.Row(("name", "alice")), d), r => Error(r, "constraint_violation")),
        new("unknown column is a clean 400", d => Db.Insert(Ns, Players, Q.Row(("name", "q"), ("nope", 1)), d), r => Error(r, "unknown_column")),
        new("too long value is refused", d => Db.Insert(Ns, Players, Q.Row(("name", new string('z', 40))), d), r => Error(r, "value_too_long")),
        new("update without filter is refused", d => Db.Call("update", "{\"namespace\":\"smoke_test\",\"table\":\"players\",\"set\":{\"score\":0}}", d), r => Error(r, "filter_required")),
        new("unfiltered delete needs the key permission", d => Db.Call("delete", "{\"namespace\":\"smoke_test\",\"table\":\"players\",\"all\":true}", d), r => Error(r, "forbidden")),
        new("namespace outside the allow-list is refused", d => Db.Count("smoke_denied", Players, d), r => Error(r, "namespace_not_allowed")),
        new("dangerous filter is rejected", d => Db.Call("find", "{\"namespace\":\"smoke_test\",\"table\":\"players\",\"filter\":{\"field\":\"name\",\"op\":\"$where\",\"value\":1}}", d), r => Error(r, "invalid_filter")),
        new("delete one row", d => Db.Delete(Ns, Players, F.Eq("name", "bob"), d), r => Both(Ok(r), () => Db.Number(r) == 1 ? null : "expected 1 affected")),
        new("delete is visible", d => Db.Count(Ns, Players, d), r => Both(Ok(r), () => Db.Number(r) == 5 ? null : $"count was {Db.Number(r)}")),
        new("dry run of schema v2 plans, changes nothing", d => Db.Sync(Ns, SchemaV2(), d, dryRun: true),
            r => Both(Ok(r), () => Both(Field(r, "status", "planned"), () => r.Json.Contains("\"destructive\":true") ? null : "no destructive step reported"))),
        new("sync v2 renames, widens, adds and drops", d => Db.Sync(Ns, SchemaV2(), d), r => Both(Ok(r), () => Field(r, "status", "applied"))),
        new("data survived the schema change", d => Db.Find(Ns, Players, d, filterJson: F.Eq("name", "alice")),
            r => Both(Ok(r), () => Both(Rows(r, 1), () =>
            {
                var row = Db.Rows(r)[0];
                if (row.ContainsKey("banned") || row.ContainsKey("bio")) return "dropped or renamed columns are still there";
                if (row["about"] as string != "updated") return "renamed column lost its data";
                return Num(row["level"]) == 1 ? null : "new column default missing";
            }))),
        new("longer name fits after widening", d => Db.Insert(Ns, Players, Q.Row(("name", new string('n', 60))), d), Ok),
        new("cleanup: drop everything again", d => Db.Sync(Ns, S.Schema(), d), Ok),
        new("table is gone after cleanup", d => Db.Count(Ns, Players, d), r => Error(r, "namespace_unknown") is null || Error(r, "unknown_table") is null ? null : $"expected unknown table, got {r.Code}"),
    ];
}
