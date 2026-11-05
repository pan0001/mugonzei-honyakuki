using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mugonzei.Server;

internal static partial class Program
{
    private static class AdminFixture
    {
        public static async Task RunAsync(string dbPath, Plan[] plans, string? adminToken, Func<HttpClient, Task> run,
            string environment = "Development", string adminHost = "", string trustedProxy = "", TimeProvider? clock = null,
            bool mockPayments = false)
        {
            var ready = new TaskCompletionSource<(Uri, IHostApplicationLifetime)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = MugonzeiHost.RunAsync(["--environment", environment, "--urls", "http://127.0.0.1:0",
                "--MUGONZEI_DATABASE", dbPath, "--MUGONZEI_PLANS_JSON", JsonSerializer.Serialize(plans),
                "--MUGONZEI_LIVE_PAYMENTS", "false", "--MUGONZEI_MOCK_PAYMENTS", mockPayments.ToString(), "--MUGONZEI_TRUSTED_PROXIES", trustedProxy,
                "--MUGONZEI_ADMIN_TOKEN", adminToken ?? "", "--MUGONZEI_ADMIN_HOST", adminHost,
                "--Translation:Mock", "false", "--Translation:BaseUrl", "", "--Translation:ApiKey", "", "--Logging:LogLevel:Default", "Warning"],
                services =>
                {
                    if (clock is not null) services.AddSingleton<TimeProvider>(clock);
                    services.AddSingleton<IHostedService>(sp => new CheckoutTestAddress(sp.GetRequiredService<IHostApplicationLifetime>(),
                        sp.GetRequiredService<IServer>(), ready));
                });
            if (await Task.WhenAny(host, ready.Task) == host) { await host; throw new Exception("Admin fixture did not start"); }
            var (address, life) = await ready.Task;
            try
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true })
                    { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("Origin", address.GetLeftPart(UriPartial.Authority));
                http.DefaultRequestHeaders.Add("X-Mugonzei-Admin", "1");
                await run(http);
            }
            finally { life.StopApplication(); await host.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    private static async Task AdminEndpointTests()
    {
        using var fixture = new Fixture();
        const string session = "/admin/api/session", coupons = "/admin/api/coupons";
        string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string customerToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
        fixture.Store.CreateSession(fixture.User(), Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(customerToken))), DateTimeOffset.UtcNow.AddHours(1));
        var plan = new Plan("fixture", "Synthetic", 200, 500, new string('a', 32));
        var input = new { planId = plan.Id, amountFen = 400, afdianPlanId = new string('b', 32), days = 5, customCode = "FIXTURE-SALE" };
        var clock = new RegistrationClock();
        string issueKey = Guid.NewGuid().ToString("D"), oldCookie = "";
        static async Task<HttpResponseMessage> Issue(HttpClient http, object body, string? key)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, coupons) { Content = JsonContent.Create(body) };
            if (key is not null) req.Headers.Add("Idempotency-Key", key);
            return await http.SendAsync(req);
        }
        static string Cookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        static async Task<HttpStatusCode> Replay(Uri address, string cookie)
        {
            using var anon = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = address };
            anon.DefaultRequestHeaders.Add("Cookie", cookie);
            using var res = await anon.GetAsync(coupons);
            return res.StatusCode;
        }

        await AdminFixture.RunAsync(fixture.Path, [plan], secret, async http =>
        {
            Check(!(await http.GetFromJsonAsync<JsonElement>(session)).GetProperty("authenticated").GetBoolean(), "Session is anonymous before login");
            http.DefaultRequestHeaders.Authorization = new("Bearer", customerToken);
            foreach (var route in new[] { coupons, "/admin/api/plans", "/admin/api/summary", "/admin/api/redemptions" })
                Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(route)).StatusCode, "Even a valid customer bearer cannot access " + route);
            using var malformed = await http.PostAsync(coupons, new StringContent("{", Encoding.UTF8, "application/json"));
            Equal(HttpStatusCode.Unauthorized, malformed.StatusCode, "Manager authentication precedes body deserialization");
            Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync(session, new { token = customerToken })).StatusCode, "Customer token cannot sign in as admin");
            using var login = await http.PostAsJsonAsync(session, new { token = secret });
            Equal(HttpStatusCode.OK, login.StatusCode, "Manager session created");
            string header = login.Headers.GetValues("Set-Cookie").Single();
            Check(header.Contains("path=/admin", StringComparison.OrdinalIgnoreCase) && header.Contains("httponly", StringComparison.OrdinalIgnoreCase) &&
                header.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase) && !header.Contains("domain=", StringComparison.OrdinalIgnoreCase) &&
                !header.Contains(secret, StringComparison.Ordinal), "Opaque session is HttpOnly, host-only, same-site strict and restricted to /admin");
            string sessionJson = await login.Content.ReadAsStringAsync();
            Check(!sessionJson.Contains(secret) && !sessionJson.Contains(Cookie(login)), "Login response does not expose the session cookie or sign-in token");
            string previousCookie = Cookie(login);
            using var rotated = await http.PostAsJsonAsync(session, new { token = secret });
            oldCookie = Cookie(rotated);
            Check(oldCookie != previousCookie && await Replay(http.BaseAddress!, previousCookie) == HttpStatusCode.Unauthorized, "Re-login rotates and revokes old cookie");
            var rows = await http.GetFromJsonAsync<JsonElement>("/admin/api/plans");
            Equal(plan.Id, rows[0].GetProperty("id").GetString(), "Only authenticated operators can list plans even when payments are disabled");
            string origin = http.BaseAddress!.GetLeftPart(UriPartial.Authority);
            http.DefaultRequestHeaders.Remove("Origin");
            foreach (string untrusted in new[] { "", "https://other.example", origin + "/", origin + ".example", "null" })
            {
                if (untrusted.Length > 0) http.DefaultRequestHeaders.Add("Origin", untrusted);
                using var denied = await Issue(http, input, issueKey);
                Equal(HttpStatusCode.Forbidden, denied.StatusCode, "Reject absent, mismatched or malformed browser Origin");
                http.DefaultRequestHeaders.Remove("Origin");
            }
            http.DefaultRequestHeaders.Add("Origin", origin);
            http.DefaultRequestHeaders.Remove("X-Mugonzei-Admin");
            Equal(HttpStatusCode.Forbidden, (await http.DeleteAsync(session)).StatusCode, "Logout requires the same write protection");
            http.DefaultRequestHeaders.Add("X-Mugonzei-Admin", "1");
            Equal(0, fixture.Store.ListCoupons().Count, "Rejected cross-site activity changes nothing");
            foreach (string? invalid in new[] { null, "123", Guid.Empty.ToString("D") })
                Equal(HttpStatusCode.BadRequest, (await Issue(http, input, invalid)).StatusCode, "Issuance requires a nonempty UUID key");
            var both = await Task.WhenAll(Issue(http, input, issueKey), Issue(http, input, issueKey));
            using var created = both.Single(r => r.StatusCode == HttpStatusCode.Created);
            using var duplicate = both.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            var issued = await created.Content.ReadFromJsonAsync<JsonElement>();
            string id = issued.GetProperty("id").GetString()!;
            Equal(input.customCode, issued.GetProperty("code").GetString(), "Only the committed issuance shows its plaintext code");
            string retry = await duplicate.Content.ReadAsStringAsync();
            Check(retry.Contains("coupon_issue_exists") && !retry.Contains(input.customCode), "Concurrent duplicate reveals no secret");
            string listing = await http.GetStringAsync(coupons);
            Check(listing.Contains(id) && !listing.Contains(input.customCode) && !listing.Contains("codeHash"), "Activity list contains metadata only");
            string retryKey = Guid.NewGuid().ToString("D");
            Equal(HttpStatusCode.Conflict, (await Issue(http, input, retryKey)).StatusCode, "Database rejection of a duplicate custom code is recoverable");
            var corrected = new { input.planId, input.amountFen, input.afdianPlanId, customCode = "SECOND-SALE" };
            Equal(HttpStatusCode.Created, (await Issue(http, corrected, retryKey)).StatusCode, "Failed issuance rolls its marker back with the transaction");
            string revoke = coupons + "/" + id + "/revoke";
            using var ended = await http.PostAsync(revoke, null);
            Equal(true, (await ended.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetBoolean(), "Manager ends a campaign");
            using var endedAgain = await http.PostAsync(revoke, null);
            Equal(false, (await endedAgain.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetBoolean(), "Repeat revoke is harmless");
            Equal(HttpStatusCode.NoContent, (await http.DeleteAsync(session)).StatusCode, "Logout succeeds");
            Equal(HttpStatusCode.Unauthorized, await Replay(http.BaseAddress!, oldCookie), "Logged-out cookie cannot be replayed");
            using var renewed = await http.PostAsJsonAsync(session, new { token = secret });
            oldCookie = Cookie(renewed);
            clock.Advance(TimeSpan.FromMinutes(31));
            Equal(HttpStatusCode.Unauthorized, await Replay(http.BaseAddress!, oldCookie), "Cookie expires server-side without waiting on browser expiry");
            using var fresh = await http.PostAsJsonAsync(session, new { token = secret });
            oldCookie = Cookie(fresh);
        }, clock: clock);

        await AdminFixture.RunAsync(fixture.Path, [plan], secret, async http =>
        {
            Equal(HttpStatusCode.Unauthorized, await Replay(http.BaseAddress!, oldCookie), "Restart clears old manager sessions");
            using var loggedIn = await http.PostAsJsonAsync(session, new { token = secret });
            Equal(HttpStatusCode.OK, loggedIn.StatusCode, "New login after restart");
            Equal(HttpStatusCode.Conflict, (await Issue(http, input, issueKey)).StatusCode, "Issuance marker survives restart");
            Equal(2, fixture.Store.ListCoupons().Count, "Network retries and restart did not issue extra campaigns");
            for (int i = 0; i < 4; i++)
                Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync(session, new { token = "wrong" })).StatusCode, "Wrong secret rejected");
            Equal(HttpStatusCode.TooManyRequests, (await http.PostAsJsonAsync(session, new { token = secret })).StatusCode, "Per-IP login budget includes all attempts");
        });
        foreach (string? invalid in new[] { null, "", "short", new string('z', 48), secret + " " })
            await AdminFixture.RunAsync(fixture.Path, [plan], invalid, async http =>
                Equal(HttpStatusCode.NotFound, (await http.GetAsync(session)).StatusCode, "Missing or unsuitable secret disables admin routes"));
        await AdminFixture.RunAsync(fixture.Path, [plan], secret, async http =>
        {
            http.DefaultRequestHeaders.Host = "wrong.example";
            Equal(HttpStatusCode.NotFound, (await http.GetAsync(session)).StatusCode, "Development rejects foreign Host (DNS rebinding)");
            http.DefaultRequestHeaders.Host = null;
            http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            Equal(HttpStatusCode.NotFound, (await http.GetAsync(session)).StatusCode, "Development accepts only direct loopback requests");
        });

        const string pinned = "admin.example.test:8443";
        await AdminFixture.RunAsync(fixture.Path, [plan], secret, async http =>
        {
            http.DefaultRequestHeaders.Host = pinned;
            http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            Equal(HttpStatusCode.NotFound, (await http.GetAsync(session)).StatusCode, "A spoofed scheme from an untrusted proxy cannot enable production admin");
        }, environment: "Production", adminHost: pinned);
        await AdminFixture.RunAsync(fixture.Path, [plan], secret, async http =>
        {
            http.DefaultRequestHeaders.Host = pinned;
            Equal(HttpStatusCode.NotFound, (await http.GetAsync(session)).StatusCode, "Production rejects cleartext even from the configured proxy");
            http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            http.DefaultRequestHeaders.Host = "admin.example.test";
            Equal(HttpStatusCode.NotFound, (await http.GetAsync(session)).StatusCode, "Production pins the configured host and port");
            http.DefaultRequestHeaders.Host = pinned;
            http.DefaultRequestHeaders.Remove("Origin");
            http.DefaultRequestHeaders.Add("Origin", "https://" + pinned);
            using var login = await http.PostAsJsonAsync(session, new { token = secret });
            Equal(HttpStatusCode.OK, login.StatusCode, "Trusted HTTPS and pinned origin permit login");
            Check(login.Headers.GetValues("Set-Cookie").Single().Contains("; secure", StringComparison.OrdinalIgnoreCase),
                "TLS deployment requires secure session cookies");
        }, environment: "Production", adminHost: pinned, trustedProxy: "127.0.0.1");
    }
}
