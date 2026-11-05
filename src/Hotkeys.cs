using System.Runtime.InteropServices;

namespace SteamVrTranslator;

internal sealed class Hotkeys(IntPtr window) : IDisposable
{
    public const int Translate = 101, Next = 102, Previous = 103, Toggle = 104;
    private readonly Dictionary<int, (int NativeId, uint Modifiers, uint Key)> registered = [];

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
                    if (key != Keys.None) throw new ArgumentException("快捷键只能包含一个主键。");
                    if (part.Length == 1 && char.IsAsciiDigit(part[0]))
                        key = (Keys)((int)Keys.D0 + part[0] - '0');
                    else if (!char.IsAsciiLetter(part[0]) || !Enum.TryParse(part, true, out key) || !Enum.IsDefined(key) ||
                        (int)key is <= 0 or > 255 || key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
                            or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu)
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
        bool replacing = registered.TryGetValue(id, out var old);
        if (replacing && old.Modifiers == modifiers && old.Key == key) return;
        // Reserve the new combination before releasing the working one. A conflict
        // must leave the user's current global shortcut registered.
        int nextId = replacing && old.NativeId == id ? id + 0x1000 : id;
        if (!RegisterHotKey(window, nextId, modifiers, key))
            throw new InvalidOperationException($"快捷键 {text} 被其他程序占用或无法注册，请换一个。");
        if (replacing && !UnregisterHotKey(window, old.NativeId))
        {
            UnregisterHotKey(window, nextId);
            throw new InvalidOperationException("无法替换原快捷键，请重试。");
        }
        registered[id] = (nextId, modifiers, key);
    }

    public int ActionFor(int nativeId)
    {
        foreach (var binding in registered)
            if (binding.Value.NativeId == nativeId) return binding.Key;
        return 0;
    }

    public void Dispose()
    {
        foreach (var binding in registered.Values) UnregisterHotKey(window, binding.NativeId);
        registered.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
