using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mugonzei.Server;

internal static partial class Program
{
    private static async Task PublicStatusContract()
    {
        var window = new TranslationTimings();
        window.Add(TimeSpan.FromDays(1), TimeSpan.FromDays(1), TimeSpan.FromDays(1));
        for (int i = 0; i < 32; i++)
            window.Add(TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(17), TimeSpan.FromMilliseconds(2));
        Equal(new PublicTimings(32, 3, 17, 2), window.Snapshot(), "An old slow task ages out of the published averages");
        foreach (bool healthy in new[] { true, false })
        {
            using var f = new Fixture();
            var processor = new BlockingProcessor();
            using var service = new TranslationService(f.Store, processor,
                new() { Concurrency = 1, QueueCapacity = 2 }, NullLogger<TranslationService>.Instance);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            builder.Services.AddSingleton(f.Store); builder.Services.AddSingleton(service);
            builder.Services.AddSingleton(new ServerOptions { LivePayments = true, AfdianToken = "private-test-token" });
            await using var app = builder.Build();
            PublicStatusEndpoint.Map(app);
            if (!healthy) RedemptionSql(f.Path, "ALTER TABLE users RENAME TO unavailable_fixture");
            await app.StartAsync();
            try
            {
                if (healthy)
                {
                    await service.StartAsync(default);
                    string user = f.User(2);
                    service.Submit(user, Guid.NewGuid(), TextInput("private active text"));
                    await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    service.Submit(user, Guid.NewGuid(), TextInput("private waiting text"));
                }
                using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
                using var response = await client.GetAsync("/v1/status");
                Equal(healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode, "Status tracks a real account table read");
                Check(response.Headers.CacheControl?.NoStore == true, "Status responses must not be cached by proxies");
                string json = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(json);
                var body = document.RootElement; var queue = body.GetProperty("queue");
                Equal(healthy ? "operational" : "degraded", body.GetProperty("status").GetString(), "Summary agrees with storage");
                Equal(healthy ? "readable" : "unavailable", body.GetProperty("storage").GetString(), "Missing schema is not healthy");
                Equal(healthy ? 1 : 0, queue.GetProperty("running").GetInt32(), "Only active count is public");
                Equal(healthy ? 1 : 0, queue.GetProperty("waiting").GetInt32(), "Only queue depth is public");
                Check(body.GetProperty("checkedAt").GetDateTimeOffset().Offset == TimeSpan.Zero && body.GetProperty("uptimeSeconds").GetInt64() >= 0, "Sample is dated in UTC");
                Check(!json.Contains("private") && !json.Contains(f.Path) && !json.Contains("sqlite", StringComparison.OrdinalIgnoreCase), "Status does not include jobs, credentials or error details");
                var timings = body.GetProperty("timings");
                Check(body.EnumerateObject().Count() == 8 && queue.EnumerateObject().Count() == 4 &&
                    timings.EnumerateObject().Count() == 4, "Public fields are limited to the monitoring contract");
                Check(timings.GetProperty("samples").GetInt32() == 0 && timings.GetProperty("modelMs").ValueKind == JsonValueKind.Null,
                    "Unfinished work is not reported as fast completed work");
                if (healthy)
                {
                    processor.Finish.TrySetResult(new(false, "source", "result"));
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    while (true)
                    {
                        await Task.Delay(120, deadline.Token);
                        using var updated = JsonDocument.Parse(await client.GetStringAsync("/v1/status", deadline.Token));
                        var elapsed = updated.RootElement.GetProperty("timings");
                        if (elapsed.GetProperty("samples").GetInt32() != 2) continue;
                        Check(elapsed.GetProperty("queueMs").GetDouble() >= 0 && elapsed.GetProperty("modelMs").GetDouble() > 0 &&
                            elapsed.GetProperty("settlementMs").GetDouble() >= 0, "Completed jobs report real stage durations after cache expiry");
                        break;
                    }
                }
            }
            finally
            {
                processor.Finish.TrySetResult(new(false, "source", "result"));
                if (healthy) await service.StopAsync(default);
                await app.StopAsync();
            }
        }
    }
}
