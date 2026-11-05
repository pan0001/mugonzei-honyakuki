using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Mugonzei.Server;

internal static partial class Program
{
    private static async Task AdminAccountMutationTests()
    {
        using var fixture = new Fixture();
        var store = fixture.Store;
        var credentials = new Credentials("account_owner", "synthetic-long-password");
        fixture.LegacyUser(credentials);
        string owner = store.FindUser(credentials.Username)!.Id, other = fixture.User(5);
        fixture.Fund(owner, 9);
        var oldAuth = new AuthService(store);
        var oldSession = oldAuth.Login(credentials);
        var secondSession = oldAuth.Login(credentials);
        var plan = new Plan("test", "Synthetic", 4, 100, "fixture-plan");
        var pendingOrder = store.CreateOrder(owner, Guid.NewGuid().ToString(), plan);
        string key = Guid.NewGuid().ToString(), token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        object credit = new { action = "adjust", delta = 6, reason = "人工补发测试" };
        var voucher = store.CreateRedemptionCodes(1, 1, null, "").Single();
        async Task<(HttpStatusCode Code, JsonElement Data)> Act(HttpClient http, string user, object input, string? nonce = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "admin/api/accounts/" + user + "/actions") { Content = JsonContent.Create(input) };
            request.Headers.Add("Idempotency-Key", nonce ?? Guid.NewGuid().ToString());
            using var response = await http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
        }
        await AdminFixture.RunAsync(fixture.Path, [plan], token, async http =>
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", oldSession.Token);
            Equal(HttpStatusCode.Unauthorized, (await Act(http, owner, credit, key)).Code, "Customer cannot adjust even their own balance");
            using var login = await http.PostAsJsonAsync("admin/api/session", new { token });
            login.EnsureSuccessStatusCode();
            http.DefaultRequestHeaders.Remove("Origin");
            Equal(HttpStatusCode.Forbidden, (await Act(http, owner, credit, key)).Code, "Administrative write requires a same-origin request");
            http.DefaultRequestHeaders.Add("Origin", http.BaseAddress!.GetLeftPart(UriPartial.Authority));
            foreach (var invalid in new[] { new {action="adjust",delta=0,reason="x"}, new {action="adjust",delta=-1000001,reason="x"},
                new {action="adjust",delta=1,reason=" "}, new {action="adjust",delta=1,reason="a\nb"},
                new {action="remove",delta=0,reason="x"}, new {action="suspend",delta=1,reason="x"} })
                Equal(HttpStatusCode.BadRequest, (await Act(http, owner, invalid)).Code, "Invalid operations leave no record");
            Equal(HttpStatusCode.BadRequest, (await Act(http, owner, credit, "bad")).Code, "Mutation requires a valid operation ID");
            Equal(HttpStatusCode.NotFound, (await Act(http, Guid.NewGuid().ToString("N"), credit)).Code, "Missing account cannot be adjusted");
            var replies = await Task.WhenAll(Enumerable.Range(0,4).Select(_ => Act(http, owner, credit, key)));
            Check(replies.All(r => r.Code == HttpStatusCode.OK) && replies.Count(r => !r.Data.GetProperty("alreadyApplied").GetBoolean()) == 1,
                "Concurrent retries commit one adjustment");
            Equal(15L, store.GetAccount(owner).Balance, "Credits added once");
            Equal(HttpStatusCode.Conflict, (await Act(http, other, credit, key)).Code, "Operation ID cannot move to another account");
            Equal(HttpStatusCode.Conflict, (await Act(http, owner, new { action="adjust", delta=9, reason="changed" }, key)).Code, "Operation ID cannot change its payload");
            string job = Guid.NewGuid().ToString();
            store.ReserveTranslation(owner, job, "fixture", "{}");
            Equal(HttpStatusCode.Conflict, (await Act(http, owner, new { action="adjust", delta=-15, reason="超出可用额度" })).Code, "Cannot subtract reserved funds");
            var debit = await Act(http, owner, new { action="adjust", delta=-3, reason="修正测试积分" });
            Check(debit.Code == HttpStatusCode.OK && debit.Data.GetProperty("available").GetInt64() == 11 && store.GetAccount(owner).Reserved == 1, "Debit protects reserved credits");
            // Even a failure after the ledger insert must roll back balance and deduplication state together.
            string failKey = Guid.NewGuid().ToString();
            RedemptionSql(fixture.Path, "CREATE TRIGGER block_admin_audit BEFORE INSERT ON admin_account_actions BEGIN SELECT RAISE(ABORT,'fixture failure'); END");
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "admin/api/accounts/"+owner+"/actions") {Content=JsonContent.Create(credit)};
                req.Headers.Add("Idempotency-Key", failKey);
                using var failed = await http.SendAsync(req);
                Equal(HttpStatusCode.InternalServerError, failed.StatusCode, "Audit failure rolls back the entire adjustment");
            }
            finally { RedemptionSql(fixture.Path, "DROP TRIGGER block_admin_audit"); }
            Equal(12L, store.GetAccount(owner).Balance, "Failed accounting did not move balance");
            string suspendId = Guid.NewGuid().ToString();
            object stop = new { action="suspend", delta=0, reason="测试停用" };
            var suspend = await Act(http, owner, stop, suspendId);
            Check(suspend.Code == HttpStatusCode.OK && suspend.Data.GetProperty("disabled").GetBoolean(), "Admin can suspend selected user");
            Equal(HttpStatusCode.Conflict, (await Act(http, owner, stop)).Code, "A different operation cannot suspend an already suspended account");
            Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("v1/account")).StatusCode, "Existing customer bearer rejected immediately after suspension");
            Throws(403, () => new AuthService(new Store(fixture.Path)).Login(credentials));
            Throws(403, () => store.CreateSession(owner,"fixture-late-login",DateTimeOffset.UtcNow.AddDays(1)));
            Throws(403, () => store.CreateOrder(owner,Guid.NewGuid().ToString(),plan));
            Throws(403, () => store.ReserveTranslation(owner,Guid.NewGuid().ToString(),"late","{}"));
            Throws(403, () => store.RedeemCode(owner,voucher.Code));
            store.CreditOrder(pendingOrder.Id, "fixture-settlement-after-suspension", 100, plan.AfdianPlanId, pendingOrder.CustomId);
            store.CancelTranslation(owner, job);
            Equal(16L, store.GetAccount(owner).Balance, "Orders already paid can settle while suspended");
            string resumeId = Guid.NewGuid().ToString();
            var resume = await Act(http, owner, new { action="restore", delta=0, reason="测试恢复" }, resumeId);
            Check(resume.Code == HttpStatusCode.OK && !resume.Data.GetProperty("disabled").GetBoolean(), "Restore permits a fresh login");
            Check((await Act(http, owner, stop, suspendId)).Data.GetProperty("alreadyApplied").GetBoolean(), "Replaying an old suspension cannot suspend a restored account");
            foreach (var old in new[] {oldSession.Token, secondSession.Token})
            {
                var ctx=Context(old); oldAuth.Authenticate(ctx); Throws(401, () => AuthService.UserId(ctx));
            }
            var current=Context(oldAuth.Login(credentials).Token); oldAuth.Authenticate(current);
            Equal(owner, AuthService.UserId(current), "Only a newly issued session becomes valid after restore");
            Check(store.GetLedger(owner).Where(x=>x.Kind=="admin_adjustment").Select(x=>x.Amount).SequenceEqual(new long[]{-3,6}),
                "Customer ledger contains one entry per committed adjustment, no extra during suspend/restore");
            Equal(5L, store.GetAccount(other).Balance, "Management never affected the other user");
        });
        await AdminFixture.RunAsync(fixture.Path, [plan], token, async http =>
        {
            using var login = await http.PostAsJsonAsync("admin/api/session",new {token});
            login.EnsureSuccessStatusCode();
            var replay = await Act(http,owner,credit,key);
            Check(replay.Code==HttpStatusCode.OK && replay.Data.GetProperty("alreadyApplied").GetBoolean()
                && replay.Data.GetProperty("balance").GetInt64()==16, "Retries after restart return current state without applying twice");
        });
    }
}
