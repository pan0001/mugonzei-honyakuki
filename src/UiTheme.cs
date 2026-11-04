using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SteamVrTranslator;

internal static class UiTheme
{
    public static Color Background { get; } = Color.FromArgb(0x10, 0x15, 0x1C);
    public static Color Sidebar { get; } = Color.FromArgb(0x14, 0x1C, 0x25);
    public static Color Card { get; } = Color.FromArgb(0x1A, 0x25, 0x31);
    public static Color Input { get; } = Color.FromArgb(0x11, 0x1C, 0x27);
    public static Color Border { get; } = Color.FromArgb(0x2C, 0x3B, 0x4B);
    public static Color Ink { get; } = Color.FromArgb(0xED, 0xF4, 0xFA);
    public static Color Muted { get; } = Color.FromArgb(0x93, 0xA8, 0xBA);
    public static Color Accent { get; } = Color.FromArgb(0x72, 0xE0, 0xC1);

    internal static Color Mix(Color first, Color second, float amount) => Color.FromArgb(
        (int)Math.Round(first.R + (second.R - first.R) * amount),
        (int)Math.Round(first.G + (second.G - first.G) * amount),
        (int)Math.Round(first.B + (second.B - first.B) * amount));

    internal static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (diameter <= 0) return path;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static Color ParentBackground(Control control)
    {
        for (Control? parent = control.Parent; parent != null; parent = parent.Parent)
            if (parent.BackColor.A == 255) return parent.BackColor;
        return Background;
    }
}

internal sealed class UiButton : Button
{
    private bool primary;
    private bool selected;
    private bool hover;
    private bool pressed;

    [DefaultValue(false)]
    public bool Primary
    {
        get => primary;
        set { if (primary == value) return; primary = value; Invalidate(); }
    }

    [DefaultValue(false)]
    public bool Selected
    {
        get => selected;
        set { if (selected == value) return; selected = value; Invalidate(); }
    }

    public UiButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.Opaque, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = UiTheme.Input;
        ForeColor = UiTheme.Ink;
        Padding = new Padding(16, 8, 16, 8);
        MinimumSize = new Size(88, 38);
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Cursor = Cursors.Hand;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
        Size text = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, Size.Empty, flags);
        int width = text.Width + Padding.Horizontal + 2;
        int height = text.Height + Padding.Vertical + 2;
        if (Image is not null)
        {
            width += Image.Width + (Text.Length > 0 ? 8 : 0);
            height = Math.Max(height, Image.Height + Padding.Vertical + 2);
        }
        return new Size(Math.Max(MinimumSize.Width, width), Math.Max(MinimumSize.Height, height));
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(UiTheme.ParentBackground(this));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        var graphics = e.Graphics;
        graphics.Clear(UiTheme.ParentBackground(this));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = Primary ? UiTheme.Accent : Selected ? UiTheme.Card : UiTheme.Input;
        Color text = Primary ? UiTheme.Background : ForeColor;
        Color border = Primary ? UiTheme.Accent : Selected ? UiTheme.Mix(UiTheme.Border, UiTheme.Accent, .25f) : UiTheme.Border;
        if (!Enabled)
        {
            fill = UiTheme.Mix(fill, UiTheme.ParentBackground(this), .6f);
            border = UiTheme.Mix(UiTheme.Border, UiTheme.ParentBackground(this), .4f);
            text = UiTheme.Mix(UiTheme.Muted, fill, .35f);
        }
        else if (pressed)
        {
            fill = UiTheme.Mix(fill, Color.Black, Primary ? .12f : .18f);
            border = UiTheme.Mix(border, UiTheme.Accent, .3f);
        }
        else if (hover)
        {
            fill = UiTheme.Mix(fill, Color.White, Primary ? .08f : .045f);
            border = UiTheme.Mix(border, UiTheme.Accent, .32f);
        }
        using var shape = UiTheme.RoundedRectangle(new RectangleF(.5f, .5f, ClientSize.Width - 1, ClientSize.Height - 1), 8);
        using var brush = new SolidBrush(fill);
        using var outline = new Pen(border, 1);
        graphics.FillPath(brush, shape);
        graphics.DrawPath(outline, shape);
        if (Selected && !Primary)
        {
            using var marker = UiTheme.RoundedRectangle(new RectangleF(5, 10, 3, Math.Max(2, Height - 20)), 1.5f);
            using var accent = new SolidBrush(Enabled ? UiTheme.Accent : UiTheme.Muted);
            graphics.FillPath(accent, marker);
        }
        var textBounds = new Rectangle(Padding.Left, Padding.Top,
            Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
        if (Image is not null)
        {
            int y = (Height - Image.Height) / 2;
            if (Enabled) graphics.DrawImage(Image, Padding.Left, y, Image.Width, Image.Height);
            else ControlPaint.DrawImageDisabled(graphics, Image, Padding.Left, y, fill);
            int offset = Image.Width + (Text.Length > 0 ? 8 : 0);
            textBounds.X += offset;
            textBounds.Width = Math.Max(0, textBounds.Width - offset);
        }
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        flags |= TextAlign switch
        {
            ContentAlignment.TopLeft or ContentAlignment.MiddleLeft or ContentAlignment.BottomLeft => TextFormatFlags.Left,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter
        };
        flags |= TextAlign switch
        {
            ContentAlignment.TopLeft or ContentAlignment.TopCenter or ContentAlignment.TopRight => TextFormatFlags.Top,
            ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => TextFormatFlags.Bottom,
            _ => TextFormatFlags.VerticalCenter
        };
        if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
        else if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
        if (pressed) textBounds.Offset(0, 1);
        TextRenderer.DrawText(graphics, Text, Font, textBounds, text, flags);
        if (Focused && ShowFocusCues && Enabled)
        {
            using var focusShape = UiTheme.RoundedRectangle(new RectangleF(3.5f, 3.5f, Width - 7, Height - 7), 6);
            using var focus = new Pen(Primary ? UiTheme.Background : UiTheme.Accent, 1) { DashStyle = DashStyle.Dot };
            graphics.DrawPath(focus, focusShape);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs mevent) { if (mevent.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(mevent); }
    protected override void OnMouseUp(MouseEventArgs mevent) { pressed = false; Invalidate(); base.OnMouseUp(mevent); }
    protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) { pressed = false; Invalidate(); } base.OnMouseCaptureChanged(e); }
    protected override void OnKeyDown(KeyEventArgs kevent) { if (kevent.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(kevent); }
    protected override void OnKeyUp(KeyEventArgs kevent) { if (kevent.KeyCode == Keys.Space) { pressed = false; Invalidate(); } base.OnKeyUp(kevent); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { pressed = false; hover = false; Invalidate(); base.OnEnabledChanged(e); }
}

internal sealed class UiCard : Panel
{
    private string caption = "";
    private int headerHeight = 64;

    [DefaultValue("")]
    public string Heading { get => Text; set => Text = value ?? ""; }

    [DefaultValue("")]
    public string Caption
    {
        get => caption;
        set { caption = value ?? ""; Invalidate(); }
    }

    [DefaultValue(64)]
    public int HeaderHeight
    {
        get => headerHeight;
        set
        {
            int updated = Math.Max(0, value);
            if (headerHeight == updated) return;
            headerHeight = updated;
            Padding = new Padding(Padding.Left, headerHeight, Padding.Right, Padding.Bottom);
            Invalidate();
        }
    }

    public UiCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Card;
        ForeColor = UiTheme.Ink;
        Padding = new Padding(20, 64, 20, 20);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(UiTheme.ParentBackground(this));

    protected override void OnPaint(PaintEventArgs e)
    {
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.RoundedRectangle(new RectangleF(.5f, .5f, Width - 1, Height - 1), 12);
        using var fill = new SolidBrush(BackColor);
        using var outline = new Pen(UiTheme.Border, 1);
        graphics.FillPath(fill, shape);
        graphics.DrawPath(outline, shape);
        if (HeaderHeight > 0 && (Text.Length > 0 || Caption.Length > 0))
        {
            using var headingFont = new Font(Font.FontFamily, 11.5f, FontStyle.Bold, GraphicsUnit.Point);
            using var captionFont = new Font(Font.FontFamily, 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            var flags = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int headingHeight = TextRenderer.MeasureText("标题", headingFont, Size.Empty, flags).Height;
            int captionHeight = TextRenderer.MeasureText("说明", captionFont, Size.Empty, flags).Height;
            int x = Math.Max(20, Padding.Left);
            int y = Caption.Length == 0 ? Math.Max(8, (HeaderHeight - headingHeight) / 2) : 12;
            int availableWidth = Math.Max(0, Width - x - Math.Max(20, Padding.Right));
            TextRenderer.DrawText(graphics, Text, headingFont,
                new Rectangle(x, y, availableWidth, Math.Min(headingHeight, Math.Max(0, HeaderHeight - y))), ForeColor, flags);
            int captionY = y + headingHeight + 3;
            if (Caption.Length > 0 && captionY < HeaderHeight)
                TextRenderer.DrawText(graphics, Caption, captionFont,
                    new Rectangle(x, captionY, availableWidth, Math.Min(captionHeight, HeaderHeight - captionY)), UiTheme.Muted, flags);
        }
        base.OnPaint(e);
    }

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnFontChanged(EventArgs e) { Invalidate(); base.OnFontChanged(e); }
}

internal sealed class UiComboBox : ComboBox
{
    private bool hover;

    public UiComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        BackColor = UiTheme.Input;
        ForeColor = UiTheme.Ink;
        ItemHeight = 28;
        IntegralHeight = false;
        DropDownHeight = 240;
        MinimumSize = new Size(120, 34);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // The collapsed field has its own border; retain the native combo/list behavior.
            parameters.Style &= ~0x00800000; // WS_BORDER
            parameters.ExStyle &= ~0x00000200; // WS_EX_CLIENTEDGE
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        int message = m.Msg;
        IntPtr targetDc = m.WParam;
        base.WndProc(ref m);
        if (IsDisposed || !IsHandleCreated || DropDownStyle != ComboBoxStyle.DropDownList) return;
        if ((message == 0x0317 || message == 0x0318) && targetDc != IntPtr.Zero)
        {
            // DrawToBitmap supplies its own DC through WM_PRINT/WM_PRINTCLIENT.
            using var graphics = Graphics.FromHdc(targetDc);
            PaintCollapsedField(graphics);
        }
        else if (message == 0x000F)
        {
            using var graphics = targetDc != IntPtr.Zero ? Graphics.FromHdc(targetDc) : Graphics.FromHwnd(Handle);
            PaintCollapsedField(graphics);
        }
    }

    private void PaintCollapsedField(Graphics graphics)
    {
        if (Width < 2 || Height < 2) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var parent = new SolidBrush(UiTheme.ParentBackground(this));
        graphics.FillRectangle(parent, ClientRectangle);
        using var shape = UiTheme.RoundedRectangle(new RectangleF(.5f, .5f, Width - 1, Height - 1), 7);
        using var fill = new SolidBrush(BackColor);
        Color borderColor = Enabled && (Focused || DroppedDown) ? UiTheme.Accent
            : Enabled && hover ? UiTheme.Mix(UiTheme.Border, UiTheme.Accent, .3f) : UiTheme.Border;
        using var border = new Pen(borderColor, 1);
        graphics.FillPath(fill, shape);
        graphics.DrawPath(border, shape);
        int arrowWidth = Math.Max(30, (int)Math.Round(34 * DeviceDpi / 96f));
        bool rtl = RightToLeft == RightToLeft.Yes;
        int arrowLeft = rtl ? 1 : Math.Max(1, Width - arrowWidth);
        float centerX = arrowLeft + arrowWidth / 2f;
        float centerY = Height / 2f;
        Color ink = Enabled ? ForeColor : UiTheme.Muted;
        using var arrow = new Pen(Enabled ? UiTheme.Muted : UiTheme.Mix(UiTheme.Muted, BackColor, .4f), 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLines(arrow,
        [
            new PointF(centerX - 4, centerY - 2),
            new PointF(centerX, centerY + 2),
            new PointF(centerX + 4, centerY - 2)
        ]);
        string text = (SelectedIndex >= 0 ? GetItemText(SelectedItem) : Text) ?? "";
        var bounds = new Rectangle(rtl ? arrowWidth + 8 : 12, 1, Math.Max(0, Width - arrowWidth - 20), Math.Max(0, Height - 2));
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        flags |= rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left;
        TextRenderer.DrawText(graphics, text, Font, bounds, ink, flags);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiNativeTheme.ApplyDarkControl(this);
    }

    protected override void OnDropDown(EventArgs e) { base.OnDropDown(e); UiNativeTheme.ApplyDarkControl(this); Invalidate(); }
    protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; base.OnMouseLeave(e); Invalidate(); }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool isSelected = (e.State & DrawItemState.Selected) != 0;
        bool isCollapsed = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool isDisabled = !Enabled || (e.State & DrawItemState.Disabled) != 0;
        Color background = isSelected && !isCollapsed && !isDisabled
            ? UiTheme.Mix(UiTheme.Card, UiTheme.Accent, .15f) : BackColor;
        Color foreground = isDisabled ? UiTheme.Muted : ForeColor;
        using var fill = new SolidBrush(background);
        e.Graphics.FillRectangle(fill, e.Bounds);
        string text = (e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text) ?? "";
        var bounds = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 20), e.Bounds.Height);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft | TextFormatFlags.Right;
        TextRenderer.DrawText(e.Graphics, text, Font, bounds, foreground, flags);
        if ((e.State & DrawItemState.Focus) != 0 && ShowFocusCues && !isDisabled)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -2, -2), UiTheme.Accent, background);
        base.OnDrawItem(e);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ItemHeight = Math.Max(28, Font.Height + 10);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

/// <summary>Best-effort per-window dark theme; never changes Windows-wide appearance settings.</summary>
internal static class UiNativeTheme
{
    public static void ApplyDarkWindow(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsDisposed) return;
        form.HandleCreated -= OnFormHandleCreated;
        form.HandleCreated += OnFormHandleCreated;
        if (form.IsHandleCreated) SetDarkTitleBar(form.Handle);
        ApplyDarkControl(form);
    }

    public static void ApplyDarkControl(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (control.IsDisposed) return;
        control.HandleCreated -= OnControlHandleCreated;
        control.HandleCreated += OnControlHandleCreated;
        control.ControlAdded -= OnControlAdded;
        control.ControlAdded += OnControlAdded;
        if (control.IsHandleCreated) SetDarkControl(control);
        foreach (Control child in control.Controls) ApplyDarkControl(child);
    }

    private static void OnFormHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Form { IsDisposed: false, IsHandleCreated: true } form) SetDarkTitleBar(form.Handle);
    }

    private static void OnControlHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Control { IsDisposed: false, IsHandleCreated: true } control) SetDarkControl(control);
    }

    private static void OnControlAdded(object? sender, ControlEventArgs e)
    {
        if (e.Control is not null) ApplyDarkControl(e.Control);
    }

    private static void SetDarkTitleBar(IntPtr handle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        try
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) < 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int caption = ColorTranslator.ToWin32(UiTheme.Background);
                int text = ColorTranslator.ToWin32(UiTheme.Ink);
                DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
                DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static void SetDarkControl(Control control)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        // Native themes are only needed for system-painted fields and scrollbars.
        // Applying them to our painted buttons adds a native non-client frame.
        if (control is UiButton or UiCard or UiPages or UiSubtitlePreview) return;
        try
        {
            SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
            if (control is ComboBox)
            {
                var information = new ComboBoxInfo { Size = Marshal.SizeOf<ComboBoxInfo>() };
                if (GetComboBoxInfo(control.Handle, ref information))
                {
                    if (information.List != IntPtr.Zero) SetWindowTheme(information.List, "DarkMode_Explorer", null);
                    if (information.Item != IntPtr.Zero) SetWindowTheme(information.Item, "DarkMode_Explorer", null);
                }
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        public int Size;
        public NativeRect ItemRectangle;
        public NativeRect ButtonRectangle;
        public uint ButtonState;
        public IntPtr Combo;
        public IntPtr Item;
        public IntPtr List;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetWindowTheme(IntPtr window, string? subAppName, string? subIdList);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr combo, ref ComboBoxInfo information);
}

/// <summary>Native tab-page navigation with the tab strip covered by its full-size selected page.</summary>
internal sealed class UiPages : TabControl
{
    private const int TcmAdjustRect = 0x1328;

    public UiPages()
    {
        Appearance = TabAppearance.FlatButtons;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(1, 1);
        Padding = Point.Empty;
        Multiline = true;
        TabStop = false;
    }

    public override Rectangle DisplayRectangle => new(0, 0, ClientSize.Width, ClientSize.Height);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == TcmAdjustRect)
        {
            // Leave the input RECT unchanged: pages occupy the whole client area, without a header inset.
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var selected = SelectedTab;
        if (selected is not null && selected.Bounds != DisplayRectangle) selected.Bounds = DisplayRectangle;
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        if (SelectedTab is { } selected && selected.Bounds != DisplayRectangle) selected.Bounds = DisplayRectangle;
        Invalidate();
    }
}
