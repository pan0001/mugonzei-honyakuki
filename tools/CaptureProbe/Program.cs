using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SteamVrTranslator;
using Valve.VR;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string label = args.FirstOrDefault() ?? "capture-probe";
        var records = new List<object>();
        string assemblyPath = typeof(VrService).Assembly.Location;
        string assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant();
        int threadId = Environment.CurrentManagedThreadId;
        using var vr = new VrService();
        string? error = null;
        bool prepareEyeCaptureInvoked = false;
        double? prepareEyeCaptureDurationMs = null;
        try
        {
            if (!vr.Connect()) throw new InvalidOperationException(vr.Status);
            var prepare = typeof(VrService).GetMethod("PrepareEyeCapture", Type.EmptyTypes);
            if (prepare is not null)
            {
                var preparing = Stopwatch.StartNew();
                prepare.Invoke(vr, null);
                prepareEyeCaptureDurationMs = preparing.Elapsed.TotalMilliseconds;
                prepareEyeCaptureInvoked = true;
                // Match the application's short hide/warmup interval after acquiring its mirror.
                Thread.Sleep(80);
            }
            var elapsed = Stopwatch.StartNew();
            for (int sample = 0; sample < 9; sample++)
            {
                long targetMs = sample * 500L;
                while (elapsed.ElapsedMilliseconds < targetMs)
                {
                    vr.Poll();
                    long remainingMs = targetMs - elapsed.ElapsedMilliseconds;
                    if (remainingMs > 0) Thread.Sleep((int)Math.Min(15, remainingMs));
                }
                vr.Poll();
                var before = FrameTiming();
                DateTime startedUtc = DateTime.UtcNow;
                long startMs = elapsed.ElapsedMilliseconds;
                using Bitmap image = vr.CaptureEye();
                DateTime completedUtc = DateTime.UtcNow;
                var after = FrameTiming();
                string pixelHash = PixelHash(image);
                records.Add(new
                {
                    sample,
                    captureStartedUtc = startedUtc,
                    captureCompletedUtc = completedUtc,
                    elapsedStartMs = startMs,
                    captureDurationMs = (completedUtc - startedUtc).TotalMilliseconds,
                    image.Width,
                    image.Height,
                    pixelSha256 = pixelHash,
                    compositorBefore = before,
                    compositorAfter = after
                });
            }
        }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        finally { vr.Disconnect(); }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            label,
            assemblyPath,
            assemblySha256 = assemblyHash,
            threadId,
            completedThreadId = Environment.CurrentManagedThreadId,
            sampleIntervalMs = 500,
            prepareEyeCaptureInvoked,
            prepareEyeCaptureDurationMs,
            preCaptureWarmupMs = prepareEyeCaptureInvoked ? 80 : 0,
            pixelHashFormat = "SHA256 of complete top-to-bottom BGRA32 rows, excluding row padding; no images written",
            note = "Compositor frame indexes bracket the CaptureEye call; they do not identify the texture frame returned by CaptureEye.",
            captures = records,
            error
        }, new JsonSerializerOptions { WriteIndented = true }));
        return error is null ? 0 : 1;
    }

    private static object FrameTiming()
    {
        var timing = new Compositor_FrameTiming { m_nSize = (uint)Marshal.SizeOf<Compositor_FrameTiming>() };
        bool available = OpenVR.Compositor?.GetFrameTiming(ref timing, 0) ?? false;
        return new { available, frameIndex = timing.m_nFrameIndex, systemTimeSeconds = timing.m_flSystemTimeInSeconds };
    }

    private static string PixelHash(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] row = new byte[checked(bitmap.Width * 4)];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride)), row, 0, row.Length);
                hash.AppendData(row);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { bitmap.UnlockBits(data); }
    }
}
