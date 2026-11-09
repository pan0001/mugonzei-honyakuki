using System.Text.Json;
using SteamVrTranslator;

internal static class WebSetupFlowTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static async Task Run()
    {
        await SignInOnlyAdvancesOnSuccess();
        await ProviderDraftKeepsCredentialsAtTheirOrigin();
        await SearchAiRequiresImageAndKeepsOtherCredentials();
        await StandaloneRegistrationAndLogin();
        Console.WriteLine("PASS embedded setup account gating, cancel/retry, automatic routing and origin-bound keys");
    }

    private static async Task StandaloneRegistrationAndLogin()
    {
        const string custom = "https://fixture.example/custom/";
        var previous = new AppSettings { TranslationBackend = "Codex", CreditServerUrl = custom, ApiBaseUrl = "https://private-model.invalid/v1" };
        var events = new List<JsonElement>();
        var directory = Path.Combine(Path.GetTempPath(), "mugonzei-web-registration-qa-" + Guid.NewGuid().ToString("N"));
        DialogResult result = DialogResult.None;
        var paths = new List<string>();
        using var flow = new WebSetupFlow(previous, _ => throw new Exception("Standalone login tried to save provider settings"),
            item => events.Add(JsonSerializer.SerializeToElement(item, Web)), r => result = r, true, false,
            loginOnly:true, registrationService: uri => new CreditServiceClient(uri, new CreditSessionStore(directory), new RegistrationReplies(paths), TimeSpan.FromSeconds(1)));
        Task Send(object message) => flow.ReceiveAsync(JsonSerializer.SerializeToElement(message));
        try
        {
            await Send(new { type = "ready" });
            Assert(events[0].GetProperty("loginOnly").GetBoolean() && events[0].GetProperty("loginOrigin").GetString() == custom);
            await Send(new { type = "service", backend = "GoogleTranslate" });
            await Send(new { type = "register" });
            Assert(events.Last().GetProperty("registering").GetBoolean() && paths.Count == 0);
            await Send(new { type = "back" });
            Assert(!events.Last().GetProperty("registering").GetBoolean() && events.Last().GetProperty("step").GetInt32() == 1);
            await Send(new { type = "register" });
            await Send(new { type = "sendCode", email = "new@example.com" });
            await Send(new { type = "sendCode", email = "new@example.com" });
            Assert(paths.SequenceEqual([custom + "v1/auth/register/email-code"]));
            await Send(new { type = "signup", username = "fixture-user", password = "fixture-password", email = "other@example.com", code = "123456" });
            Assert(result == DialogResult.None && paths.Count == 1);
            await Send(new { type = "signup", username = "fixture-user", password = "fixture-password", email = "new@example.com", code = "123456" });
            Assert(result == DialogResult.OK && paths.Last() == custom + "v1/auth/register");
            Assert(previous.TranslationBackend == "Codex" && !JsonSerializer.Serialize(events).Contains("private-model.invalid"));
            Assert(new CreditSessionStore(directory).Load(custom)?.Username == "fixture-user");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        using var cancel = new WebSetupFlow(previous, _ => throw new Exception(), _ => {}, r => result = r, false, false, loginOnly:true);
        await cancel.ReceiveAsync(JsonSerializer.SerializeToElement(new { type = "back" }));
        Assert(result == DialogResult.Cancel);
    }

    private sealed class RegistrationReplies(List<string> paths) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string uri = request.RequestUri!.AbsoluteUri;
            paths.Add(uri);
            Assert(request.Method == HttpMethod.Post && request.Headers.Authorization == null);
            var reply = uri.EndsWith("/email-code", StringComparison.Ordinal)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NoContent)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                    { username = "fixture-user", token = "synthetic-session", expiresAt = DateTimeOffset.UtcNow.AddMinutes(5) })) };
            return Task.FromResult(reply);
        }
    }

    private static async Task SignInOnlyAdvancesOnSuccess()
    {
        var events = new List<JsonElement>();
        var logins = new Queue<TaskCompletionSource<CreditSession>>();
        CancellationToken loginToken = default;
        AppSettings? saved = null;
        DialogResult closed = DialogResult.None;
        using var flow = new WebSetupFlow(new AppSettings(), settings => saved = settings,
            item => events.Add(JsonSerializer.SerializeToElement(item, Web)), result => closed = result,
            true, false, (url, user, password, token) =>
            {
                Assert(url == "https://api.mugonzei.top/" && user == "test-user" && password == "example-password");
                loginToken = token;
                var response = new TaskCompletionSource<CreditSession>();
                logins.Enqueue(response);
                return response.Task;
            });
        Task Send(object message) => flow.ReceiveAsync(JsonSerializer.SerializeToElement(message));
        await Send(new { type = "ready" });
        Assert(events.First().GetProperty("runtimeMode").GetString() == "Auto");
        var finish = new { type = "finish", runtimeMode = "Auto", hotkey = "Ctrl+Alt+T", captureMode = "VR 左眼", controller = true };
        await Send(finish);
        await Send(new { type = "service", backend = "Credits" });
        await Send(finish);
        Assert(saved == null && events.Last().GetProperty("step").GetInt32() == 1);
        var login = new { type = "login", username = "TEST-USER", password = "example-password", server = "https://untrusted.invalid" };
        var pending = Send(login);
        await Send(login);
        Assert(logins.Count == 1 && !loginToken.IsCancellationRequested);
        await Send(new { type = "back" });
        Assert(loginToken.IsCancellationRequested);
        logins.Dequeue().SetResult(new CreditSession("fixture-token", "test-user", DateTimeOffset.UtcNow.AddMinutes(2), null));
        await pending;
        Assert(events.Last().GetProperty("step").GetInt32() == 0);
        await Send(new { type = "service", backend = "Credits" });
        pending = Send(login);
        logins.Dequeue().SetException(new HttpRequestException("fixture private details"));
        await pending;
        Assert(events.Last().GetProperty("error").GetString()!.Length > 0);
        pending = Send(login);
        logins.Dequeue().SetResult(new CreditSession("fixture-token", "test-user", DateTimeOffset.UtcNow.AddMinutes(2), null));
        await pending;
        Assert(events.Last().GetProperty("step").GetInt32() == 2);
        await Send(finish);
        Assert(saved?.RuntimeMode == "Auto" && closed == DialogResult.OK);
        string messages = JsonSerializer.Serialize(events);
        Assert(!messages.Contains("example-password") && !messages.Contains("fixture-token") && !messages.Contains("fixture private details"));
    }

    private static async Task ProviderDraftKeepsCredentialsAtTheirOrigin()
    {
        var previous = new AppSettings { TranslationBackend = "OpenAI", ApiBaseUrl = "https://ai.example/v1",
            ApiKey = "saved-fixture-key", ApiModel = "fixture-model", ApiStream = true, TargetLanguage = "日语" };
        var events = new List<JsonElement>();
        AppSettings? saved = null;
        using var flow = new WebSetupFlow(previous, value => saved = value, item => events.Add(JsonSerializer.SerializeToElement(item, Web)), _ => {}, false, false);
        Task Send(object message) => flow.ReceiveAsync(JsonSerializer.SerializeToElement(message));
        object Api(string url, string key) => new { type = "configure", processingMode = "Image", targetLanguage = "日语",
            apiBaseUrl = url, apiModel = "fixture-model", apiKey = key, clearKey = false, apiImageDetail = "Default", apiStream = true };
        await Send(new { type = "ready" });
        Assert(events[0].GetProperty("hasApiKey").GetBoolean());
        await Send(new { type = "service", backend = "OpenAI" });
        await Send(Api("https://other.example", ""));
        Assert(events.Last().GetProperty("step").GetInt32() == 1 && events.Last().GetProperty("error").GetString()!.Length > 0);
        await Send(Api("https://ai.example/v1", ""));
        Assert(events.Last().GetProperty("step").GetInt32() == 2);
        await Send(new { type = "back" });
        await Send(Api("https://other.example", "new-fixture-key"));
        await Send(new { type = "finish", runtimeMode = "Desktop", hotkey = "Ctrl+Alt+F9" });
        Assert(saved is { RuntimeMode: "Desktop", DesktopMode: true, ApiKey: "new-fixture-key", Hotkey: "Ctrl+Alt+F9" } && previous.ApiKey == "saved-fixture-key");
        var messages = JsonSerializer.Serialize(events);
        Assert(!messages.Contains("new-fixture-key") && !messages.Contains("saved-fixture-key"));
    }

    private static void Assert(bool result)
    {
        if (!result) throw new InvalidOperationException("Embedded setup state assertion failed.");
    }

    private static async Task SearchAiRequiresImageAndKeepsOtherCredentials()
    {
        var previous = new AppSettings { TranslationBackend = "GoogleTranslate", ProcessingMode = "LocalJapanese", ApiKey = "unused-fixture" };
        var events = new List<JsonElement>();
        AppSettings? saved = null;
        using var flow = new WebSetupFlow(previous, s => saved = s, m => events.Add(JsonSerializer.SerializeToElement(m, Web)), _ => {}, false, false);
        Task Send(object message) => flow.ReceiveAsync(JsonSerializer.SerializeToElement(message));
        await Send(new { type = "ready" });
        await Send(new { type = "service", backend = "GoogleSearchAI" });
        Assert(events[^1].GetProperty("processingMode").GetString() == "Image" && events[^1].GetProperty("step").GetInt32() == 1);
        await Send(new { type = "configure", processingMode = "Ocr", targetLanguage = "日本語" });
        Assert(events[^1].GetProperty("error").GetString()!.Length > 0 && saved == null);
        await Send(new { type = "configure", processingMode = "Image", targetLanguage = "日本語" });
        await Send(new { type = "finish", runtimeMode = "Desktop", hotkey = "Ctrl+Alt+F9" });
        Assert(saved is { TranslationBackend: "GoogleSearchAI", ProcessingMode: "Image", TargetLanguage: "日本語", ApiKey: "unused-fixture" });
        Assert(previous.TranslationBackend == "GoogleTranslate" && !JsonSerializer.Serialize(events).Contains("unused-fixture"));
    }
}
