using System.Runtime.InteropServices;
using SteamVrTranslator;

internal static class WindowChromeTests
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    internal static void Run()
    {
        if (SystemInformation.HighContrast) return; // The native caption remains in charge of contrast mode.
        using var form = new Form { ShowInTaskbar = false, MinimizeBox = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(520, 360) };
        var content = new Panel { Dock = DockStyle.Fill };
        form.Controls.Add(content);
        WindowChrome.Apply(form);
        WindowChrome.Apply(form);
        form.Show();
        Application.DoEvents();
        var caption = form.Controls.Find("windowCaption", false).Single();
        var buttons = caption.Controls.OfType<Button>().ToArray();
        if (caption.Bottom != content.Top || buttons.Single(b => b.AccessibleName == "最小化").Visible)
            throw new Exception("The caption must reserve its own row and respect the window's caption buttons.");
        var maximize = buttons.Single(b => b.AccessibleName == "最大化");
        if (HitTest(form, caption.PointToScreen(new Point(25, caption.Height / 2))) != 2
            || HitTest(form, maximize.PointToScreen(new Point(maximize.Width / 2, maximize.Height / 2))) != 9
            || HitTest(form, new Point(form.Right - 1, form.Bottom - 1)) != 17)
            throw new Exception("Drag, native maximize/snap and the resize corner must remain reachable.");

        bool closing = false;
        form.FormClosing += (_, e) => { closing = true; e.Cancel = true; };
        buttons.Single(b => b.AccessibleName == "关闭").PerformClick();
        Application.DoEvents();
        if (!closing || form.IsDisposed || !form.Visible)
            throw new Exception("The caption close button must honor the host's cancellable FormClosing event.");
    }

    private static int HitTest(Form form, Point point)
    {
        var packed = (IntPtr)((point.X & 0xffff) | (point.Y << 16));
        return (int)SendMessage(form.Handle, 0x84 /* WM_NCHITTEST */, IntPtr.Zero, packed);
    }
}
