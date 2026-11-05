using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mugonzei.Server;

internal static partial class Program
{
    private static async Task AdminAccountQueryTests()
    {
        using var fixture = new Fixture();
        var plan = new Plan("account-fixture", "测试套餐", 20, 500, new string('a', 32));
        string first = fixture.User(), second = fixture.User(), third = fixture.User();
        foreach (var (id, name) in new[] { (first, "sample_alpha"), (second, "samplebeta"), (third, "third") })
            RedemptionSql(fixture.Path, "UPDATE users SET username=$name WHERE id=$id", ("$name", name), ("$id", id));
        var paid = fixture.Store.CreateOrder(first, Guid.NewGuid().ToString(), plan);
        const string tradeNo = "private-provider-order-reference";
        fixture.Store.CreditOrder(paid.Id, tradeNo, paid.AmountFen, paid.AfdianPlanId, paid.CustomId);
        fixture.Store.HoldRefund(paid.Id, Guid.NewGuid().ToString());
        var pending = fixture.Store.CreateOrder(first, Guid.NewGuid().ToString(), plan);
        // Force a timestamp tie and prove that navigation still visits both orders exactly once.
        RedemptionSql(fixture.Path, "UPDATE orders SET created_at=$time WHERE user_id=$id", ("$id", first), ("$time", DateTimeOffset.UtcNow.ToString("O")));
        fixture.Store.CreateOrder(second, Guid.NewGuid().ToString(), plan);
        var award = fixture.Store.CreateRedemptionCodes(100, 1, DateTimeOffset.UtcNow.AddDays(1), "").Single();
        fixture.Store.RedeemCode(first, award.Code);
        fixture.Store.RedeemCode(second, fixture.Store.CreateRedemptionCodes(7, 1, null, "").Single().Code);
        string operatorToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        const string ordinaryToken = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        fixture.Store.CreateSession(second, Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(ordinaryToken))), DateTimeOffset.UtcNow.AddDays(1));

        await AdminFixture.RunAsync(fixture.Path, [plan], operatorToken, async http =>
        {
            const string path = "admin/api/accounts";
            string detail = path + "/" + first;
            async Task<JsonElement> Read(string url)
            {
                using var response = await http.GetAsync(url);
                response.EnsureSuccessStatusCode();
                Check(response.Headers.CacheControl?.NoStore == true, "Admin reads cannot be cached");
                return await response.Content.ReadFromJsonAsync<JsonElement>();
            }
            async Task Status(string url, HttpStatusCode expected)
            {
                using var response = await http.GetAsync(url);
                Equal(expected, response.StatusCode, "Admin account query enforces session, pagination and identity");
            }
            static JsonElement Items(JsonElement page) => page.GetProperty("items");
            static string RowId(JsonElement row) => row.GetProperty("id").GetString()!;
            static void Fields(JsonElement row, params string[] names) =>
                Check(row.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(names), "Only allowlisted operator fields are returned");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ordinaryToken);
            foreach (string url in new[] { path, detail, detail + "/actions" })
                await Status(url, HttpStatusCode.Unauthorized);
            http.DefaultRequestHeaders.Authorization = null;
            using var auth = await http.PostAsJsonAsync("admin/api/session", new { token = operatorToken });
            auth.EnsureSuccessStatusCode();
            var list = await Read(path + "?pageSize=1");
            var row = Items(list)[0];
            Fields(row, "id", "username", "balance", "reserved", "available", "disabled");
            Check(list.GetProperty("total").GetInt32() == 3 && RowId(row) == first && row.GetProperty("available").GetInt64() == 100 &&
                !row.GetProperty("disabled").GetBoolean(), "Operator sees total, first name in stable order and available credit");
            Equal(second, RowId(Items(await Read(path + "?page=2&pageSize=1"))[0]), "Pagination shows distinct accounts");
            Equal(2, (await Read(path + "?q=sAmPlE")).GetProperty("total").GetInt32(), "Search counts case-insensitive username fragments");
            Equal(first, RowId(Items(await Read(path + "?q=_ALPHA"))[0]), "Underscore is literal in a username search");
            Equal(0, (await Read(path + "?q=%25")).GetProperty("total").GetInt32(), "Percent does not expand into a wildcard");
            Equal(second, RowId(Items(await Read(path + "?q=" + Guid.Parse(second).ToString("D").ToUpperInvariant()))[0]), "Exact ID search accepts a pasted UUID");
            Equal(0, Items(await Read(path + "?page=2147483647")).GetArrayLength(), "Overflow-size offset returns an empty page");
            foreach (string url in new[] { path + "?page=0", path + "?pageSize=51", path + "?q=" + new string('a', 65),
                        detail + "?orderPage=0", detail + "?ledgerPage=-1", detail + "/actions?pageSize=0" })
                await Status(url, HttpStatusCode.BadRequest);
            string missing = path + "/" + Guid.NewGuid().ToString("N");
            await Status(missing, HttpStatusCode.NotFound);
            await Status(missing + "/actions", HttpStatusCode.NotFound);

            var firstPage = await Read(detail + "?pageSize=1");
            var account = firstPage.GetProperty("account");
            var orderPage = firstPage.GetProperty("orders");
            var ledgerPage = firstPage.GetProperty("ledger");
            Fields(account, "id", "username", "balance", "reserved", "available", "disabled");
            Fields(Items(orderPage)[0], "id", "name", "credits", "amountFen", "status", "createdAt");
            Fields(Items(ledgerPage)[0], "id", "kind", "amount", "balance", "createdAt");
            Check(account.GetProperty("balance").GetInt64() == 120 && account.GetProperty("reserved").GetInt64() == 20 &&
                orderPage.GetProperty("total").GetInt32() == 2 && ledgerPage.GetProperty("total").GetInt32() == 3,
                "Detail counts only this customer's records and separates reserved from available credits");
            var secondOrders = await Read(detail + "?pageSize=1&orderPage=2");
            Check(new[] { RowId(Items(orderPage)[0]), RowId(Items(secondOrders.GetProperty("orders"))[0]) }.ToHashSet().SetEquals([paid.Id, pending.Id]) &&
                Items(secondOrders.GetProperty("ledger"))[0].GetRawText() == Items(ledgerPage)[0].GetRawText(),
                "Order paging is deterministic on tied timestamps and independent from ledger paging");
            var kinds = new[] { "redemption", "refund_hold", "recharge" };
            for (int i = 0; i < kinds.Length; i++)
            {
                var page = await Read(detail + "?pageSize=1&ledgerPage=" + (i + 1));
                var entry = Items(page.GetProperty("ledger"))[0];
                Equal(kinds[i], entry.GetProperty("kind").GetString(), "Shows only this account's ledger in descending sequence");
                Check(!page.GetRawText().Contains(tradeNo) && !page.GetRawText().Contains(award.Code), "Payment and redeem credentials stay private");
            }
            var empty = await Read(path + "/" + third);
            Check(Items(empty.GetProperty("orders")).GetArrayLength() == 0 && Items(empty.GetProperty("ledger")).GetArrayLength() == 0,
                "Accounts without any financial history still have a detail page");

            async Task Act(string action, string reason)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, detail + "/actions")
                    { Content = JsonContent.Create(new { action, delta = 0, reason }) };
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
                using var response = await http.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }
            await Act("suspend", "fixture operator note");
            Check((await Read(detail)).GetProperty("account").GetProperty("disabled").GetBoolean() &&
                Items(await Read(path + "?q=sample_alpha"))[0].GetProperty("disabled").GetBoolean(), "A suspended account remains visible to its operator");
            await Act("restore", "fixture restored");
            var audit = await Read(detail + "/actions?pageSize=1&page=2");
            var actionRow = Items(audit)[0];
            Fields(actionRow, "sequence", "action", "delta", "reason", "createdAt", "balanceAfter");
            Check(audit.GetProperty("total").GetInt32() == 2 && actionRow.GetProperty("action").GetString() == "suspend" &&
                actionRow.GetProperty("reason").GetString() == "fixture operator note" && actionRow.GetProperty("balanceAfter").GetInt64() == 120 &&
                !Items(await Read(path + "?q=sample_alpha"))[0].GetProperty("disabled").GetBoolean(), "Audit history retains the reason and prior balance after restoration");
            Equal(0, Items(await Read(path + "/" + third + "/actions")).GetArrayLength(), "Private operator notes do not cross account records");
        });
    }
}
