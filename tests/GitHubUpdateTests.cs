using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SteamVrTranslator;

internal static class GitHubUpdateTests
{
    private const string ReleasesUrl = "https://github.com/pan0001/mugonzei-honyakuki/releases";
    private const string ApiUrl = "https://api.github.com/repos/pan0001/mugonzei-honyakuki/releases?per_page=100&page=";

    public static async Task RunAsync()
    {
        var assemblyVersion = typeof(GitHubUpdateChecker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Check(GitHubUpdateChecker.CurrentVersion == assemblyVersion.Split('+', 2)[0], "Reads product version from desktop assembly");
        var set = new[] {
            Release("v0.2.4-preview"), Release("v1.0.0", draft: true), Release("v0.2.10-preview.2"),
            Release("v0.2.10-preview.10"), Release("v0.2.10-preview.03"), Release("v0.3.0", page: "https://example.org/new")
        };
        var preview = await FromJson(set, "0.2.5-preview+git.commit");
        Check(preview.UpdateAvailable && preview.CurrentVersion == "0.2.5-preview" && preview.LatestVersion == "0.2.10-preview.10",
            "Finds highest published preview, compares numeric components and ignores build metadata");
        Check(preview.ReleaseUrl.AbsoluteUri == ReleasesUrl + "/tag/v0.2.10-preview.10" &&
            preview.InstallerUrl?.AbsoluteUri == Download("v0.2.10-preview.10"), "Returns matching release and installer URLs");
        Check((await FromJson([Release("v1.0.0-preview"), Release("v1.0.0")], "1.0.0-preview")).LatestVersion == "1.0.0",
            "Stable wins at the same core version");
        foreach (string current in new[] { "0.2.5-preview+abc", "0.2.5", "0.3.0-preview" })
        {
            var outcome = await FromJson([Release("v0.2.5-preview")], current);
            Check(!outcome.UpdateAvailable && outcome.InstallerUrl is null, "Never suggests a reinstall or downgrade");
        }
        const string tag = "v1.2.3-preview";
        foreach (var asset in new[]
        {
            Asset(tag, url: Download(tag).Replace("github.com", "github.com.example.org")),
            Asset(tag, url: Download(tag) + "?redirect=other"), Asset(tag, url: Download("v1.0.0")),
            Asset(tag, name: "Mugonzei.exe"), Asset(tag, state: "starter")
        })
        {
            var outcome = await FromJson([Release(tag, assets: [asset])], "1.0.0");
            Check(outcome.UpdateAvailable && outcome.InstallerUrl is null, "Unverified asset leaves only the release page");
        }
        await ExpectFailure(() => FromJson([Release("v1.0.0", page: ReleasesUrl.Replace("pan0001", "other") + "/tag/v1.0.0"),
            Release("v1.0.0/../../malicious"), Release("v1.0.0#fragment"), Release("v1.0.0", draft: true)], "0.1.0"), "公开版本");

        int pages = 0;
        using (var client = Mock((request, _) =>
        {
            pages++;
            Check(request.RequestUri!.AbsoluteUri == ApiUrl + pages && request.Method == HttpMethod.Get, "Only requests this repository");
            Check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie") &&
                request.Headers.UserAgent.Count > 0 && request.Headers.Accept.Any(a => a.MediaType == "application/vnd.github+json"),
                "Anonymous GitHub request uses required headers");
            return Task.FromResult(Json(pages == 1 ? Enumerable.Repeat(Release("v1.0.0"), 100).ToArray() : [Release("v2.0.0")]));
        }))
        {
            var result = await GitHubUpdateChecker.CheckAsync(client, "1.0.0", TimeSpan.FromSeconds(2), default);
            Check(result.LatestVersion == "2.0.0" && pages == 2, "Compares all pages before deciding");
        }
        foreach (HttpStatusCode code in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            using var client = Mock((_, _) => Task.FromResult(new HttpResponseMessage(code)));
            await ExpectFailure(() => GitHubUpdateChecker.CheckAsync(client, "1.0.0", TimeSpan.FromSeconds(2), default), "限制");
        }
        using (var client = Mock((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect))))
            await ExpectFailure(() => GitHubUpdateChecker.CheckAsync(client, "1.0.0", TimeSpan.FromSeconds(2), default), "302");
        using (var client = Mock((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<html>proxy error</html>") })))
            await ExpectFailure(() => GitHubUpdateChecker.CheckAsync(client, "1.0.0", TimeSpan.FromSeconds(2), default), "读取");
        using (var client = Mock((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new OversizedContent() })))
            await ExpectFailure(() => GitHubUpdateChecker.CheckAsync(client, "1.0.0", TimeSpan.FromSeconds(2), default), "大小限制");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slow = Mock(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json([]);
        });
        using var cancel = new CancellationTokenSource();
        var pending = GitHubUpdateChecker.CheckAsync(slow, "1.0.0", TimeSpan.FromSeconds(3), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        try { await pending; throw new Exception("Caller cancellation was swallowed"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        await ExpectFailure(() => GitHubUpdateChecker.CheckAsync(slow, "1.0.0", TimeSpan.FromMilliseconds(50), default), "超时");
        Console.WriteLine("PASS GitHub preview updates, version precedence, trusted URLs, pagination, limits, timeouts and cancellation");
    }

    private static object Release(string tag, string? page = null, bool draft = false, object[]? assets = null) =>
        new { tag_name = tag, html_url = page ?? ReleasesUrl + "/tag/" + tag, draft, prerelease = tag.Contains('-'), assets = assets ?? [Asset(tag)] };

    private static string Filename(string tag) => $"Mugonzei-Honyakuki-Setup-win-x64-{tag}.exe";
    private static string Download(string tag) => $"{ReleasesUrl}/download/{tag}/{Filename(tag)}";
    private static object Asset(string tag, string? name = null, string? url = null, string state = "uploaded") =>
        new { name = name ?? Filename(tag), state, browser_download_url = url ?? Download(tag) };
    private static HttpResponseMessage Json(object[] releases) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(releases), Encoding.UTF8, "application/json") };
    private static async Task<GitHubUpdateResult> FromJson(object[] releases, string current)
    {
        using var client = Mock((_, _) => Task.FromResult(Json(releases)));
        return await GitHubUpdateChecker.CheckAsync(client, current, TimeSpan.FromSeconds(2), default);
    }

    private static HttpClient Mock(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => new(new Handler(respond));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private sealed class OversizedContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[64 * 1024];
            for (int i = 0; i < 65; i++) await stream.WriteAsync(chunk);
        }
    }

    private static async Task ExpectFailure(Func<Task> operation, string expected)
    {
        try { await operation(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(expected, StringComparison.Ordinal)) { return; }
        throw new Exception("Expected failure containing " + expected);
    }
    private static void Check(bool passed, string message)
    {
        if (!passed) throw new Exception("GitHubUpdateTests: " + message);
    }
}
