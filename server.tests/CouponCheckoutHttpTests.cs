using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mugonzei.Server;

internal static partial class Program
{
    private sealed class CheckoutTestAddress(IHostApplicationLifetime life, IServer server,
        TaskCompletionSource<(Uri, IHostApplicationLifetime)> ready) : IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            life.ApplicationStarted.Register(() => ready.TrySetResult((new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single()), life)));
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task CouponCheckoutHttpTests()
    {
        CouponOperatorCli();
        using var fixture = new Fixture();
        var p = new Plan("credits-200", "200 积分", 200, 500, new string('a', 32));
        var larger = new Plan("credits-500", "500 积分", 500, 1000, new string('c', 32));
        var other = new Plan("unrelated-200", "普通套餐", 200, 500, new string('d', 32));
        var deal = fixture.Store.CreateCoupon([p], p.Id, 400, new string('b', 32), DateTimeOffset.UtcNow.AddDays(1), "TEST-SAVE");
        string buyer = fixture.User(), token = new string('x', 43);
        fixture.Store.CreateSession(buyer, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), DateTimeOffset.UtcNow.AddMinutes(5));
        var ready = new TaskCompletionSource<(Uri, IHostApplicationLifetime)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = MugonzeiHost.RunAsync(["--environment", "Development", "--urls", "http://127.0.0.1:0",
            "--MUGONZEI_DATABASE", fixture.Path, "--MUGONZEI_PLANS_JSON", JsonSerializer.Serialize(new[] { p, larger, other }),
            "--MUGONZEI_LIVE_PAYMENTS", "false", "--MUGONZEI_MOCK_PAYMENTS", "true", "--MUGONZEI_TRUSTED_PROXIES", "",
            "--Translation:Mock","false","--Translation:BaseUrl","","--Translation:ApiKey","","--Logging:LogLevel:Default","Warning"],
            services => services.AddSingleton<IHostedService>(sp => new CheckoutTestAddress(
                sp.GetRequiredService<IHostApplicationLifetime>(), sp.GetRequiredService<IServer>(), ready)));
        if (await Task.WhenAny(host, ready.Task) == host) { await host; throw new Exception("Coupon test server not ready"); }
        var (address, life) = await ready.Task;
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
            var quote = new {planId = p.Id, couponCode = deal.Code };
            string req = Guid.NewGuid().ToString("D");
            var checkout = new { planId = p.Id, couponCode = deal.Code, requestId = req, expectedAmountFen = 400 };
            async Task<JsonElement> Post(string url, object body, HttpStatusCode expected)
            {
                using var response = await http.PostAsJsonAsync(url, body);
                Equal(expected, response.StatusCode, url + " has expected HTTP status");
                return expected == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
            }
            var catalog = await http.GetFromJsonAsync<JsonElement[]>("v1/plans");
            foreach (var plan in catalog!)
            {
                bool supported = plan.GetProperty("id").GetString() != other.Id;
                Equal(supported ? 100 : 0, plan.GetProperty("firstRechargeBonusCredits").GetInt32(), "Only designated packages advertise a possible first recharge bonus");
                Equal(plan.GetProperty("id").GetString() == larger.Id ? 500 : 200, plan.GetProperty("credits").GetInt32(), "Package credits keep the base amount");
                Check(!plan.TryGetProperty("firstRechargeEligible", out _), "Public package response never includes account eligibility");
            }
            Equal(3, catalog.Length, "Public catalog contains both bonus packages and an ordinary one");
            await Post("v1/checkout/quote", quote, HttpStatusCode.Unauthorized);
            await Post("v1/checkout/orders", checkout, HttpStatusCode.Unauthorized);
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            async Task<bool> Eligible() => (await http.GetFromJsonAsync<JsonElement>("v1/account")).GetProperty("firstRechargeEligible").GetBoolean();
            Check(await Eligible(), "Fresh account is eligible before checkout");
            var priced = await Post("v1/checkout/quote", quote, HttpStatusCode.OK);
            Equal(100, priced.GetProperty("firstRechargeBonusCredits").GetInt32(), "Coupon price keeps the first recharge opportunity on the original package");
            Check(priced.GetProperty("credits").GetInt32()==200 && priced.GetProperty("originalAmountFen").GetInt32()==500
                && priced.GetProperty("discountFen").GetInt32()==100 && priced.GetProperty("amountFen").GetInt32()==400,
                "Quote displays 5 yuan less 1 yuan for all 200 credits");
            await Post("v1/orders", checkout, HttpStatusCode.BadRequest);
            await Post("v1/checkout/orders", new { planId = p.Id, couponCode = deal.Code, requestId = req, expectedAmountFen=1 }, HttpStatusCode.Conflict);
            Equal(0,fixture.Store.GetOrders(buyer).Count,"Preview and rejected prices do not make orders");
            var placed = await Post("v1/checkout/orders", checkout, HttpStatusCode.OK);
            Check(placed.GetProperty("credits").GetInt32() == 200 && placed.GetProperty("creditedCredits").ValueKind == JsonValueKind.Null
                && placed.GetProperty("firstRechargeBonusCredits").GetInt32() == 0 && placed.GetProperty("potentialFirstRechargeBonusCredits").GetInt32() == 100
                && await Eligible(), "Pending checkout does not award credits or consume eligibility");
            Check(placed.GetProperty("amountFen").GetInt32()==400 && placed.GetProperty("discountFen").GetInt32()==100,
                "Placed order uses confirmed quotation");
            string id = placed.GetProperty("id").GetString()!;
            var retry = await Post("v1/checkout/orders", checkout, HttpStatusCode.OK);
            Equal(id,retry.GetProperty("id").GetString(),"HTTP retry returns the same discounted order");
            var pay = new Uri(placed.GetProperty("payUrl").GetString()!);
            Check(pay.Authority == address.Authority && pay.AbsolutePath == "/dev/payments/"+id,"Checkout points to isolated payment simulator");
            using var noSession = new HttpClient();
            string page = await noSession.GetStringAsync(pay);
            Check(page.Contains("4.00") && page.Contains("5.00") && page.Contains("1.00"),"Simulated checkout shows actual price and discount");
            string confirmation = fixture.Store.GetOrder(buyer, id).CustomId;
            for (int i=0;i<2;i++)
            {
                using var response=await noSession.PostAsync(pay,new FormUrlEncodedContent(new Dictionary<string,string>{{"confirmation",confirmation}}));
                Equal(HttpStatusCode.OK,response.StatusCode,"Simulated discount payment succeeds or replays");
            }
            Equal(300L,fixture.Store.GetAccount(buyer).Balance,"Repeated payment credits 200 plus the first recharge bonus once");
            Check(!await Eligible(), "A settled payment consumes first-recharge eligibility");
            var nextQuote = await Post("v1/checkout/quote", quote, HttpStatusCode.OK);
            Equal(0, nextQuote.GetProperty("firstRechargeBonusCredits").GetInt32(), "Returning account no longer receives a coupon bonus preview");
            var paid = await Post("v1/checkout/orders", checkout, HttpStatusCode.OK);
            Check(paid.GetProperty("id").GetString() == id && paid.GetProperty("credits").GetInt32() == priced.GetProperty("credits").GetInt32()
                && paid.GetProperty("creditedCredits").GetInt32() == 300 && paid.GetProperty("firstRechargeBonusCredits").GetInt32() == 100,
                "Paid retry preserves the old checkout contract and exposes actual credited total and bonus separately");
            var history = await http.GetFromJsonAsync<JsonElement[]>("v1/orders");
            Equal(300, history!.Single().GetProperty("creditedCredits").GetInt32(), "Order history exposes the same credited total as checkout retries");
            fixture.Store.RevokeCoupon(deal.Id);
            await Post("v1/checkout/quote",quote,HttpStatusCode.BadRequest);
            await Post("v1/checkout/orders",new {planId=p.Id, couponCode=deal.Code, expectedAmountFen=400, requestId=Guid.NewGuid()},HttpStatusCode.BadRequest);
            var normal=await Post("v1/orders",new {planId=p.Id,requestId=Guid.NewGuid()},HttpStatusCode.OK);
            Check(normal.GetProperty("amountFen").GetInt32()==500 && normal.GetProperty("discountFen").GetInt32()==0,"Ordinary checkout still charges normal price");
        }
        finally { life.StopApplication(); await host.WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    private static void CouponOperatorCli()
    {
        using var fixture = new Fixture();
        var originalDb = Environment.GetEnvironmentVariable("MUGONZEI_DATABASE");
        var originalPlans = Environment.GetEnvironmentVariable("MUGONZEI_PLANS_JSON");
        var (stdout, stderr, stdin) = (Console.Out, Console.Error, Console.In);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        using var input = new StringReader("SPRING-TEST\n");
        try
        {
            var plan = new Plan("test-200", "200 credits", 200, 500, new string('a', 32));
            Environment.SetEnvironmentVariable("MUGONZEI_DATABASE", fixture.Path);
            Environment.SetEnvironmentVariable("MUGONZEI_PLANS_JSON", JsonSerializer.Serialize(new[] { plan }));
            Console.SetOut(output); Console.SetError(errors); Console.SetIn(input);
            string[] create = ["coupons", "create", "--plan", plan.Id, "--amount-fen", "400", "--afdian-plan", new string('b', 32)];
            using (var lease = new DatabaseLease(fixture.Path))
                Equal(1, CouponCommands.Run(create), "Operator cannot issue coupons while the service owns the database");
            Equal(1, CouponCommands.Run([..create, "--days", "0"]), "Operator must set a positive lifetime");
            Equal(1, CouponCommands.Run([..create, "--code", "example"]), "CLI does not accept a plaintext code argument");
            Equal(0, fixture.Store.ListCoupons().Count, "Rejected operator commands issue no codes");
            Equal(0, CouponCommands.Run([..create, "--code-stdin"]), "Operator can issue a named campaign via stdin");
            using var issued = JsonDocument.Parse(output.ToString());
            string id = issued.RootElement.GetProperty("id").GetString()!;
            Equal("SPRING-TEST", issued.RootElement.GetProperty("code").GetString(), "Creation returns the printable code");
            output.GetStringBuilder().Clear();
            Equal(0, CouponCommands.Run(["coupons", "list"]), "Operator can list issued coupons");
            using var listed = JsonDocument.Parse(output.ToString());
            var record = listed.RootElement.EnumerateArray().Single();
            Check(record.GetProperty("id").GetString() == id && record.GetProperty("amountFen").GetInt32() == 400
                && !output.ToString().Contains("SPRING", StringComparison.OrdinalIgnoreCase)
                && !record.TryGetProperty("codeHash", out _), "Listing contains price and activity ID but no code or digest");
            output.GetStringBuilder().Clear();
            Equal(0, CouponCommands.Run(["coupons", "revoke", "--id", id]), "Operator can end a campaign");
            Check(fixture.Store.ListCoupons().Single().RevokedAt.HasValue, "Revocation persists");
        }
        finally
        {
            Console.SetIn(stdin); Console.SetOut(stdout); Console.SetError(stderr);
            Environment.SetEnvironmentVariable("MUGONZEI_DATABASE", originalDb);
            Environment.SetEnvironmentVariable("MUGONZEI_PLANS_JSON", originalPlans);
        }
    }
}
