using System.Collections.Concurrent;

namespace SteamVrTranslator;

/// <summary>Pure helpers and an injected session only; never creates WebView2 or accesses Google.</summary>
public static class GoogleTranslateWebTests
{
    public static async Task RunAsync()
    {
        InputAndUnicode();
        NavigationAndLanguages();
        ResultIsolation();
        BlockedPages();
        await PreflightAndCredentialIsolationAsync();
        await CancellationAndDeadlineAsync();
        await QueuedCancellationAndShutdownAsync();
        await OutputBoundsAndDisposalAsync();
        Console.WriteLine("PASS Google webpage input bounds, fixed navigation, fresh-result isolation, blocking, cancellation, timeout and disposal (offline)");
    }

    private static AppSettings Settings(string target = "简体中文") => new()
    {
        TargetLanguage = target,
        ApiBaseUrl = "https://private-service.example/should-not-be-used",
        ApiKey = "private-key-never-used-by-webpage",
        ApiModel = "private-model-never-used-by-webpage"
    };

    private static void InputAndUnicode()
    {
        GoogleTranslateWebClient.ValidateInput("");
        GoogleTranslateWebClient.ValidateInput(" \t\r\n");
        GoogleTranslateWebClient.ValidateInput("日本語\n中文 English 😀 e\u0301 &<>\"'");
        GoogleTranslateWebClient.ValidateInput(new string('日', 5000));
        GoogleTranslateWebClient.ValidateInput(new string('日', 4998) + "😀");
        foreach (string invalid in new[] { new string('日', 5001), new string('日', 4999) + "😀", "\uD800", "\uDC00", "a\uD800x", "\uDC00\uD800", "a\0b", "a\u0001b" })
            Throws<InvalidOperationException>(() => GoogleTranslateWebClient.ValidateInput(invalid));
        Check(GoogleTranslateWebClient.NormalizeSource("first\r\n第二行\rthird") == "first\n第二行\nthird",
            "Textarea newline normalization handles CRLF and standalone CR");
    }

    private static void NavigationAndLanguages()
    {
        foreach (var sample in new[] { ("简体中文", "zh-CN"), ("日语", "ja"), ("英语", "en"), ("fr", "fr") })
        {
            Uri page = GoogleTranslateWebClient.BuildPageUri(sample.Item1);
            Check(page.Scheme == "https" && page.Host == "translate.google.com" && page.Port == 443,
                "Page location always uses the fixed Google Translate HTTPS origin");
            Check(page.UserInfo.Length == 0 && page.Fragment.Length == 0, "Page URI does not contain credentials or a fragment");
            var query = ParseQuery(page);
            Check(query.TryGetValue("sl", out string? source) && source == "auto"
                && query.TryGetValue("tl", out string? target) && target == sample.Item2,
                "Only the intended source and target language are selected");
            Check(!query.ContainsKey("text") && !query.ContainsKey("q") && !query.ContainsKey("key")
                && !query.ContainsKey("api_key") && !query.ContainsKey("token"),
                "Navigation never puts source text or credentials into the URL");
            Check(GoogleTranslateWebClient.IsAllowedNavigation(page.AbsoluteUri), "Generated page navigation is allowed");
        }
        Check(GoogleTranslateWebClient.IsAllowedNavigation("https://TRANSLATE.GOOGLE.COM:443/?sl=auto&tl=ja"),
            "Canonical HTTPS host comparison handles host casing and the default port");
        foreach (string url in new[]
        {
            "https://translate.google.com.evil.example/", "https://evil.example/?next=https://translate.google.com/",
            "https://translate.google.com@evil.example/", "https://user:password@translate.google.com/",
            "http://translate.google.com/", "https://translate.google.com:444/", "https://sub.translate.google.com/",
            "https://127.0.0.1/", "https://consent.google.com/", "https://accounts.google.com/",
            "javascript:alert(1)", "data:text/html,test", "file:///C:/private.png", "/relative", "", "not a URL"
        }) Check(!GoogleTranslateWebClient.IsAllowedNavigation(url), "Cross-origin, credential-bearing or non-HTTPS navigation is refused");
        foreach (string target in new[] { "auto", "en&text=private-source", "ja#private-key", "English with private instructions" })
            Throws<InvalidOperationException>(() => GoogleTranslateWebClient.BuildPageUri(target));
    }

    private static void ResultIsolation()
    {
        const long requestId = 42;
        const string source = "日本語\nSecond line 😀";
        const string target = "zh-CN";
        var result = new WebTranslationSnapshot(requestId, source, target, "译文\n第二行", false);
        Check(GoogleTranslateWebClient.MatchesResult(result, requestId, source, target), "A complete result matches its request");
        Check(!GoogleTranslateWebClient.MatchesResult(result, requestId + 1, source, target),
            "Repeating identical source and target never accepts the previous request's result");
        Check(!GoogleTranslateWebClient.MatchesResult(result with { RequestId = requestId - 1 }, requestId, source, target),
            "An older page generation is ignored");
        Check(!GoogleTranslateWebClient.MatchesResult(result with { Source = "別の原文" }, requestId, source, target),
            "A result for different source text is ignored");
        Check(!GoogleTranslateWebClient.MatchesResult(result with { Target = "en" }, requestId, source, target),
            "A result for the previously selected language is ignored");
        Check(!GoogleTranslateWebClient.MatchesResult(result with { IsBlocked = true }, requestId, source, target),
            "Challenge pages are never accepted as translated text");
        Check(!GoogleTranslateWebClient.MatchesResult(result with { Text = " \r\n" }, requestId, source, target),
            "An empty pending output is not a fresh translation");
        Check(!GoogleTranslateWebClient.MatchesResult(result with { Text = new string('译', 32001) }, requestId, source, target),
            "Oversized page output is not accepted as a result");
        Check(GoogleTranslateWebClient.MatchesResult(result, requestId, source.Replace("\n", "\r\n"), target),
            "Browser textarea CRLF normalization does not cause a false stale-result timeout");
    }

    private static void BlockedPages()
    {
        const string page = "https://translate.google.com/?sl=auto&tl=zh-CN";
        Check(!GoogleTranslateWebClient.IsBlockedPage(page, "Google Translate", false), "The ordinary page is usable");
        Check(GoogleTranslateWebClient.IsBlockedPage(page, "", true), "A challenge element stops automatic translation");
        Check(GoogleTranslateWebClient.IsBlockedPage("https://www.google.com/sorry/index", "", false),
            "Google's unusual-traffic page is recognized");
        Check(GoogleTranslateWebClient.IsBlockedPage("https://consent.google.com/m", "", false),
            "Consent requires user attention instead of accepting unrelated page text");
        Check(GoogleTranslateWebClient.IsBlockedPage(page, "Our systems have detected unusual traffic from your computer network.", false),
            "An explicit automated-traffic rejection is recognized");
        Check(GoogleTranslateWebClient.IsBlockedPage(page, "We're sorry, but your computer or network may be sending automated queries.", false),
            "The observed Google refusal is recognized");
    }

    private static async Task PreflightAndCredentialIsolationAsync()
    {
        using var session = new FakeSession();
        using var client = new GoogleTranslateWebClient(session);
        Check(await client.TranslateAsync(" \t\r\n", Settings(), default) == "", "Empty input has no browser activity");
        Check(session.Requests.IsEmpty, "Empty input does not invoke a session");
        await ThrowsAsync<InvalidOperationException>(() => client.TranslateAsync(new string('日', 5001), Settings(), default));
        await ThrowsAsync<InvalidOperationException>(() => client.TranslateAsync("\uD800", Settings(), default));
        await ThrowsAsync<InvalidOperationException>(() => client.TranslateAsync("private-source", Settings("invalid prose"), default));
        Check(session.Requests.IsEmpty, "Invalid source and target are rejected before starting a browser session");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => client.TranslateAsync("private-source", Settings(), cancelled.Token));
        Check(session.Requests.IsEmpty, "Pre-cancelled work never starts browser activity");

        const string source = "日本語 😀 </textarea><script>window.location='https://evil.example'</script> \"&?=#";
        session.Run = (text, target, token) => Task.FromResult("纯文字译文");
        Check(await client.TranslateAsync(source, Settings(), default) == "纯文字译文", "The browser result is returned as plain text");
        Check(session.Requests.TryPeek(out var request) && request.Source == source && request.Target == "zh-CN",
            "Hostile-looking source stays source data; unrelated API URL, key and model never reach the session");
    }

    private static async Task CancellationAndDeadlineAsync()
    {
        using var session = new FakeSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Run = async (_, _, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return "unreachable"; };
        using var client = new GoogleTranslateWebClient(session, TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();
        Task<string> pending = client.TranslateAsync("source", Settings(), cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Check(session.LastToken.IsCancellationRequested, "Caller cancellation reaches the browser session");
        }
        finally
        {
            cancel.Cancel();
            await ObserveAsync(pending);
        }

        using var timeoutSession = new FakeSession
        {
            Run = async (_, _, token) => { await Task.Delay(Timeout.Infinite, token); return "unreachable"; }
        };
        using var timeoutClient = new GoogleTranslateWebClient(timeoutSession, TimeSpan.FromMilliseconds(40));
        await ThrowsAsync<TimeoutException>(() => timeoutClient.TranslateAsync("private-source", Settings(), default)
            .WaitAsync(TimeSpan.FromSeconds(3)), "超时");
        Check(timeoutSession.LastToken.IsCancellationRequested, "The deadline cancels browser work instead of abandoning it");

        using var lateCancellation = new CancellationTokenSource();
        using var lateSession = new FakeSession { Run = (_, _, _) => { lateCancellation.Cancel(); return Task.FromResult("stale result"); } };
        using var lateClient = new GoogleTranslateWebClient(lateSession);
        await ThrowsAsync<OperationCanceledException>(() => lateClient.TranslateAsync("source", Settings(), lateCancellation.Token));
    }

    private static async Task OutputBoundsAndDisposalAsync()
    {
        var session = new FakeSession { Run = (_, _, _) => Task.FromResult(new string('译', 32000)) };
        var client = new GoogleTranslateWebClient(session);
        try
        {
            Check((await client.TranslateAsync("source", Settings(), default)).Length == 32000, "The exact result limit is accepted");
            session.Run = (_, _, _) => Task.FromResult(new string('译', 32001));
            var error = await ThrowsAsync<InvalidOperationException>(() => client.TranslateAsync("private-source", Settings(), default), "32,000");
            Check(!error.ToString().Contains("private-source") && !error.ToString().Contains("private-key"), "Bounds errors do not expose text or API credentials");
        }
        finally { client.Dispose(); }
        client.Dispose();
        Check(session.DisposeCalls == 1, "Disposal closes the dedicated session exactly once");
        int before = session.Requests.Count;
        await ThrowsAsync<ObjectDisposedException>(() => client.TranslateAsync("source", Settings(), default));
        Check(session.Requests.Count == before, "Disposed clients cannot queue new browser work");
    }

    private static async Task QueuedCancellationAndShutdownAsync()
    {
        using var session = new FakeSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Run = (_, _, token) => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        using var client = new GoogleTranslateWebClient(session, TimeSpan.FromSeconds(10));
        using var firstCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        Task<string> first = client.TranslateAsync("first", Settings(), firstCancellation.Token);
        Task<string>? queued = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            queued = client.TranslateAsync("second", Settings("ja"), queuedCancellation.Token);
            queuedCancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(3)));
            Check(session.Requests.Count == 1, "Cancelling queued work cannot overwrite the active page's source or language");
            release.TrySetResult("first translation");
            Check(await first == "first translation", "A cancelled queued request does not cancel the active request");
        }
        finally
        {
            firstCancellation.Cancel();
            queuedCancellation.Cancel();
            await ObserveAsync(first);
            if (queued is not null) await ObserveAsync(queued);
        }

        var shutdownSession = new FakeSession();
        var shutdownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shutdownSession.Run = async (_, _, token) => { shutdownEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return "unreachable"; };
        var shutdownClient = new GoogleTranslateWebClient(shutdownSession);
        Task<string> pending = shutdownClient.TranslateAsync("source", Settings(), default);
        try
        {
            await shutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            shutdownClient.Dispose();
            await ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Check(shutdownSession.LastToken.IsCancellationRequested && shutdownSession.DisposeCalls == 1,
                "Closing the client cancels in-flight page work and disposes the session");
        }
        finally
        {
            shutdownClient.Dispose();
            await ObserveAsync(pending);
        }
    }

    private static Dictionary<string, string> ParseQuery(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]), part => part.Length == 2 ? Uri.UnescapeDataString(part[1]) : "");

    private sealed class FakeSession : IGoogleTranslateWebSession
    {
        public readonly ConcurrentQueue<(string Source, string Target)> Requests = new();
        public Func<string, string, CancellationToken, Task<string>> Run = (_, _, _) => Task.FromResult("translation");
        public CancellationToken LastToken;
        public int DisposeCalls;
        public Task<string> TranslateAsync(string source, string target, CancellationToken token)
        {
            Requests.Enqueue((source, target));
            LastToken = token;
            return Run(source, target, token);
        }
        public Task ShowAsync(CancellationToken token) => throw new Exception("Offline tests must not show a browser.");
        public Task SavePreviewAsync(string path, CancellationToken token) => throw new Exception("Offline tests must not capture a browser.");
        public void Dispose() => DisposeCalls++;
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new Exception("Google webpage test expected " + typeof(T).Name);
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action, string? message = null) where T : Exception
    {
        try { await action(); }
        catch (T error)
        {
            Check(message == null || error.Message.Contains(message, StringComparison.Ordinal), "Expected localized failure message");
            return error;
        }
        throw new Exception("Google webpage test expected " + typeof(T).Name);
    }
    private static async Task ObserveAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Google webpage test failed: " + message);
    }
}
