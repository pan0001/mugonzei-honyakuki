using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;
using Valve.VR;

namespace SteamVrTranslator;

public static class SpatialSelectionGeometryTests
{
    public static void Run()
    {
        CoordinatesAndRays();
        ProjectionAndRectification();
        SettingsMigration();
        Console.WriteLine("PASS spatial selection rigid transforms, hand rays, eye offset, perspective crop, bounds and settings migration");
    }

    private static HmdMatrix34_t Identity(float x = 0, float y = 0, float z = 0) => new()
        { m0 = 1, m5 = 1, m10 = 1, m3 = x, m7 = y, m11 = z };
    private static HmdMatrix44_t Perspective() => new() { m0 = 1, m5 = 1, m10 = -1, m11 = -.1f, m14 = -1 };
    private static void Close(float actual, float expected, string message, float tolerance = .0001f)
    {
        if (MathF.Abs(actual - expected) > tolerance) throw new Exception(message + $": {actual} != {expected}");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Invalid spatial input was accepted");
    }

    private static void CoordinatesAndRays()
    {
        var plane = SpatialSelectionGeometry.CreatePlane(Identity(), 1.2f);
        Close(plane.m11, -1.2f, "Plane is forward along negative Z");
        Check(SpatialSelectionGeometry.TryRayPoint(Identity(.3f, .2f), plane, out var point), "Forward pointer hits plane");
        Close(point.X, .3f, "Pointer horizontal intersection"); Close(point.Y, .2f, "Pointer vertical intersection");
        var yaw = new HmdMatrix34_t { m2 = 1, m5 = 1, m8 = -1, m3 = 1, m7 = 2, m11 = 3 };
        var rotated = SpatialSelectionGeometry.CreatePlane(yaw, 1.2f);
        Close(rotated.m3, -.2f, "90-degree head turn moves the plane along world X");
        Close(rotated.m7, 2, "Height stays in world space"); Close(rotated.m11, 3, "Depth stays in world space");
        var worldEye = SpatialSelectionGeometry.Multiply(yaw, Identity(-.03f));
        Close(worldEye.m3, 1, "Rotated eye offset X"); Close(worldEye.m11, 3.03f, "Head-local eye offset rotates into world Z");
        Check(!SpatialSelectionGeometry.TryRayPoint(yaw, plane, out _), "A parallel ray is rejected");
        Check(!SpatialSelectionGeometry.TryRayPoint(Identity(z: -2), plane, out _), "A plane behind the pointer is rejected");
        Check(!SpatialSelectionGeometry.TryRayPoint(default, plane, out _), "Missing tracking matrix is rejected");
        Check(SpatialSelectionGeometry.TryCreateAnchor(plane, new(.4f, -.2f), new(-.4f, .2f), ETrackingUniverseOrigin.TrackingUniverseStanding, 7, out var anchor), "Crossed hands still define a rectangle");
        Close(anchor.Width, .8f, "Selected width"); Close(anchor.Height, .4f, "Selected height");
        Close(anchor.Transform.m11, -1.2f, "Frozen selection plane retained");
        Check(anchor.Epoch == 7, "Tracking universe epoch travels with the anchor");
        Check(!SpatialSelectionGeometry.TryCreateAnchor(plane, Vector2.Zero, new(.03f, .4f), anchor.Origin, 7, out _), "Tiny selection rejected");
        Check(!SpatialSelectionGeometry.TryCreateAnchor(plane, Vector2.Zero, new(2.5f, .1f), anchor.Origin, 7, out _), "Extreme aspect rejected");
        Check(!SpatialSelectionGeometry.TryCreateAnchor(plane, Vector2.Zero, new(float.NaN, 1), anchor.Origin, 7, out _), "Nonfinite selection rejected");
        Reject(() => SpatialSelectionGeometry.CreatePlane(Identity(), float.NaN));
    }

    private static void ProjectionAndRectification()
    {
        var projection = Perspective();
        var uv = SpatialSelectionGeometry.Project(new(0, 0, -1.2f), Identity(-.03f), projection);
        Close(uv.X, .5125f, "Left-eye IPD offset participates in projection"); Close(uv.Y, .5f, "Center vertical projection");
        projection.m2 = .2f;
        uv = SpatialSelectionGeometry.Project(new(0, 0, -1.2f), Identity(), projection);
        Close(uv.X, .4f, "Asymmetric eye frustum participates in projection");
        projection = Perspective();
        Reject(() => SpatialSelectionGeometry.Project(new(0, 0, 1), Identity(), projection));

        // The source is a 3-D tilted, screen-rotated panel with green outside its exact quadrilateral.
        // Bounding-box cropping would include obvious green corners; rectification must not.
        float angle = .43f, c = MathF.Cos(angle), s = MathF.Sin(angle);
        var roll = new HmdMatrix34_t { m0 = c, m1 = -s, m4 = s, m5 = c, m10 = 1 };
        float yaw = .35f;
        var turn = new HmdMatrix34_t { m0 = MathF.Cos(yaw), m2 = MathF.Sin(yaw), m5 = 1,
            m8 = -MathF.Sin(yaw), m10 = MathF.Cos(yaw), m11 = -1.5f };
        var pose = SpatialSelectionGeometry.Multiply(turn, roll);
        var anchor = new SpatialAnchor(pose, 1.1f, .65f, ETrackingUniverseOrigin.TrackingUniverseStanding, 1);
        var corners = new[] { new Vector3(-anchor.Width/2, anchor.Height/2, 0), new Vector3(anchor.Width/2, anchor.Height/2, 0),
            new Vector3(anchor.Width/2, -anchor.Height/2, 0), new Vector3(-anchor.Width/2, -anchor.Height/2, 0) };
        using var source = new Bitmap(800, 800, PixelFormat.Format32bppArgb);
        var polygon = corners.Select(p => SpatialSelectionGeometry.Project(SpatialSelectionGeometry.TransformPoint(pose, p), Identity(), projection))
            .Select(p => new PointF(p.X * source.Width, p.Y * source.Height)).ToArray();
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.Lime);
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.FillPolygon(Brushes.DarkRed, polygon);
        }
        using var crop = SpatialSelectionGeometry.Crop(source, anchor, Identity(), projection);
        Close((float)crop.Width / crop.Height, anchor.Aspect, "Rectified panel retains physical aspect ratio", .02f);
        for (int y = 3; y < crop.Height - 3; y += 9)
            for (int x = 3; x < crop.Width - 3; x += 9)
                Check(crop.GetPixel(x,y).G < 15 && crop.GetPixel(x,y).R > 90, "Rectification must exclude outside-frame green pixels");
        Check(crop.Width <= 2560 && crop.Height <= 2560 && (long)crop.Width * crop.Height <= 4_000_000, "Crop memory bound");
        string directory = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);
        source.Save(Path.Combine(directory, "selection-projection-source.png"));
        crop.Save(Path.Combine(directory, "selection-projection-crop.png"));
        var offscreen = anchor with { Transform = Identity(5, 0, -1) };
        Reject(() => SpatialSelectionGeometry.Crop(source, offscreen, Identity(), projection).Dispose());
        var tiny = anchor with { Width = .08f, Height = .08f, Transform = Identity(z: -10) };
        Reject(() => SpatialSelectionGeometry.Crop(source, tiny, Identity(), projection).Dispose());
    }

    private static void SettingsMigration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SteamVrTranslatorSpatialTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Previous settings documents omit the new fields, preserving the previous hold interaction.
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"Version\":1,\"Settings\":{},\"ProtectedApiKey\":\"\"}");
            var old = SettingsStore.Load(directory);
            Check(!old.SpatialSelectionEnabled && old.SelectionDistance == 1.2f, "Existing settings preserve old interaction");
            old.SpatialSelectionEnabled = true; old.SelectionDistance = 1.8f;
            SettingsStore.Save(old, directory);
            var saved = SettingsStore.Load(directory);
            Check(saved.SpatialSelectionEnabled && saved.SelectionDistance == 1.8f, "Selection preference roundtrips");
            saved.SelectionDistance = float.NaN;
            Reject(() => SettingsStore.Save(saved, directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
