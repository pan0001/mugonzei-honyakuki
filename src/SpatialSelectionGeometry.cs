using System.Drawing.Imaging;
using System.Numerics;
using Valve.VR;

namespace SteamVrTranslator;

/// <summary>A rectangle in one SteamVR tracking universe, never a game-object transform.</summary>
public readonly record struct SpatialAnchor(HmdMatrix34_t Transform, float Width, float Height,
    ETrackingUniverseOrigin Origin, long Epoch)
{
    public float Aspect => Width / Height;
}

public readonly record struct SpatialSelection(SpatialAnchor Anchor, long GestureId);

/// <summary>OpenVR row-major matrices acting on column vectors; no Numerics matrix transpose ambiguity.</summary>
internal static class SpatialSelectionGeometry
{
    internal static Vector3 Position(HmdMatrix34_t m) => new(m.m3, m.m7, m.m11);
    internal static Vector3 Right(HmdMatrix34_t m) => new(m.m0, m.m4, m.m8);
    internal static Vector3 Up(HmdMatrix34_t m) => new(m.m1, m.m5, m.m9);
    internal static Vector3 Back(HmdMatrix34_t m) => new(m.m2, m.m6, m.m10);
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

    internal static bool IsRigid(HmdMatrix34_t m)
    {
        var r = Right(m); var u = Up(m); var n = Back(m);
        return Finite(Position(m)) && Finite(r) && Finite(u) && Finite(n)
            && MathF.Abs(r.LengthSquared() - 1) < .03f && MathF.Abs(u.LengthSquared() - 1) < .03f
            && MathF.Abs(n.LengthSquared() - 1) < .03f && MathF.Abs(Vector3.Dot(r, u)) < .02f
            && MathF.Abs(Vector3.Dot(r, n)) < .02f && MathF.Abs(Vector3.Dot(u, n)) < .02f
            && Vector3.Dot(Vector3.Cross(r, u), n) > .97f;
    }

    internal static bool IsValidAnchor(SpatialAnchor anchor) => IsRigid(anchor.Transform)
        && float.IsFinite(anchor.Width) && float.IsFinite(anchor.Height)
        && anchor.Width >= .08f && anchor.Width <= 3 && anchor.Height >= .08f && anchor.Height <= 3
        && anchor.Aspect >= .2f && anchor.Aspect <= 5;

    internal static Vector3 TransformPoint(HmdMatrix34_t m, Vector3 p) => Position(m)
        + Right(m) * p.X + Up(m) * p.Y + Back(m) * p.Z;

    internal static Vector3 InversePoint(HmdMatrix34_t m, Vector3 p)
    {
        Vector3 delta = p - Position(m);
        return new(Vector3.Dot(delta, Right(m)), Vector3.Dot(delta, Up(m)), Vector3.Dot(delta, Back(m)));
    }

    internal static HmdMatrix34_t Multiply(HmdMatrix34_t a, HmdMatrix34_t b)
    {
        Vector3 Rotate(Vector3 v) => Right(a) * v.X + Up(a) * v.Y + Back(a) * v.Z;
        var r = Rotate(Right(b)); var u = Rotate(Up(b)); var n = Rotate(Back(b));
        var p = TransformPoint(a, Position(b));
        return new HmdMatrix34_t { m0 = r.X, m1 = u.X, m2 = n.X, m3 = p.X,
            m4 = r.Y, m5 = u.Y, m6 = n.Y, m7 = p.Y, m8 = r.Z, m9 = u.Z, m10 = n.Z, m11 = p.Z };
    }

    internal static HmdMatrix34_t CreatePlane(HmdMatrix34_t head, float distance)
    {
        if (!IsRigid(head) || !float.IsFinite(distance) || distance < .4f || distance > 4)
            throw new InvalidOperationException("头显定位或框选距离无效，请重新定位后再框选。");
        var p = Position(head) - Back(head) * distance;
        head.m3 = p.X; head.m7 = p.Y; head.m11 = p.Z;
        return head;
    }

    internal static bool TryRayPoint(HmdMatrix34_t pointer, HmdMatrix34_t plane, out Vector2 point)
    {
        point = default;
        if (!IsRigid(pointer) || !IsRigid(plane)) return false;
        Vector3 direction = -Back(pointer), normal = Back(plane);
        float denominator = Vector3.Dot(direction, normal);
        if (denominator > -.02f) return false;
        float t = Vector3.Dot(Position(plane) - Position(pointer), normal) / denominator;
        if (!float.IsFinite(t) || t <= .01f || t > 10) return false;
        Vector3 local = InversePoint(plane, Position(pointer) + direction * t);
        point = new(local.X, local.Y);
        return Finite(point) && MathF.Abs(point.X) <= 8 && MathF.Abs(point.Y) <= 8;
    }

    internal static bool TryCreateAnchor(HmdMatrix34_t plane, Vector2 a, Vector2 b,
        ETrackingUniverseOrigin origin, long epoch, out SpatialAnchor anchor)
    {
        anchor = default;
        if (!IsRigid(plane) || !Finite(a) || !Finite(b)) return false;
        Vector3 center = TransformPoint(plane, new((a.X + b.X) / 2, (a.Y + b.Y) / 2, 0));
        plane.m3 = center.X; plane.m7 = center.Y; plane.m11 = center.Z;
        var candidate = new SpatialAnchor(plane, MathF.Abs(a.X - b.X), MathF.Abs(a.Y - b.Y), origin, epoch);
        if (!IsValidAnchor(candidate)) return false;
        anchor = candidate;
        return true;
    }

    internal static Vector2 Project(Vector3 world, HmdMatrix34_t eyeToWorld, HmdMatrix44_t projection)
    {
        Vector3 p = InversePoint(eyeToWorld, world);
        float x = projection.m0 * p.X + projection.m1 * p.Y + projection.m2 * p.Z + projection.m3;
        float y = projection.m4 * p.X + projection.m5 * p.Y + projection.m6 * p.Z + projection.m7;
        float w = projection.m12 * p.X + projection.m13 * p.Y + projection.m14 * p.Z + projection.m15;
        if (p.Z >= -.01f || !float.IsFinite(w) || w <= .001f || !float.IsFinite(x) || !float.IsFinite(y))
            throw new InvalidOperationException("选区位于视野后方或定位无效，请面对整个框后重试。");
        return new((x / w + 1) / 2, (1 - y / w) / 2);
    }

    /// <summary>Rectifies exactly the frozen world rectangle, rather than the surrounding image bounding box.</summary>
    internal static unsafe Bitmap Crop(Bitmap image, SpatialAnchor anchor, HmdMatrix34_t eyeToWorld, HmdMatrix44_t projection)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!IsValidAnchor(anchor) || !IsRigid(eyeToWorld))
            throw new InvalidOperationException("选区定位已失效，请重新框选。");
        if (image.PixelFormat != PixelFormat.Format32bppArgb)
        {
            using var converted = image.Clone(new Rectangle(0, 0, image.Width, image.Height), PixelFormat.Format32bppArgb);
            return Crop(converted, anchor, eyeToWorld, projection);
        }
        Vector3 topLeft = TransformPoint(anchor.Transform, new(-anchor.Width / 2, anchor.Height / 2, 0));
        Vector3 dx = Right(anchor.Transform) * anchor.Width, dy = -Up(anchor.Transform) * anchor.Height;
        Vector2 Pixel(Vector3 world)
        {
            var uv = Project(world, eyeToWorld, projection);
            if (!Finite(uv) || uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1)
                throw new InvalidOperationException("整个选区必须在左眼视野内，请面对框并缩小范围后重试。");
            return new(uv.X * image.Width, uv.Y * image.Height);
        }
        var tl = Pixel(topLeft); var tr = Pixel(topLeft + dx);
        var bl = Pixel(topLeft + dy); var br = Pixel(topLeft + dx + dy);
        float horizontal = MathF.Max(Vector2.Distance(tl, tr), Vector2.Distance(bl, br));
        float vertical = MathF.Max(Vector2.Distance(tl, bl), Vector2.Distance(tr, br));
        if (horizontal < 16 || vertical < 16)
            throw new InvalidOperationException("选区太小，无法读取清晰文字，请放大框或靠近文字。");
        float width = MathF.Max(horizontal, vertical * anchor.Aspect), height = width / anchor.Aspect;
        float scale = MathF.Min(1, MathF.Min(2560 / MathF.Max(width, height), MathF.Sqrt(4_000_000 / (width * height))));
        int outWidth = Math.Max(16, (int)MathF.Floor(width * scale));
        int outHeight = Math.Max(16, (int)MathF.Floor(height * scale));
        // Floating-point rounding near the area limit must not overflow the PNG preparation bound.
        if ((long)outWidth * outHeight > 4_000_000) outHeight = 4_000_000 / outWidth;
        var result = new Bitmap(outWidth, outHeight, PixelFormat.Format32bppArgb);
        BitmapData? input = null, output = null;
        try
        {
            input = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            output = result.LockBits(new Rectangle(0, 0, outWidth, outHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < outHeight; y++)
            {
                byte* row = (byte*)output.Scan0 + y * output.Stride;
                Vector3 rowStart = topLeft + dy * ((y + .5f) / outHeight);
                for (int x = 0; x < outWidth; x++)
                {
                    var pixel = Pixel(rowStart + dx * ((x + .5f) / outWidth));
                    float sx = Math.Clamp(pixel.X - .5f, 0, image.Width - 1), sy = Math.Clamp(pixel.Y - .5f, 0, image.Height - 1);
                    int ix = (int)sx, iy = (int)sy;
                    int ix2 = Math.Min(ix + 1, image.Width - 1), iy2 = Math.Min(iy + 1, image.Height - 1);
                    float fx = sx - ix, fy = sy - iy;
                    byte* a = (byte*)input.Scan0 + iy * input.Stride + ix * 4;
                    byte* b = (byte*)input.Scan0 + iy * input.Stride + ix2 * 4;
                    byte* c = (byte*)input.Scan0 + iy2 * input.Stride + ix * 4;
                    byte* d = (byte*)input.Scan0 + iy2 * input.Stride + ix2 * 4;
                    for (int channel = 0; channel < 3; channel++)
                        row[x * 4 + channel] = (byte)Math.Clamp((int)MathF.Round(
                            (a[channel] * (1 - fx) + b[channel] * fx) * (1 - fy)
                            + (c[channel] * (1 - fx) + d[channel] * fx) * fy), 0, 255);
                    row[x * 4 + 3] = 255;
                }
            }
            result.UnlockBits(output); output = null;
            image.UnlockBits(input); input = null;
            return result;
        }
        catch
        {
            if (output is not null) result.UnlockBits(output);
            result.Dispose();
            throw;
        }
        finally { if (input is not null) image.UnlockBits(input); }
    }
}
