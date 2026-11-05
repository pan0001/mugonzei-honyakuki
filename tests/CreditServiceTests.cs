using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamVrTranslator;

public static class CreditServiceTests
{
    private const string Endpoint = "https://credits.example/base/";
    private const string Secret = "test-credit-token-never-print";
    private const string Password = "test-password-never-store";

    public static async Task RunAsync()
    {
        ValidateAddresses();
        ProtectedSessions();
        await AuthenticationAndAccountContract();
        await EmailDeliveryFailures();
        await RedemptionContract();
        await RecoverWithoutResubmission();
        await MissingAfterDisconnectReusesId();
        await CancellationUsesIndependentToken();
        await FailedConnectionCanBeRecoveredLater();
        await ResponsesAndErrorsStayPrivate();
        RenderOfflineWindow();
        Console.WriteLine("PASS credit endpoint binding, DPAPI sessions, account/order contract, idempotent task recovery, cancellation, private errors and offline account layout");
    }

    private static void ValidateAddresses()
    {
        Equal(Endpoint, CreditServiceClient.NormalizeServerUrl("HTTPS://CREDITS.EXAMPLE:443/base"), "canonical endpoint");
        Equal("http://127.0.0.1:5187/", CreditServiceClient.NormalizeServerUrl("http://127.0.0.1:5187"), "local HTTP");
        Equal("http://[::1]:5187/", CreditServiceClient.NormalizeServerUrl("http://[::1]:5187"), "IPv6 loopback");
        foreach (string url in new[] { "http://remote.example", "ftp://example.com", "https://user:pass@example.com", "https://example.com?secret=x", "https://example.com/#x", "https://example.com\\evil", "example.com", "" })
            Throws(() => CreditServiceClient.NormalizeServerUrl(url), "unsafe endpoint");
        foreach (string host in new[] { "afdian.com", "www.afdian.com", "afdian.net", "ifdian.net" })
            Equal(host, CreditServiceClient.ValidatePaymentUrl("https://" + host + "/order/create?id=sample", Endpoint).Host, "official payment host");
        Equal("127.0.0.1", CreditServiceClient.ValidatePaymentUrl("http://127.0.0.1:5187/mock/pay/1", "http://127.0.0.1:5187/").Host, "same local origin");
        foreach (string url in new[] { "https://afdian.com.evil.example/pay", "https://evil.afdian.com/pay", "http://afdian.com/pay", "https://afdian.com:8443/pay", "https://user@afdian.com/pay", "javascript:alert(1)", "http://127.0.0.1:5188/pay" })
            Throws(() => CreditServiceClient.ValidatePaymentUrl(url, "http://127.0.0.1:5187/"), "unsafe payment target");
        Throws(() => CreditServiceClient.ValidatePaymentUrl("http://127.0.0.1:5187/pay", Endpoint), "remote server cannot open local payment");
    }

    private static void ProtectedSessions()
    {
        using var temp = new TemporarySessions();
        temp.Store.Save(Endpoint, Secret, "tester", DateTimeOffset.UtcNow.AddHours(1));
        var id = Guid.NewGuid(); temp.Store.RememberTask(Endpoint, Secret, id);
        var loaded = temp.Store.Load(Endpoint)!;
        Equal(Secret, loaded.Token, "DPAPI token roundtrip"); Equal(id, loaded.RecentTaskId, "task ID roundtrip");
        Equal(null, temp.Store.Load("https://other.example/base/"), "another origin has no token");
        Equal(null, temp.Store.Load("https://credits.example/other/"), "another base path has no token");
        string path = Directory.GetFiles(temp.Path).Single();
        string bytes = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert(!bytes.Contains(Secret) && !bytes.Contains("tester") && !bytes.Contains(Endpoint), "whole session encrypted");
        const string other = "https://other.example/";
        temp.Store.Save(other, "other-test-token", "other_user", DateTimeOffset.UtcNow.AddHours(1));
        string otherPath = Path.Combine(temp.Path, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(other))) + ".bin");
        File.WriteAllBytes(otherPath, File.ReadAllBytes(path));
        Throws(() => temp.Store.Load(other), "DPAPI entropy rejects cross-endpoint file copy");
        temp.Store.Remove(other); Assert(!File.Exists(otherPath), "corrupt session can be cleared without decrypting");
        temp.Store.Save(Endpoint, "new-test-token", "tester", DateTimeOffset.UtcNow.AddHours(1));
        Equal(id, temp.Store.Load(Endpoint)!.RecentTaskId, "same account re-login preserves recovery");
        temp.Store.Remove(Endpoint, Secret); Assert(temp.Store.Load(Endpoint) is not null, "stale logout does not delete newer login");
        temp.Store.Save(Endpoint, "third-test-token", "different_user", DateTimeOffset.UtcNow.AddHours(1));
        Equal(null, temp.Store.Load(Endpoint)!.RecentTaskId, "different account never inherits previous task");
    }

    private static async Task AuthenticationAndAccountContract()
    {
        const string email = "synthetic@example.test", code = "012345";
        using var temp = new TemporarySessions();
        int requests = 0; var orderRequest = Guid.NewGuid();
        using var client = Client(temp, async (request, token) =>
        {
            requests++;
            string path = request.RequestUri!.AbsolutePath;
            Assert(path.StartsWith("/base/v1/", StringComparison.Ordinal), "API keeps configured base path");
            if (path.EndsWith("/register/email-code"))
            {
                Assert(request.Method == HttpMethod.Post && request.RequestUri.Query == "" && request.Headers.Authorization is null,
                    "verification email is requested without an existing session, query string or login credentials");
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert(json.RootElement.EnumerateObject().Count() == 1 && json.RootElement.GetProperty("email").GetString() == email,
                    "only destination mailbox is submitted to the code endpoint");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/login") || path.EndsWith("/register"))
            {
                Assert(request.Headers.Authorization is null, "credentials request does not reuse a token");
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Equal("tester", json.RootElement.GetProperty("username").GetString(), "username normalized");
                Equal(Password, json.RootElement.GetProperty("password").GetString(), "password only in login body");
                if (path.EndsWith("/register"))
                {
                    Equal(email, json.RootElement.GetProperty("email").GetString(), "registration supplies the verified mailbox");
                    Equal(code, json.RootElement.GetProperty("code").GetString(), "leading zero in code preserved");
                }
                else Assert(json.RootElement.EnumerateObject().Count() == 2, "existing account login only uses username and password");
                return Response(new { token = Secret, username = "tester", expiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            }
            Equal(Secret, request.Headers.Authorization?.Parameter, "bound bearer session");
            if (path.EndsWith("/account")) return Response(new { username = "tester", balance = 10, reserved = 1 });
            if (path.EndsWith("/ledger")) return Response(new[] { new { kind = "purchase", amount = 10, balance = 10, createdAt = DateTimeOffset.UtcNow, reference = "order-1" } });
            if (path.EndsWith("/plans")) return Response(new[] { new { id = "plan-1", name = "测试次数包", credits = 10, amountFen = 100 } });
            if (path.EndsWith("/orders") && request.Method == HttpMethod.Post)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Equal(orderRequest, json.RootElement.GetProperty("requestId").GetGuid(), "order idempotency ID");
                Equal("plan-1", json.RootElement.GetProperty("planId").GetString(), "plan ID");
                return Response(Order());
            }
            if (path.EndsWith("/orders")) return Response(new[] { Order() });
            if (path.EndsWith("/orders/order-1")) { Equal("?refresh=true", request.RequestUri.Query, "refresh requests provider reconciliation"); return Response(Order()); }
            if (path.EndsWith("/logout")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            throw new Exception("Unexpected credit route.");
        });
        Equal(0, requests, "constructor does not connect");
        await ExpectAsync<InvalidOperationException>(() => client.LoginAsync("ab", Password), "3–32");
        await ExpectAsync<InvalidOperationException>(() => client.RegisterAsync("tester", "short", email, code), "10–128");
        foreach (string invalid in new[] { "", "name", "two@example.test,more@example.test", "Fixture <name@example.test>", "bad\r\nX@example.test" })
            await ExpectAsync<InvalidOperationException>(() => client.RequestRegistrationCodeAsync(invalid), "邮箱");
        foreach (string invalid in new[] { "", "12345", "1234567", "１２３４５６", "abcd56" })
            await ExpectAsync<InvalidOperationException>(() => client.RegisterAsync("tester", Password, email, invalid), "6 位");
        Equal(0, requests, "invalid credentials stay local");
        await client.RequestRegistrationCodeAsync(" " + email + " ");
        Assert(!Directory.Exists(temp.Path) || Directory.GetFiles(temp.Path).Length == 0,
            "requesting an email creates no local credential or session");
        await client.RegisterAsync("Tester", Password, email, code);
        await client.LoginAsync("Tester", Password);
        Equal(10L, (await client.GetAccountAsync()).Balance, "account contract");
        Equal(1, (await client.GetLedgerAsync()).Length, "ledger contract");
        Equal(100L, (await client.GetPlansAsync())[0].AmountFen, "plan fen contract");
        Equal("order-1", (await client.CreateOrderAsync("plan-1", orderRequest)).Id, "create order contract");
        Equal(1, (await client.GetOrdersAsync()).Length, "orders contract");
        Equal("order-1", (await client.GetOrderAsync("order-1", true)).Id, "refresh order contract");
        await client.LogoutAsync(); Equal(null, temp.Store.Load(Endpoint), "logout removes local session");
        await ExpectAsync<InvalidOperationException>(() => client.GetAccountAsync(), "登录");
    }

    private static async Task EmailDeliveryFailures()
    {
        using var temp = Authenticated();
        string stored = Directory.GetFiles(temp.Path).Single();
        byte[] original = File.ReadAllBytes(stored);
        foreach (var (status, hint) in new[] { (HttpStatusCode.ServiceUnavailable, "暂时无法发送"),
            (HttpStatusCode.TooManyRequests, "稍后"), (HttpStatusCode.NotFound, "更新服务端"),
            (HttpStatusCode.OK, "响应格式") })
        {
            using var client = Client(temp, (request, _) =>
            {
                Assert(request.Headers.Authorization is null, "email delivery never sends an account token");
                return Task.FromResult(Response(new { message = Secret }, status));
            });
            var failure = await ExpectAsync<CreditServiceException>(() => client.RequestRegistrationCodeAsync("synthetic@example.test"), hint);
            Assert(!failure.Message.Contains(Secret), "mail delivery failure never discloses raw diagnostics");
        }
        Assert(File.ReadAllBytes(stored).AsSpan().SequenceEqual(original), "requesting codes leaves a preexisting session intact");
    }

    private static async Task RedemptionContract()
    {
        const string code = "MU-0123-4567-89AB-CDEF-0123-4567-89AB-CDEF";
        using var temp = Authenticated();
        Guid recent = Guid.NewGuid(); temp.Store.RememberTask(Endpoint, Secret, recent);
        var savedFiles = Directory.GetFiles(temp.Path).ToDictionary(path => path, File.ReadAllBytes);
        int requests = 0;
        using (var client = Client(temp, async (request, token) =>
        {
            requests++;
            Equal(HttpMethod.Post, request.Method, "redemption uses POST");
            Equal("/base/v1/redemptions", request.RequestUri!.AbsolutePath, "redemption retains configured base path");
            Equal("", request.RequestUri.Query, "code never appears in URL query");
            Equal(Secret, request.Headers.Authorization?.Parameter, "redemption uses the destination-bound bearer session");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Equal(1, body.RootElement.EnumerateObject().Count(), "redemption body cannot select an amount or beneficiary");
            Equal(code, body.RootElement.GetProperty("code").GetString(), "redemption normalizes casing and pasted whitespace");
            return Response(new { credits = 10, balance = 31L, reserved = 2L, alreadyRedeemed = requests > 1, reference = "redemption:synthetic-id" });
        }))
        {
            var first = await client.RedeemAsync(" \t" + code.ToLowerInvariant().Replace("-", " -\n") + " ");
            Assert(first.Credits == 10 && first.Balance == 31 && first.Reserved == 2 && !first.AlreadyRedeemed,
                "redemption amount and balance contract");
            var retry = await client.RedeemAsync(code.Replace("-", ""));
            Assert(retry.AlreadyRedeemed && retry.Reference == first.Reference, "same-code retry reports the prior redemption");
            foreach (string invalid in new[] { "", "MU-1234", "MU-" + new string('G', 32), "MU-" + new string('\uff21', 32), new string('x', 257) })
                await ExpectAsync<InvalidOperationException>(() => client.RedeemAsync(invalid), "兑换码");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => client.RedeemAsync(code, cancelled.Token));
            Equal(2, requests, "invalid and already cancelled redemptions never send requests");
        }
        Equal(recent, temp.Store.Load(Endpoint)!.RecentTaskId, "redemption never replaces translation recovery state");
        Assert(Directory.GetFiles(temp.Path).Length == savedFiles.Count && savedFiles.All(pair => File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value)),
            "redeeming codes does not persist codes or rewrite the session file");

        using (var loggedOut = new TemporarySessions())
        using (var client = Client(loggedOut, (_, _) => throw new Exception("Logged-out redemption reached transport")))
            await ExpectAsync<InvalidOperationException>(() => client.RedeemAsync(code), "登录");
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.TooManyRequests, HttpStatusCode.Redirect })
        {
            int calls = 0;
            using var client = Client(temp, (_, _) =>
            {
                calls++;
                var response = Response(new { error = "private upstream text", code, token = Secret }, status);
                response.Headers.Location = new Uri("https://other.example/" + code);
                return Task.FromResult(response);
            });
            var error = await ExpectAsync<CreditServiceException>(() => client.RedeemAsync(code));
            Assert(!error.ToString().Contains(code) && !error.ToString().Contains(Secret) && !error.ToString().Contains("private upstream text"),
                "redemption errors never expose code, credentials or remote error body");
            Equal(1, calls, "rejected redemption does not automatically retry or follow redirects");
        }
        int disconnectedCalls = 0;
        using (var disconnected = Client(temp, (_, _) =>
        {
            disconnectedCalls++;
            throw new HttpRequestException("synthetic transport diagnostics " + code);
        }))
        {
            var error = await ExpectAsync<CreditServiceException>(() => disconnected.RedeemAsync(code), "同一账户");
            Assert(error.IsTransient && !error.ToString().Contains(code), "ambiguous redemption gives a private safe-retry explanation");
            Equal(1, disconnectedCalls, "an uncertain redemption result is not silently resubmitted");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var client = Client(temp, async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new Exception("Cancelled redemption unexpectedly resumed");
        }))
            await ExpectAsync<OperationCanceledException>(() => client.RedeemAsync(code, cancellation.Token));
        foreach (var invalid in new[]
        {
            new { credits = 0, balance = 1L, reserved = 0L, alreadyRedeemed = false, reference = "valid" },
            new { credits = 1, balance = -1L, reserved = 0L, alreadyRedeemed = false, reference = "valid" },
            new { credits = 1, balance = 1L, reserved = 2L, alreadyRedeemed = false, reference = "valid" },
            new { credits = 1, balance = 1L, reserved = 0L, alreadyRedeemed = false, reference = "" }
        })
        {
            using var malformed = Client(temp, (_, _) => Task.FromResult(Response(invalid)));
            await ExpectAsync<CreditServiceException>(() => malformed.RedeemAsync(code), "响应格式");
        }
    }

    private static async Task RecoverWithoutResubmission()
    {
        using var temp = Authenticated(); var id = Guid.NewGuid(); int put = 0, get = 0;
        using var client = Client(temp, async (request, token) =>
        {
            Equal(id.ToString(), request.RequestUri!.Segments.Last(), "single task ID");
            if (request.Method == HttpMethod.Put)
            {
                put++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Equal("handwriting", body.RootElement.GetProperty("mode").GetString(), "handwriting is one billed task");
                Equal(Convert.ToBase64String([1, 2, 3]), body.RootElement.GetProperty("imageBase64").GetString(), "image upload");
                throw new HttpRequestException("private provider diagnostics " + Secret);
            }
            get++;
            return Response(TaskResult(id, get == 1 ? "running" : "succeeded"));
        });
        var result = await client.ExecuteAsync(id, "handwriting", null, [1, 2, 3], "日本語", default);
        Equal("succeeded", result.Status, "same task finishes"); Equal(1, put, "lost PUT response is not blindly resubmitted"); Equal(2, get, "queries original task");
        Equal(id, temp.Store.Load(Endpoint)!.RecentTaskId, "recovery ID persisted");
        foreach (string path in Directory.GetFiles(temp.Path)) Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("private source"), "no translation history on disk");
    }

    private static async Task MissingAfterDisconnectReusesId()
    {
        using var temp = Authenticated(); var id = Guid.NewGuid(); var sequence = new List<string>();
        using var client = Client(temp, (request, _) =>
        {
            sequence.Add(request.Method.Method);
            Assert(request.RequestUri!.AbsolutePath.EndsWith(id.ToString()), "retry retains caller request ID");
            if (sequence.Count == 1) throw new HttpRequestException();
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(new { error = "not_found" }, HttpStatusCode.NotFound));
            return Task.FromResult(Response(TaskResult(id, "no_text")));
        });
        Equal("no_text", (await client.ExecuteAsync(id, "text", "sample", null, "English", default)).Status, "no-text result");
        Equal("PUT,GET,PUT", string.Join(',', sequence), "GET before same-ID resubmit");
    }

    private static async Task CancellationUsesIndependentToken()
    {
        using var temp = Authenticated(); var id = Guid.NewGuid(); int requests = 0, cancel = 0;
        using var cts = new CancellationTokenSource();
        using var client = Client(temp, async (request, token) =>
        {
            requests++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/cancel"))
            { cancel++; Assert(!token.IsCancellationRequested, "remote cancel has a fresh token"); return Response(TaskResult(id, "succeeded")); }
            cts.Cancel(); await Task.Delay(Timeout.Infinite, token);
            throw new Exception("unreachable");
        });
        await ExpectAsync<OperationCanceledException>(() => client.ExecuteAsync(id, "text", "sample", null, "English", cts.Token));
        Equal(1, cancel, "attempt cancellation once even after local cancellation"); Equal(2, requests, "no replacement billable request");
        using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => client.ExecuteAsync(Guid.NewGuid(), "text", "sample", null, "English", alreadyCancelled.Token));
        Equal(2, requests, "pre-cancel sends nothing");
    }

    private static async Task FailedConnectionCanBeRecoveredLater()
    {
        using var temp = Authenticated(); var id = Guid.NewGuid(); int puts = 0;
        using (var first = Client(temp, (request, _) =>
        {
            if (request.Method == HttpMethod.Put) puts++;
            throw new HttpRequestException();
        })) await ExpectAsync<CreditServiceException>(() => first.ExecuteAsync(id, "text", "sample", null, "English", default), "原编号");
        Equal(1, puts, "uncertain query failure never starts another task");
        using var recovered = Client(temp, (request, _) =>
        {
            Equal(HttpMethod.Get, request.Method, "recovery only queries");
            Assert(request.RequestUri!.AbsolutePath.EndsWith(id.ToString()), "recovery remembers original ID");
            return Task.FromResult(Response(TaskResult(id, "succeeded")));
        });
        Equal("translated", (await recovered.ResumeLatestAsync()).Translation, "restart recovery result");
    }

    private static async Task ResponsesAndErrorsStayPrivate()
    {
        using var temp = Authenticated();
        foreach (var code in new[] { HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.PaymentRequired })
        {
            int count = 0;
            using var client = Client(temp, (_, _) =>
            {
                count++; var response = Response(new { password = Password, token = Secret }, code);
                response.Headers.Location = new Uri("https://evil.example/"); return Task.FromResult(response);
            });
            var error = await ExpectAsync<CreditServiceException>(() => client.GetAccountAsync());
            Assert(!error.ToString().Contains(Secret) && !error.ToString().Contains(Password), "server error body never shown");
            Equal(1, count, "no redirect follow");
        }
        using (var malformed = Client(temp, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("invalid " + Secret) })))
        {
            var error = await ExpectAsync<CreditServiceException>(() => malformed.GetAccountAsync());
            Assert(!error.ToString().Contains(Secret), "parser error does not expose response body");
        }
        var id = Guid.NewGuid();
        using var failed = Client(temp, (_, _) => Task.FromResult(Response(new { id, status = "failed", sourceText = "", translation = "", error = Secret, balance = 8 })));
        Assert(!(await failed.GetTaskAsync(id)).Error!.Contains(Secret), "task provider error redacted");
        using var expired = Client(temp, (_, _) => Task.FromResult(Response(new { id, status = "succeeded", sourceText = "", translation = "", error = "result_expired", balance = 8 })));
        var expiredResult = await expired.GetTaskAsync(id);
        Assert(expiredResult.IsExpired && expiredResult.Error!.Contains("保存期"), "expired retained success is explicitly distinguished");
        await ExpectAsync<InvalidOperationException>(() => failed.ExecuteAsync(Guid.NewGuid(), "text", new string('x', CreditServiceClient.MaxTextCharacters + 1), null, "English", default), "16,000");
        await ExpectAsync<InvalidOperationException>(() => failed.ExecuteAsync(Guid.NewGuid(), "image", null, new byte[CreditServiceClient.MaxImageBytes + 1], "English", default), "12 MB");
        using var differentServer = new CreditServiceClient("https://another.example/", temp.Store, new Handler((_, _) => throw new Exception("Credential was sent to a different endpoint.")));
        await ExpectAsync<InvalidOperationException>(() => differentServer.GetAccountAsync(), "登录");
    }

    private static void RenderOfflineWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new CreditAccountForm("http://127.0.0.1:5187", _ => throw new Exception("Opening the account window must not create a network client."));
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.ShowInTaskbar = false;
                form.Show(); Application.DoEvents(); form.PerformLayout(); Application.DoEvents();
                var controls = Descendants(form).ToArray();
                Assert(controls.OfType<TextBox>().Single(c => c.Name == "creditPassword").UseSystemPasswordChar, "password masked");
                Equal(4, controls.OfType<TabControl>().Single().TabPages.Count, "account tabs");
                string directory = Path.Combine(AppContext.BaseDirectory, "artifacts"); Directory.CreateDirectory(directory);
                using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(directory, "credit-account-preview.png"));
                form.Size = form.MinimumSize; form.PerformLayout(); Application.DoEvents();
                Assert(controls.OfType<Button>().All(b => b.Width > 0 && b.Height > 0), "minimum layout controls remain measurable");
                form.Close();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Credit account offline UI test failed.", failure);
    }

    private static IEnumerable<Control> Descendants(Control control)
    { foreach (Control child in control.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static object Order() => new { id = "order-1", status = "pending", payUrl = "https://afdian.com/order/create?id=test", credits = 10, amountFen = 100, createdAt = DateTimeOffset.UtcNow };
    private static object TaskResult(Guid id, string status) => new { id, status, sourceText = "private source", translation = status == "succeeded" ? "translated" : "", error = (string?)null, balance = 9 };
    private static TemporarySessions Authenticated() { var temp = new TemporarySessions(); temp.Store.Save(Endpoint, Secret, "tester", DateTimeOffset.UtcNow.AddHours(1)); return temp; }
    private static CreditServiceClient Client(TemporarySessions temp, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(Endpoint, temp.Store, new Handler(send));
    private static HttpResponseMessage Response(object data, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
    private static void Throws(Action action, string message) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected credit validation error: " + message); }
    private static async Task<T> ExpectAsync<T>(Func<Task> action, string? text = null) where T : Exception
    { try { await action(); } catch (T error) { if (text is not null) Assert(error.Message.Contains(text), "expected friendly error"); return error; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Assert(bool value, string message) { if (!value) throw new Exception("Credit service test failed: " + message); }
    private static void Equal<T>(T expected, T actual, string message) => Assert(EqualityComparer<T>.Default.Equals(expected, actual), message);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class TemporarySessions : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MugonzeiCreditTests-" + Guid.NewGuid().ToString("N"));
        public CreditSessionStore Store { get; }
        public TemporarySessions() => Store = new CreditSessionStore(Path);
        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            // Only exact files in this uniquely allocated directory; never recurse through an unverified path.
            foreach (string file in Directory.GetFiles(Path)) File.Delete(file);
            Directory.Delete(Path);
        }
    }
}
