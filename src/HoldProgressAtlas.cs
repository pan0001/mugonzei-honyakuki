using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Valve.VR;

namespace SteamVrTranslator;

/// <summary>
/// Pre-rendered hold animation. The owner controls its lifetime; the borrowed bitmap must not
/// be modified or disposed. Both stages share one immutable texture with a nine-MiB pixel buffer.
/// </summary>
internal sealed class HoldProgressAtlas : IDisposable
{
    public const int Buckets = 60;
    public const int TileSize = 128;
    public const int GridSize = 12;
    public const int AtlasSize = TileSize * GridSize;

    private readonly Bitmap texture;
    private int disposed;

    private HoldProgressAtlas(Bitmap texture) => this.texture = texture;

    /// <summary>Creates both stages synchronously. The owner may cache Task.Run(Create) for background preparation.</summary>
    public static HoldProgressAtlas Create() => new(CreateTexture());

    /// <summary>Returns a borrowed, immutable-by-contract bitmap. The caller must not dispose it.</summary>
    public Bitmap GetTexture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return texture;
    }

    /// <summary>Returns the nearest frame, preserving exact bucket/60 values despite float rounding.</summary>
    public static int FrameIndex(float progress)
    {
        if (float.IsNaN(progress) || progress <= 0) return 0;
        if (progress >= 1) return Buckets;
        return Math.Clamp((int)MathF.Round(progress * Buckets), 0, Buckets);
    }

    /// <summary>Samples between the first and last texel centers, inside a single tile.</summary>
    public static VRTextureBounds_t Bounds(int bucket, bool cancelStage)
    {
        int frame = Math.Clamp(bucket, 0, Buckets) + (cancelStage ? Buckets + 1 : 0);
        int left = frame % GridSize * TileSize;
        int top = frame / GridSize * TileSize;
        return new VRTextureBounds_t
        {
            uMin = (left + .5f) / AtlasSize,
            vMin = (top + .5f) / AtlasSize,
            uMax = (left + TileSize - .5f) / AtlasSize,
            vMax = (top + TileSize - .5f) / AtlasSize
        };
    }

    private static Bitmap CreateTexture()
    {
        var atlas = new Bitmap(AtlasSize, AtlasSize, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(atlas);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            for (int index = 0; index < 2 * (Buckets + 1); index++)
            {
                bool cancelStage = index > Buckets;
                int bucket = index % (Buckets + 1);
                using var frame = OverlayRenderer.RenderHoldProgress(bucket / (float)Buckets, cancelStage);
                var tile = new Rectangle(index % GridSize * TileSize, index / GridSize * TileSize, TileSize, TileSize);
                var saved = graphics.Save();
                try
                {
                    graphics.SetClip(tile);
                    graphics.DrawImage(frame, tile, 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel, attributes);
                }
                finally { graphics.Restore(saved); }
            }
            return atlas;
        }
        catch { atlas.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        texture.Dispose();
    }
}
