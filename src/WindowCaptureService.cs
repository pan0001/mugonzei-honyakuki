using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamVrTranslator;

internal sealed record DesktopWindowOption(string Id, string Title, string ProcessName)
{
    public override string ToString() => $"{Title} · {ProcessName}";
}

internal static class WindowCaptureService
{
    private sealed record Identity(nint Handle, uint Pid, uint Thread, long Start, string Class);
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim FrameSlot = new(1, 1);
    private static readonly Dictionary<string, Identity> Windows = new(StringComparer.Ordinal);
    private delegate bool EnumProc(nint hwnd, nint parameter);
    [DllImport("user32", SetLastError = true)] private static extern bool EnumWindows(EnumProc callback, nint state);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int count);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32")] private static extern nint GetShellWindow();
    [DllImport("dwmapi")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out uint result, int bytes);
    [DllImport("user32")] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);

    public static IReadOnlyList<DesktopWindowOption> ListWindows()
    {
        var found = new List<(Identity Window, string Title, string Name)>();
        nint shell = GetShellWindow();
        if (!EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || !Visible(hwnd) || !TryIdentify(hwnd, out var identity, out string process)) return true;
            var title = new StringBuilder(513);
            if (GetWindowText(hwnd, title, title.Capacity) > 0 && !string.IsNullOrWhiteSpace(title.ToString()))
                found.Add((identity!, title.ToString(), process));
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Windows 窗口列表。");
        lock (Gate)
        {
            // Preserve the identifier of a minimized selection until its actual HWND lifetime ends.
            foreach (var entry in Windows.ToArray())
                if (!TryIdentify(entry.Value.Handle, out var current, out _) || current != entry.Value) Windows.Remove(entry.Key);
            var byWindow = Windows.ToDictionary(pair => pair.Value, pair => pair.Key);
            var result = new List<DesktopWindowOption>(found.Count);
            foreach (var item in found)
            {
                if (!byWindow.TryGetValue(item.Window, out string? id))
                {
                    id = Guid.NewGuid().ToString("N");
                    Windows.Add(id, item.Window);
                }
                result.Add(new(id, item.Title, item.Name));
            }
            return result;
        }
    }

    private static bool Visible(nint hwnd) => IsWindowVisible(hwnd) && !IsIconic(hwnd)
        && (GetWindowLongPtr(hwnd, -20).ToInt64() & (0x80 | 0x08000000)) == 0 // tool / non-activating surfaces
        && (DwmGetWindowAttribute(hwnd, 14, out uint cloaked, sizeof(uint)) != 0 || cloaked == 0);

    private static bool TryIdentify(nint hwnd, out Identity? identity, out string name)
    {
        identity = null; name = "";
        uint thread = GetWindowThreadProcessId(hwnd, out uint pid);
        if (thread == 0 || pid == 0 || pid == Environment.ProcessId) return false;
        var type = new StringBuilder(257);
        if (GetClassName(hwnd, type, type.Capacity) == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            identity = new(hwnd, pid, thread, process.StartTime.ToUniversalTime().Ticks, type.ToString());
            name = process.ProcessName;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return false; }
    }

    internal static bool IsSelectable(string id)
    {
        try { _ = Resolve(id); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static nint Resolve(string id)
    {
        Identity? saved;
        lock (Gate) Windows.TryGetValue(id ?? "", out saved);
        if (saved == null || !TryIdentify(saved.Handle, out var current, out _) || current != saved)
            throw new InvalidOperationException("所选应用窗口已关闭或尚未选择。请在「截图与按键」刷新并选择窗口。");
        if (IsIconic(saved.Handle)) throw new InvalidOperationException("所选窗口已最小化，请还原后再截图。");
        if (!Visible(saved.Handle)) throw new InvalidOperationException("所选窗口已隐藏或位于其他桌面，请显示该窗口后再截图。");
        if (GetWindowDisplayAffinity(saved.Handle, out var affinity) && affinity != 0)
            throw new InvalidOperationException("所选窗口禁止截图。");
        return saved.Handle;
    }

    public static async Task<Bitmap> CaptureAsync(AppSettings settings, CancellationToken token)
    {
        var snapshot = settings.Copy();
        await FrameSlot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                nint hwnd = Resolve(snapshot.DesktopWindowId);
                using var pixels = GraphicsWindowCapture.Read(hwnd, token);
                _ = Resolve(snapshot.DesktopWindowId);
                token.ThrowIfCancellationRequested();
                return CaptureService.Crop(pixels, snapshot.CropX, snapshot.CropY, snapshot.CropWidth, snapshot.CropHeight);
            }, token).ConfigureAwait(false);
        }
        finally { FrameSlot.Release(); }
    }
}
