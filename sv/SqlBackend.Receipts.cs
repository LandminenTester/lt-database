using LtDatabase;

namespace LtDatabase.Server;

/// <summary>
/// Atomic mutation receipts (mutation.execute / mutation.receipt): the business mutation and its permanent receipt commit in one
/// transaction. Same contract as lt-database-api, see docs/SQL-BACKEND.md for what the SQL engines guarantee.
/// </summary>
internal sealed partial class SqlBackend
{
    const string ReceiptTable = "_ldb_mutation_receipts";

    async Future<re.sql.SqlTransaction> BeginReadCommitted(re.sql.SqlConnection db) =>
        // SQLite has a single writer and serializes transactions itself; it does not take an isolation level.
        dialect!.Name == "postgres" ? await db.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted) : await db.BeginTransactionAsync();

    async Future<string> ReadReceipt(DatabaseConfig cfg, string payload)
    {
        var env = MutationReceipts.ParseEnvelope(payload, "namespace", "effectId", "operation", "fingerprint");
        var ns = (string)env["namespace"]!; var effectId = (string)env["effectId"]!; var operation = (string)env["operation"]!; var fingerprint = (string)env["fingerprint"]!;
        MutationReceipts.ValidateIdentity(ns, effectId, operation, fingerprint);

        await using var db = await Open(cfg);
        await using var tx = await BeginReadCommitted(db);
        MutationReceipt? receipt;
        try
        {
            // Reading a receipt never executes, retries or infers the outcome of the business command.
            receipt = await LoadReceipt(cfg, db, tx, ns, effectId, operation, fingerprint, forUpdate: false, null);
            await tx.CommitAsync();
        }
        catch (Exception)
        {
            await tx.RollbackAsync();
            throw;
        }
        return receipt is null ? JsonText.Object(("found", false)) : JsonText.Object(("found", true), ("receipt", new JsonText.Raw(receipt.ToJson())));
    }

    async Future<string> ExecuteReceipted(DatabaseConfig cfg, string payload)
    {
        var env = MutationReceipts.ParseEnvelope(payload, "namespace", "effectId", "operation", "fingerprint", "json");
        var request = new MutationReceiptRequest((string)env["namespace"]!, (string)env["effectId"]!, (string)env["operation"]!, (string)env["fingerprint"]!, (string)env["json"]!);
        MutationReceipts.Validate(request);

        await using var db = await Open(cfg);
        var d = dialect!;
        await using var tx = await BeginReadCommitted(db);
        try
        {
            // The unique reservation blocks concurrent writers until the first transaction commits or rolls back.
            // A pending reservation is never committed, so it is never visible to receipt readers.
            var ps = new SqlParams();
            var owner = Guid.NewGuid().ToString("N");
            var values = new[] { ps.Add(request.Namespace), ps.Add(request.EffectId), ps.Add(request.Operation), ps.Add(request.Fingerprint), ps.Add("pending"), ps.Add(owner), ps.Add("{}") };
            var reserve = d.UpsertSql(ReceiptTable, ["ns", "effect_id", "operation", "fingerprint", "state", "code", "result_json"], values, ["ns", "effect_id"], []);
            await Run(cfg, db, tx, reserve, ps.Values);

            var existing = await LoadReceipt(cfg, db, tx, request.Namespace, request.EffectId, request.Operation, request.Fingerprint, forUpdate: true, owner);
            if (existing is not null)
            {
                await tx.CommitAsync();
                return existing.ToJson();
            }

            await Run(cfg, db, tx, "SAVEPOINT ldb_mutation", []);
            MutationReceipt receipt;
            try
            {
                receipt = await ApplyMutation(cfg, db, tx, request);
            }
            catch (SqlRequestException ex) when (ex.Status is >= 400 and < 500)
            {
                await Run(cfg, db, tx, "ROLLBACK TO SAVEPOINT ldb_mutation", []);
                receipt = Failed(request, ex.Code);
            }
            catch (Exception ex) when (SqlErrors.Classify(ex.GetType().Name, ex.Message).Code is "constraint_violation" or "invalid_value")
            {
                Console.WriteLine($"[lt_database] sql mutation rejected: {ex.GetType().Name}");
                await Run(cfg, db, tx, "ROLLBACK TO SAVEPOINT ldb_mutation", []);
                receipt = Failed(request, SqlErrors.Classify(ex.GetType().Name, ex.Message).Code);
            }

            var rp = new SqlParams();
            var finish = $"UPDATE {d.Quote(ReceiptTable)} SET state = {rp.Add(receipt.State)}, code = {rp.Add(receipt.Code)}, result_json = {rp.Add(receipt.Json)} WHERE ns = {rp.Add(request.Namespace)} AND effect_id = {rp.Add(request.EffectId)} AND state = 'pending'";
            if (await Run(cfg, db, tx, finish, rp.Values) != 1)
                throw new SqlRequestException(500, "receipt_corrupt", "The mutation receipt could not be finalized.");
            // A failure or lost reply during commit is deliberately propagated. Only a later lookup can resolve it.
            await tx.CommitAsync();
            return receipt.ToJson();
        }
        catch (Exception)
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    static MutationReceipt Failed(MutationReceiptRequest r, string code) =>
        new(r.EffectId, r.Namespace, r.Operation, r.Fingerprint, "failed", code, "{\"affected\":0}");

    async Future<MutationReceipt?> LoadReceipt(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction tx, string ns, string effectId, string operation, string fingerprint, bool forUpdate, string? pendingOwner)
    {
        var d = dialect!;
        var lockClause = forUpdate && d.Name == "postgres" ? " FOR UPDATE" : "";
        var rows = await Query(cfg, db, tx, $"SELECT operation, fingerprint, state, code, result_json FROM {d.Quote(ReceiptTable)} WHERE ns = $1 AND effect_id = $2{lockClause}", [ns, effectId]);
        if (rows.Count == 0) return null;
        var r = rows[0];
        if ((string)r[0]! != operation || (string)r[1]! != fingerprint)
            throw new SqlRequestException(409, "receipt_conflict", "This effect ID is already bound to another mutation.");
        var state = (string)r[2]!;
        if (state == "pending" && forUpdate && (string)r[3]! == pendingOwner) return null;
        if (state is not ("succeeded" or "failed"))
            throw new SqlRequestException(500, "receipt_corrupt", "A receipt has no committed terminal outcome.");
        return new MutationReceipt(effectId, ns, operation, fingerprint, state, (string)r[3]!, (string)r[4]!);
    }

    async Future<MutationReceipt> ApplyMutation(DatabaseConfig cfg, re.sql.SqlConnection db, re.sql.SqlTransaction tx, MutationReceiptRequest request)
    {
        var d = dialect!;
        // The schema is read on the transaction's own connection, never from the cache.
        var stored = await LoadStored(cfg, db, tx, request.Namespace);
        var schema = stored.Schema ?? throw new SqlRequestException(404, "namespace_unknown", "This namespace has no schema yet.");
        var command = MiniJson.AsObject(MiniJson.Parse(request.Json))!;
        var table = TableOf(schema, SqlSchema.Table(MiniJson.GetString(command, "table")));
        var physical = SqlSchema.Physical(request.Namespace, table.Name);
        var query = table.ToQuery();
        var statements = new SqlStatements(d);

        SqlStatement write;
        string? matchSql = null; IReadOnlyList<object?> matchParams = [];
        switch (request.Operation)
        {
            case "Upsert":
                write = statements.Upsert(physical, query, command);
                break;
            default:
                write = request.Operation == "Update" ? statements.Update(physical, query, command) : statements.Delete(physical, query, command);
                var ps = new SqlParams();
                var where = statements.Where(command.GetValueOrDefault("filter"), query, ps) ?? throw new SqlRequestException(400, "filter_required", "Atomic mutations require a filter.");
                matchSql = $"SELECT 1 FROM {d.Quote(physical)} WHERE {where} LIMIT 2{(d.Name == "postgres" ? " FOR UPDATE" : "")}";
                matchParams = ps.Values;
                break;
        }

        if (matchSql is not null && (await Query(cfg, db, tx, matchSql, matchParams)).Count > 1)
            throw new SqlRequestException(409, "multiple_rows", "An atomic mutation may affect at most one row.");
        var affected = await Run(cfg, db, tx, write.Sql, write.Parameters);
        if (affected < 0 || affected > 1)
            throw new SqlRequestException(409, "multiple_rows", "An atomic mutation may affect at most one row.");
        var matched = request.Operation == "Upsert" || affected == 1;
        return new MutationReceipt(request.EffectId, request.Namespace, request.Operation, request.Fingerprint,
            matched ? "succeeded" : "failed", matched ? "" : "no_match", matched ? "{\"affected\":1}" : "{\"affected\":0}");
    }
}
