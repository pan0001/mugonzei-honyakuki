using System.Drawing;
using System.Globalization;
using System.Security.Cryptography;

namespace SteamVrTranslator;

public static class ApiVisionProbeTests
{
    public static void Run()
    {
        byte[] image = ApiVisionProbe.CreateImage();
        VisionImage.ValidatePng(image);
        Check(image[25] == 2, "Synthetic image is RGB PNG");
        Check(image.Length < VisionImage.MaxBytes, "Synthetic image fits upload bounds");
        using var stream = new MemoryStream(image);
        using var bitmap = new Bitmap(stream);
        Check(bitmap.Width == 800 && bitmap.Height == 300, "Fixture dimensions");
        Check(bitmap.GetPixel(10, 10).ToArgb() == Color.White.ToArgb(), "Fixture white background");
        Color circle = bitmap.GetPixel(665, 153);
        Check(circle.B > circle.R + 100 && circle.B > circle.G + 50, "Fixture includes a clearly blue circle");
        Check(SHA256.HashData(image).SequenceEqual(SHA256.HashData(ApiVisionProbe.CreateImage())), "Synthetic test is stable, not a live screenshot");
        using var ocr = new OcrService();
        string recognized = ocr.Recognize(bitmap, "eng");
        Check(recognized.Contains("42", StringComparison.Ordinal) && recognized.Contains("VISION", StringComparison.OrdinalIgnoreCase), "Actual local OCR reads synthetic vision marker: " + recognized);

        string passed = ApiVisionProbe.DescribeResult("  图中有 VISION TEST、数字42和蓝色圆形。 \n");
        Check(passed.StartsWith("图片测试通过：", StringComparison.Ordinal), "Read marker reports success");
        Check(passed.EndsWith("图中有 VISION TEST、数字42和蓝色圆形。", StringComparison.Ordinal), "Result snippet is trimmed");
        string uncertain = ApiVisionProbe.DescribeResult("图中有一个蓝色圆形。");
        Check(uncertain.Contains("未读到测试编号42", StringComparison.Ordinal) && !uncertain.StartsWith("图片测试通过", StringComparison.Ordinal), "Missing marker does not claim success");
        foreach (string empty in new[] { "", " \r\n\t ", null! })
        {
            string description = ApiVisionProbe.DescribeResult(empty);
            Check(description.Contains("服务没有返回内容", StringComparison.Ordinal) && !description.Contains("通过", StringComparison.Ordinal), "Empty result has a localized failure");
        }

        string longText = "42 " + string.Concat(Enumerable.Repeat("👩‍💻e\u0301世界", 90));
        string longDescription = ApiVisionProbe.DescribeResult(longText);
        string snippet = longDescription[(longDescription.IndexOf('\n') + 1)..];
        Check(snippet.Length <= 400 && snippet.EndsWith('…'), "Result snippet is bounded with an ellipsis");
        string prefix = snippet[..^1];
        Check(longText.StartsWith(prefix, StringComparison.Ordinal), "Snippet preserves original text");
        Check(StringInfo.ParseCombiningCharacters(longText).Contains(prefix.Length), "Snippet stops at a complete grapheme boundary");
        string exact = new('字', 400);
        string exactDescription = ApiVisionProbe.DescribeResult(exact);
        Check(exactDescription.EndsWith(exact, StringComparison.Ordinal), "Exactly400 characters are retained without truncation");
        Console.WriteLine("PASS synthetic API vision fixture, local OCR marker and bounded localized result descriptions");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("ApiVisionProbeTests: " + message);
    }
}
