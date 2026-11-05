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
            await Run("document.querySelector('[data-page=history]').click();window.chrome.webview.postMessage({type:'verify',id:'ours'});");
            await Wait("document.querySelector('#orders').textContent.includes('已到账') && !document.querySelector('#refresh').disabled");
            await Run("document.querySelector('[data-page=recovery]').click();document.querySelector('#recover').click()");
            await Wait("document.querySelector('#translation').textContent==='fixture translation' && !document.querySelector('#refresh').disabled");
            await Run("document.querySelector('#cancel').click()");
            await Wait("document.querySelector('#task-status').textContent==='已取消' && !document.querySelector('#refresh').disabled");
            await Run("document.querySelector('#logout').click()");
            await Wait("document.querySelector('#content').hidden && document.querySelector('#translation').textContent===''");
            if (store.Load(Endpoint) != null || nativeMessages.Any(message => message.Contains(Session) || message.Contains(Code) || message.Contains("private-fixture-checkout")))
                throw new Exception("A credential or checkout query escaped the native boundary");
            Console.WriteLine("PASS WebView2 account balance, redemption, validated checkout, order verification, ledger, recovery, cancellation and sign-out using isolated fixtures");
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
        internal int Purchases;
        private int balance = 5;

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
                        if (body.RootElement.GetProperty("planId").GetString() != "test-plan" || body.RootElement.GetProperty("requestId").GetGuid() == Guid.Empty)
                            throw new Exception("Wrong plan or missing idempotency key");
                    Purchases++;
                    response = Order("ours", Checkout); break;
                case "/v1/orders": response = new[] { Order("bad-link", "https://unexpected.example/pay") }; break;
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
