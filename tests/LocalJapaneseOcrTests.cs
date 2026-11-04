using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace SteamVrTranslator;

public static class LocalJapaneseOcrTests
{
    public static async Task RunAsync()
    {
        await PersistentWorkerAsync();
        await FailureAndLimitsAsync();
        await CancellationAndRestartAsync();
        await DisposeAndTimeoutAsync();
        Console.WriteLine("PASS local Japanese OCR persistent process, Unicode, request IDs, limits, cancellation/restart and PNG cleanup");
    }

    private static byte[] Png()
    {
        using var bitmap = new Bitmap(24, 16);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
        using var memory = new MemoryStream(); bitmap.Save(memory, ImageFormat.Png); return memory.ToArray();
    }

    private static async Task PersistentWorkerAsync()
    {
        using var fixture = new Fixture("ok");
        string first = await fixture.Service.RecognizeAsync(Png(), default);
        string second = await fixture.Service.RecognizeAsync(Png(), default);
        Check(first.StartsWith("おはよ！\nHello ") && second.StartsWith("おはよ！\nHello "), "Unicode and line breaks preserved");
        Check(first.EndsWith(":1") && second.EndsWith(":2") && fixture.Starts == 1, "Two requests reuse one loaded worker");
        Check(first.Split(' ')[^1].Split(':')[0] == second.Split(' ')[^1].Split(':')[0], "Same child process handles both requests");
        Check(Directory.GetDirectories(fixture.Root).Length == 0, "Each successful request removes its unique image directory");
        int pid = int.Parse(File.ReadAllText(fixture.PidFile));
        fixture.Service.Dispose();
        Check(!Alive(pid), "Disposal stops the idle persistent worker");
        using var empty = new Fixture("empty");
        Check(await empty.Service.RecognizeAsync(Png(), default) == "", "Blank image may return an empty result");
        using var literal = new Fixture("literal");
        Check(await literal.Service.RecognizeAsync(Png(), default) == "[[NO_TEXT]]", "Local OCR does not interpret AI-only sentinels");
        using var extra = new Fixture("extra");
        await extra.Service.RecognizeAsync(Png(), default);
        string directory = Directory.GetDirectories(extra.Root).Single();
        Check(File.Exists(Path.Combine(directory, "keep.txt")) && !File.Exists(Path.Combine(directory, "screenshot.png")),
            "Cleanup removes only its own image, never recursively removes unrelated content");
        File.Delete(Path.Combine(directory, "keep.txt")); Directory.Delete(directory, false);
    }

    private static async Task FailureAndLimitsAsync()
    {
        foreach (string mode in new[] { "stale", "error", "malformed", "exit", "large", "longline", "startup" })
        {
            using var fixture = new Fixture(mode);
            var error = await ThrowsAsync<InvalidOperationException>(() => fixture.Service.RecognizeAsync(Png(), default));
            Check(!error.Message.Contains("private-worker-detail"), "Worker diagnostics are not exposed to users");
            Check(Directory.GetDirectories(fixture.Root).Length == 0, "Failed requests remove their temporary PNG");
            if (File.Exists(fixture.PidFile)) Check(!Alive(int.Parse(File.ReadAllText(fixture.PidFile))), "Protocol failure stops worker");
        }
        using var invalid = new Fixture("ok");
        await ThrowsAsync<InvalidOperationException>(() => invalid.Service.RecognizeAsync([], default));
        await ThrowsAsync<InvalidOperationException>(() => invalid.Service.RecognizeAsync(new byte[VisionImage.MaxBytes + 1], default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => invalid.Service.RecognizeAsync(Png(), cancellation.Token));
        Check(invalid.Starts == 0, "Invalid/pre-canceled requests do not launch any process");
    }

    private static async Task CancellationAndRestartAsync()
    {
        using var fixture = new Fixture("wait", "ok");
        using var firstCancellation = new CancellationTokenSource();
        var first = fixture.Service.RecognizeAsync(Png(), firstCancellation.Token);
        int firstPid = await fixture.WaitForPidAsync();
        using var queuedCancellation = new CancellationTokenSource();
        var queued = fixture.Service.RecognizeAsync(Png(), queuedCancellation.Token);
        queuedCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => queued);
        Check(!first.IsCompleted && fixture.Starts == 1, "Cancelling a waiting request does not stop the active owner");
        firstCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => first);
        Check(!Alive(firstPid), "Cancellation kills active worker before a newer request may use it");
        string restarted = await fixture.Service.RecognizeAsync(Png(), default);
        Check(restarted.EndsWith(":1") && fixture.Starts == 2, "A canceled process is replaced, never reused with stale output");
        Check(Directory.GetDirectories(fixture.Root).Length == 0, "Cancellation and restart clean image files");
    }

    private static async Task DisposeAndTimeoutAsync()
    {
        using (var fixture = new Fixture("wait"))
        {
            Task<string> active = fixture.Service.RecognizeAsync(Png(), default);
            int pid = await fixture.WaitForPidAsync();
            fixture.Service.Dispose();
            await ThrowsAsync<OperationCanceledException>(() => active);
            Check(!Alive(pid), "Closing stops inference and prevents a late result");
            await ThrowsAsync<ObjectDisposedException>(() => fixture.Service.RecognizeAsync(Png(), default));
        }
        using var timed = new Fixture(TimeSpan.FromMilliseconds(800), "wait");
        await ThrowsAsync<TimeoutException>(() => timed.Service.RecognizeAsync(Png(), default));
        if (File.Exists(timed.PidFile)) Check(!Alive(int.Parse(File.ReadAllText(timed.PidFile))), "Timeout kills the worker");
        Check(Directory.GetDirectories(timed.Root).Length == 0, "Timeout removes image files");
    }

    public static async Task RunFakeAsync(string mode)
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        string? pidFile = Environment.GetEnvironmentVariable("LOCAL_OCR_TEST_PID");
        if (pidFile is not null) File.WriteAllText(pidFile, Environment.ProcessId.ToString());
        await Console.Error.WriteAsync(new string('x', 65536));
        if (mode == "startup") { Console.WriteLine("{\"type\":\"fatal\",\"code\":\"private-worker-detail\"}"); return; }
        Console.WriteLine("{\"type\":\"ready\",\"protocol\":1}");
        int calls = 0;
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("id").GetString()!;
            string path = json.RootElement.GetProperty("image_path").GetString()!;
            VisionImage.ValidatePng(await File.ReadAllBytesAsync(path));
            if (mode == "wait") { await Task.Delay(Timeout.Infinite); return; }
            if (mode == "exit") return;
            if (mode == "malformed") { Console.WriteLine("private-worker-detail"); continue; }
            if (mode == "longline") { Console.WriteLine(new string('x', 262145)); continue; }
            if (mode == "error") { Console.WriteLine(JsonSerializer.Serialize(new { id, ok = false, code = "private-worker-detail" })); continue; }
            if (mode == "extra") File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "keep.txt"), "preserve");
            string text = mode switch
            {
                "empty" => "", "literal" => "[[NO_TEXT]]", "large" => new string('字', 32001),
                _ => $" おはよ！\nHello {Environment.ProcessId}:{++calls} "
            };
            Console.WriteLine(JsonSerializer.Serialize(new { id = mode == "stale" ? "old-request" : id, ok = true, text }));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalOcrTests-" + Guid.NewGuid().ToString("N"));
        public string PidFile => Path.Combine(Root, "worker.pid");
        public int Starts { get; private set; }
        public LocalJapaneseOcrService Service { get; }
        public Fixture(params string[] modes) : this(TimeSpan.FromSeconds(10), modes) { }
        public Fixture(TimeSpan timeout, params string[] modes)
        {
            Directory.CreateDirectory(Root);
            Service = new LocalJapaneseOcrService(() =>
            {
                string mode = modes[Math.Min(Starts++, modes.Length - 1)];
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                start.ArgumentList.Add("--fake-local-ocr"); start.ArgumentList.Add(mode);
                start.Environment["LOCAL_OCR_TEST_PID"] = PidFile;
                return start;
            }, timeout, Root);
        }
        public async Task<int> WaitForPidAsync()
        {
            for (int attempt = 0; attempt < 300; attempt++)
            {
                if (File.Exists(PidFile) && int.TryParse(await File.ReadAllTextAsync(PidFile), out int pid)) return pid;
                await Task.Delay(10);
            }
            throw new Exception("Fake local OCR did not start.");
        }
        public void Dispose()
        {
            Service.Dispose();
            if (File.Exists(PidFile)) File.Delete(PidFile);
            // Fail visibly if the service left any unexpected files; never recursively delete them.
            Directory.Delete(Root, false);
        }
    }

    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Local OCR test: " + message);
    }
}
