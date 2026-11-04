using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamVrTranslator;

/// <summary>Optional, unofficial Google Translate text endpoint. No API credentials are used.</summary>
public sealed class GoogleTranslateClient : IDisposable
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";
    private const int MaxResponseBytes = 1_048_576;
    internal const int MaxChunkCharacters = 1000;
    internal const int MaxRequests = 64;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private const string Blocked = "谷歌暂时拒绝自动翻译请求，请稍后重试或切换翻译服务。";
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["简体中文"] = "zh-CN", ["中文"] = "zh-CN", ["中文简体"] = "zh-CN", ["中文（简体）"] = "zh-CN",
        ["繁体中文"] = "zh-TW", ["中文繁体"] = "zh-TW", ["中文（繁体）"] = "zh-TW",
        ["日语"] = "ja", ["日文"] = "ja", ["英语"] = "en", ["英文"] = "en", ["韩语"] = "ko", ["韩文"] = "ko",
        ["English"] = "en", ["Japanese"] = "ja", ["Korean"] = "ko",
        ["Simplified Chinese"] = "zh-CN", ["Traditional Chinese"] = "zh-TW",
        ["zh"] = "zh-CN", ["zh-cn"] = "zh-CN", ["zh-tw"] = "zh-TW"
    };

    public GoogleTranslateClient(HttpClient? httpClient = null)
    {
        ownsClient = httpClient is null;
        client = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static string NormalizeTargetLanguage(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "zh-CN";
        value = value.Trim();
        if (Languages.TryGetValue(value, out string? language)) return language;
        // Preserve well-formed codes without guessing availability from a stale local language list.
        if (value.Length <= 35 && !value.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(value, "^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,2}$", RegexOptions.CultureInvariant))
            return value;
        throw new InvalidOperationException("谷歌翻译目标语言无效，请选择简体中文、繁体中文、日语、英语、韩语，或填写语言代码（例如 fr）。");
    }

    public async Task<string> TranslateAsync(string text, AppSettings settings, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(settings);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length > TranslationClient.MaxInputCharacters)
            throw new InvalidOperationException("识别文字超过 16,000 个字符，请缩小截取范围；本次未发送到谷歌。");
        string language = NormalizeTargetLanguage(settings.TargetLanguage);
        var parts = Split(text);
        if (parts.Count(part => part.Translate) > MaxRequests)
            throw new InvalidOperationException("识别文字需要超过 64 次分块翻译，请缩小截取范围或减少短行；本次未发送到谷歌。");
        // An injected client must also be dedicated to this public, credential-free endpoint.
        if (client.DefaultRequestHeaders.Authorization is not null
            || new[] { "X-Api-Key", "Api-Key", "X-Goog-Api-Key" }.Any(client.DefaultRequestHeaders.Contains))
            throw new InvalidOperationException("谷歌免密钥翻译不能使用带有身份凭据的连接，请使用独立连接。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var output = new StringBuilder();
        try
        {
            foreach (var part in parts)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string value = part.Translate
                    ? await TranslateChunkAsync(part.Text, language, deadline.Token).ConfigureAwait(false) : part.Text;
                if (output.Length + value.Length > TranslationClient.MaxOutputCharacters)
                    throw new InvalidOperationException("谷歌返回内容超过 32,000 个字符，无法显示；请缩小截取范围。");
                output.Append(value);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return output.ToString();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("谷歌翻译等待超过 60 秒或连接超时，请稍后重试或切换服务。"); }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("无法连接谷歌翻译或响应读取中断，请检查网络或切换服务。");
        }
    }

    private async Task<string> TranslateChunkAsync(string text, string language, CancellationToken token)
    {
        string query = "?client=gtx&sl=auto&tl=" + Uri.EscapeDataString(language) + "&dt=t&q=" + Uri.EscapeDataString(text);
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + query);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException(Blocked);
        if (status is >= 300 and <= 399)
            throw new InvalidOperationException("谷歌翻译返回了重定向，程序未跟随跳转；请稍后重试或切换服务。");
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidOperationException("谷歌翻译响应过大，请缩小截取范围或稍后重试。");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + count > MaxResponseBytes)
                throw new InvalidOperationException("谷歌翻译响应过大，请缩小截取范围或稍后重试。");
            memory.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        byte[] body = memory.ToArray();
        if (!response.IsSuccessStatusCode)
        {
            string errorText = Encoding.UTF8.GetString(body);
            if ((response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
                    || errorText.AsSpan().TrimStart().StartsWith("<"))
                && (errorText.Contains("automated queries", StringComparison.OrdinalIgnoreCase)
                    || errorText.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(Blocked);
            throw new InvalidOperationException($"谷歌翻译请求失败（HTTP {status}），请检查目标语言或稍后重试。");
        }
        return ParseResponse(body, response.Content.Headers.ContentType?.MediaType);
    }

    private static string ParseResponse(byte[] body, string? mediaType)
    {
        try
        {
            string payload = new UTF8Encoding(false, true).GetString(body).TrimStart('\uFEFF');
            bool looksLikeJson = payload.AsSpan().TrimStart().StartsWith("[");
            if (mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
                || payload.AsSpan().TrimStart().StartsWith("<")
                || (!looksLikeJson && (payload.Contains("automated queries", StringComparison.OrdinalIgnoreCase)
                    || payload.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException(Blocked);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0
                || root[0].ValueKind != JsonValueKind.Array || root[0].GetArrayLength() == 0) throw new JsonException();
            var translated = new StringBuilder();
            foreach (var segment in root[0].EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Array || segment.GetArrayLength() == 0
                    || segment[0].ValueKind != JsonValueKind.String) throw new JsonException();
                string value = segment[0].GetString()!;
                if (translated.Length + value.Length > TranslationClient.MaxOutputCharacters)
                    throw new InvalidOperationException("谷歌返回内容超过 32,000 个字符，无法显示；请缩小截取范围。");
                translated.Append(value);
            }
            if (translated.Length == 0) throw new InvalidOperationException("谷歌未返回译文，请稍后重试或切换服务。");
            return translated.ToString();
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new InvalidOperationException("谷歌翻译响应格式无效，非官方接口可能暂时不可用；请稍后重试或切换服务。"); }
    }

    private static List<Part> Split(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) throw InvalidUnicode();
            }
            else if (char.IsLowSurrogate(text[index])) throw InvalidUnicode();
        }
        var parts = new List<Part>();
        int start = 0;
        while (start < text.Length)
        {
            if (char.IsWhiteSpace(text[start]))
            {
                int end = start + 1;
                while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
                parts.Add(new(text[start..end], false)); start = end; continue;
            }
            int lineEnd = start;
            while (lineEnd < text.Length && text[lineEnd] is not ('\r' or '\n')) lineEnd++;
            int stop = Math.Min(start + MaxChunkCharacters, lineEnd);
            if (stop < lineEnd)
            {
                if (char.IsHighSurrogate(text[stop - 1]) && char.IsLowSurrogate(text[stop])) stop--;
                int boundary = -1;
                for (int i = stop - 1; i >= start + MaxChunkCharacters / 2; i--)
                    if ("。！？.!?；;".Contains(text[i])) { boundary = i + 1; break; }
                if (boundary < 0)
                    for (int i = stop - 1; i >= start + MaxChunkCharacters / 2; i--)
                        if (char.IsWhiteSpace(text[i])) { boundary = i + 1; break; }
                if (boundary > start) stop = boundary;
            }
            int coreEnd = stop;
            while (coreEnd > start && char.IsWhiteSpace(text[coreEnd - 1])) coreEnd--;
            parts.Add(new(text[start..coreEnd], true));
            if (coreEnd < stop) parts.Add(new(text[coreEnd..stop], false));
            start = stop;
        }
        return parts;
    }

    private static InvalidOperationException InvalidUnicode() => new("识别文字包含无效 Unicode 字符，请重新识别；本次未发送到谷歌。");
    private readonly record struct Part(string Text, bool Translate);
    public void Dispose() { if (ownsClient) client.Dispose(); }
}
