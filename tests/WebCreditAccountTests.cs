using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SteamVrTranslator;

internal static class WebCreditAccountTests
{
    private const string Endpoint = "https://account-fixture.example/";
    private const string Session = "synthetic-private-session-for-tests";
    private const string Checkout = "https://ifdian.net/order/create?remark=private-fixture-checkout";
    private const string Code = "MU-1111-2222-3333-4444-5555-6666-7777-8888";

    internal static Task RunAsync() => DesktopResponsivenessTests.OnUiThread(async () =>
    {
        string directory = Directory.CreateTempSubdirectory("MugonzeiAccountWeb-").FullName;
        Guid recent = Guid.NewGuid();
        var store = new CreditSessionStore(Path.Combine(directory, "sessions"));
        store.Save(Endpoint, Session, "fixture", DateTimeOffset.UtcNow.AddHours(1));
        store.RememberTask(Endpoint, Session, recent);
        var api = new FixtureHttp(recent);
        using var client = new CreditServiceClient(Endpoint, store, api);
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(840, 670), ShowInTaskbar = false };
        var urls = new List<Uri>();
        var nativeMessages = new List<string>();
        WebCreditAccount? host = null;
        using var surface = new LocalWebSurface("account.html", m => host!.Receive(m), Path.Combine(directory, "web"));
        using var account = host = new WebCreditAccount(client, payload =>
        {
            nativeMessages.Add(JsonSerializer.Serialize(payload, LocalWebSurface.Json));
            surface.Post(payload);
        }, urls.Add);
        form.Controls.Add(surface);
        try
        {
            form.Show();
            async Task Wait(string script)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromSeconds(20))
                {
                    if (surface.CoreForTests is { } core && await core.ExecuteScriptAsync(script) == "true") return;
                    await Task.Delay(50);
                }
                throw new TimeoutException("Account Web UI did not reach the expected state: " + script);
            }
            Task Run(string script) => surface.CoreForTests!.ExecuteScriptAsync(script);
            await Wait("document.querySelector('#available')?.textContent==='4' && document.querySelector('#ledger').textContent.includes('充值到账') && !document.querySelector('#refresh').disabled");
            await Run("document.querySelector('#code').value='" + Code + "';document.querySelector('#redeem-form').requestSubmit()");
            await Wait("document.querySelector('#available').textContent==='14' && !document.querySelector('#refresh').disabled");
            await Run("document.querySelector('#plans button').click()");
            await Wait("document.querySelector('#notice').textContent.includes('已打开付款页面')");
            if (urls.Count != 1 || urls.Single().AbsoluteUri != Checkout || api.Purchases != 1) throw new Exception("Checkout did not use the verified native order URL");
            await Run("window.chrome.webview.postMessage({type:'pay',id:'foreign'});");
            await Wait("document.querySelector('#notice').textContent.includes('选择一个订单')");
            await Run("window.chrome.webview.postMessage({type:'pay',id:'bad-link'});");
            await Wait("document.querySelector('#notice').textContent.includes('未打开浏览器')");
            if (urls.Count != 1) throw new Exception("Untrusted checkout was opened");
            async Task ApplyCoupon(string value)
            {
                await Run($"{{const input = document.querySelector('#coupon-code'); input.value = '{value}'; input.dispatchEvent(new Event('input')); document.querySelector('#plans button').click();}}");
            }
            await ApplyCoupon("unsupported");
            await Wait("document.querySelector('#notice').textContent.includes('不支持优惠码') && !document.querySelector('#refresh').disabled");
            if (urls.Count != 1 || api.Purchases != 1 || api.DiscountOrders != 0)
                throw new Exception("Unsupported coupon server fell back to a full-price order");
            await ApplyCoupon("price-changed");
            await Wait("!document.querySelector('#coupon-quote').hidden && !document.querySelector('#confirm-coupon').disabled");
            await Run("document.querySelector('#confirm-coupon').click()");
            await Wait("document.querySelector('#coupon-quote').hidden && document.querySelector('#notice').textContent.includes('不一致') && !document.querySelector('#refresh').disabled");
            if (urls.Count != 1) throw new Exception("An order at a different price opened a payment link");
            await Run("document.querySelector('#refresh').click()");
            await Wait("[...document.querySelectorAll('#orders article')].some(e => e.textContent.includes('coupon-review') && e.textContent.includes('已阻止付款') && !e.textContent.includes('去付款')) && !document.querySelector('#refresh').disabled");
            await Run("window.chrome.webview.postMessage({type:'pay',id:'coupon-review'})");
            await Wait("document.querySelector('#notice').textContent.includes('该订单已阻止付款') && !document.querySelector('#refresh').disabled");
            using (var reopened = new WebCreditAccount(new CreditServiceClient(Endpoint, store, new FixtureHttp(recent) { IncludeReviewOrder = true }),
                _ => { }, urls.Add))
            {
                await reopened.Receive(JsonSerializer.SerializeToElement(new { type = "ready" }));
                await reopened.Receive(JsonSerializer.SerializeToElement(new { type = "pay", id = "coupon-review" }));
            }
            if (urls.Count != 1) throw new Exception("Refreshing or reopening the account allowed payment of a rejected order");
            await ApplyCoupon("fixture-coupon");
            await Wait("document.querySelector('#quote-total').textContent.includes('应付 ¥0.75') && document.querySelector('#quote-details').textContent.includes('原价 ¥1.00 · 优惠 ¥0.25') && !document.querySelector('#refresh').disabled");
            if (urls.Count != 1 || api.DiscountOrders != 1) throw new Exception("Getting a quote started payment");
            await Run("document.querySelector('#coupon-code').value = 'changed'; document.querySelector('#coupon-code').dispatchEvent(new Event('input'));");
            await Wait("document.querySelector('#coupon-quote').hidden");
            await ApplyCoupon("fixture-coupon");
            await Wait("!document.querySelector('#coupon-quote').hidden && !document.querySelector('#confirm-coupon').disabled");
            await Run("document.querySelector('#confirm-coupon').click()");
            await Wait("document.querySelector('#notice').textContent.includes('稍后重试') && !document.querySelector('#confirm-coupon').disabled");
            await Run("document.querySelector('#confirm-coupon').click()");
            await Wait("document.querySelector('#coupon-quote').hidden && document.querySelector('#coupon-code').value === '' && document.querySelector('#notice').textContent.includes('已打开付款页面') && !document.querySelector('#refresh').disabled");
            if (urls.Count != 2 || urls.Last().AbsoluteUri != Checkout || api.DiscountOrders != 3 || api.Purchases != 1)
                throw new Exception("Confirmed discounted purchase or idempotent retry failed");
            await Run("document.querySelector('[data-page=history]').click();window.chrome.webview.postMessage({type:'verify',id:'ours'});");
            await Wait("document.querySelector('#orders').textContent.includes('已到账') && !document.querySelector('#refresh').disabled");
            await Run("document.querySelector('[data-page=recovery]').click();document.querySelector('#recover').click()");
            await Wait("document.querySelector('#translation').textContent==='fixture translation' && !document.querySelector('#refresh').disabled");
            await Run("document.querySelector('#cancel').click()");
            await Wait("document.querySelector('#task-status').textContent==='已取消' && !document.querySelector('#refresh').disabled");
            await Run("document.querySelector('#logout').click()");
            await Wait("document.querySelector('#content').hidden && document.querySelector('#translation').textContent===''");
            if (store.Load(Endpoint) != null || nativeMessages.Any(message => message.Contains(Session) || message.Contains(Code) || message.Contains("fixture-coupon") || message.Contains("private-fixture-checkout")))
                throw new Exception("A credential or checkout query escaped the native boundary");
            Console.WriteLine("PASS WebView2 account, redemption, ordinary checkout, coupon quotes, price confirmation, safe retries, order verification, recovery and sign-out using isolated fixtures");
        }
        finally
        {
            form.Dispose();
            for (int attempt = 0; attempt < 15 && Directory.Exists(directory); attempt++)
            {
                try { Directory.Delete(directory, true); }
                catch (IOException) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) { await Task.Delay(100); }
            }
        }
    });

    private sealed class FixtureHttp(Guid recent) : HttpMessageHandler
    {
        internal int Purchases, DiscountOrders;
        internal bool IncludeReviewOrder;
        private int balance = 5;
        private Guid? retryId;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Parameter != Session) throw new Exception("Missing bound session");
            var uri = request.RequestUri!;
            string route = uri.AbsolutePath;
            object Order(string id, string url, string status = "pending") => new { id, payUrl = url, status, credits = 10, amountFen = 100, createdAt = DateTimeOffset.UtcNow };
            object response;
            switch (route)
            {
                case "/v1/account": response = new { balance, reserved = 1 }; break;
                case "/v1/plans": response = new[] { new { id = "test-plan", name = "fixture plan", credits = 10, amountFen = 100 } }; break;
                case "/v1/ledger": response = new[] { new { kind = "recharge", amount = 1, balance, reference = "test-ledger", createdAt = DateTimeOffset.UtcNow } }; break;
                case "/v1/redemptions":
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                        if (body.RootElement.GetProperty("code").GetString() != Code) throw new Exception("Wrong redemption body");
                    balance = 15;
                    response = new { balance, reserved = 1, credits = 10, reference = "redeem-fixture" }; break;
                case "/v1/orders" when request.Method == HttpMethod.Post:
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                        if (body.RootElement.GetProperty("planId").GetString() != "test-plan" || body.RootElement.GetProperty("requestId").GetGuid() == Guid.Empty
                            || body.RootElement.EnumerateObject().Count() != 2)
                            throw new Exception("Wrong plan or missing idempotency key");
                    Purchases++;
                    response = Order("ours", Checkout); break;
                case "/v1/checkout/quote":
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                    {
                        if (body.RootElement.GetProperty("couponCode").GetString() == "unsupported")
                            return new HttpResponseMessage(HttpStatusCode.NotFound);
                        response = new { planId = "test-plan", originalAmountFen = 100, discountFen = 25, amountFen = 75, credits = 10 };
                    }
                    break;
                case "/v1/checkout/orders":
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                    {
                        DiscountOrders++;
                        string? coupon = body.RootElement.GetProperty("couponCode").GetString();
                        Guid id = body.RootElement.GetProperty("requestId").GetGuid();
                        if (id == Guid.Empty || body.RootElement.GetProperty("expectedAmountFen").GetInt32() != 75)
                            throw new Exception("Discounted order does not bind to the confirmed quote");
                        if (coupon == "fixture-coupon")
                        {
                            if (retryId == null) { retryId = id; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
                            if (retryId != id) throw new Exception("Discounted order retry used a new idempotency key");
                        }
                        int actual = coupon == "price-changed" ? 90 : 75;
                        if (coupon == "price-changed") IncludeReviewOrder = true;
                        response = new { id = coupon == "price-changed" ? "coupon-review" : "discounted", planId = "test-plan", status = "pending", credits = 10, amountFen = actual,
                            originalAmountFen = 100, discountFen = 100 - actual, payUrl = Checkout, createdAt = DateTimeOffset.UtcNow };
                    }
                    break;
                case "/v1/orders":
                    response = IncludeReviewOrder ? new[] { Order("bad-link", "https://unexpected.example/pay"), Order("coupon-review", Checkout), Order("ours", Checkout) }
                        : new[] { Order("bad-link", "https://unexpected.example/pay") };
                    break;
                case "/v1/orders/ours" when uri.Query == "?refresh=true": response = Order("ours", Checkout, "paid"); break;
                case "/v1/auth/logout": return new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    if (route == "/v1/translations/" + recent) response = new { id = recent, status = "succeeded", sourceText = "fixture original", translation = "fixture translation" };
                    else if (route == "/v1/translations/" + recent + "/cancel") response = new { id = recent, status = "cancelled" };
                    else throw new Exception("Unknown fixture route");
                    break;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }
}
