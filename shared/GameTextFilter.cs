using System.Text.RegularExpressions;

namespace Mugonzei.Shared;

// Text-only OCR has no positions or confidence scores. Drop a short ambiguous label only
// when its neighbours also identify the UI. Keep the original spelling/layout of retained text.
internal static class GameTextFilter
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex Metric = new(
        @"^(?:FPS|GPU|CPU|LAT(?:ENCY)?|帧率|幀率|フレームレート|延迟|延遲|遅延)(?:\s*(?:usage|temp(?:erature)?|clock|utilization|使用率|温度|占用率))?[\s:：=]*" +
        @"(?:(?:N/?A|--|[+\-]?[0-9]+(?:[.,][0-9]+)?\s*(?:%|°\s*[CF]|℃|℉|MHz|GHz|mV|V|W|RPM|ms|fps|MiB|GiB|GB|MB)?)\s*)+$", Options);
    private static readonly Regex MetricUnit = new(@"(?:%|°\s*[CF]|℃|MHz|GHz|RPM|\bms\b)", Options);
    private static readonly Regex Key = new(@"^(?:Esc(?:ape)?|Tab|PTT|F(?:[1-9]|1[0-9]|2[0-4])|Shift|Ctrl|Alt|[A-Z])$", Options);
    private static readonly Regex KeySeparator = new(@"[\s|｜·,，\[\]【】]+", Options);
    private static readonly Regex OscControl = new(
        @"^(?:(?:输入|輸入)(?:文字|文本)(?:并|並)?(?:发送|發送)\s*OSC\s*(?:翻译|翻譯)|" +
        @"(?:enter|input|type) (?:text|a message) (?:and|to) send (?:an? )?OSC translation|" +
        @"(?:テキスト|文字)を入力して\s*OSC\s*翻訳を送信)[。.!！]?$", Options);
    private static readonly Regex MediaWidget = new(@"^iwaSync3\s+v[0-9]+(?:\.[0-9]+)*$", Options);
    private static readonly Regex Connected = new(@"^(?:已连接|已連接|接続済み|接続しました|connected)[。.！!]?$", Options);
    private static readonly Regex TaskbarToken = new(
        @"(?<date>[0-9]{1,4}[-/.][0-9]{1,2}[-/.][0-9]{1,4}|[0-9]{4}年[0-9]{1,2}月[0-9]{1,2}日)|" +
        @"(?<time>(?:(?:午前|午後|上午|下午)\s*)?[0-9]{1,2}[:：][0-9]{2}(?:\s*[AP]M)?)|" +
        @"(?<temperature>[+\-−]?[0-9]{1,2}(?:[.,][0-9])?\s*(?:°\s*[CF]|℃|℉))|" +
        @"(?<weather>partly cloudy|mostly cloudy|cloudy|sunny|overcast|light rain|rainy|多云|多雲|阴天|陰天|晴れ|晴天|曇り|くもり|小雨)|" +
        @"(?<search>type here to search|search|ここに入力して検索|検索|搜索|搜尋)|" +
        @"(?<ime>拼音|拼|英|简|繁|あ|半角|ENG|CHS|JPN|IME|US|JP|A)", Options);
    private static readonly Regex Spaces = new(@"\s+", Options);

    private sealed class Line(int index, string raw)
    {
        internal readonly int Index = index;
        internal readonly string Label = MatchingLabel(raw);
        internal bool Removed;
        internal bool IsHud;
    }

    private static string MatchingLabel(string raw)
    {
        string label = raw.Trim().TrimStart('•', '●').Trim();
        return label.StartsWith("- ", StringComparison.Ordinal) || label.StartsWith("* ", StringComparison.Ordinal)
            ? label[2..].TrimStart() : label;
    }

    internal static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string[] raw = text.ReplaceLineEndings("\n").Split('\n');
        var lines = new List<Line>();
        for (int i = 0; i < raw.Length; i++)
            if (!string.IsNullOrWhiteSpace(raw[i])) lines.Add(new(i, raw[i]));

        var visible = lines;
        // A deliberately nested HUD can reveal only one new pair each pass. Limit the total
        // number of line visits, including when Clean runs on the desktop UI thread.
        long remainingRows = 4L * lines.Count;
        while (visible.Count > 0)
        {
            if (visible.Count > remainingRows)
            {
                RemoveInterleavedTelemetry(visible);
                var retained = visible.Where(row => !row.Removed).ToList();
                if (retained.Count == visible.Count) return text;
                // Return a partial cleanup only if it is already stable. Otherwise preserve the
                // original, including ambiguous dialogue, on every retry.
                RemoveInterface(retained);
                RemoveRepeatedBlocks(retained);
                if (retained.Any(row => row.Removed)) return text;
                break;
            }
            remainingRows -= visible.Count;
            RemoveInterface(visible);
            RemoveRepeatedBlocks(visible);
            if (!visible.Any(row => row.Removed)) break;
            // Removing an overlay can join two parts of another overlay. Revisit only surviving
            // rows. Each further pass must remove at least one row; the original characters never grow.
            visible = visible.Where(row => !row.Removed).ToList();
        }
        if (lines.All(line => !line.Removed)) return text;

        var kept = new List<string>();
        int previous = -1;
        foreach (var row in lines.Where(row => !row.Removed))
        {
            if (previous >= 0 && row.Index > previous + 1)
            {
                bool removedBetween = false;
                int blanks = 0;
                for (int j = previous + 1; j < row.Index; j++)
                    if (string.IsNullOrWhiteSpace(raw[j])) blanks++; else removedBetween = true;
                for (int j = 0; j < (removedBetween ? Math.Min(1, blanks) : blanks); j++) kept.Add("");
            }
            kept.Add(raw[row.Index]);
            previous = row.Index;
        }
        return string.Join(text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n", kept);
    }

    private static void RemoveInterface(List<Line> lines)
    {
        RemoveMetrics(lines);
        for (int i = 0; i < lines.Count; i++)
        {
            if (!MediaWidget.IsMatch(lines[i].Label) && !OscControl.IsMatch(lines[i].Label)) continue;
            lines[i].IsHud = lines[i].Removed = true;
            for (int j = Math.Max(0, i - 2); j <= Math.Min(lines.Count - 1, i + 2); j++)
                if (IsSendButton(lines[j].Label)) lines[j].Removed = true;
        }
        bool gameUi = lines.Any(r => r.IsHud || r.Label.Equals("VRChat", StringComparison.OrdinalIgnoreCase));
        RemoveKeyLegends(lines.Where(row => !row.Removed).ToList(), gameUi);
        for (int i = 0; i < lines.Count; i++)
        {
            string label = lines[i].Label;
            if (label.Equals("VRChat", StringComparison.OrdinalIgnoreCase) && NearHud(lines, i, 3)) lines[i].Removed = true;
            if (Connected.IsMatch(label) && NearHud(lines, i, 7) &&
                Neighbours(lines, i, 4).Any(row => row != lines[i] && Connected.IsMatch(row.Label)))
                lines[i].Removed = true;
        }
        // Merge a repeated short name only when there is a connection notification immediately below it.
        for (int i = 0; i + 2 < lines.Count; i++)
        {
            string name = lines[i].Label;
            if (lines[i + 2].Removed && Connected.IsMatch(lines[i + 2].Label) && name == lines[i + 1].Label &&
                name.Length is >= 2 and <= 10 && name.All(c => c is >= '\u3400' and <= '\u9fff'))
                lines[i + 1].Removed = true;
        }
        RemoveTaskbars(lines.Where(row => !row.Removed).ToList());
    }

    private static IEnumerable<Line> Neighbours(List<Line> lines, int i, int radius)
    {
        for (int j = Math.Max(0, i - radius); j <= Math.Min(lines.Count - 1, i + radius); j++) yield return lines[j];
    }

    private static bool NearHud(List<Line> lines, int i, int radius) => Neighbours(lines, i, radius).Any(row => row.IsHud);
    private static bool IsSendButton(string label) => label.Equals("Send translation", StringComparison.OrdinalIgnoreCase) ||
        label is "发送翻译" or "發送翻譯" or "翻訳を送信" or "翻訳送信";

    private static bool IsMetricLine(string label, out bool strong)
    {
        strong = false;
        if (label.Length > 512) return false;
        var parts = label.Split(['|', '｜'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts.All(Metric.IsMatch)) return false;
        strong = parts.Length >= 2 || MetricUnit.Matches(label).Count >= 2;
        return true;
    }

    private static void RemoveMetrics(List<Line> lines)
    {
        int i = 0;
        while (i < lines.Count)
        {
            int start = i;
            bool hasTelemetry = false;
            while (i < lines.Count && IsMetricLine(lines[i].Label, out bool strong)) { hasTelemetry |= strong; i++; }
            if (hasTelemetry || i - start >= 2)
                for (int j = start; j < i; j++) lines[j].IsHud = lines[j].Removed = true;
            if (i == start) i++;
        }
    }

    // At the pass limit, classify a dense performance panel as a single run. Require several
    // measured rows plus explicit multi-key legends including push-to-talk. A name, a single
    // key or any prose ends the run and remains eligible for display.
    private static void RemoveInterleavedTelemetry(List<Line> lines)
    {
        int i = 0;
        while (i < lines.Count)
        {
            int start = i, metrics = 0, legends = 0;
            bool pushToTalk = false;
            while (i < lines.Count)
            {
                string label = lines[i].Label;
                if (IsMetricLine(label, out _)) metrics++;
                else
                {
                    if (label.Length > 80) break;
                    var keys = KeySeparator.Split(label).Where(k => k.Length > 0).ToArray();
                    if (keys.Length < 2 || !keys.All(k => k.Length > 1 && Key.IsMatch(k))) break;
                    legends++;
                    pushToTalk |= keys.Contains("PTT", StringComparer.OrdinalIgnoreCase);
                }
                i++;
            }
            if (metrics >= 3 && legends >= 3 && pushToTalk)
                for (int j = start; j < i; j++) lines[j].Removed = true;
            if (i == start) i++;
        }
    }

    private static void RemoveKeyLegends(List<Line> lines, bool gameUi)
    {
        int i = 0;
        while (i < lines.Count)
        {
            int start = i;
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (i < lines.Count && lines[i].Label.Length <= 80)
            {
                var tokens = KeySeparator.Split(lines[i].Label).Where(k => k.Length > 0).ToArray();
                if (tokens.Length == 0 || !tokens.All(Key.IsMatch)) break;
                keys.UnionWith(tokens);
                i++;
            }
            int named = keys.Count(k => k.Length > 1);
            bool documented = start > 0 && lines[start - 1].Label.TrimEnd(':', '：').ToLowerInvariant() is
                "keyboard shortcuts" or "key bindings" or "快捷键" or "键盘快捷键" or "キーボードショートカット" or "ショートカットキー";
            if (!documented && (gameUi || keys.Contains("PTT")) && keys.Count >= 3 && named >= 2)
                for (int j = start; j < i; j++) lines[j].IsHud = lines[j].Removed = true;
            if (i == start) i++;
        }
    }

    [Flags]
    private enum Bar { None = 0, Date = 1, Time = 2, Temperature = 4, Weather = 8, Search = 16, Ime = 32, WeakIme = 64 }
    private static Bar TaskbarParts(string label)
    {
        if (label.Length > 160) return Bar.None;
        int end = 0;
        Bar result = Bar.None;
        foreach (Match match in TaskbarToken.Matches(label))
        {
            if (!Separators(label, end, match.Index)) return Bar.None;
            end = match.Index + match.Length;
            if (match.Groups["date"].Success) result |= Bar.Date;
            else if (match.Groups["time"].Success) result |= Bar.Time;
            else if (match.Groups["temperature"].Success) result |= Bar.Temperature;
            else if (match.Groups["weather"].Success) result |= Bar.Weather;
            else if (match.Groups["search"].Success) result |= Bar.Search;
            else result |= match.Length <= 2 ? Bar.WeakIme : Bar.Ime;
        }
        return Separators(label, end, label.Length) ? result : Bar.None;
    }

    private static bool Separators(string s, int start, int end)
    {
        for (int i = start; i < end; i++) if (!char.IsWhiteSpace(s[i]) && s[i] is not ('|' or '｜' or '·')) return false;
        return true;
    }

    private static void RemoveTaskbars(List<Line> lines)
    {
        int i = 0;
        while (i < lines.Count)
        {
            int start = i;
            Bar seen = Bar.None;
            while (i < lines.Count)
            {
                var bits = TaskbarParts(lines[i].Label);
                if (bits == Bar.None) break;
                seen |= bits;
                i++;
            }
            bool clock = seen.HasFlag(Bar.Time | Bar.Date);
            bool weather = seen.HasFlag(Bar.Temperature | Bar.Weather);
            if (((clock || weather) && (seen & (Bar.Search | Bar.Ime)) != 0) || (clock && weather && seen.HasFlag(Bar.WeakIme)))
                for (int j = start; j < i; j++) lines[j].Removed = true;
            if (i == start) i++;
        }
    }

    private static void RemoveRepeatedBlocks(List<Line> all)
    {
        var suffix = new List<(Line Row, string Key)>();
        foreach (var row in all.Where(r => !r.Removed))
        {
            string key = Spaces.Replace(row.Label.TrimEnd('。', '.', '．'), " ");
            suffix.Add((row, key));
            int end = suffix.Count;
            int longest = Math.Min(48, end / 2);
            int characters = 0;
            bool differentLines = false;
            for (int length = 1; length <= longest; length++)
            {
                string first = suffix[end - length].Key;
                characters += first.Length;
                differentLines |= first != key;
                if (characters < 16 || suffix[end - length - 1].Key != key) continue;
                if (length > 1 && !differentLines) continue;
                int j = end - length;
                while (j < end && suffix[j].Key == suffix[j - length].Key) j++;
                if (j != end) continue;
                // The retained prefix has already been folded. Removing its repeated suffix cannot
                // introduce an overlap (A B B A B becomes A B in one traversal).
                for (j = end - length; j < end; j++) suffix[j].Row.Removed = true;
                suffix.RemoveRange(end - length, length);
                break;
            }
        }
    }
}
