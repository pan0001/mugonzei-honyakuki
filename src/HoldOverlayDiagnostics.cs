using System.Diagnostics;
using System.Text.Json;

namespace SteamVrTranslator;

/// <summary>Plays generated rings and checks native bounds/visibility. Does not capture images or call a model.</summary>
internal static class HoldOverlayDiagnostics
{
    public static void Run()
    {
        var report = new Dictionary<string, object> { ["createdAtUtc"] = DateTimeOffset.UtcNow, ["capturesImages"] = false, ["callsModel"] = false };
        var checkpoints = new List<JsonElement>();
        report["checkpoints"] = checkpoints;
        var clock = Stopwatch.StartNew();
        using var vr = new VrService();
        try
        {
            report["connected"] = vr.Connect(); report["status"] = vr.Status;
            if (!vr.IsConnected) throw new InvalidOperationException(vr.Status);
            while (true)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("圆环图集未能及时加载。");
                if (vr.ShowHoldProgress(0, false, 1.2f, -.2f))
                {
                    var loaded = JsonSerializer.SerializeToElement(vr.GetHoldProgressDiagnostics());
                    if (loaded.GetProperty("width").GetUInt32() == HoldProgressAtlas.AtlasSize
                        && loaded.GetProperty("height").GetUInt32() == HoldProgressAtlas.AtlasSize) break;
                }
                vr.Poll(); Thread.Sleep(16);
            }
            report["prepareMilliseconds"] = clock.ElapsedMilliseconds;
            for (int cycle = 0; cycle < 2; cycle++)
            {
                foreach (bool stage in new[] { false, true })
                {
                    for (int bucket = 0; bucket <= HoldProgressAtlas.Buckets; bucket++)
                    {
                        if (!vr.ShowHoldProgress(bucket / (float)HoldProgressAtlas.Buckets, stage, 1.2f, -.2f))
                            throw new InvalidOperationException("已加载的圆环资源意外丢失。");
                        vr.Poll();
                        if (bucket % 15 == 0)
                        {
                            var state = JsonSerializer.SerializeToElement(vr.GetHoldProgressDiagnostics());
                            var actual = state.GetProperty("bounds");
                            var expected = HoldProgressAtlas.Bounds(bucket, stage);
                            if (!state.GetProperty("visible").GetBoolean()
                                || state.GetProperty("imageError").GetString() != "None"
                                || state.GetProperty("boundsError").GetString() != "None"
                                || state.GetProperty("aspectError").GetString() != "None"
                                || state.GetProperty("width").GetUInt32() != HoldProgressAtlas.AtlasSize
                                || state.GetProperty("height").GetUInt32() != HoldProgressAtlas.AtlasSize
                                || state.GetProperty("atlasBucket").GetInt32() != bucket
                                || state.GetProperty("atlasCancelStage").GetBoolean() != stage
                                || Math.Abs(state.GetProperty("texelAspect").GetSingle() - 1) > .00001f
                                || Math.Abs(actual.GetProperty("uMin").GetSingle() - expected.uMin) > .00001f
                                || Math.Abs(actual.GetProperty("uMax").GetSingle() - expected.uMax) > .00001f
                                || Math.Abs(actual.GetProperty("vMin").GetSingle() - expected.vMin) > .00001f
                                || Math.Abs(actual.GetProperty("vMax").GetSingle() - expected.vMax) > .00001f)
                            {
                                report["failedCheckpoint"] = state;
                                report["expectedBucket"] = bucket;
                                report["expectedCancelStage"] = stage;
                                throw new InvalidOperationException("圆环纹理区域或可见性与请求不一致。");
                            }
                            checkpoints.Add(state);
                        }
                        Thread.Sleep(25);
                    }
                }
                vr.HideHoldOverlay();
                var hiddenWait = Stopwatch.StartNew();
                while (JsonSerializer.SerializeToElement(vr.GetHoldProgressDiagnostics()).GetProperty("visible").GetBoolean())
                {
                    if (hiddenWait.ElapsedMilliseconds > 500) throw new InvalidOperationException("圆环隐藏失败。");
                    vr.Poll(); Thread.Sleep(16);
                }
                report["lastHideMilliseconds"] = hiddenWait.ElapsedMilliseconds;
                Thread.Sleep(100);
            }
            report["success"] = true;
            report["cycles"] = 2;
        }
        catch (Exception error)
        {
            report["success"] = false; report["error"] = error.Message;
            Environment.ExitCode = 1;
        }
        finally
        {
            try { vr.HideHoldOverlay(); } catch { }
            report["seconds"] = clock.Elapsed.TotalSeconds;
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath, "hold-diagnostics.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
