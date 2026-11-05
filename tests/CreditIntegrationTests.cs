using System.Reflection;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using SteamVrTranslator;

public static class CreditIntegrationTests
{
    public static async Task RunEndToEndAsync(string testHostDll, string dotnetPath)
    {
        testHostDll = Path.GetFullPath(testHostDll);
        if (Path.GetFileName(testHostDll) != "Mugonzei.Server.Tests.dll" || !File.Exists(testHostDll) || !File.Exists(dotnetPath))
            throw new Exception("Build server.tests and pass Mugonzei.Server.Tests.dll and the SDK dotnet path.");
        string fixture = Path.Combine(Path.GetTempPath(), "mugonzei-credit-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        string address = $"http://127.0.0.1:{port}/";
        var start = new ProcessStartInfo(dotnetPath) { WorkingDirectory = fixture, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        string inbox = Path.Combine(fixture, "synthetic-inbox.txt");
        start.ArgumentList.Add(testHostDll); start.ArgumentList.Add("--serve-e2e"); start.ArgumentList.Add(inbox);
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add(address);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["MUGONZEI_DATABASE"] = Path.Combine(fixture, "synthetic.db");
        start.Environment["MUGONZEI_LIVE_PAYMENTS"] = "false";
        start.Environment["MUGONZEI_MOCK_PAYMENTS"] = "true";
        start.Environment["AFDIAN_USER_ID"] = ""; start.Environment["AFDIAN_TOKEN"] = "";
        start.Environment["Translation__Mock"] = "true";
        start.Environment["Translation__BaseUrl"] = "";
        start.Environment["Translation__ApiKey"] = "";
        start.Environment["Translation__Model"] = "";
        start.Environment["MUGONZEI_PLANS_JSON"] = "[{\"id\":\"fixture\",\"name\":\"本机测试100积分\",\"credits\":100,\"amountFen\":100,\"afdianPlanId\":\"mock-plan\"}]";
        using var process = Process.Start(start) ?? throw new Exception("Could not start isolated server.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
            bool ready = false;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (process.HasExited) throw new Exception("Local server exited: " + await errors);
                try
                {
                    using var health = await http.GetAsync("health", deadline.Token);
                    if (health.IsSuccessStatusCode) { ready = true; break; }
                }
                catch (HttpRequestException) { }
                await Task.Delay(100, deadline.Token);
            }
            Check(ready, "Isolated server starts");
            var sessionStore = new CreditSessionStore(Path.Combine(fixture, "sessions"));
            using var client = new CreditServiceClient(address, sessionStore);
            const string syntheticEmail = "smoke@example.test", syntheticPassword = "Synthetic-only-password-123";
            await client.RequestRegistrationCodeAsync(syntheticEmail, deadline.Token);
            string emailCode = await File.ReadAllTextAsync(inbox, deadline.Token);
            await client.RegisterAsync("smoke_user", syntheticPassword, syntheticEmail, emailCode, deadline.Token);
            await client.LogoutAsync(deadline.Token);
            await client.LoginAsync("smoke_user", syntheticPassword, deadline.Token);
            Check((await client.GetAccountAsync(deadline.Token)).Balance == 0, "New account starts at zero");
            var plans = await client.GetPlansAsync(deadline.Token);
            Check(plans.Length == 1 && plans[0].Credits == 100, "Client reads the actual server plan schema");
            Guid orderRequest = Guid.NewGuid();
            var order = await client.CreateOrderAsync(plans[0].Id, orderRequest, deadline.Token);
            Check((await client.CreateOrderAsync(plans[0].Id, orderRequest, deadline.Token)).Id == order.Id,
                "Retry after an ambiguous order response returns the same order");
            var payment = CreditServiceClient.ValidatePaymentUrl(order.PayUrl!, address);
            string page = await http.GetStringAsync(payment, deadline.Token);
            Check((await client.GetAccountAsync(deadline.Token)).Balance == 0, "Opening the mock payment page never credits an account");
            var match = Regex.Match(page, "name=confirmation value=\"([^\"]+)\"");
            Check(match.Success, "Local checkout exposes its explicit confirmation form");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var confirmation = new FormUrlEncodedContent(new Dictionary<string, string>
                    { ["confirmation"] = WebUtility.HtmlDecode(match.Groups[1].Value) });
                using var paid = await http.PostAsync(payment, confirmation, deadline.Token);
                paid.EnsureSuccessStatusCode();
            }
            Check((await client.GetOrderAsync(order.Id, true, deadline.Token)).Status == "paid"
                && (await client.GetAccountAsync(deadline.Token)).Balance == 100, "Repeated mock confirmation credits once");
            Guid translationId = Guid.NewGuid();
            var translated = await client.ExecuteAsync(translationId, "text", "こんにちは / Hello / 你好", null, "简体中文", deadline.Token);
            Check(translated.Id == translationId && translated.Status == "succeeded"
                && translated.Translation!.Contains("开发模拟") && translated.Balance == 99,
                "Real desktop client and HTTP server agree on UUIDs, task completion and one-credit billing");
            await client.ExecuteAsync(translationId, "text", "こんにちは / Hello / 你好", null, "简体中文", deadline.Token);
            using var recovered = new CreditServiceClient(address, sessionStore);
            Check((await recovered.ResumeLatestAsync(deadline.Token)).Translation == translated.Translation
                && (await recovered.GetAccountAsync(deadline.Token)).Balance == 99,
                "Restarted client restores the same result without a second charge");
            Check((await recovered.CancelAsync(translationId, deadline.Token)).Status == "succeeded",
                "Cancelling completed work cannot refund a successful translation");
            Check((await recovered.GetLedgerAsync(deadline.Token)).Length == 2,
                "Exactly one recharge and one translation entry exist");
            Guid earlyCancel = Guid.NewGuid();
            Check((await recovered.CancelAsync(earlyCancel, deadline.Token)).Status == "cancelled",
                "Cancellation can arrive before the original upload finishes");
            Check((await recovered.ExecuteAsync(earlyCancel, "text", "late upload", null, "简体中文", deadline.Token)).Status == "cancelled"
                && (await recovered.GetAccountAsync(deadline.Token)).Balance == 99,
                "A delayed submit after cancellation cannot start or charge a new translation");
            await recovered.LogoutAsync(deadline.Token);
            Console.WriteLine("PASS real client ↔ local HTTP server: synthetic inbox verification, registration, password login, checkout, duplicate payment, translation, recovery, ledger and logout.");
        }
        catch
        {
            if (process.HasExited) Console.Error.WriteLine(await output + await errors);
            throw;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await output; await errors;
            if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
        }
    }

    public static void Run()
    {
        SettingsUpgrade();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { WizardAndCaptureGuards(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Credit service desktop integration failed", failure);
        Console.WriteLine("PASS credit settings upgrade, isolated wizard configuration, saved destination and recognition-only guard");
    }

    private static void SettingsUpgrade()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mugonzei-credit-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = new AppSettings
            {
                TranslationBackend = "Credits", CreditServerUrl = "https://credit-fixture.invalid/service/",
                ProcessingMode = "Handwriting", CodexModel = "preserved-model", CodexReasoningEffort = "low",
                ApiKey = "synthetic-credit-upgrade-fixture", ApiModel = "preserved-api"
            };
            SettingsStore.Save(original, directory);
            var loaded = SettingsStore.Load(directory);
            Check(loaded.TranslationBackend == "Credits" && loaded.CreditServerUrl == original.CreditServerUrl
                && loaded.ApiKey == original.ApiKey && loaded.CodexReasoningEffort == "low",
                "Saving the credit backend must preserve protected credentials and other provider preferences");
            string file = Path.Combine(directory, "settings.json");
            var document = JsonNode.Parse(File.ReadAllText(file))!;
            document["Settings"]!["TranslationBackend"] = "Codex";
            document["Settings"]!.AsObject().Remove("CreditServerUrl");
            File.WriteAllText(file, document.ToJsonString());
            loaded = SettingsStore.Load(directory);
            Check(loaded.TranslationBackend == "Codex" && loaded.ApiKey == original.ApiKey
                && loaded.CreditServerUrl == new AppSettings().CreditServerUrl,
                "Legacy version-1 settings without credit fields must load without resetting previous secrets");
            const string official = "https://api.mugonzei.top";
            const string loopback = "http://127.0.0.1:5187";
            Check(loaded.CreditServerUrl == official && new AppSettings().CreditServerUrl == official,
                "New installations and older settings without an account URL use the official account service");
            document.AsObject().Remove("CustomCreditServer");
            document["Settings"]!["CreditServerUrl"] = loopback;
            File.WriteAllText(file, document.ToJsonString());
            loaded = SettingsStore.Load(directory);
            Check(loaded.CreditServerUrl == official && loaded.ApiKey == original.ApiKey && loaded.TranslationBackend == "Codex",
                "An unused legacy loopback default migrates without disturbing the provider or its protected key");
            loaded.CreditServerUrl = loopback;
            SettingsStore.Save(loaded, directory);
            Check(SettingsStore.Load(directory).CreditServerUrl == loopback, "An explicitly saved self-hosted account server survives a restart");
            File.WriteAllText(file, document.ToJsonString());
            var sessions = new CreditSessionStore(Path.Combine(directory, "CreditSessions"));
            sessions.Save(loopback, "fixture-local-only-token", "fixture-user", DateTimeOffset.UtcNow.AddDays(-1));
            Check(SettingsStore.Load(directory).CreditServerUrl == loopback && sessions.Load(official) is null,
                "Prior local sign-in, even expired, preserves its destination and never transfers that session to the official host");
            document["Settings"]!["CreditServerUrl"] = "http://127.0.0.1:8188/dev";
            File.WriteAllText(file, document.ToJsonString());
            Check(SettingsStore.Load(directory).CreditServerUrl == "http://127.0.0.1:8188/dev",
                "Other self-hosted servers are preserved even without any session");
            foreach (string endpoint in new[] { "http://remote.invalid", "https://user:password@fixture.invalid", "https://fixture.invalid/?token=x" })
            {
                loaded.TranslationBackend = "Credits"; loaded.CreditServerUrl = endpoint;
                Reject(() => SettingsValidation.ValidateForSave(loaded), "Unsafe credit server destination must be rejected before save");
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void WizardAndCaptureGuards()
    {
        var initial = new AppSettings { TranslationBackend = "Credits", ProcessingMode = "Handwriting",
            CreditServerUrl = "https://credit-fixture.invalid/", ApiKey = "synthetic-kept-key" };
        AppSettings? saved = null;
        using (var wizard = new SetupWizardForm(initial, draft => saved = draft, firstRun: false,
                   authenticate: SetupWizardTests.FixtureLogin, startAtLogin: true))
        {
            wizard.StartPosition = FormStartPosition.Manual; wizard.Location = new Point(-20000, -20000); wizard.ShowInTaskbar = false;
            wizard.Show(); Application.DoEvents();
            Check(wizard.CurrentStep == 1 && !wizard.TryAdvance() && wizard.ReadSettingsDraft().CreditServerUrl == initial.CreditServerUrl,
                "Existing logged-out credit settings open directly on sign-in for their original service");
            SetupWizardTests.SubmitFixtureLogin(wizard);
            Check(wizard.TryFinish() && saved is { TranslationBackend: "Credits", ProcessingMode: "Handwriting" }
                && saved.ApiKey == initial.ApiKey && saved.CreditServerUrl == initial.CreditServerUrl,
                "A successful login and mode choice preserve credentials and billing destination");
        }
        using var form = new MainForm(true);
        try
        {
            Field("settings").SetValue(form, initial.Copy());
            Method("LoadSettings").Invoke(form, null);
            Check(!form.HasPendingAiSettings, "Loaded credit settings are applied");
            var server = (TextBox)Field("creditServer").GetValue(form)!;
            server.Text = "https://another-fixture.invalid/";
            Check(form.HasPendingAiSettings, "Changing the billing destination must block translation until saved");
            server.Text = initial.CreditServerUrl;
            Check(form.ReadSettingsDraft().CreditServerUrl == initial.CreditServerUrl, "Main settings editor retains the selected billing destination");
            var task = (Task)Method("CaptureAndTranslate").Invoke(form, [true, "isolated test", 0L, null])!;
            task.GetAwaiter().GetResult();
            Check((long)Field("captureNumber").GetValue(form)! == 0
                && ((Label)Field("status").GetValue(form)!).Text.Contains("仅识别"),
                "AI-only recognition in credit mode cannot start a screenshot or an unbilled model request");
        }
        finally { Method("DisposeServices").Invoke(form, null); }
    }

    private static FieldInfo Field(string name) => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static IEnumerable<Control> Find(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Find(child)) yield return descendant;
        }
    }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return; }
        throw new Exception(message);
    }
    private static void Check(bool success, string message) { if (!success) throw new Exception(message); }
}
