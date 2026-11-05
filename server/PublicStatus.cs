using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Mugonzei.Server;

internal sealed record PublicQueueState(int Running, int Concurrency, int Waiting, int Capacity);

public static class PublicStatusEndpoint
{
    public static void Map(WebApplication app)
    {
        var started = Stopwatch.GetTimestamp();
        var gate = new object();
        (long Stamp, IResult Reply)? recent = null;
        app.MapGet("/v1/status", (Store store, TranslationService translation, ServerOptions options, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            // Share one short observation across all public visitors, including when SQLite is unhealthy.
            lock (gate)
            {
                if (recent is { } last && Stopwatch.GetElapsedTime(last.Stamp) < TimeSpan.FromSeconds(3))
                    return last.Reply;
                bool readable = store.CanReadAccountStore();
                var (state, queue) = translation.InspectQueue();
                bool operational = readable && state != "stopping";
                var reply = Results.Json(new
                {
                    status = operational ? "operational" : "degraded",
                    checkedAt = DateTimeOffset.UtcNow,
                    uptimeSeconds = (long)Stopwatch.GetElapsedTime(started).TotalSeconds,
                    storage = readable ? "readable" : "unavailable",
                    translation = state,
                    payments = options.LivePayments ? "enabled" : options.MockPayments ? "simulated" : "disabled",
                    queue,
                    timings = translation.Timings.Snapshot()
                }, statusCode: operational ? 200 : 503);
                recent = (Stopwatch.GetTimestamp(), reply);
                return reply;
            }
        });
    }
}

public sealed partial class Store
{
    internal bool CanReadAccountStore()
    {
        // A missing or unreadable database must not be re-created by a public monitoring request.
        try
        {
            var readOnly = new SqliteConnectionStringBuilder(connectionString)
                { Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 1, Pooling = false };
            using var db = new SqliteConnection(readOnly.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT 1 FROM users LIMIT 1";
            command.ExecuteScalar(); // An empty users table is healthy. No account data is returned.
            return true;
        }
        catch (SqliteException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
