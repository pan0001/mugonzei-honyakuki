using System.Drawing.Imaging;

namespace SteamVrTranslator;

internal sealed partial class MainForm
{
    private void SaveCurrentScreenshot()
    {
        if (preview.Image is null) { SetStatus("还没有可以保存的截图。"); return; }
        // Keep the exact image selected when the dialog opens, even if a controller
        // capture replaces the preview while the modal dialog pumps UI messages.
        using var snapshot = new Bitmap(preview.Image);
        using var dialog = new SaveFileDialog
        {
            Title = "保存当前原始截图",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = "png",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"VR截图-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        snapshot.Save(dialog.FileName, ImageFormat.Png);
        SetStatus($"已保存当前截图 · {snapshot.Width} × {snapshot.Height}；可用它复查错字和漏字。");
    }
}
