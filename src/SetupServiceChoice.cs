using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace SteamVrTranslator;

/// <summary>A keyboard-accessible radio option with a compact service description.</summary>
internal sealed class SetupServiceChoice : RadioButton
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Description { get; init; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Number { get; init; } = "";
    private bool hovered;

    public SetupServiceChoice()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Cursor = Cursors.Hand;
        ForeColor = UiTheme.Ink;
        UseMnemonic = false;
        Margin = new Padding(0, 0, 0, 10);
        AccessibleRole = AccessibleRole.RadioButton;
    }

    public override Size GetPreferredSize(Size proposedSize) => new(
        proposedSize.Width > 0 ? proposedSize.Width : 500, (int)Math.Ceiling(72 * DeviceDpi / 96f));

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 10 || Height < 10) return;
        float scale = DeviceDpi / 96f;
        int S(float value) => (int)Math.Round(value * scale);
        var g = e.Graphics;
        g.Clear(UiTheme.ParentBackground(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = Checked ? UiTheme.Mix(UiTheme.Card, UiTheme.Accent, .08f)
            : hovered ? UiTheme.Mix(UiTheme.Card, Color.White, .025f) : UiTheme.Card;
        using var shape = UiTheme.RoundedRectangle(new RectangleF(.5f, .5f, Width - 1, Height - 1), S(12));
        using var brush = new SolidBrush(fill);
        using var outline = new Pen(Checked ? UiTheme.Accent : hovered ? UiTheme.Muted : UiTheme.Border, Checked ? S(1.5f) : 1);
        g.FillPath(brush, shape); g.DrawPath(outline, shape);

        var numberBounds = new Rectangle(S(16), S(18), S(32), S(32));
        using var numberBrush = new SolidBrush(Checked ? UiTheme.Mix(fill, UiTheme.Accent, .14f) : UiTheme.Input);
        g.FillEllipse(numberBrush, numberBounds);
        using var numberFont = new Font(Font.FontFamily, 10f, FontStyle.Bold);
        TextRenderer.DrawText(g, Number, numberFont, numberBounds, Checked ? UiTheme.Accent : UiTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        int left = S(64), right = S(46);
        using var titleFont = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        using var descriptionFont = new Font(Font.FontFamily, 9f);
        TextRenderer.DrawText(g, Text, titleFont, new Rectangle(left, S(10), Math.Max(1, Width - left - right), S(27)), UiTheme.Ink,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Description, descriptionFont, new Rectangle(left, S(36), Math.Max(1, Width - left - S(20)), Height - S(39)), UiTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        var circle = new Rectangle(Width - S(33), S(18), S(15), S(15));
        using var circlePen = new Pen(Checked ? UiTheme.Accent : UiTheme.Muted, Math.Max(1, S(1.3f)));
        g.DrawEllipse(circlePen, circle);
        if (Checked)
        {
            circle.Inflate(-S(4), -S(4));
            using var selected = new SolidBrush(UiTheme.Accent);
            g.FillEllipse(selected, circle);
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(UiTheme.Accent) { DashStyle = DashStyle.Dot };
            using var focusShape = UiTheme.RoundedRectangle(new RectangleF(S(4), S(4), Width - S(8), Height - S(8)), S(9));
            g.DrawPath(focus, focusShape);
        }
    }

    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}
