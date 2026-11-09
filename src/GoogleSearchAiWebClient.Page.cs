using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SteamVrTranslator;

public sealed partial class GoogleSearchAiWebClient
{
    // ExecuteScriptAsync may run in a new top-level document if navigation races it. Refuse
    // to evaluate the payload until the destination and, for uploads, original document match.
    internal static string GuardedScript(string expression, string? expectedDocument)
    {
        return """
            (()=>{const url=new URL(location.href);
            if(window!==window.top||url.origin!=='https://www.google.com'||url.username||url.password)return {state:'unsafe'};
            const path=url.pathname;
            if(path!=='/aimode'){
              const values=url.searchParams.getAll(path==='/webhp'?'aep':'udm');
              if(values.length!==1||!((path==='/webhp'&&values[0]==='11')||(path==='/search'&&values[0]==='50')))return {state:'unsafe'};
            }
            """ + (expectedDocument is null ? "" : $"if(window.__mugonzeiSearchAiDocument!=={JsonSerializer.Serialize(expectedDocument)})return {{state:'unsafe'}};")
            + "return (" + expression.TrimEnd().TrimEnd(';') + ");})()";
    }

    // One instance per WebView/document lifetime. The hidden WebView is disposed on success,
    // cancellation, failure, or timeout. Only read-only polling may continue after submission.
    private sealed class SearchPage
    {
        private readonly CoreWebView2 core;
        private readonly TaskCompletionSource<string> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string id = Guid.NewGuid().ToString("N");
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private const int ChunkBytes = 192 * 1024; // Multiple of three: concatenate base64 chunks without internal padding.

        internal SearchPage(CoreWebView2 core)
        {
            this.core = core;
            core.ProcessFailed += (_, _) => failure.TrySetResult("Google AI 模式的浏览器进程已停止，请重试。");
            core.WebResourceResponseReceived += (_, e) =>
            {
                if (e.Response.StatusCode is 403 or 429 && SecureGoogleUri(e.Request.Uri, out var uri) && IsChallenge(uri!))
                    failure.TrySetResult(BlockedMessage);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (BlockReason(e.Uri) is not { } reason) return;
                e.Cancel = true;
                failure.TrySetResult(reason);
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (e.HttpStatusCode is 403 or 429) failure.TrySetResult(BlockedMessage);
                else if (!e.IsSuccess) failure.TrySetResult("Google AI 模式网页加载失败，请检查网络或手动打开网页。");
                else if (IsAiPage(core.Source)) loaded.TrySetResult(true);
            };
        }

        private async Task<T> AwaitPage<T>(Task<T> task, CancellationToken token)
        {
            await Task.WhenAny(task, failure.Task).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (failure.Task.IsCompletedSuccessfully) throw new InvalidOperationException(failure.Task.Result);
            return await task;
        }

        private async Task<GoogleSearchAiPage> RunScript(string expression, bool requireDocument, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (failure.Task.IsCompletedSuccessfully) throw new InvalidOperationException(failure.Task.Result);
            if (BlockReason(core.Source) is { } reason) throw new InvalidOperationException(reason);
            string script = GuardedScript(expression, requireDocument ? id : null);
            string raw = await AwaitPage(core.ExecuteScriptAsync(script), token);
            if (BlockReason(core.Source) is { } after) throw new InvalidOperationException(after);
            if (raw.Length > 260_000) throw new InvalidOperationException("Google AI 模式的回答过长，请缩小截图范围。");
            var page = JsonSerializer.Deserialize<GoogleSearchAiPage>(raw, Json)
                ?? throw new InvalidOperationException(ChangedMessage);
            switch (page.State)
            {
                case "blocked": throw new InvalidOperationException(BlockedMessage);
                case "login": case "consent": throw new InvalidOperationException(LoginMessage);
                case "unsupported": throw new InvalidOperationException(PageMessage);
                case "unsafe": case "uncertain": throw new InvalidOperationException(ChangedMessage);
            }
            return page;
        }

        private async Task<GoogleSearchAiPage> WaitState(Func<string> script, string expected, TimeSpan limit,
            bool requireDocument, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            do
            {
                var page = await RunScript(script(), requireDocument, token);
                if (page.State == expected) return page;
                if (page.State is not ("waiting" or "attached" or "submitted" or "generating"))
                    throw new InvalidOperationException(ChangedMessage);
                await Task.Delay(250, token);
            } while (watch.Elapsed < limit);
            throw new TimeoutException(expected switch
            {
                "ready" => PageMessage,
                "uploadReady" => "Google AI 模式未确认图片上传。请打开网页检查图片入口或网络。",
                _ => "Google AI 模式未返回可确认的完整回答。请打开网页查看或重新截图。"
            });
        }

        internal async Task<string> TranslateAsync(byte[] png, string prompt, CancellationToken token)
        {
            core.Navigate(EntryUrl);
            await AwaitPage(loaded.Task, token);
            var start = await RunScript($"(()=>{{window.__mugonzeiSearchAiDocument='{id}';window.__mugonzeiSearchAiUpload={{id:'{id}',parts:[]}};return {{state:'ready'}};}})()", false, token);
            if (start.State != "ready") throw new InvalidOperationException(ChangedMessage);
            await WaitState(GoogleSearchAiDom.Inspect, "ready", TimeSpan.FromSeconds(18), true, token);
            for (int offset = 0; offset < png.Length; offset += ChunkBytes)
            {
                token.ThrowIfCancellationRequested();
                string chunk = Convert.ToBase64String(png, offset, Math.Min(ChunkBytes, png.Length - offset));
                string append = $"(()=>{{const b=window.__mugonzeiSearchAiUpload;if(b?.id!=='{id}'||b.parts.length!=={offset / ChunkBytes})return {{state:'unsafe'}};b.parts.push('{chunk}');return {{state:'buffered'}};}})()";
                if ((await RunScript(append, true, token)).State != "buffered") throw new InvalidOperationException(ChangedMessage);
            }
            var attached = await RunScript(GoogleSearchAiDom.AttachBuffered(id), true, token);
            if (attached.State is not ("attached" or "uploadReady")) throw new InvalidOperationException(PageMessage);
            if (attached.State != "uploadReady")
                await WaitState(() => GoogleSearchAiDom.Read(id), "uploadReady", TimeSpan.FromSeconds(35), true, token);
            if ((await RunScript(GoogleSearchAiDom.Submit(prompt, id), true, token)).State != "submitted")
                throw new InvalidOperationException(ChangedMessage);
            // The result may be in a new canonical Search conversation after the submit click.
            // Read(id) requires this request's paired answer markers, even across a reload.
            var answer = await WaitState(() => GoogleSearchAiDom.Read(id), "complete", TimeSpan.FromSeconds(90), false, token);
            if (string.IsNullOrWhiteSpace(answer.Text) || answer.Text.Length > 32_000)
                throw new InvalidOperationException("Google AI 模式返回的译文为空或过长。请查看网页后重试。");
            return answer.Text;
        }
    }
}
