using System.Drawing.Drawing2D;

namespace SteamVrTranslator;

internal sealed class UiSubtitlePreview : Control
{
    public UiSubtitlePreview()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(600, 200);
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Ink;
        TabStop = false;
        AccessibleName = "字幕布局示意";
        AccessibleDescription = "下方显示三行翻译字幕，上方小圆环表示长按进度。这是布局示意，不是实时 VR 画面。";
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(UiTheme.ParentBackground(this));

    protected override void OnPaint(PaintEventArgs e)
    {
        if (ClientSize.Width < 40 || ClientSize.Height < 60) return;
        var graphics = e.Graphics;
        var state = graphics.Save();
        try
        {
            float scale = graphics.DpiX / 96f;
            float width = ClientSize.Width, height = ClientSize.Height;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(.5f, .5f, width - 1, height - 1);
            using var frame = UiTheme.RoundedRectangle(bounds, 10 * scale);
            graphics.SetClip(frame, CombineMode.Intersect);
            using var background = new LinearGradientBrush(bounds, UiTheme.Background,
                UiTheme.Mix(UiTheme.Input, UiTheme.Card, .35f), LinearGradientMode.Vertical);
            graphics.FillPath(background, frame);

            float horizon = height * .34f;
            float centerX = width / 2;
            using var horizonPen = new Pen(Color.FromArgb(38, UiTheme.Muted), scale);
            using var gridPen = new Pen(Color.FromArgb(16, UiTheme.Muted), scale);
            graphics.DrawLine(horizonPen, 0, horizon, width, horizon);
            for (int i = -4; i <= 4; i++)
                graphics.DrawLine(gridPen, centerX + i * width * .04f, horizon,
                    centerX + i * width * .22f, height);
            foreach (float fraction in new[] { .40f, .49f, .63f, .83f })
                graphics.DrawLine(gridPen, 0, height * fraction, width, height * fraction);

            float subtitleWidth = Math.Max(1, Math.Min(width - 32 * scale, 440 * scale));
            float subtitleHeight = Math.Min(76 * scale, height * .40f);
            var subtitle = new RectangleF((width - subtitleWidth) / 2, height - subtitleHeight - 12 * scale, subtitleWidth, subtitleHeight);
            using var subtitleShape = UiTheme.RoundedRectangle(subtitle, 9 * scale);
            using var subtitleFill = new SolidBrush(Color.FromArgb(228, UiTheme.Card));
            using var subtitleOutline = new Pen(Color.FromArgb(160, UiTheme.Border), scale);
            graphics.FillPath(subtitleFill, subtitleShape);
            graphics.DrawPath(subtitleOutline, subtitleShape);

            float ringRadius = Math.Min(10 * scale, height * .055f);
            float ringY = subtitle.Top - 22 * scale;
            var ring = new RectangleF(centerX - ringRadius, ringY - ringRadius, ringRadius * 2, ringRadius * 2);
            using var ringTrack = new Pen(Color.FromArgb(95, UiTheme.Border), 2.5f * scale);
            using var ringProgress = new Pen(UiTheme.Accent, 2.5f * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawEllipse(ringTrack, ring);
            graphics.DrawArc(ringProgress, ring, -90, 265);

            using var subtitleFont = new Font("Microsoft YaHei UI", 11, FontStyle.Regular, GraphicsUnit.Point);
            using var captionFont = new Font("Microsoft YaHei UI", 8, FontStyle.Regular, GraphicsUnit.Point);
            var textFlags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping;
            string[] sampleLines = ["欢迎来到这个世界。", "字幕最多显示三行。", "超出三行可以翻页。"];
            float lineHeight = (subtitleHeight - 8 * scale) / sampleLines.Length;
            for (int line = 0; line < sampleLines.Length; line++)
            {
                var lineBounds = new RectangleF(subtitle.Left, subtitle.Top + 4 * scale + line * lineHeight, subtitleWidth, lineHeight);
                TextRenderer.DrawText(graphics, sampleLines[line], subtitleFont,
                    Rectangle.Round(lineBounds), UiTheme.Ink, textFlags | TextFormatFlags.HorizontalCenter);
            }
            var captionBounds = new Rectangle((int)(12 * scale), (int)(10 * scale),
                Math.Max(0, (int)(width - 26 * scale)), (int)(20 * scale));
            TextRenderer.DrawText(graphics, "布局示意", captionFont, captionBounds,
                UiTheme.Muted, textFlags | TextFormatFlags.Right);
            using var border = new Pen(UiTheme.Border, scale);
            graphics.DrawPath(border, frame);
        }
        finally { graphics.Restore(state); }
        base.OnPaint(e);
    }
}

internal static class UiPreview
{
    internal static void PaintCaptureEmpty(Graphics graphics, Rectangle bounds, Font font)
    {
        if (bounds.Width < 48 || bounds.Height < 80) return;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds, CombineMode.Intersect);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = graphics.DpiX / 96f;
            int textWidth = Math.Max(1, Math.Min(bounds.Width - (int)(32 * scale), (int)(480 * scale)));
            using var headingFont = new Font(font.FontFamily, font.SizeInPoints + .5f, FontStyle.Bold, GraphicsUnit.Point);
            using var detailFont = new Font(font.FontFamily, Math.Max(8, font.SizeInPoints - 1), FontStyle.Regular, GraphicsUnit.Point);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix
                | TextFormatFlags.WordBreak | TextFormatFlags.PreserveGraphicsClipping;
            const string heading = "等待下一张截图";
            const string detail = "按截图快捷键，或点击「截屏并翻译」";
            int headingHeight = TextRenderer.MeasureText(graphics, heading, headingFont,
                new Size(textWidth, 1000), flags).Height;
            int detailHeight = TextRenderer.MeasureText(graphics, detail, detailFont,
                new Size(textWidth, 1000), flags).Height;
            float iconWidth = 42 * scale, iconHeight = 32 * scale;
            float blockHeight = iconHeight + 18 * scale + headingHeight + 7 * scale + detailHeight;
            float top = bounds.Top + Math.Max(8 * scale, (bounds.Height - blockHeight) / 2);
            float centerX = bounds.Left + bounds.Width / 2f;
            var icon = new RectangleF(centerX - iconWidth / 2, top, iconWidth, iconHeight);
            float corner = 9 * scale;
            using var viewfinder = new Pen(UiTheme.Mix(UiTheme.Muted, UiTheme.Border, .35f), 1.7f * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawLines(viewfinder, new PointF[] { new(icon.Left, icon.Top + corner), new(icon.Left, icon.Top), new(icon.Left + corner, icon.Top) });
            graphics.DrawLines(viewfinder, new PointF[] { new(icon.Right - corner, icon.Top), new(icon.Right, icon.Top), new(icon.Right, icon.Top + corner) });
            graphics.DrawLines(viewfinder, new PointF[] { new(icon.Left, icon.Bottom - corner), new(icon.Left, icon.Bottom), new(icon.Left + corner, icon.Bottom) });
            graphics.DrawLines(viewfinder, new PointF[] { new(icon.Right - corner, icon.Bottom), new(icon.Right, icon.Bottom), new(icon.Right, icon.Bottom - corner) });
            using var centerPen = new Pen(Color.FromArgb(130, UiTheme.Muted), scale);
            float centerY = icon.Top + icon.Height / 2;
            graphics.DrawLine(centerPen, centerX - 4 * scale, centerY, centerX + 4 * scale, centerY);
            graphics.DrawLine(centerPen, centerX, centerY - 4 * scale, centerX, centerY + 4 * scale);

            int textLeft = (int)(centerX - textWidth / 2f);
            int textTop = (int)(icon.Bottom + 18 * scale);
            TextRenderer.DrawText(graphics, heading, headingFont,
                new Rectangle(textLeft, textTop, textWidth, headingHeight), UiTheme.Ink, flags);
            TextRenderer.DrawText(graphics, detail, detailFont,
                new Rectangle(textLeft, textTop + headingHeight + (int)(7 * scale), textWidth, detailHeight), UiTheme.Muted, flags);
        }
        finally { graphics.Restore(state); }
    }
}
