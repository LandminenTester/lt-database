using LtDatabase.Server;
using LtDatabase;

public static class App
{
    public static void Start()
    {
        // Server realm only. There is no client component, so players have no way to reach any of this.
        re.Exports.Add<DbTicket, DbCall>("Call", DbClient.Call);
        re.Exports.Add<DbResult, DbResultQuery>("TakeResult", DbClient.Take);

        re.Con.RegisterCommand("lt-db-status", _ => Console.WriteLine("[lt_database] " + DbClient.Summary));
        re.Con.RegisterCommand("lt-db-reload", _ => DbClient.Reload());
        re.Con.RegisterCommand("lt-db-health", _ =>
        {
            var ticket = DbClient.Call(new DbCall("health", "{}"));
            if (!ticket.Accepted) { Console.WriteLine("[lt_database] health: " + ticket.Error); return; }
            void Poll()
            {
                var r = DbClient.Take(new DbResultQuery(ticket.Id));
                if (r.State == "pending") { Futures.Scheduler.Push(100, Poll); return; }
                Console.WriteLine($"[lt_database] health: {r.State} status={r.Status} {r.Code} {r.Json}");
            }
            Futures.Scheduler.Push(100, Poll);
        });

        DbClient.Reload();
    }
}
