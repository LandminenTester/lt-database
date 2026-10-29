using LtDatabase;
using LtDatabaseClient;

// Scenario for mutation.execute / mutation.receipt: db-smoke-mutation <label> [connector-bundle].
// Every effect ID carries a per-run prefix because receipts are permanent and survive the schema cleanup at the end.
public static partial class App
{
    static readonly string RunId = "t" + DateTime.UtcNow.Ticks;
    static DbResult? raceFirst;
    static int effectCounter;

    static string Eff(string name) => RunId + "-" + name;

    static string Fp(string effect, string op, string json) => MutationReceipts.Fingerprint(effect, Ns, op, json);

    static string Envelope(string effect, string op, string json, string? fingerprint = null) =>
        JsonText.Object(("namespace", Ns), ("effectId", effect), ("operation", op), ("fingerprint", fingerprint ?? Fp(effect, op, json)), ("json", json));

    static string Lookup(string effect, string op, string json) =>
        JsonText.Object(("namespace", Ns), ("effectId", effect), ("operation", op), ("fingerprint", Fp(effect, op, json)));

    static string UpsertJson(long id, string name, int qty) =>
        JsonText.Object(("namespace", Ns), ("table", "items"), ("row", new JsonText.Raw(Q.Row(("id", id), ("name", name), ("qty", qty)))), ("keys", new JsonText.Raw(JsonText.Array(["id"]))));

    static string UpdateJson(string filter, string? set = null, string? inc = null)
    {
        var parts = new List<(string, object?)> { ("namespace", Ns), ("table", "items"), ("filter", new JsonText.Raw(filter)) };
        if (set is not null) parts.Add(("set", new JsonText.Raw(set)));
        if (inc is not null) parts.Add(("inc", new JsonText.Raw(inc)));
        return JsonText.Object([.. parts]);
    }

    static string DeleteJson(string filter) => JsonText.Object(("namespace", Ns), ("table", "items"), ("filter", new JsonText.Raw(filter)));

    static string ItemsSchema() => S.Schema(S.Table("items", ["id"],
        [S.Int64("id"), S.Text("name", 32, unique: true), S.Int32("qty", @default: 0)]));

    static Step Mut(string name, string op, string json, Func<DbResult, string?> check, string? effect = null, string? fingerprint = null) =>
        new(name, d => Db.Call("mutation.execute", Envelope(effect ?? Eff("auto" + (++effectCounter)), op, json, fingerprint), d), check);

    /// <summary>Receipt object of an executed mutation: the call succeeded and the receipt has the expected state and code.</summary>
    static Func<DbResult, string?> Receipt(string state, string code = "") =>
        r => Both(Ok(r), () => Both(Field(r, "state", state), () => Field(r, "code", code)));

    static string? ItemQty(DbResult r, long expected)
    {
        var rows = Db.Rows(r);
        if (rows.Count != 1) return $"expected 1 row, got {rows.Count}";
        return Num(rows[0]["qty"]) == expected ? null : $"qty was {Num(rows[0]["qty"])}, expected {expected}";
    }


    static List<Step> BuildMutation()
    {
        var inc = UpdateJson(F.Eq("id", 1), inc: Q.Row(("qty", 1)));
        var nomatch = UpdateJson(F.Eq("id", 999), set: Q.Row(("qty", 1)));
        var many = UpdateJson(F.Gte("qty", 0), set: Q.Row(("qty", 0)));
        var clash = UpsertJson(20, "a", 1);
        var race = UpdateJson(F.Eq("id", 3), inc: Q.Row(("qty", 1)));
        var ids = new[] { Eff("upsert"), Eff("inc"), Eff("nomatch"), Eff("many"), Eff("clash"), Eff("delete"), Eff("race") };
        var otherNamespace = JsonText.Object(("namespace", "smoke_other"), ("table", "items"), ("filter", new JsonText.Raw(F.Eq("id", 1))));
        var allTrue = JsonText.Object(("namespace", Ns), ("table", "items"), ("all", true), ("set", new JsonText.Raw(Q.Row(("qty", 0)))));
        var duplicateKey = "{\"namespace\":\"smoke_test\",\"table\":\"items\",\"table\":\"items\",\"filter\":" + F.Eq("id", 1) + "}";

        return
        [
            new("sync items schema", d => Db.Sync(Ns, ItemsSchema(), d), Ok),
            new("seed three rows", d => Db.Call("insert", JsonText.Object(("namespace", Ns), ("table", "items"), ("rows", new JsonText.Raw("[" + string.Join(",", Q.Row(("id", 1), ("name", "a"), ("qty", 1)), Q.Row(("id", 2), ("name", "b"), ("qty", 1)), Q.Row(("id", 3), ("name", "c"), ("qty", 5))) + "]"))), d), Ok),

            Mut("execute upsert creates the row", "Upsert", UpsertJson(10, "m10", 7), Receipt("succeeded"), ids[0]),
            new("upserted row exists", d => Db.Find(Ns, "items", d, filterJson: F.Eq("id", 10)), r => Both(Ok(r), () => ItemQty(r, 7))),
            Mut("execute update inc applies once", "Update", inc, Receipt("succeeded"), ids[1]),
            Mut("same effect and fingerprint replays the receipt", "Update", inc, Receipt("succeeded"), ids[1]),
            new("replay did not apply the increment twice", d => Db.Find(Ns, "items", d, filterJson: F.Eq("id", 1)), r => Both(Ok(r), () => ItemQty(r, 2))),
            Mut("same effect with other mutation is a receipt conflict", "Update", UpdateJson(F.Eq("id", 1), inc: Q.Row(("qty", 100))), r => Error(r, "receipt_conflict"), ids[1]),
            Mut("same effect with other operation is a receipt conflict", "Delete", DeleteJson(F.Eq("id", 1)), r => Error(r, "receipt_conflict"), ids[1]),
            new("receipt lookup finds the stored receipt", d => Db.Call("mutation.receipt", Lookup(ids[1], "Update", inc), d),
                r => Both(Ok(r), () => Both(Field(r, "found", "true"), () => r.Json.Contains("\"state\":\"succeeded\"") ? null : "stored receipt is not succeeded"))),
            new("receipt lookup of an unknown effect is not found", d => Db.Call("mutation.receipt", Lookup(Eff("never"), "Update", inc), d), r => Both(Ok(r), () => Field(r, "found", "false"))),
            new("receipt lookup with another fingerprint is a conflict", d => Db.Call("mutation.receipt", Lookup(ids[1], "Update", many), d), r => Error(r, "receipt_conflict")),

            Mut("update without a match is a no_match receipt", "Update", nomatch, Receipt("failed", "no_match"), ids[2]),
            Mut("update of several rows is refused and recorded", "Update", many, Receipt("failed", "multiple_rows"), ids[3]),
            new("refused update changed nothing", d => Db.Count(Ns, "items", d, F.Eq("qty", 0)), r => Both(Ok(r), () => Db.Number(r) == 0 ? null : $"{Db.Number(r)} row(s) were changed")),
            Mut("constraint violation is a durable failed receipt", "Upsert", clash, Receipt("failed", "constraint_violation"), ids[4]),
            Mut("repeating the failed effect returns the same receipt", "Upsert", clash, Receipt("failed", "constraint_violation"), ids[4]),
            new("failed upsert left no row behind", d => Db.Count(Ns, "items", d, F.Eq("id", 20)), r => Both(Ok(r), () => Db.Number(r) == 0 ? null : "row 20 exists")),

            Mut("all=true is rejected", "Update", allTrue, r => Error(r, "invalid_request")),
            Mut("namespace mismatch in the command is rejected", "Delete", otherNamespace, r => Error(r, "invalid_request")),
            Mut("duplicate keys in the command are rejected", "Delete", duplicateKey, r => Error(r, "invalid_request")),
            Mut("wrong fingerprint is rejected", "Delete", DeleteJson(F.Eq("id", 1)), r => Error(r, "receipt_conflict"), null, new string('0', 64)),
            new("envelope with an extra member is rejected", d => Db.Call("mutation.execute", JsonText.Object(("namespace", Ns), ("effectId", Eff("extra")), ("operation", "Delete"), ("fingerprint", new string('0', 64)), ("json", "{}"), ("x", "y")), d), r => Error(r, "invalid_request")),

            Mut("delete one row", "Delete", DeleteJson(F.Eq("id", 2)), Receipt("succeeded"), ids[5]),
            new("deleted row is gone", d => Db.Count(Ns, "items", d, F.Eq("id", 2)), r => Both(Ok(r), () => Db.Number(r) == 0 ? null : "row 2 still exists")),

            new("two parallel executions of one effect apply once", d =>
            {
                raceFirst = null;
                void Finish(DbResult r)
                {
                    if (raceFirst is null) { raceFirst = r; return; }
                    d(r);
                }
                var env = Envelope(ids[6], "Update", race);
                Db.Call("mutation.execute", env, Finish);
                Db.Call("mutation.execute", env, Finish);
            }, r =>
            {
                // SQLite has one writer: the host may answer the waiting call with conflict_retry instead of making it wait.
                bool Good(DbResult x) => (x.State == "ok" && Field(x, "state", "succeeded") is null) || (x.State == "error" && x.Code == "conflict_retry");
                if (raceFirst is null || !Good(raceFirst) || !Good(r)) return $"unexpected results: {raceFirst?.State}/{raceFirst?.Code} and {r.State}/{r.Code}";
                return raceFirst.State == "ok" || r.State == "ok" ? null : "neither call succeeded";
            }),
            new("parallel effect was applied exactly once", d => Db.Find(Ns, "items", d, filterJson: F.Eq("id", 3)), r => Both(Ok(r), () => ItemQty(r, 6))),
            new("receipt of the parallel effect is stored", d => Db.Call("mutation.receipt", Lookup(ids[6], "Update", race), d), r => Both(Ok(r), () => Field(r, "found", "true"))),

            new("cleanup: drop everything again", d => Db.Sync(Ns, S.Schema(), d), Ok),
        ];
    }
}
