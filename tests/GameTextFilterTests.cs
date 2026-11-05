using Mugonzei.Shared;

internal static class GameTextFilterTests
{
    public static void Run()
    {
        // Synthetic replacement names; cover the user's mixture of HUD, player dialogue, mirrored
        // captions, OSC and Windows taskbar without requiring a screenshot or any translation API.
        string mixed = """
            VRChat
            FPS N/A N/A | GPU 14 % 38 °C 1582 MHz 0.895 V 61 W 1190 RPM 15001 MHz | CPU 28 % 65 °C 5350 MHz 127.2 W | 延迟 N/A N/A
            Esc
            R
            Tab
            Y
            PTT
            V
            F4
            F5
            青岚
            青岚
            已连接
            星舟 102a
            已连接。
            已连接。
            地图在这里。
            等等，等等，等等。
            嗯——找到了
            灯塔的钟再响三次，我们就乘小舟绕过海岬。
            风一停，就把蓝色旗子升起来。

            灯塔的钟再响三次，我们就乘小舟绕过海岬。
            风一停，就把蓝色旗子升起来
            输入文字并发送 OSC 翻译
            发送翻译
            iwaSync3 V2
            12°C
            多云
            搜索
            英
            拼
            11:28 PM
            11/5/2026
            """;
        Expect(mixed, """
            青岚
            星舟 102a
            地图在这里。
            等等，等等，等等。
            嗯——找到了
            灯塔的钟再响三次，我们就乘小舟绕过海岬。
            风一停，就把蓝色旗子升起来。
            """);
        Expect("VRChat\n[Esc] [Tab] [PTT] [F5]\nRina: Ready?\nQuest: Press F4 to open the gate.\n" +
            "Input text and send OSC translation\nSend translation\n12 °C Partly cloudy Search ENG 11:28 PM 11/5/2026",
            "Rina: Ready?\nQuest: Press F4 to open the gate.");
        Expect("VRChat\nFPS 90 ｜ GPU 25 % ｜ 遅延 11 ms\nEsc\nTab\nPTT\n" +
            "コハル：はい、はい。\n今夜は12℃。橋の向こうで会おう。\nテキストを入力して OSC 翻訳を送信\n翻訳を送信\n" +
            "12℃\n曇り\n検索\nあ\n午後11:28\n2026/11/5", "コハル：はい、はい。\n今夜は12℃。橋の向こうで会おう。");
        Expect("FPS 90 | GPU 20 %\n[Tab] [Esc] [F4]\n12°C\nCloudy\nSearch\nENG\n11:28 PM\n11/5/2026", "");
        foreach (string original in new[] {
            "  CPU design\r\n\tGPU usage is 14%. Press Esc to exit VRChat.\r\n\r\n\tSearch results: cloudy, 12°C.  ",
            "FPS\nCPU\nGPU\nESC\n次の章", "天气记录\n12°C\n多云\n11:28 PM\n11/5/2026",
            "R\nV\nはい\nはい", "已连接。\n搜索\n公交站", "Aster: Connected.\nYuki: What is OSC?",
            "VRChat\n玩家：PTT 是什么？\n任务：找到标记为 V 的房间。", "等等，等等，等等。",
            "发布日期\n08:00\n11/5/2026\nA\nPlease attend.",
            "Keyboard shortcuts\nEsc\nTab\nShift\nUse these to navigate the dialog."
        }) Expect(original, original);
        Expect("Before the train leaves the station.\nBefore the train leaves the station.\nWait here.", "Before the train leaves the station.\nWait here.");
        Expect("A\nBefore the train leaves the station.\nA\nBefore the train leaves the station.\nA", "A\nBefore the train leaves the station.\nA");
        const string first = "The river freezes before sunrise.", second = "Take the long road to the lighthouse.";
        Expect(string.Join('\n', first, second, second, first, second), first + "\n" + second);
        Expect("Esc\nFPS 90 | GPU 12 %\nTab\nFPS 90 | GPU 12 %\nF4", "");
        Expect("VRChat\n已连接\n已连接。\n输入文字并发送 OSC 翻译\n发送翻译\niwaSync3 V2", "");
        Console.WriteLine("PASS conservative screenshot UI filtering and repeated captions across Chinese, Japanese and English");
    }

    private static void Expect(string input, string expected)
    {
        string actual = GameTextFilter.Clean(input);
        if (actual != expected) throw new Exception($"Unexpected filtered text.\nExpected: [{expected}]\nActual: [{actual}]");
        if (GameTextFilter.Clean(actual) != actual) throw new Exception("Filtering a retained transcript changed it again.");
    }
}
