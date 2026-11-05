using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
            using (var deniedShare = await http.PostAsJsonAsync(endpoint + "/" + Guid.NewGuid().ToString("N") + "/share", new { }))
                Equal(HttpStatusCode.Unauthorized, deniedShare.StatusCode, "Changing code eligibility requires an administrator session");
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
            var allowed = new[] { "claimCount", "createdAt", "credits", "expiresAt", "id", "mode", "note", "redeemedAt", "status" };
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
            string batchKey = Guid.NewGuid().ToString("D");
            var badMode = await Issue(http, new { credits = 3, count = 2, days = 1, mode = "unknown" }, batchKey);
            Equal(HttpStatusCode.BadRequest, badMode.Status, "Unknown modes cannot silently issue codes with different eligibility");
            var publicBatch = await Issue(http, new { credits = 3, count = 2, days = 1, mode = "oncePerAccount" }, batchKey);
            Check(publicBatch.Status == HttpStatusCode.Created && publicBatch.Data.GetProperty("codes").GetArrayLength() == 2,
                "A valid shared batch can reuse a key from failed validation");
            foreach (var item in publicBatch.Data.GetProperty("codes").EnumerateArray())
            {
                var record = fixture.Store.ListRedemptionCodes().Single(x => x.Id == item.GetProperty("id").GetString());
                Check(record.Mode == "oncePerAccount" && record.Status == "active" && record.ClaimCount == 0,
                    "Each newly issued shared code starts active with no claims");
            }
            string revokedId = fixture.Store.ListRedemptionCodes().Single(x => x.Status == "revoked").Id;
            using var inactive = await http.PostAsJsonAsync(endpoint + "/" + revokedId + "/share", new { });
            Equal(HttpStatusCode.Conflict, inactive.StatusCode, "A revoked individual code cannot be reactivated through promotion");
            var expired = fixture.Store.CreateRedemptionCodes(9, 1, null, "expired promotion fixture").Single();
            RedemptionSql(fixture.Path, "UPDATE redemption_codes SET expires_at=$expiry WHERE id=$id",
                ("$expiry", DateTimeOffset.UtcNow.AddDays(-1).ToString("O")), ("$id", expired.Id));
            using var past = await http.PostAsJsonAsync(endpoint + "/" + expired.Id + "/share", new { });
            Check(past.StatusCode == HttpStatusCode.Conflict
                && (await past.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() == "redemption_share_unavailable",
                "Expired individual codes cannot start a public campaign");
        });
        await CustomAdminRedemptions();
        await SharedRedemptionTests();
    }

    private static async Task CustomAdminRedemptions()
    {
        using var fixture = new Fixture();
        var plan = new Plan("test-credits", "Fixture", 200, 500, new string('a', 32));
        var plans = new[] { plan };
        string operatorToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string user = fixture.User(), other = fixture.User();
        string bearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        fixture.Store.CreateSession(user, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer))), DateTimeOffset.UtcNow.AddMinutes(10));
        await AdminFixture.RunAsync(fixture.Path, plans, operatorToken, async http =>
        {
            using var login = await http.PostAsJsonAsync("admin/api/session", new { token = operatorToken });
            login.EnsureSuccessStatusCode();
            const string path = "admin/api/redemptions";
            async Task<(HttpStatusCode Status, JsonElement Data)> Issue(string code, int count = 1, string? key = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path)
                    { Content = JsonContent.Create(new { credits = 17, count, days = 3, note = "fixture", customCode = code }) };
                request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("D"));
                using var response = await http.SendAsync(request);
                return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
            }
            foreach (var code in new[] { "", "ABC", "A BC D", "AB--CD", "-ABCD", "ＡＢＣＤ", "test🙂", new string('字', 41),
                "mu-random", "mc-coupon", "mU-" + new string('a', 32), "m-u" + new string('f', 32), "MC" + new string('2', 32) })
                Equal(HttpStatusCode.BadRequest, (await Issue(code)).Status, "Invalid custom format cannot issue a code");
            Equal(HttpStatusCode.BadRequest, (await Issue("Test-Valid", 2)).Status, "Named codes can only be issued singly");
            string key = Guid.NewGuid().ToString("D");
            var issued = await Issue("合成字样-17", key: key);
            Equal(HttpStatusCode.Created, issued.Status, "Operator can issue a Chinese custom code");
            Equal("合成字样-17", issued.Data.GetProperty("codes")[0].GetProperty("code").GetString(), "Only the successful creation response returns the printable phrase");
            var repeat = await Issue("合成字样-17", key: key);
            Check(repeat.Status == HttpStatusCode.Conflict && repeat.Data.GetProperty("error").GetString() == "redemption_batch_exists"
                && !repeat.Data.TryGetProperty("codes", out _), "Named issuance cannot replay the plaintext");
            using var buyer = new HttpClient { BaseAddress = http.BaseAddress };
            buyer.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
            async Task<JsonElement> Redeem(string code)
            {
                using var response = await buyer.PostAsJsonAsync("v1/redemptions", new { code });
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<JsonElement>();
            }
            var first = await Redeem(" 合成字样17 ");
            var retry = await Redeem("合成-字样-17");
            Check(first.GetProperty("credits").GetInt32() == 17 && !first.GetProperty("alreadyRedeemed").GetBoolean()
                && retry.GetProperty("alreadyRedeemed").GetBoolean() && retry.GetProperty("balance").GetInt64() == 17,
                "The existing public redemption endpoint credits a named code once across grouping variants");
            RedemptionThrows(400, "redemption_unavailable", () => fixture.Store.RedeemCode(other, "合成字样17"));
            Equal(HttpStatusCode.Conflict, (await Issue("合成字样17")).Status, "Consumed custom text stays reserved");

            var same = await Task.WhenAll(Issue("Stream-Test17"), Issue("streamtest17"));
            Equal(1, same.Count(r => r.Status == HttpStatusCode.Created), "Concurrent requests cannot issue the same normalized text twice");
            Equal("redemption_code_exists", same.Single(r => r.Status == HttpStatusCode.Conflict).Data.GetProperty("error").GetString(), "Name conflict is distinct from request replay");
            var id = same.Single(r => r.Status == HttpStatusCode.Created).Data.GetProperty("codes")[0].GetProperty("id").GetString();
            using var revoke = await http.PostAsJsonAsync(path + "/" + id + "/revoke", new { });
            revoke.EnsureSuccessStatusCode();
            Equal(HttpStatusCode.Conflict, (await Issue("STREAMTEST17")).Status, "Revoked phrases cannot be reissued");
            RedemptionThrows(400, "redemption_unavailable", () => fixture.Store.RedeemCode(user, "streamtest17"));
            RedemptionThrows(409, "coupon_exists", () => fixture.Store.CreateCoupon(plans, plan.Id, 300, new string('b', 32), customCode: "STREAMTEST17"));
            var coupon = fixture.Store.CreateCoupon(plans, plan.Id, 300, new string('b', 32), customCode: "Fixture-Promo");
            fixture.Store.RevokeCoupon(coupon.Id);
            Equal(HttpStatusCode.Conflict, (await Issue("fixturepromo")).Status, "Coupon history also reserves a normalized name against direct credits");
            RedemptionThrows(400, "redemption_unavailable", () => fixture.Store.RedeemCode(other, "Fixture-Promo"));
            Equal(HttpStatusCode.Created, (await Issue("MugonzeiTest17")).Status, "An ordinary MU-prefixed word is valid outside the legacy shape");
            var english = await Redeem("mugonzeitest17");
            Equal(34L, english.GetProperty("balance").GetInt64(), "ASCII custom codes accept lowercase input through the public endpoint");
            string promote = path + "/" + issued.Data.GetProperty("codes")[0].GetProperty("id").GetString() + "/share";
            using (var invalid = await http.PostAsJsonAsync(promote, new { credits = 1000 }))
                Equal(HttpStatusCode.BadRequest, invalid.StatusCode, "Promotion rejects attempts to change the denomination");
            using (var shared = await http.PostAsJsonAsync(promote, new { }))
            {
                var details = await shared.Content.ReadFromJsonAsync<JsonElement>();
                Check(shared.StatusCode == HttpStatusCode.OK && shared.Headers.CacheControl?.NoStore == true
                    && details.GetProperty("mode").GetString() == "oncePerAccount" && details.GetProperty("claimCount").GetInt64() == 1
                    && details.GetProperty("status").GetString() == "active" && details.GetProperty("redeemedAt").ValueKind == JsonValueKind.Null
                    && !details.TryGetProperty("code", out _) && !details.TryGetProperty("redeemedBy", out _),
                    "Sharing a claimed code returns its original claim count without account identifiers or secrets");
            }
            var ownerRetry = await Redeem("合成字样17");
            Check(ownerRetry.GetProperty("alreadyRedeemed").GetBoolean() && ownerRetry.GetProperty("balance").GetInt64() == 34
                && ownerRetry.GetProperty("reference").GetString() == first.GetProperty("reference").GetString(),
                "An old claimant keeps the same ledger reference and current balance through the unchanged HTTP redemption contract");
            Equal(17L, fixture.Store.RedeemCode(other, "合成字样17").Balance, "A different customer may now claim the same custom phrase");
            using var list = await http.GetAsync(path);
            var metadata = await list.Content.ReadFromJsonAsync<JsonElement>();
            Check(metadata.EnumerateArray().All(r => !r.TryGetProperty("code", out _) && !r.TryGetProperty("codeHash", out _)),
                "Named codes and their hashes never appear in inventory responses");
        });
    }
}
