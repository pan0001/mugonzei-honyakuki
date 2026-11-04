using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SteamVrTranslator;

/// <summary>Runs the installed NDLOCR-Lite CPU worker locally. Never installs or downloads anything.</summary>
public sealed class LocalJapaneseOcrService : IDisposable
{
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly object lifecycle = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly CancellationToken shutdownToken;
    private readonly Func<ProcessStartInfo> createStart;
    private readonly TimeSpan timeout;
    private readonly string temporaryRoot;
    private Process? worker;
    private BoundedLines? output;
    private bool disposed;
    private static readonly string[] RequiredFiles =
    [
        "runtime-manifest.json", "python/python.exe", "local-japanese-ocr-worker.py", "ndlocr-lite/src/ocr.py",
        "ndlocr-lite/src/config/ndl.yaml", "ndlocr-lite/src/config/NDLmoji.yaml",
        "ndlocr-lite/src/model/deim-s-1024x1024.onnx",
        "ndlocr-lite/src/model/parseq-ndl-24x256-30-tiny-189epoch-tegaki3-r8data-202604.onnx",
        "ndlocr-lite/src/model/parseq-ndl-24x384-50-tiny-300epoch-tegaki3-r8data-202604.onnx",
        "ndlocr-lite/src/model/parseq-ndl-24x768-100-tiny-153epoch-tegaki3-r8data-202604.onnx"
    ];

    public LocalJapaneseOcrService() : this(() => CreateStartInfo(RuntimeDirectory), TimeSpan.FromSeconds(120)) { }
    internal LocalJapaneseOcrService(Func<ProcessStartInfo> factory, TimeSpan requestTimeout, string? tempRoot = null)
    {
        createStart = factory;
        timeout = requestTimeout;
        temporaryRoot = tempRoot ?? Path.Combine(Path.GetTempPath(), "SteamVrTranslator-LocalOCR");
        shutdownToken = shutdown.Token;
    }

    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "local-ocr-runtime");
    public static string GetAvailabilityMessage()
    {
        string? missing = MissingFile(RuntimeDirectory);
        return missing is null ? "本地日语 OCR 已安装 · NDLOCR-Lite · CPU · 首次识别需加载模型"
            : "本地日语 OCR 运行时不完整，请重新解压包含本地 OCR 的完整程序包。";
    }

    private static string? MissingFile(string root)
    {
        foreach (string relative in RequiredFiles)
        {
            try { var file = new FileInfo(Path.Combine(root, relative)); if (!file.Exists || file.Length == 0) return relative; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return relative; }
        }
        return null;
    }

    internal static ProcessStartInfo CreateStartInfo(string runtime)
    {
        if (MissingFile(runtime) is not null)
            throw new InvalidOperationException("本地日语 OCR 运行时不完整，请安装 local-ocr-runtime 后重试。");
        var start = new ProcessStartInfo(Path.Combine(runtime, "python", "python.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runtime,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { "-I", "-X", "utf8", "-u", Path.Combine(runtime, "local-japanese-ocr-worker.py"), "--runtime", runtime })
            start.ArgumentList.Add(arg);
        start.Environment.Remove("OPENAI_API_KEY"); start.Environment.Remove("CODEX_API_KEY");
        return start;
    }

    public async Task<string> RecognizeAsync(byte[] png, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        VisionImage.ValidatePng(png);
        lock (lifecycle) ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdownToken);
        await requests.WaitAsync(linked.Token).ConfigureAwait(false);
        string? work = null, imagePath = null;
        bool succeeded = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        deadline.CancelAfter(timeout);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            await EnsureWorkerAsync(deadline.Token).ConfigureAwait(false);
            work = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            imagePath = Path.Combine(work, "screenshot.png");
            await File.WriteAllBytesAsync(imagePath, png, deadline.Token).ConfigureAwait(false);
            string id = Guid.NewGuid().ToString("N");
            string request = JsonSerializer.Serialize(new { id, image_path = imagePath });
            Process active;
            BoundedLines reader;
            lock (lifecycle)
            {
                deadline.Token.ThrowIfCancellationRequested();
                active = worker ?? throw new InvalidOperationException("本地 OCR 进程已退出，请重试。");
                reader = output!;
            }
            await active.StandardInput.WriteLineAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
            await active.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            string line = await reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            using var response = JsonDocument.Parse(line);
            var item = response.RootElement;
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var responseId)
                || responseId.ValueKind != JsonValueKind.String || responseId.GetString() != id)
                throw new InvalidOperationException("本地 OCR 返回了不匹配的请求结果，已丢弃，请重试。");
            if (!item.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException();
            if (!ok.GetBoolean())
            {
                string? code = item.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                throw new InvalidOperationException(code switch
                {
                    "image" => "本地 OCR 无法读取截图，请缩小范围后重新截屏。",
                    "too_long" => "本地 OCR 原文超过 32,000 个字符，请缩小截取范围。",
                    _ => "本地日语 OCR 推理失败，请检查运行时是否完整，或缩小截图范围后重试。"
                });
            }
            if (!item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw new JsonException();
            string result = text.GetString()!;
            if (result.Length > TranslationClient.MaxOutputCharacters)
                throw new InvalidOperationException("本地 OCR 原文超过 32,000 个字符，请缩小截取范围。");
            // Plain OCR has no model sentinel: preserve literal [[NO_TEXT]] if present in the source.
            result = new string(result.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray()).Trim();
            deadline.Token.ThrowIfCancellationRequested();
            succeeded = true;
            return result;
        }
        catch (Exception error)
        {
            StopWorker();
            if (linked.IsCancellationRequested) throw new OperationCanceledException(linked.Token);
            if (deadline.IsCancellationRequested) throw new TimeoutException("本地日语 OCR 等待超过 120 秒，已停止本次识别；请缩小截图范围后重试。");
            if (error is JsonException or DecoderFallbackException)
                throw new InvalidOperationException("本地 OCR 返回格式无效，已停止进程，请重试。");
            if (error is Win32Exception)
                throw new InvalidOperationException("无法启动本地日语 OCR，请检查便携 Python 和运行时是否完整。");
            if (error is IOException or ObjectDisposedException)
                throw new InvalidOperationException("本地 OCR 进程已退出或通信中断，请重试。");
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (imagePath is not null)
                try { File.Delete(imagePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleanupFailure = ex; }
            if (work is not null)
                try { Directory.Delete(work, false); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            requests.Release();
            if (succeeded && cleanupFailure is not null)
                throw new InvalidOperationException("本地 OCR 临时截图清理失败，请关闭占用文件的程序后重试。", cleanupFailure);
        }
    }

    private async Task EnsureWorkerAsync(CancellationToken token)
    {
        BoundedLines reader;
        lock (lifecycle)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (worker is not null && !worker.HasExited) return;
            worker?.Dispose();
            worker = new Process { StartInfo = createStart() };
            if (!worker.Start()) throw new InvalidOperationException("无法启动本地 OCR 进程。");
            output = reader = new BoundedLines(worker.StandardOutput);
            _ = DrainErrorAsync(worker.StandardError);
        }
        using var ready = JsonDocument.Parse(await reader.ReadAsync(token).ConfigureAwait(false));
        var item = ready.RootElement;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var kind)
            || kind.ValueKind != JsonValueKind.String || kind.GetString() != "ready"
            || !item.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.Number
            || !protocol.TryGetInt32(out int version) || version != 1)
            throw new InvalidOperationException("本地日语 OCR 模型加载失败，请重新安装完整运行时后重试。");
    }

    private static async Task DrainErrorAsync(StreamReader error)
    {
        // Never retain OCR text or private paths from upstream diagnostics. Drain to prevent pipe deadlock.
        char[] buffer = new char[2048];
        try { while (await error.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void StopWorker()
    {
        lock (lifecycle)
        {
            if (worker is null) return;
            try { if (!worker.HasExited) { worker.Kill(entireProcessTree: true); worker.WaitForExit(2000); } }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
            worker.Dispose(); worker = null; output = null;
        }
    }

    public void Dispose()
    {
        lock (lifecycle) { if (disposed) return; disposed = true; shutdown.Cancel(); }
        StopWorker();
        // Waiting calls can still register their linked tokens; primitives live until those calls complete.
    }

    private sealed class BoundedLines(StreamReader reader)
    {
        private readonly char[] buffer = new char[4096];
        private int offset, count;
        public async Task<string> ReadAsync(CancellationToken token)
        {
            var line = new StringBuilder();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (offset == count)
                {
                    count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false); offset = 0;
                    if (count == 0) throw new IOException("Worker stdout closed.");
                }
                char next = buffer[offset++];
                if (next == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length >= 262144) throw new InvalidOperationException("本地 OCR 返回内容过大，已停止进程。");
                line.Append(next);
            }
        }
    }
}
