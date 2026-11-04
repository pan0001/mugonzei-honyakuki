using System.Text.Json;

namespace SteamVrTranslator;

/// <summary>Small local fault log: no images, recognized text, URLs, keys or model responses.</summary>
internal static class RuntimeFaultLog
{
    private static readonly object Gate = new();
    private static string lastSignature = "";
    private static long lastWritten;

    public static void Record(string operation, Exception? error = null)
    {
        try
        {
            lock (Gate)
            {
                string signature = operation + error?.GetType().FullName + (error as VrOverlayException)?.Error;
                long now = Environment.TickCount64;
                if (signature == lastSignature && now - lastWritten < 5000) return;
                lastSignature = signature; lastWritten = now;
                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                string path = Path.Combine(SettingsStore.DirectoryPath, "runtime-faults.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= 262144)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    time = DateTimeOffset.UtcNow, operation,
                    exception = error?.GetType().FullName, hresult = error?.HResult,
                    overlayOperation = (error as VrOverlayException)?.Operation,
                    overlayError = (error as VrOverlayException)?.Error.ToString(),
                    stack = error?.StackTrace
                }) + Environment.NewLine);
            }
        }
        catch { /* A diagnostic failure must never break cancellation or controller input. */ }
    }
}
