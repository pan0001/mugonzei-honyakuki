using System.Runtime.InteropServices;

namespace SteamVrTranslator;

internal sealed class Hotkeys(IntPtr window) : IDisposable
{
    public const int Translate = 101, Next = 102, Previous = 103, Toggle = 104;
    private readonly HashSet<int> registered = [];

    public static (uint Modifiers, uint Key) Parse(string text)
    {
        uint modifiers = 0;
        Keys key = Keys.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= 2; break;
                case "alt": modifiers |= 1; break;
                case "shift": modifiers |= 4; break;
                case "win": modifiers |= 8; break;
                default:
                    if (key != Keys.None || !Enum.TryParse(part, true, out key) || key == Keys.None || (int)key > 255 ||
                        key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
                        throw new ArgumentException("快捷键格式示例：Ctrl+Alt+T、F8、Shift+F9。");
                    break;
            }
        }
        if (key == Keys.None) throw new ArgumentException("快捷键需要一个字母、方向键或功能键。");
        return (modifiers | 0x4000, (uint)key);
    }

    public void Register(int id, string text)
    {
        var (modifiers, key) = Parse(text);
        if (registered.Remove(id)) UnregisterHotKey(window, id);
        if (!RegisterHotKey(window, id, modifiers, key))
            throw new InvalidOperationException($"快捷键 {text} 被其他程序占用或无法注册，请换一个。");
        registered.Add(id);
    }

    public void Dispose()
    {
        foreach (var id in registered) UnregisterHotKey(window, id);
        registered.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
