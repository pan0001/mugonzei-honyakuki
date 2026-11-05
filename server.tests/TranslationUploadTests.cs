using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mugonzei.Server;

internal static partial class Program
{
    private static async Task TranslationUploadIsolation()
    {
        using var fixture = new Fixture();
        string accountA = fixture.User(5), accountB = fixture.User(5), accountC = fixture.User(5);
        string Session(string account)
        {
            string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
            fixture.Store.CreateSession(account, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), DateTimeOffset.UtcNow.AddHours(1));
            return token;
        }
        string a = Session(accountA), anotherA = Session(accountA), b = Session(accountB), c = Session(accountC);
        var auth = new AuthService(fixture.Store);
        var processor = new FakeProcessor();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(fixture.Store);
        builder.Services.AddSingleton<ITranslationProcessor>(processor);
        builder.Services.AddSingleton(new TranslationUploadGate(TimeSpan.FromSeconds(5)));
        builder.Services.AddRateLimiter(policies =>
        {
            policies.AddFixedWindowLimiter("translation-submit", o => { o.PermitLimit = 1000; o.Window = TimeSpan.FromMinutes(1); });
            policies.AddFixedWindowLimiter("translation-cancel", o => { o.PermitLimit = 1000; o.Window = TimeSpan.FromMinutes(1); });
        });
        TranslationEndpoints.AddTranslationServices(builder.Services, builder.Configuration, builder.Environment);
        await using var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            try { auth.Authenticate(ctx); await next(ctx); }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
            catch (ApiException error)
            {
                ctx.Response.StatusCode = error.StatusCode;
                await ctx.Response.WriteAsJsonAsync(new { error = error.Code });
            }
        });
        app.UseRateLimiter();
        TranslationEndpoints.MapTranslationEndpoints(app);
        await app.StartAsync();
        try
        {
            var address = new Uri(app.Urls.Single());
            using var client = new HttpClient { BaseAddress = address };
            async Task<HttpResponseMessage> Put(string token, Guid id, string text = "fixture")
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, "/v1/translations/" + id)
                {
                    Content = JsonContent.Create(new { mode = "text", text, targetLanguage = "zh" })
                };
                request.Headers.Authorization = new("Bearer", token);
                return await client.SendAsync(request);
            }
            async Task Busy(string token, Guid id, string explanation)
            {
                using var response = await Put(token, id);
                Equal(HttpStatusCode.TooManyRequests, response.StatusCode, explanation);
                Check((await response.Content.ReadAsStringAsync()).Contains("uploads_busy"), "Client can distinguish busy uploads from minute quota");
            }
            var service = app.Services.GetRequiredService<TranslationService>();
            Guid interrupted = Guid.NewGuid(), timedOut = Guid.NewGuid(), rejected = Guid.NewGuid();
            using var slowA = await StartSlowUpload(address, a, interrupted);
            using var slowAnotherA = await StartSlowUpload(address, anotherA, timedOut);
            await Busy(anotherA, rejected, "Two account sessions share the same two upload places");
            Throws(404, () => service.Get(accountA, rejected));
            using var slowB = await StartSlowUpload(address, b, Guid.NewGuid());
            using var slowOtherB = await StartSlowUpload(address, b, Guid.NewGuid());
            await Busy(c, Guid.NewGuid(), "Two other uploads use the remaining two places; the global cap stays four");
            Equal(0L, fixture.Store.GetAccount(accountA).Reserved, "An incomplete or rejected upload holds no credit");

            slowA.Client.LingerState = new LingerOption(true, 0);
            slowA.Close();
            for (int retry = 0; ; retry++)
            {
                using var response = await Put(anotherA, interrupted);
                if (response.StatusCode == HttpStatusCode.Accepted || response.StatusCode == HttpStatusCode.OK) break;
                if (response.StatusCode != HttpStatusCode.TooManyRequests || retry >= 30)
                    throw new Exception("Disconnected upload did not return its account/global place");
                await Task.Delay(20);
            }
            Equal("succeeded", (await Terminal(service, accountA, interrupted)).Status, "A client can retry an interrupted upload with the same id");
            using var replay = await Put(a, interrupted);
            Equal(HttpStatusCode.OK, replay.StatusCode, "Retrying an accepted transfer returns its terminal receipt");
            Equal(1, processor.Calls, "A same-id replay is processed only once");

            using var timeoutReader = new StreamReader(slowAnotherA.GetStream());
            string timeoutReply = await timeoutReader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Check(timeoutReply.StartsWith("HTTP/1.1 408") && timeoutReply.Contains("upload_timeout"), "Real Kestrel slow request receives the upload deadline response");
            Throws(404, () => service.Get(accountA, timedOut));
            using var afterTimeout = await Put(anotherA, timedOut);
            Check(afterTimeout.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, "Timed-out request leaves the same id and capacity available for retry");
            Equal("succeeded", (await Terminal(service, accountA, timedOut)).Status, "Retry after timeout settles normally");
            Equal(3L, fixture.Store.GetAccount(accountA).Balance, "Only the two completed translations are charged");
            Equal(0L, fixture.Store.GetAccount(accountA).Reserved, "Deadlines and disconnects leave no reservations");
            Equal(5L, fixture.Store.GetAccount(accountB).Balance, "Unfinished second-account slow transfers do not debit its balance");
        }
        finally { await app.StopAsync(); }
    }

    private static async Task<TcpClient> StartSlowUpload(Uri uri, string token, Guid id)
    {
        var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync(uri.Host, uri.Port);
            var stream = socket.GetStream();
            string headers = $"PUT /v1/translations/{id} HTTP/1.1\r\nHost: {uri.Authority}\r\nAuthorization: Bearer {token}\r\nContent-Type: application/json\r\nContent-Length: 256\r\nExpect: 100-continue\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
            // Kestrel emits 100 Continue when the endpoint starts reading the body.
            byte[] ack = new byte["HTTP/1.1 100 Continue\r\n\r\n".Length];
            await stream.ReadExactlyAsync(ack).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Equal("HTTP/1.1 100 Continue\r\n\r\n", Encoding.ASCII.GetString(ack), "Authenticated slow upload has actually reached the body reader");
            await stream.WriteAsync("{"u8.ToArray());
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
}
