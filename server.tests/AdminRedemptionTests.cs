using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mugonzei.Server;

internal static partial class Program
{
    private static async Task AdminRedemptionTests()
    {
        using var fixture = new Fixture();
        const string adminToken = "synthetic-operator-test-token-for-redemption-endpoints-only";
        const string endpoint = "admin/api/redemptions";
        var plans = new[] { new Plan("fixture-credits", "Fixture", 200, 500, new string('c', 32)) };
        string user = fixture.User();
        var batch = new { credits = 19, count = 2, days = 2, note = "operator fixture" };
        string key = Guid.NewGuid().ToString("D");

        async Task<(HttpStatusCode Status, JsonElement Data, bool NoStore)> Issue(HttpClient http, object input, string? id)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(input) };
            if (id != null) request.Headers.Add("Idempotency-Key", id);
            using var response = await http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(), response.Headers.CacheControl?.NoStore == true);
        }

        async Task Login(HttpClient http)
        {
            using var login = await http.PostAsJsonAsync("admin/api/session", new { token = adminToken });
            login.EnsureSuccessStatusCode();
        }

        await AdminFixture.RunAsync(fixture.Path, plans, adminToken, async http =>
        {
            using (var denied = await http.GetAsync(endpoint))
                Equal(HttpStatusCode.Unauthorized, denied.StatusCode, "Redemption metadata requires an administrator session");
            Equal(HttpStatusCode.Unauthorized, (await Issue(http, batch, key)).Status, "An unauthenticated request cannot issue codes");
            await Login(http);
            foreach (var invalid in new[] { null, "invalid", Guid.Empty.ToString("D") })
                Equal(HttpStatusCode.BadRequest, (await Issue(http, batch, invalid)).Status, "Admin issuance requires a nonempty UUID key");
            foreach (var (credits, count, days, note) in new[] { (0, 2, 1, ""), (1, 0, 1, ""), (1, 51, 1, ""),
                (1, 1, 0, ""), (1, 1, 366, ""), (1, 1, 1, new string('x', 121)), (1, 1, 1, "bad\nline") })
            {
                var invalid = await Issue(http, new { credits, count, days, note }, key);
                Equal(HttpStatusCode.BadRequest, invalid.Status, "Invalid batch leaves no issued codes or used key");
            }
            Equal(0, fixture.Store.ListRedemptionCodes().Count, "Rejected batches do not modify redemption inventory");
            var replies = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Issue(http, batch, key)));
            var success = replies.Single(r => r.Status == HttpStatusCode.Created);
            Check(success.NoStore && replies.Count(r => r.Status == HttpStatusCode.Conflict
                && r.Data.GetProperty("error").GetString() == "redemption_batch_exists") == 2,
                "A concurrent request key creates one private batch; duplicate responses contain only a conflict");
            var codes = success.Data.GetProperty("codes").EnumerateArray().ToArray();
            Equal(2, codes.Length, "First response supplies the full requested batch");
            Check(codes.All(c => c.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "code", "credits", "expiresAt", "id" })
                && c.GetProperty("credits").GetInt32() == 19 && c.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddHours(47)),
                "Issued items expose only one-time code, ID, value and expiry");
            string firstId = codes[0].GetProperty("id").GetString()!, secondId = codes[1].GetProperty("id").GetString()!;
            string firstCode = codes[0].GetProperty("code").GetString()!, secondCode = codes[1].GetProperty("code").GetString()!;
            Check(firstCode.StartsWith("MU-") && secondCode.StartsWith("MU-") && firstCode != secondCode, "Issued codes are distinct");
            foreach (var duplicate in replies.Where(r => r.Status != HttpStatusCode.Created))
                Check(!duplicate.Data.GetRawText().Contains(firstCode) && !duplicate.Data.GetRawText().Contains(secondCode),
                    "Concurrent duplicate replies never disclose a plaintext code");
            Equal(19L, fixture.Store.RedeemCode(user, firstCode).Balance, "Admin-issued codes use normal single-use redemption accounting");
            using (var used = await http.PostAsJsonAsync(endpoint + "/" + firstId + "/revoke", new { }))
                Equal(HttpStatusCode.Conflict, used.StatusCode, "Cannot revoke a used code or debit its user");
            using (var invalid = await http.PostAsJsonAsync(endpoint + "/" + secondId + "/revoke", new { extra = true }))
                Equal(HttpStatusCode.BadRequest, invalid.StatusCode, "Invalid revoke body does not mutate the code");
            for (int i = 0; i < 2; i++)
            {
                using var revoke = await http.PostAsJsonAsync(endpoint + "/" + secondId + "/revoke", new { });
                Equal(HttpStatusCode.OK, revoke.StatusCode, "Revocation retries are idempotent");
                var metadata = await revoke.Content.ReadFromJsonAsync<JsonElement>();
                Check(metadata.GetProperty("status").GetString() == "revoked" && !metadata.TryGetProperty("code", out _)
                    && revoke.Headers.CacheControl?.NoStore == true, "Revocation only returns non-cacheable metadata");
            }
            RedemptionThrows(400, "redemption_unavailable", () => fixture.Store.RedeemCode(user, secondCode));
            using var list = await http.GetAsync(endpoint);
            list.EnsureSuccessStatusCode();
            var result = await list.Content.ReadFromJsonAsync<JsonElement>();
            var allowed = new[] { "createdAt", "credits", "expiresAt", "id", "note", "redeemedAt", "status" };
            Check(result.GetArrayLength() == 2 && result.EnumerateArray().All(r => r.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(allowed))
                && list.Headers.CacheControl?.NoStore == true, "List is bounded metadata, with no hash, plaintext code, account ID or email fields");
            Check(!result.GetRawText().Contains(user) && !result.GetRawText().Contains(firstCode) && !result.GetRawText().Contains(secondCode),
                "Neither customer identifiers nor issued secrets enter list values");
            Check(result.EnumerateArray().Any(r => r.GetProperty("status").GetString() == "redeemed")
                && result.EnumerateArray().Any(r => r.GetProperty("status").GetString() == "revoked"), "List reflects redeemed and revoked states");
            Equal(19L, fixture.Store.GetAccount(user).Balance, "Revocation never changes credited balances");
        });

        await AdminFixture.RunAsync(fixture.Path, plans, adminToken, async http =>
        {
            await Login(http);
            var replay = await Issue(http, new { credits = 6, count = 1, days = 3, note = "" }, key);
            Check(replay.Status == HttpStatusCode.Conflict && replay.Data.GetProperty("error").GetString() == "redemption_batch_exists"
                && !replay.Data.TryGetProperty("codes", out _), "Restart and changed parameters cannot reuse a consumed request key");
            Equal(2, fixture.Store.ListRedemptionCodes().Count, "Restart retry issued no additional codes");
        });
    }
}
