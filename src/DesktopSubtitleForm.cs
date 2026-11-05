namespace SteamVrTranslator;

// A separate top-level window keeps subtitles visible when the workbench is in the tray.
internal sealed class DesktopSubtitleForm : Form
{
    private Bitmap? frame;

    public DesktopSubtitleForm()
    {
        Text = "无言势翻译器 · 桌面字幕";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(15, 19, 27);
        Opacity = .94;
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var style = base.CreateParams;
            // Tool window, no activation, layered + transparent for mouse click-through.
            style.ExStyle |= 0x00000080 | 0x08000000 | 0x00080000 | 0x00000020;
            return style;
        }
    }

    public void Present(Bitmap image)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? throw new InvalidOperationException("没有可用的主显示器。");
        int width = Math.Max(1, Math.Min(area.Width * 4 / 5, 960 * DeviceDpi / 96));
        int height = Math.Max(1, width * image.Height / image.Width);
        Bounds = new Rectangle(area.Left + (area.Width - width) / 2, area.Bottom - height - area.Height / 20, width, height);
        var previous = frame;
        frame = new Bitmap(image);
        previous?.Dispose();
        if (!Visible) Show();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (frame is not null) e.Graphics.DrawImage(frame, ClientRectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) frame?.Dispose();
        base.Dispose(disposing);
    }
}
