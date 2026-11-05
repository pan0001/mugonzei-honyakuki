using System.Reflection;
using System.Runtime.InteropServices;
using SteamVrTranslator;

public static class DesktopModeTests
{
    public static Task RunAsync() => DesktopResponsivenessTests.OnUiThread(() =>
    {
        CheckStoredModes();
        CheckAutomaticSelection();
        if (SteamVrPresence.ReadServerPid() == 0)
        {
            using var passive = new VrService();
            Require(!passive.Connect() && !passive.IsConnected && passive.Status.Contains("未就绪"),
                "The real OpenVR service refuses to initialize when SteamVR is closed");
        }
        CheckQueuedPixels();
        using var form = new MainForm(true);
        try
        {
            var settings = Field<AppSettings>(form, "settings");
            Require(settings.RuntimeMode == "Auto" && form.ActiveDesktopMode, "Automatic mode starts on desktop until a runtime is connected");
            settings.CaptureMode = "VR 左眼";
            form.SetRuntimeMode("Desktop");
            Invoke(form, "LoadSettings");
            Invoke(form, "ApplyCaptureEnvironment");
            Invoke(form, "ConnectVr");
            Invoke(form, "Poll");
            Require(Field<VrService>(form, "vr").Status == "尚未连接 SteamVR", "Desktop mode skips connection, manual reconnect and input polling");
            Require(form.ReadSettingsDraft().RuntimeMode == "Desktop" && form.ReadSettingsDraft().CaptureMode == "VR 左眼",
                "Editing desktop preferences preserves the saved VR capture source");
            Invoke(form, "ShowDemo");
            var subtitle = Field<DesktopSubtitleForm>(form, "desktopSubtitle");
            Require(subtitle.Visible && !form.Visible && !subtitle.ShowInTaskbar, "A hidden workbench presents a separate desktop subtitle");
            Invoke(form, "HideOverlay");
            Require(!subtitle.Visible, "Hide shortcut can dismiss the desktop subtitle");
            Invoke(form, "ShowPage");
            Require(subtitle.Visible, "The existing result can be shown again");
            Set(form, "hideAt", Environment.TickCount64 - 1);
            Invoke(form, "Poll");
            Require(!subtitle.Visible, "Desktop subtitle expiry works without VR polling");
            Field<ComboBox>(form, "workspaceMode").SelectedIndex = 0;
            Require(form.ReadSettingsDraft().RuntimeMode == "SteamVR", "The workbench can select SteamVR");
            form.SetRuntimeMode("SteamVR");
            Require(form.ActiveDesktopMode && settings.CaptureMode == "VR 左眼", "Selecting VR before the server starts keeps desktop capture available");
            form.SetRuntimeMode("Auto");
            Require(form.ReadSettingsDraft().RuntimeMode == "Auto", "Automatic detection can be selected again");
            CheckConflictingShortcuts(form.Handle);
        }
        finally { Invoke(form, "DisposeServices"); }
        Console.WriteLine("PASS desktop mode, tray subtitle, paging, timeout and real hotkey conflict recovery");
        return Task.CompletedTask;
    });

    private static void CheckStoredModes()
    {
        string directory = Directory.CreateTempSubdirectory("MugonzeiDesktopMode-").FullName;
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path,
                """{"Version":1,"Settings":{"CaptureMode":"桌面主显示器","Hotkey":"Ctrl+Alt+Y"}}""");
            var old = SettingsStore.Load(directory);
            Require(old.RuntimeMode == "Auto" && old.CaptureMode == "桌面主显示器", "Older configurations use detection and retain the VR capture source");
            File.WriteAllText(path, """{"Version":1,"Settings":{"DesktopMode":false}}""");
            Require(SettingsStore.Load(directory).RuntimeMode == "Auto", "The old default follows runtime detection");
            File.WriteAllText(path, """{"Version":1,"Settings":{"RuntimeMode":"Auto","DesktopMode":true}}""");
            Require(SettingsStore.Load(directory).RuntimeMode == "Auto", "A new explicit preference supersedes the legacy desktop flag");
            File.WriteAllText(path,
                """{"Version":1,"Settings":{"DesktopMode":true,"CaptureMode":"桌面主显示器","Hotkey":"Ctrl+Alt+Y"}}""");
            old = SettingsStore.Load(directory);
            Require(old.RuntimeMode == "Desktop", "An explicit legacy desktop choice stays pinned after upgrade");
            SettingsStore.Save(old, directory);
            var saved = SettingsStore.Load(directory);
            Require(saved.RuntimeMode == "Desktop" && saved.Hotkey == "Ctrl+Alt+Y" && saved.CaptureMode == old.CaptureMode,
                "Desktop mode, capture source and custom shortcut survive a restart");
        }
        finally
        {
            File.Delete(Path.Combine(directory, "settings.json"));
            Directory.Delete(directory);
        }
    }

    private static void CheckAutomaticSelection()
    {
        int pid = 0, attempts = 0, probes = 0;
        bool connected = false, ready = true;
        var monitor = new RuntimeModeMonitor(
            () => { probes++; return Task.FromResult(pid); },
            () => connected,
            () => { attempts++; return connected = ready; },
            () => connected = false);
        monitor.Check("Auto", 0);
        monitor.Check("SteamVR", 1000);
        Require(!connected && attempts == 0 && probes == 1, "Absent SteamVR never reaches native initialization, including manual VR selection");
        pid = 101;
        monitor.Check("Auto", 2000);
        Require(connected && attempts == 1, "SteamVR started outside the translator automatically becomes the output");
        monitor.Check("Desktop", 2010);
        monitor.Check("Desktop", 10000);
        Require(!connected && probes == 2 && attempts == 1, "Manual desktop disconnects and suppresses probing while SteamVR is running");
        monitor.Check("Auto", 10001);
        Require(connected && attempts == 2, "Returning to automatic reattaches to the running instance");
        connected = false;
        monitor.RuntimeQuit();
        monitor.Check("Auto", 10002);
        monitor.Check("Auto", 18000);
        Require(!connected && attempts == 2, "A retiring server cannot be reattached even if its process lingers");
        pid = 0;
        monitor.Check("Auto", 20000);
        pid = 102; ready = false;
        monitor.Check("SteamVR", 22000);
        monitor.Check("SteamVR", 22016);
        Require(!connected && attempts == 3, "A starting but unready runtime leaves desktop usable and retries with backoff");
        ready = true;
        monitor.Check("SteamVR", 26000);
        Require(connected && attempts == 4, "The new SteamVR instance is attached once ready");
        pid = 0;
        monitor.Check("SteamVR", 28000);
        Require(!connected && attempts == 4, "Disappearing processes restore desktop without an initialization call");
    }

    private static void CheckConflictingShortcuts(IntPtr window)
    {
        using var otherWindow = new Form();
        using var working = new Hotkeys(window);
        using var competing = new Hotkeys(otherWindow.Handle);
        const string first = "Ctrl+Shift+Alt+F20", occupied = "Ctrl+Shift+Alt+F21", next = "Ctrl+Shift+Alt+F22";
        working.Register(Hotkeys.Translate, first);
        competing.Register(Hotkeys.Next, occupied);
        try { working.Register(Hotkeys.Translate, occupied); throw new Exception("Conflicting shortcut accepted"); }
        catch (InvalidOperationException error) { Require(error.Message.Contains(occupied), "Conflict identifies the requested key"); }
        try { competing.Register(Hotkeys.Toggle, first); throw new Exception("Conflict released the previous shortcut"); }
        catch (InvalidOperationException) { }
        working.Register(Hotkeys.Translate, next);
        competing.Register(Hotkeys.Toggle, first);
        Require(working.ActionFor(Hotkeys.Translate) == 0, "A queued message for the old key is ignored after changing it");
        Require(Hotkeys.Parse("Ctrl+1") == (0x4002u, (uint)Keys.D1), "Digit shortcuts use keyboard virtual keys");
    }

    private static void CheckQueuedPixels()
    {
        var queue = new LatestCaptureRequests();
        queue.Enqueue(new CaptureRequest(false, "active"));
        queue.TryTake(out _);
        var old = new Bitmap(2, 2);
        var replacement = new Bitmap(2, 2);
        replacement.SetPixel(0, 0, Color.Lime);
        queue.Enqueue(new CaptureRequest(false, "older", DesktopImage: old));
        queue.Enqueue(new CaptureRequest(false, "latest", DesktopImage: replacement));
        try { _ = old.Width; throw new Exception("Superseded desktop bitmap leaked"); } catch (ArgumentException) { }
        Require(queue.TryTake(out var next), "Latest keypress is pending");
        using var received = next.DesktopImage!;
        queue.Finish();
        Require(received.GetPixel(0, 0).G == 255, "Latest keypress retains its original pixels while the previous request cancels");
    }

    // Opt-in interactive Windows smoke. Only a small synthetic color patch is sent to a loopback
    // fixture. No account, user configuration, external translation or saved screenshot is used.
    public static Task RunScreenCaptureAsync() => DesktopResponsivenessTests.OnUiThread(async () =>
    {
        using var server = new HandwritingLocalApiTests.LocalServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var marker = new Marker();
        using var form = new MainForm(true);
        try
        {
            var settings = Field<AppSettings>(form, "settings");
            settings.DesktopMode = true;
            settings.RuntimeMode = "Desktop";
            settings.Hotkey = "Ctrl+Alt+Shift+F23";
            settings.ProcessingMode = "Image";
            settings.TranslationBackend = "OpenAI";
            settings.ApiBaseUrl = server.BaseUrl;
            settings.ApiModel = "test-vision";
            var screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("Interactive primary display required");
            var area = screen.WorkingArea;
            int x = area.Left + area.Width / 2, y = area.Bottom - area.Height / 20 - 35;
            marker.Bounds = new Rectangle(x - 46, y - 38, 92, 76);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(x - form.Width / 2, area.Bottom - form.Height);
            settings.CropX = (float)(x - screen.Bounds.Left - 18) / screen.Bounds.Width;
            settings.CropY = (float)(y - screen.Bounds.Top - 14) / screen.Bounds.Height;
            settings.CropWidth = 36f / screen.Bounds.Width;
            settings.CropHeight = 28f / screen.Bounds.Height;
            marker.Show();
            marker.Update();
            Invoke(form, "LoadSettings");
            form.Show();
            await Task.Delay(200, deadline.Token);
            // The marker is initially covered by the workbench: its color can only be read
            // when the capture path removes our own windows. Do not activate any test form.
            Require(SetWindowPos(form.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013), "Place the workbench over the synthetic marker");
            foreach (string placement in new[] { "visible", "behind", "tray" })
            {
                IntPtr foreground = GetForegroundWindow();
                if (placement == "behind")
                {
                    Require(foreground != IntPtr.Zero && foreground != form.Handle && foreground != marker.Handle,
                        "An independent foreground window is required for the background capture check");
                    Require(SetWindowPos(form.Handle, new IntPtr(1), 0, 0, 0, 0, 0x0013), "Place only the test workbench behind the foreground application");
                    Require(IsAbove(foreground, form.Handle) && GetForegroundWindow() == foreground, "Existing foreground application stays above the visible workbench");
                }
                if (placement == "tray") { form.WindowState = FormWindowState.Minimized; Require(!form.Visible, "Workbench entered tray"); }
                var subtitle = Field<DesktopSubtitleForm>(form, "desktopSubtitle");
                Require(subtitle.Visible && subtitle.Bounds.Contains(new Point(x, y)), "Old subtitle covers the upcoming test patch");
                Require(PostMessage(form.Handle, 0x0312, Hotkeys.Translate, 0), "Dispatch registered global shortcut notification");
                var (context, document) = await server.ReceiveAsync(deadline.Token);
                using (document)
                {
                    string url = document.RootElement.GetProperty("messages")[1].GetProperty("content")[1]
                        .GetProperty("image_url").GetProperty("url").GetString()!;
                    using var bytes = new MemoryStream(Convert.FromBase64String(url["data:image/png;base64,".Length..]));
                    using var pixels = new Bitmap(bytes);
                    var center = pixels.GetPixel(pixels.Width / 2, pixels.Height / 2);
                    Require(Math.Abs(center.R - Marker.Color.R) + Math.Abs(center.G - Marker.Color.G) + Math.Abs(center.B - Marker.Color.B) < 20,
                        $"The real desktop capture excludes the workbench and previous subtitle in {placement} state (sample RGB {center.R}/{center.G}/{center.B})");
                }
                string reply = "桌面取图成功 " + placement;
                await server.RespondAsync(context, reply, false, deadline.Token);
                while (Field<bool>(form, "busy")) await Task.Delay(20, deadline.Token);
                Require(subtitle.Visible && Field<TextBox>(form, "translatedText").Text == reply, "Loopback translation reached the visible subtitle and result panel");
                if (placement == "behind")
                    Require(form.Visible && GetForegroundWindow() == foreground && IsAbove(foreground, form.Handle),
                        "The background workbench stays behind the foreground game after capture and after the result");
                if (placement == "tray")
                    Require(!form.Visible && GetForegroundWindow() == foreground, "Tray result does not restore the workbench or steal focus");
            }
        }
        finally { form.Close(); }
        Console.WriteLine("PASS real primary desktop pixels through hotkey → cropped image → loopback vision → no-focus subtitle, visible / behind foreground window / tray");
    });

    private sealed class Marker : Form
    {
        internal static readonly Color Color = Color.FromArgb(213, 45, 136);
        public Marker()
        {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false; TopMost = true; BackColor = Color;
        }
        protected override bool ShowWithoutActivation => true;
    }

    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static void Set(MainForm form, string name, object value) => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, value);
    private static void Invoke(MainForm form, string name) => typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        for (IntPtr next = GetWindow(upper, 2); next != IntPtr.Zero; next = GetWindow(next, 2))
            if (next == lower) return true;
        return false;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr window, int message, nint wParam, nint lParam);
}
