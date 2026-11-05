using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SteamVrTranslator;

/// <summary>Replaces the caption only. Windows keeps its resize frame, system menu and window placement.</summary>
internal static class WindowChrome
{
    public static void Apply(Form form)
    {
        form.Icon = UiTheme.AppIcon;
        if (!form.ControlBox || SystemInformation.HighContrast || form.Controls.OfType<Caption>().Any()) return;
        var caption = new Caption(form);
        form.Controls.Add(caption);
        // Dock outside the existing Fill container without reparenting any controls.
        caption.SendToBack();
        var frame = new CaptionFrame(form, caption);
        form.HandleCreated += (_, _) => frame.Attach();
        form.HandleDestroyed += (_, _) => frame.Detach();
        if (form.IsHandleCreated) frame.Attach();
    }

    private const int GwlStyle = -16, WsCaption = 0x00C00000, WmStyleChanging = 0x7C;
    private const int WmNcHitTest = 0x84, WmNcMouseMove = 0xA0, WmNcMouseLeave = 0x2A2;
    private const int WmNcLeftDown = 0xA1, WmNcLeftUp = 0xA2, WmSysCommand = 0x112;
    private const int HtTransparent = -1, HtCaption = 2, HtMaxButton = 9;
    private const int ScMinimize = 0xF020, ScMaximize = 0xF030, ScClose = 0xF060, ScRestore = 0xF120;

    private sealed class CaptionFrame(Form form, Caption caption) : NativeWindow
    {
        private bool maxPressed;

        internal void Attach()
        {
            if (Handle != IntPtr.Zero) return;
            AssignHandle(form.Handle);
            // Keep WS_THICKFRAME, WS_SYSMENU and the owner's minimize/maximize flags.
            // Removing WS_CAPTION also prevents Windows from printing a duplicate caption over the client.
            SetWindowLong(Handle, GwlStyle, GetWindowLong(Handle, GwlStyle) & ~WsCaption);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int border = ColorTranslator.ToWin32(UiTheme.Border);
                _ = DwmSetWindowAttribute(Handle, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
            }
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x37);
        }

        internal void Detach()
        {
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmStyleChanging && m.WParam == GwlStyle && m.LParam != IntPtr.Zero)
            {
                // WinForms may reapply the original style when updating other window properties.
                int style = Marshal.ReadInt32(m.LParam, sizeof(int));
                Marshal.WriteInt32(m.LParam, sizeof(int), style & ~WsCaption);
            }
            if (m.Msg == WmNcHitTest && caption.IsHandleCreated)
            {
                // Signed coordinates preserve hit testing on monitors to the left/above the primary.
                long packed = m.LParam.ToInt64();
                var location = caption.PointToClient(new Point((short)packed, (short)(packed >> 16)));
                if (caption.ClientRectangle.Contains(location))
                {
                    m.Result = (IntPtr)(caption.OnMaximize(location) ? HtMaxButton : HtCaption);
                    return;
                }
            }
            if (m.Msg == WmNcMouseMove)
            {
                bool hover = m.WParam == HtMaxButton;
                if (caption.MaxHover != hover)
                {
                    caption.MaxHover = hover;
                    if (hover)
                    {
                        var tracking = new TrackMouse { Size = (uint)Marshal.SizeOf<TrackMouse>(), Flags = 0x12, Hwnd = Handle };
                        TrackMouseEvent(ref tracking);
                    }
                }
            }
            if (m.Msg == WmNcMouseLeave) caption.MaxHover = maxPressed = false;
            if (m.WParam == HtMaxButton && (m.Msg == WmNcLeftDown || m.Msg == WmNcLeftUp))
            {
                if (m.Msg == WmNcLeftDown) maxPressed = true;
                else if (maxPressed)
                {
                    maxPressed = false;
                    caption.ToggleMaximize();
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }
    }

    private sealed class Caption : Control
    {
        private readonly Form window;
        private readonly CaptionButton minimize, maximize, close;

        internal Caption(Form form)
        {
            window = form;
            Name = "windowCaption";
            AccessibleRole = AccessibleRole.TitleBar;
            Dock = DockStyle.Top;
            BackColor = UiTheme.Sidebar;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            minimize = new CaptionButton("最小化", ScMinimize);
            maximize = new CaptionButton("最大化", ScMaximize);
            close = new CaptionButton("关闭", ScClose);
            minimize.Click += (_, _) => Command(ScMinimize);
            maximize.Click += (_, _) => ToggleMaximize();
            close.Click += (_, _) => Command(ScClose);
            Controls.AddRange([minimize, maximize, close]);
            Height = LogicalToDeviceUnits(36);
            window.Activated += (_, _) => Invalidate();
            window.Deactivate += (_, _) => Invalidate();
            window.TextChanged += (_, _) => Invalidate();
            window.Resize += (_, _) => UpdateButtons();
            UpdateButtons();
        }

        [DefaultValue(false)]
        internal bool MaxHover { get => maximize.NativeHover; set => maximize.NativeHover = value; }
        internal bool OnMaximize(Point p) => maximize.Visible && maximize.Bounds.Contains(p);
        internal void ToggleMaximize() => Command(window.WindowState == FormWindowState.Maximized ? ScRestore : ScMaximize);
        private void Command(int command) => PostMessage(window.Handle, WmSysCommand, (IntPtr)command, IntPtr.Zero);

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            Height = LogicalToDeviceUnits(36);
            UpdateButtons();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (close is not null) UpdateButtons();
        }

        private void UpdateButtons()
        {
            if (IsDisposed || window.IsDisposed) return;
            int x = ClientSize.Width;
            int width = LogicalToDeviceUnits(46);
            foreach (var button in new[] { close, maximize, minimize })
            {
                bool visible = button == close || (button == minimize ? window.MinimizeBox : window.MaximizeBox);
                button.Visible = visible;
                if (!visible) continue;
                x -= width;
                button.SetBounds(x, 0, width, Math.Max(0, Height - 1));
            }
            bool restored = window.WindowState == FormWindowState.Maximized;
            maximize.Restore = restored;
            maximize.AccessibleName = restored ? "还原" : "最大化";
            Invalidate(true);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcHitTest) { m.Result = (IntPtr)HtTransparent; return; }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int x = LogicalToDeviceUnits(16), marker = LogicalToDeviceUnits(15);
            float scale = DeviceDpi / 96f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var stroke = new Pen(UiTheme.Accent, Math.Max(1, 1.5f * scale)) { LineJoin = LineJoin.Round };
            float y = (Height - marker) / 2f;
            e.Graphics.DrawLines(stroke, [new PointF(x + marker, y + marker * .72f), new PointF(x + marker, y),
                new PointF(x, y), new PointF(x, y + marker * .72f), new PointF(x + marker * .26f, y + marker * .72f),
                new PointF(x + marker * .26f, y + marker), new PointF(x + marker * .55f, y + marker * .72f),
                new PointF(x + marker, y + marker * .72f)]);
            int left = x + marker + LogicalToDeviceUnits(10);
            int right = minimize.Visible ? minimize.Left : maximize.Visible ? maximize.Left : close.Left;
            TextRenderer.DrawText(e.Graphics, window.Text, Font, new Rectangle(left, 0, Math.Max(0, right - left), Height),
                window.ContainsFocus ? UiTheme.Ink : UiTheme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using var separator = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(separator, 0, Height - 1, Width, Height - 1);
        }
    }

    private sealed class CaptionButton : Button
    {
        private readonly int command;
        private bool hovered, nativeHover;

        internal CaptionButton(string name, int command)
        {
            this.command = command;
            AccessibleName = name;
            AccessibleRole = AccessibleRole.PushButton;
            TabStop = false;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        [DefaultValue(false)] internal bool Restore { get; set; }
        [DefaultValue(false)] internal bool NativeHover { get => nativeHover; set { nativeHover = value; Invalidate(); } }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

        protected override void WndProc(ref Message m)
        {
            // Route maximize through the top-level HWND so Windows 11 can offer its snap-layout menu.
            if (command == ScMaximize && m.Msg == WmNcHitTest) { m.Result = (IntPtr)HtTransparent; return; }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            bool highlight = nativeHover || hovered;
            var fill = highlight ? command == ScClose ? Color.FromArgb(180, 50, 60) : UiTheme.Card : UiTheme.Sidebar;
            e.Graphics.Clear(fill);
            var g = e.Graphics;
            g.ScaleTransform(DeviceDpi / 96f, DeviceDpi / 96f);
            float cx = Width * 48f / DeviceDpi, cy = Height * 48f / DeviceDpi;
            using var pen = new Pen(highlight ? Color.White : UiTheme.Ink, 1);
            if (command == ScMinimize) g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
            else if (command == ScClose)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawLine(pen, cx - 4, cy - 4, cx + 4, cy + 4);
                g.DrawLine(pen, cx + 4, cy - 4, cx - 4, cy + 4);
            }
            else if (Restore)
            {
                g.DrawRectangle(pen, cx - 5, cy - 3, 8, 8);
                g.DrawLines(pen, [new PointF(cx - 3, cy - 4), new PointF(cx - 3, cy - 5),
                    new PointF(cx + 5, cy - 5), new PointF(cx + 5, cy + 3), new PointF(cx + 4, cy + 3)]);
            }
            else g.DrawRectangle(pen, cx - 4.5f, cy - 4.5f, 9, 9);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouse { public uint Size, Flags; public IntPtr Hwnd; public uint Time; }
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouse tracking);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
