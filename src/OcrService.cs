using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using Tesseract;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SteamVrTranslator.Tests")]

namespace SteamVrTranslator;

/// <summary>Local OCR. Call from a worker; a lock also guards engine replacement and disposal.</summary>
public sealed class OcrService : IDisposable
{
    private readonly object gate = new();
    private TesseractEngine? engine;
    private string loadedLanguages = "";
    private bool disposed;

    public string Recognize(Bitmap image, string languages)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string normalizedLanguages = NormalizeLanguages(languages);
            EnsureEngine(normalizedLanguages);
            using var prepared = PrepareImage(image);
            using var png = new MemoryStream();
            prepared.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            using var pix = Pix.LoadFromMemory(png.ToArray());
            using var page = engine!.Process(pix, PageSegMode.SparseText);
            return NormalizeText(page.GetText());
        }
    }

    private void EnsureEngine(string languages)
    {
        if (engine != null && loadedLanguages == languages) return;
        string dataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        var missing = languages.Split('+').Where(lang => !File.Exists(Path.Combine(dataPath, lang + ".traineddata"))).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"缺少 OCR 语言包：{string.Join("、", missing)}。请把对应 .traineddata 文件放到程序的 tessdata 文件夹。");
        TesseractEngine replacement;
        try
        {
            replacement = new TesseractEngine(dataPath, languages, EngineMode.LstmOnly);
            replacement.SetVariable("user_defined_dpi", "200");
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException or TesseractException)
        {
            throw new InvalidOperationException("无法启动本地 OCR。请确认语言包完整，并安装 Microsoft Visual C++ 2015–2022 x64 运行库。", ex);
        }
        engine?.Dispose();
        engine = replacement;
        loadedLanguages = languages;
    }

    internal static string NormalizeLanguages(string languages)
    {
        var values = (languages ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length == 0 || values.Length > 8 || values.Any(value => !Regex.IsMatch(value, "^[a-z][a-z0-9_]{1,24}$", RegexOptions.CultureInvariant)))
            throw new ArgumentException("OCR 语言请填写 eng、jpn、kor、chi_sim、chi_tra，多个语言用 + 连接。", nameof(languages));
        return string.Join('+', values);
    }

    internal static string NormalizeText(string? text)
    {
        // Remove control characters while preserving letters, CJK characters and paragraph order.
        var clean = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        clean = Regex.Replace(clean, "[\\p{Cc}-[\\n\\t]]", "");
        return string.Join('\n', clean.Split('\n').Select(line => Regex.Replace(line, "[\\t ]+", " ").Trim()).Where(line => line.Length > 0)).Trim();
    }

    private static Bitmap PrepareImage(Bitmap image)
    {
        const double maxDimension = 3840;
        const double maxPixels = 8_000_000;
        double largest = Math.Max(image.Width, image.Height);
        double scale = largest < 1600 ? Math.Min(2.0, 1600 / largest) : 1;
        scale = Math.Min(scale, maxDimension / largest);
        scale = Math.Min(scale, Math.Sqrt(maxPixels / ((double)image.Width * image.Height)));
        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(image, new Rectangle(0, 0, width, height));
        return result;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            engine?.Dispose();
            engine = null;
        }
    }
}
