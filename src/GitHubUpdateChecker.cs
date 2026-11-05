using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SteamVrTranslator;

internal sealed record GitHubUpdateResult(bool UpdateAvailable, string CurrentVersion, string LatestVersion,
    Uri ReleaseUrl, Uri? InstallerUrl);

internal static class GitHubUpdateChecker
{
    private const string Repository = "pan0001/mugonzei-honyakuki";
    private const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
    private const int PageSize = 100;
    private const int MaxPages = 5;
    private const int MaxPageBytes = 4 * 1024 * 1024;
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static string CurrentVersion { get; } = typeof(GitHubUpdateChecker).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+', 2)[0] ?? "";

    public static Task<GitHubUpdateResult> CheckAsync(CancellationToken cancellationToken) =>
        CheckAsync(Client, CurrentVersion, TimeSpan.FromSeconds(12), cancellationToken);

    internal static async Task<GitHubUpdateResult> CheckAsync(HttpClient client, string installedVersion,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string current = installedVersion.Split('+', 2)[0];
        var installed = ReleaseVersion.Parse(current)
            ?? throw new InvalidOperationException("无法读取客户端版本号。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        ReleaseVersion? newest = null;
        GitHubUpdateResult? result = null;
        try
        {
            for (int page = 1; page <= MaxPages; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{Repository}/releases?per_page={PageSize}&page={page}");
                request.Headers.UserAgent.ParseAdd("Mugonzei-Honyakuki-UpdateChecker/1.0");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                    throw new InvalidOperationException("GitHub 暂时限制了访问，请稍后再检查更新。");
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException($"无法读取 GitHub 发布列表（HTTP {(int)response.StatusCode}）。");
                await response.Content.LoadIntoBufferAsync(MaxPageBytes, token).ConfigureAwait(false);
                using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(stream);
                var releases = document.RootElement;
                if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() > PageSize)
                    throw new InvalidOperationException("GitHub 发布列表格式异常。");
                foreach (var release in releases.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    string? tag = StringField(release, "tag_name");
                    if (tag is not { Length: > 1 } || tag[0] != 'v' ||
                        !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False) continue;
                    var version = ReleaseVersion.Parse(tag[1..]);
                    string pageUrl = $"{ReleasesUrl}/tag/{tag}";
                    if (version is null || StringField(release, "html_url") != pageUrl ||
                        (newest is not null && version.CompareTo(newest) <= 0)) continue;
                    newest = version;
                    bool newer = version.CompareTo(installed) > 0;
                    result = new(newer, current, tag[1..], new Uri(pageUrl), newer ? FindInstaller(release, tag) : null);
                }
                token.ThrowIfCancellationRequested();
                if (releases.GetArrayLength() < PageSize)
                    return result ?? throw new InvalidOperationException("GitHub 暂无可识别的公开版本。");
            }
            throw new InvalidOperationException("GitHub 发布列表过长，未能完成版本比较。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("检查更新超时，请稍后重试。");
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            throw new InvalidOperationException("GitHub 发布列表超过大小限制。", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            throw new InvalidOperationException("无法读取 GitHub 发布列表，请检查网络或稍后重试。", ex);
        }
    }

    private static string? StringField(JsonElement item, string field) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri? FindInstaller(JsonElement release, string tag)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        string name = $"Mugonzei-Honyakuki-Setup-win-x64-{tag}.exe";
        string url = $"{ReleasesUrl}/download/{tag}/{name}";
        foreach (var asset in assets.EnumerateArray())
            if (StringField(asset, "name") == name && StringField(asset, "state") == "uploaded" &&
                StringField(asset, "browser_download_url") == url) return new Uri(url);
        return null;
    }

    private sealed record ReleaseVersion(Version Core, string[] Labels)
    {
        public static ReleaseVersion? Parse(string text)
        {
            if (text.Length is 0 or > 128) return null;
            string[] parts = text.Split('-', 2);
            if (!Version.TryParse(parts[0], out var core) || core.Build < 0 || core.Revision >= 0 ||
                core.ToString() != parts[0]) return null;
            string[] labels = parts.Length == 2 ? parts[1].Split('.') : [];
            foreach (string label in labels)
                if (label.Length == 0 || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                    (label.Length > 1 && label[0] == '0' && label.All(char.IsAsciiDigit))) return null;
            return new(core, labels);
        }

        public int CompareTo(ReleaseVersion other)
        {
            int core = Core.CompareTo(other.Core);
            if (core != 0) return core;
            if (Labels.Length == 0 || other.Labels.Length == 0)
                return (Labels.Length == 0).CompareTo(other.Labels.Length == 0);
            for (int i = 0; i < Math.Min(Labels.Length, other.Labels.Length); i++)
            {
                string left = Labels[i], right = other.Labels[i];
                if (left == right) continue;
                bool leftNumeric = left.All(char.IsAsciiDigit), rightNumeric = right.All(char.IsAsciiDigit);
                if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
                if (leftNumeric && left.Length != right.Length) return left.Length.CompareTo(right.Length);
                return string.CompareOrdinal(left, right);
            }
            return Labels.Length.CompareTo(other.Labels.Length);
        }
    }
}
