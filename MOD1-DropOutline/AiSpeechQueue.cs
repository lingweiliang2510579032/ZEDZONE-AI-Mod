using System;
using System.Collections.Generic;
using System.Text;

namespace Dropoutline
{
    // Pure managed presentation only. Never schedules a gameplay/task action.
    internal sealed class AiSpeechQueue
    {
        internal const int MaxLines = 3, MaxLineCharacters = 40, MaxTotalCharacters = 100;
        internal const float IntervalSeconds = 2.5f;
        internal readonly struct Line
        {
            public readonly string Text;
            public readonly bool Remember;
            public Line(string text, bool remember) { Text = text; Remember = remember; }
        }
        private readonly Queue<Line> _pending = new Queue<Line>();
        private string _scope = "", _lastKey = "";
        private float _nextAt = float.NegativeInfinity, _lastQueuedAt = float.NegativeInfinity;
        public int Count => _pending.Count;
        public void Clear()
        {
            _pending.Clear(); _scope = _lastKey = "";
            _nextAt = _lastQueuedAt = float.NegativeInfinity;
        }
        public bool Enqueue(string scope, string requestId, string text, float now, bool remember)
        {
            if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(text)) return false;
            if (_scope != scope) Clear();
            _scope = scope;
            string key = (requestId ?? "") + "\n" + text.Trim();
            if (_lastKey == key && now - _lastQueuedAt >= 0 && now - _lastQueuedAt < 10f) return false;
            _lastKey = key; _lastQueuedAt = now;
            // One bounded reply, not an ever-growing conversation backlog.
            _pending.Clear();
            foreach (string line in Split(text)) _pending.Enqueue(new Line(line, remember));
            return _pending.Count > 0;
        }
        public bool TryTake(string scope, float now, out Line line)
        {
            line = default;
            if (_scope != scope) { Clear(); return false; }
            if (_pending.Count == 0 || now < _nextAt) return false;
            line = _pending.Dequeue(); _nextAt = now + IntervalSeconds;
            return true;
        }
        private static bool Boundary(char c) => c == '。' || c == '！' || c == '？' || c == '!' || c == '?' || c == '；' || c == ';' || c == '\n';
        private static int Width(string text, int index) => char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
        internal static int Characters(string text)
        {
            int count = 0;
            for (int i = 0; i < (text?.Length ?? 0); count++) i += Width(text, i);
            return count;
        }
        private static string Prefix(string text, int count)
        {
            int i = 0;
            while (i < text.Length && count-- > 0) i += Width(text, i);
            return text.Substring(0, i);
        }
        internal static List<string> Split(string original)
        {
            var output = SplitCore(original ?? "");
            string shown = string.Concat(output);
            // A clipped success-looking prefix must never hide a later failure.
            // Prefer the actual negative clause; if its context is too long, keep
            // a conservative status rather than inventing a successful result.
            string[] negative = { "任务没有执行", "任务未执行", "没有执行", "未执行", "未完成", "没完成", "失败", "无法", "不能", "未确认", "异常", "已取消", "已停止" };
            int failureAt = -1;
            foreach (string marker in negative)
            {
                int at = (original ?? "").IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0 && shown.IndexOf(marker, StringComparison.Ordinal) < 0 && (failureAt < 0 || at < failureAt)) failureAt = at;
            }
            if (failureAt >= 0)
            {
                int begin = failureAt;
                while (begin > 0 && failureAt - begin < 12 && !Boundary(original[begin - 1]) && original[begin - 1] != '，' && original[begin - 1] != ',') begin--;
                output = SplitCore("这次不能算完成，先停下。" + original.Substring(begin));
            }
            return output;
        }
        private static List<string> SplitCore(string text)
        {
            var output = new List<string>();
            text = text.Replace("\r", "").Trim();
            int start = 0, total = 0;
            while (start < text.Length && output.Count < MaxLines && total < MaxTotalCharacters)
            {
                while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
                if (start >= text.Length) break;
                int end = start, count = 0, stop = -1;
                int limit = Math.Min(MaxLineCharacters, MaxTotalCharacters - total);
                while (end < text.Length && count < limit)
                {
                    char c = text[end]; end += Width(text, end); count++;
                    if (Boundary(c)) { stop = end; break; }
                }
                if (stop > start) end = stop;
                string part = text.Substring(start, end - start).Trim();
                start = end;
                if (part.Length == 0) continue;
                output.Add(part); total += Characters(part);
            }
            if (start < text.Length && output.Count > 0)
            {
                int last = output.Count - 1;
                string part = output[last];
                output[last] = Prefix(part, Math.Max(0, Characters(part) - 1)).TrimEnd() + "…";
            }
            return output;
        }
    }
}
