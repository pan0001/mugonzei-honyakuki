using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SteamVrTranslator;

public static class GoogleTranslateTests
{
    public static async Task RunAsync()
    {
        Languages();
        await FixedDestinationAndSegmentsAsync();
        await ChunkBoundariesAsync();
        await PreflightAsync();
        await ErrorsAsync();
        await CancellationAsync();
        Console.WriteLine("PASS Google text-only fixed endpoint, credential isolation, UTF-16 splitting, formatting, bounds and cancellation");
    }

    private static AppSettings Settings() => new()
    {
        TargetLanguage = "简体中文", ApiBaseUrl = "http://untrusted.example/steal",
        ApiKey = "private-ai-key-must-not-be-sent", ApiModel = ""
    };
    private static HttpResponseMessage Response(string body, HttpStatusCode code = HttpStatusCode.OK, string type = "application/json")
        => new(code) { Content = new StringContent(body, Encoding.UTF8, type) };
    private static string Success(params string[] segments) => JsonSerializer.Serialize(new object?[]
    {
        segments.Select(text => new object?[] { text, "source", null, null }).ToArray(), null, "auto"
    });
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(value => value.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    private static void Languages()
    {
        foreach (var sample in new[] { ("简体中文", "zh-CN"), ("繁体中文", "zh-TW"), ("日语", "ja"), ("英语", "en"),
            ("韩语", "ko"), ("zh-cn", "zh-CN"), ("fr", "fr"), ("yue", "yue"), ("mni-Mtei", "mni-Mtei"), ("", "zh-CN") })
            Check(GoogleTranslateClient.NormalizeTargetLanguage(sample.Item1) == sample.Item2, "Language mapping: " + sample.Item1);
        foreach (string invalid in new[] { "随意风格", "auto", "en&sl=xx", "english with slang", new string('x', 100) })
        {
            bool failed = false;
            try { GoogleTranslateClient.NormalizeTargetLanguage(invalid); } catch (InvalidOperationException) { failed = true; }
            Check(failed, "Unsupported prose/parameter injection rejected without a network request");
        }
    }

    private static async Task FixedDestinationAndSegmentsAsync()
    {
        const string source = "你好😀 &x=1?# +/%";
        int calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Check(request.Method == HttpMethod.Get && request.Content is null, "Fixed GET protocol");
            Check(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "translate.googleapis.com"
                && request.RequestUri.AbsolutePath == "/translate_a/single", "Only fixed Google destination is used");
            var query = Query(request.RequestUri);
            Check(query.Count == 5 && query["client"] == "gtx" && query["sl"] == "auto" && query["tl"] == "zh-CN"
                && query["dt"] == "t" && query["q"] == source, "Unicode, emoji and reserved characters are encoded without adding parameters");
            Check(request.Headers.Authorization is null && !request.Headers.Contains("X-Api-Key")
                && !request.RequestUri.AbsoluteUri.Contains("private-ai-key"), "AI key and model are never used");
            return Task.FromResult(Response(Success("你好，", "世界！", "\n第二段。")));
        }));
        using var client = new GoogleTranslateClient(http);
        Check(await client.TranslateAsync(source, Settings(), default) == "你好，世界！\n第二段。", "All returned segments are concatenated exactly");
        Check(calls == 1, "No extra model discovery, retries or alternate hosts");

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "must-not-leak");
        await ThrowsAsync<InvalidOperationException>(() => client.TranslateAsync("text", Settings(), default), "身份凭据");
        Check(calls == 1, "Injected authenticated clients are refused before sending credentials");
    }

    private static async Task ChunkBoundariesAsync()
    {
        string source = "\t  " + new string('日', 999) + "😀" + new string('a', 550) + "。  "
            + new string('b', 800) + "\r\n\n\tこんにちは  \r再见\t ";
        int calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            string chunk = Query(request.RequestUri!)["q"];
            Check(chunk.Length is > 0 and <= GoogleTranslateClient.MaxChunkCharacters, "Each source chunk is bounded");
            Check(!char.IsLowSurrogate(chunk[0]) && !char.IsHighSurrogate(chunk[^1]), "Chunks never divide an emoji surrogate pair");
            Check(!chunk.Contains('\r') && !chunk.Contains('\n'), "Line endings are preserved locally instead of normalized by the endpoint");
            return Task.FromResult(Response(Success(chunk)));
        }));
        using var client = new GoogleTranslateClient(http);
        Check(await client.TranslateAsync(source, Settings(), default) == source, "Chunk joining preserves spaces, tabs, blank lines and CR/LF exactly");
        Check(calls > 2 && calls < 10, "Long text splits into a small bounded sequence");
        int previous = calls;
        Check(await client.TranslateAsync(" \t\r\n ", Settings(), default) == " \t\r\n ", "Whitespace-only source is not discarded");
        Check(calls == previous, "Whitespace-only source makes no request");
    }

    private static async Task PreflightAsync()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Response(Success("unexpected"))); }));
        using var client = new GoogleTranslateClient(http);
        await ThrowsAsync<InvalidOperationException>(() => client.TranslateAsync(new string('x', 16001), Settings(), default), "未发送");
        await ThrowsAsync<InvalidOperationException>(() => client.TranslateAsync(string.Join('\n', Enumerable.Repeat("x", 65)), Settings(), default), "64");
        await ThrowsAsync<InvalidOperationException>(() => client.TranslateAsync("bad\uD800text", Settings(), default), "Unicode");
        var invalid = Settings(); invalid.TargetLanguage = "随意风格";
        await ThrowsAsync<InvalidOperationException>(() => client.TranslateAsync("hello", invalid, default), "目标语言");
        Check(calls == 0, "All source/count/language checks run before any network access");
    }

    private static async Task ErrorsAsync()
    {
        foreach (var sample in new[]
        {
            (HttpStatusCode.Forbidden, "private-server-body", "application/json", "拒绝自动"),
            (HttpStatusCode.TooManyRequests, "private-server-body", "application/json", "拒绝自动"),
            (HttpStatusCode.Redirect, "private-server-body", "text/html", "重定向"),
            (HttpStatusCode.OK, "<html>automated queries private-server-body</html>", "text/html", "拒绝自动"),
            (HttpStatusCode.OK, "automated queries private-server-body", "text/plain", "拒绝自动"),
            (HttpStatusCode.ServiceUnavailable, "<html>unusual traffic private-server-body</html>", "text/html", "拒绝自动"),
            (HttpStatusCode.BadRequest, "private-server-body", "application/json", "400"),
            (HttpStatusCode.ServiceUnavailable, "private-server-body", "application/json", "503"),
            (HttpStatusCode.OK, "{not-json}", "application/json", "格式"),
            (HttpStatusCode.OK, "[[[null,\"source\"]]]", "application/json", "格式"),
            (HttpStatusCode.OK, Success(""), "application/json", "未返回"),
            (HttpStatusCode.OK, Success(new string('x', 32001)), "application/json", "32,000"),
            (HttpStatusCode.OK, new string('x', 1_048_577), "application/json", "响应过大")
        })
        {
            int calls = 0;
            using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Response(sample.Item2, sample.Item1, sample.Item3)); }));
            using var client = new GoogleTranslateClient(http);
            var error = await ThrowsAsync<InvalidOperationException>(() => client.TranslateAsync("private-source-text", Settings(), default), sample.Item4);
            Check(!error.ToString().Contains("private-source-text") && !error.ToString().Contains("private-server-body"), "Errors never contain request URI or server payload");
            Check(calls == 1, "Refusals/errors are not retried or redirected");
        }

        int sequence = 0;
        using var failedHttp = new HttpClient(new Handler((_, _) => Task.FromResult(++sequence == 1
            ? Response(Success("first partial")) : Response("private", HttpStatusCode.Forbidden))));
        using var failedClient = new GoogleTranslateClient(failedHttp);
        await ThrowsAsync<InvalidOperationException>(() => failedClient.TranslateAsync("first line\nsecond line", Settings(), default), "拒绝自动");
        Check(sequence == 2, "A later chunk failure fails the whole request, without returning the first translation");

        using var totalHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Response(Success(new string('x', 20000))))));
        using var totalClient = new GoogleTranslateClient(totalHttp);
        await ThrowsAsync<InvalidOperationException>(() => totalClient.TranslateAsync("first\nsecond", Settings(), default), "32,000");

        using var streamHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekStream(new byte[1_048_577])) })));
        using var streamClient = new GoogleTranslateClient(streamHttp);
        await ThrowsAsync<InvalidOperationException>(() => streamClient.TranslateAsync("hello", Settings(), default), "响应过大");

        using var networkHttp = new HttpClient(new Handler((_, _) => throw new HttpRequestException("https://translate.googleapis.com/?q=private-source-text")));
        using var networkClient = new GoogleTranslateClient(networkHttp);
        var networkError = await ThrowsAsync<InvalidOperationException>(() => networkClient.TranslateAsync("private-source-text", Settings(), default));
        Check(!networkError.ToString().Contains("private-source-text"), "Transport exception URL is discarded, not retained as an inner exception");

        using var phraseHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Response(Success("automated queries")))));
        using var phraseClient = new GoogleTranslateClient(phraseHttp);
        Check(await phraseClient.TranslateAsync("自动查询", Settings(), default) == "automated queries", "Legitimate translated text is not mistaken for a blocking HTML page");
    }

    private static async Task CancellationAsync()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            calls++; await Task.Delay(Timeout.Infinite, token); return Response(Success("never"));
        }));
        using var client = new GoogleTranslateClient(http);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => client.TranslateAsync("hello", Settings(), canceled.Token));
        Check(calls == 0, "Pre-cancel never sends text");
        using var active = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await ThrowsAsync<OperationCanceledException>(() => client.TranslateAsync("hello\nworld", Settings(), active.Token));
        Check(calls == 1, "Cancellation prevents the next chunk and never retries");
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action, string? message = null) where T : Exception
    {
        try { await action(); }
        catch (T error) { if (message is not null) Check(error.Message.Contains(message), "Expected: " + message); return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Check(bool success, string message) { if (!success) throw new Exception("Google translation test: " + message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
    private sealed class NonSeekStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
