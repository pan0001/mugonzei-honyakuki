using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text;

namespace SteamVrTranslator;

/// <summary>Creates a synthetic vision check without reading the desktop, headset or user files.</summary>
internal static class ApiVisionProbe
{
    public static byte[] CreateImage()
    {
        using var bitmap = new Bitmap(800, 300, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var heading = new Font("Arial", 48, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var number = new Font("Arial", 112, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var blue = new SolidBrush(Color.FromArgb(35, 100, 225)))
        {
            graphics.Clear(Color.White);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString("VISION TEST", heading, Brushes.Black, 40, 30);
            graphics.DrawString("42", number, Brushes.Black, 40, 110);
            graphics.FillEllipse(blue, 590, 78, 150, 150);
        }
        return VisionImage.Prepare(bitmap);
    }

    public static string DescribeResult(string result)
    {
        string text = result?.Trim() ?? "";
        if (text.Length == 0)
            return "图片测试未完成：服务没有返回内容，请检查 API 配置和视觉模型后重试。";
        string message = text.Contains("42", StringComparison.Ordinal)
            ? "图片测试通过：已读到测试编号42。"
            : "服务已返回内容，但未读到测试编号42；请确认视觉模型。";
        return message + "\n" + Snippet(text);
    }

    private static string Snippet(string text)
    {
        const int maximum = 400;
        if (text.Length <= maximum) return text;
        var snippet = new StringBuilder(maximum);
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (snippet.Length + element.Length > maximum - 1) break;
            snippet.Append(element);
        }
        return snippet.Append('…').ToString();
    }
}
