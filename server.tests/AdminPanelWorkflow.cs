using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mugonzei.Server;

internal static partial class Program
{
    // Exercise the operator page and the ordinary customer's checkout against one running store.
    private static async Task AdminPanelWorkflow()
    {
        using var fixture = new Fixture();
        var plan = new Plan("small", "200 积分", 200, 500, new string('a', 32));
        string adminToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await AdminFixture.RunAsync(fixture.Path, [plan], adminToken, async admin =>
        {
            foreach (var (path, contentType) in new[] { ("admin/", "text/html"), ("admin/admin.css", "text/css"), ("admin/admin.js", "text/javascript") })
            {
                using var asset = await admin.GetAsync(path);
                Equal(HttpStatusCode.OK, asset.StatusCode, "Serves admin asset with embedded server build");
                Equal(contentType, asset.Content.Headers.ContentType?.MediaType, "Admin asset type");
                Check(asset.Headers.CacheControl?.NoStore == true && asset.Headers.Contains("Content-Security-Policy"), "Admin page has cache and content policies");
                var text = await asset.Content.ReadAsStringAsync();
                Check(text.Length > 20 && !text.Contains(adminToken), "Public login assets contain no operator credential");
            }
            using var login = await admin.PostAsJsonAsync("admin/api/session", new { token = adminToken });
            login.EnsureSuccessStatusCode();
            async Task<JsonElement> Issue(string endpoint, object payload)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "admin/api/" + endpoint) { Content = JsonContent.Create(payload) };
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
                using var response = await admin.SendAsync(request);
                Equal(HttpStatusCode.Created, response.StatusCode, "Admin can issue through the live store");
                return await response.Content.ReadFromJsonAsync<JsonElement>();
            }
            var campaign = await Issue("coupons", new { planId = plan.Id, amountFen = 400, afdianPlanId = new string('b', 32), days = 7 });
            var batch = await Issue("redemptions", new { credits = 21, count = 1, days = 7, note = "fixture" });

            string customer = fixture.User();
            string token = new string('x', 43);
            fixture.Store.CreateSession(customer, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), DateTimeOffset.UtcNow.AddMinutes(5));
            using var buyer = new HttpClient { BaseAddress = admin.BaseAddress };
            buyer.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var quote = new { planId = plan.Id, couponCode = campaign.GetProperty("code").GetString() };
            using var quoted = await buyer.PostAsJsonAsync("v1/checkout/quote", quote);
            quoted.EnsureSuccessStatusCode();
            var price = await quoted.Content.ReadFromJsonAsync<JsonElement>();
            Equal(400, price.GetProperty("amountFen").GetInt32(), "Customer receives the operator's discount without restarting the server");
            async Task CheckSummary(int orders, int amount)
            {
                var overview = await admin.GetFromJsonAsync<JsonElement>("admin/api/summary");
                Check(overview.GetProperty("accounts").GetInt64() == 1 && overview.GetProperty("paidOrders").GetInt64() == orders
                    && overview.GetProperty("paidAmountFen").GetInt64() == amount, "Summary includes only currently paid orders at their actual price");
            }
            using var order = await buyer.PostAsJsonAsync("v1/checkout/orders", new {
                planId = plan.Id, quote.couponCode, requestId = Guid.NewGuid(), expectedAmountFen = 400 });
            order.EnsureSuccessStatusCode();
            var placed = await order.Content.ReadFromJsonAsync<JsonElement>();
            var payment = new Uri(placed.GetProperty("payUrl").GetString()!);
            string orderId = placed.GetProperty("id").GetString()!;
            await CheckSummary(0, 0);
            using var paid = await buyer.PostAsync(payment, new FormUrlEncodedContent(new Dictionary<string,string> {
                ["confirmation"] = fixture.Store.GetOrder(customer, orderId).CustomId }));
            paid.EnsureSuccessStatusCode();
            await CheckSummary(1, 400);
            var refund = fixture.Store.HoldRefund(orderId, Guid.NewGuid().ToString());
            await CheckSummary(0, 0);
            fixture.Store.ResolveRefund(refund.Id, true);
            using var redeemed = await buyer.PostAsJsonAsync("v1/redemptions", new { code = batch.GetProperty("codes")[0].GetProperty("code").GetString() });
            redeemed.EnsureSuccessStatusCode();
            Equal(21L, fixture.Store.GetAccount(customer).Balance, "Operator-issued credits work in ordinary customer account");
            string id = campaign.GetProperty("id").GetString()!;
            using var revoked = await admin.PostAsJsonAsync($"admin/api/coupons/{id}/revoke", new { });
            revoked.EnsureSuccessStatusCode();
            using var unavailable = await buyer.PostAsJsonAsync("v1/checkout/quote", quote);
            Equal(HttpStatusCode.BadRequest, unavailable.StatusCode, "Revoking a coupon affects customer checkout immediately");
        }, mockPayments: true);
    }
}
