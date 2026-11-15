using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;

namespace Dropoutline
{
    /// <summary>
    /// MOD2 的记忆层 v2（参考 molly-qq-whale 与大肥鱼的分层记忆）：
    ///
    ///   · 日记条目分三级：normal（1 周后遗忘）/ important（1 月后遗忘）/ critical（永不忘）；
    ///   · 事实库 facts：模型按批抽取「玩家习惯 / 喜好 / 承诺」，低置信度的先进待核实；
    ///   · 心情 mood（-1..1）与好感 affinity（0..100），由聊天与事件缓慢变化；
    ///   · 全部存 ai-memory.json，跨存档保留。
    ///
    /// 省 token 的关键：抽取是**批量**的（攒够 6 条或 3 分钟一次），不是每句都调用。
    /// </summary>
    internal static class AiMemory
    {
        internal const string TierNormal = "normal";
        internal const string TierImportant = "important";
        internal const string TierCritical = "critical";

        private sealed class Entry
        {
            public string kind;     // chat / event
            public string tier;     // normal / important / critical
            public string text;
            public long ts;
            public long expireAt;   // 0 = 永不过期
        }

        private sealed class Fact
        {
            public string key;
            public string value;
            public double confidence;
            public long ts;
            public long expireAt;   // 0 = 永不过期；默认 30 天衰减
            public int hits;        // 被召回次数，越多越"记得牢"
        }

        private static readonly List<Entry> _entries = new List<Entry>();
        private static readonly List<Fact> _facts = new List<Fact>();
        private static double _mood;
        private static int _affinity = 50;
        private static bool _loaded;
        private static bool _dirty;
        private static float _lastSave;

        private const int MaxEntries = 60;
        private const int KeepAfterTrim = 30;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static string _saveId = "";
        public static string FilePath => string.IsNullOrEmpty(_saveId) ? "" :
            Path.Combine(AiCompanion.DataDir, "saves", _saveId, "ai-memory.json");

        /// <summary>
        /// One-time upgrade step: the conversation and memory now belong to the
        /// character card. Memory written under the old progress identity is
        /// copied into this character's scope — but only when this character has
        /// no memory yet AND there is exactly one legacy scope to take it from.
        /// More than one candidate is ambiguous, so nothing is copied rather than
        /// mixing two characters' memories. Scopes already written by the
        /// character-card era carry a `.character` marker and are never adopted.
        /// </summary>
        public static void AdoptLegacyScope(string legacyId)
        {
            try
            {
                if (string.IsNullOrEmpty(_saveId)) return;
                var root = Path.Combine(AiCompanion.DataDir, "saves");
                var target = Path.Combine(root, _saveId);
                if (File.Exists(Path.Combine(target, "ai-memory.json"))) return;
                if (!Directory.Exists(root)) return;
                var candidates = new List<string>();
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (name == _saveId) continue;
                    if (File.Exists(Path.Combine(dir, ".character"))) continue;   // already character-scoped
                    if (File.Exists(Path.Combine(dir, ".adopted"))) continue;      // someone took it
                    if (!File.Exists(Path.Combine(dir, "ai-memory.json"))) continue;
                    candidates.Add(dir);
                }
                if (candidates.Count != 1) return;
                // Prefer the running world's own progress scope when it is one of them.
                var source = candidates[0];
                if (!string.IsNullOrEmpty(legacyId))
                {
                    var exact = Path.Combine(root, legacyId);
                    if (candidates.Contains(exact)) source = exact;
                }
                Directory.CreateDirectory(target);
                File.Copy(Path.Combine(source, "ai-memory.json"), Path.Combine(target, "ai-memory.json"));
                var markdown = Path.Combine(source, "ai-memory.md");
                if (File.Exists(markdown)) File.Copy(markdown, Path.Combine(target, "ai-memory.md"), true);
                File.WriteAllText(Path.Combine(target, ".character"), _saveId, Utf8NoBom);
                try { File.WriteAllText(Path.Combine(source, ".adopted"), _saveId, Utf8NoBom); } catch { }
                _loaded = false;
                Load();
                DropOutlinePlugin.L?.LogInfo("[AI记忆] 已把旧进度身份的记忆接管到角色卡：" + Path.GetFileName(source));
            }
            catch (Exception e) { try { DropOutlinePlugin.L?.LogWarning("[AI记忆] 接管旧记忆失败: " + e.Message); } catch { } }
        }

        public static void SelectSave(string saveId)
        {
            if (saveId == _saveId) return;
            Save(true);
            _saveId = saveId ?? "";
            _entries.Clear(); _facts.Clear(); _mood = 0; _affinity = 50;
            _dirty = false; _loaded = false; _lastSave = -999f;
            _lastExtractCount = 0; _lastExtractAt = 0;
            // Unscoped legacy memory is intentionally NOT imported into any save.
            if (_saveId.Length > 0) Load();
        }

        public static double Mood { get { Load(); return _mood; } }
        public static int Affinity { get { Load(); return _affinity; } }

        // ───────────────────────── 读写 ─────────────────────────

        public static void Load()
        {
            if (_loaded || string.IsNullOrEmpty(_saveId)) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                var obj = MiniJson.Parse(File.ReadAllText(FilePath, Encoding.UTF8));

                var arr = MiniJson.AsList(MiniJson.Get(obj, "entries"));
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        var e = new Entry
                        {
                            kind = MiniJson.Get(item, "kind") as string ?? "chat",
                            tier = MiniJson.Get(item, "tier") as string ?? TierNormal,
                            text = MiniJson.Get(item, "text") as string,
                            ts = ToLong(MiniJson.Get(item, "ts")),
                            expireAt = ToLong(MiniJson.Get(item, "expireAt")),
                        };
                        if (!string.IsNullOrEmpty(e.text)) _entries.Add(e);
                    }
                }

                var facts = MiniJson.AsList(MiniJson.Get(obj, "facts"));
                if (facts != null)
                {
                    foreach (var item in facts)
                    {
                        var f = new Fact
                        {
                            key = MiniJson.Get(item, "key") as string,
                            value = MiniJson.Get(item, "value") as string,
                            confidence = ToDouble(MiniJson.Get(item, "confidence")),
                            ts = ToLong(MiniJson.Get(item, "ts")),
                            expireAt = ToLong(MiniJson.Get(item, "expireAt")),
                            hits = (int)ToLong(MiniJson.Get(item, "hits")),
                        };
                        if (!string.IsNullOrEmpty(f.key) && !string.IsNullOrEmpty(f.value)) _facts.Add(f);
                    }
                }

                _mood = ToDouble(MiniJson.Get(obj, "mood"));
                var aff = MiniJson.Get(obj, "affinity");
                if (aff is double d) _affinity = (int)d;

                Expire();
                try { _facts.RemoveAll(f => f.expireAt > 0 && f.expireAt < Now()); } catch { }
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[记忆] 读取失败: {e.Message}");
            }
        }

        private static long ToLong(object o)
        {
            if (o is double d) return (long)d;
            if (o is string s && long.TryParse(s, out var v)) return v;
            return 0;
        }

        private static double ToDouble(object o)
        {
            if (o is double d) return d;
            if (o is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            return 0;
        }

        public static void Save(bool force = false)
        {
            if (!_dirty || string.IsNullOrEmpty(_saveId)) return;
            if (!force && UnityEngine.Time.unscaledTime - _lastSave < 5f) return;
            _lastSave = UnityEngine.Time.unscaledTime;
            _dirty = false;

            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"mood\":").Append(_mood.ToString("0.###", CultureInfo.InvariantCulture));
                sb.Append(",\"affinity\":").Append(_affinity);
                sb.Append(",\"entries\":[");
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var e = _entries[i];
                    sb.Append("{\"kind\":\"").Append(MiniJson.Escape(e.kind)).Append("\",");
                    sb.Append("\"tier\":\"").Append(MiniJson.Escape(e.tier)).Append("\",");
                    sb.Append("\"ts\":").Append(e.ts).Append(',');
                    sb.Append("\"expireAt\":").Append(e.expireAt).Append(',');
                    sb.Append("\"text\":\"").Append(MiniJson.Escape(e.text)).Append("\"}");
                }
                sb.Append("],\"facts\":[");
                for (int i = 0; i < _facts.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var f = _facts[i];
                    sb.Append("{\"key\":\"").Append(MiniJson.Escape(f.key)).Append("\",");
                    sb.Append("\"value\":\"").Append(MiniJson.Escape(f.value)).Append("\",");
                    sb.Append("\"confidence\":").Append(f.confidence.ToString("0.##", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append("\"hits\":").Append(f.hits).Append(',');
                    sb.Append("\"expireAt\":").Append(f.expireAt).Append(',');
                    sb.Append("\"ts\":").Append(f.ts).Append('}');
                }
                sb.Append("]}");

                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Utf8NoBom);
                File.Move(tmp, FilePath, true);
                // Marks a scope that was written by the character-card era, so no
                // other character can later adopt it as if it were legacy.
                try { File.WriteAllText(Path.Combine(dir, ".character"), _saveId, Utf8NoBom); } catch { }
                ExportMarkdown();
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[记忆] 保存失败: {e.Message}");
            }
        }

        // ───────────────────────── 写入 ─────────────────────────

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static void AddChat(string speaker, string text, string tier = TierNormal)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_saveId)) return;
            Load();
            AddEntry("chat", tier, $"{speaker}：{text.Trim()}");
        }

        public static void AddEvent(string text, string tier = TierNormal)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_saveId)) return;
            Load();
            AddEntry("event", tier, text.Trim());
        }

        private static void AddEntry(string kind, string tier, string text)
        {
            if (text.Length > 500) text = text.Substring(0, 500);

            // 一分钟内重复的话不重复记
            if (_entries.Count > 0)
            {
                var last = _entries[_entries.Count - 1];
                if (last.text == text && Now() - last.ts < 60) return;
            }

            long now = Now();
            _entries.Add(new Entry
            {
                kind = kind,
                tier = tier ?? TierNormal,
                text = text,
                ts = now,
                expireAt = ExpireAt(tier, now),
            });
            _dirty = true;

            // 只保留最近 N 条（critical 不丢）
            if (_entries.Count > MaxEntries + 20) Trim();
        }

        private static long ExpireAt(string tier, long now)
        {
            switch (tier)
            {
                case TierImportant: return now + 30L * 24 * 3600;
                case TierCritical: return 0;
                default: return now + 7L * 24 * 3600;
            }
        }

        private static void Trim()
        {
            var kept = new List<Entry>();
            int excess = _entries.Count - KeepAfterTrim;
            foreach (var e in _entries)
            {
                if (excess > 0 && e.tier == TierNormal) { excess--; continue; }
                kept.Add(e);
            }
            _entries.Clear();
            _entries.AddRange(kept);
            _dirty = true;
        }

        /// <summary>清掉过期日记（普通一周 / 重要一月 / 非常重要永不）。</summary>
        public static void Expire()
        {
            Load();
            long now = Now();
            int before = _entries.Count;
            _entries.RemoveAll(e => e.expireAt > 0 && e.expireAt < now);
            if (_entries.Count != before) _dirty = true;
        }

        // ───────────────────────── 心情 / 好感 ─────────────────────────

        public static void NudgeMood(double delta)
        {
            Load();
            _mood = Math.Max(-1.0, Math.Min(1.0, _mood + delta));
            _dirty = true;
        }

        public static void NudgeAffinity(int delta)
        {
            Load();
            _affinity = Math.Max(0, Math.Min(100, _affinity + delta));
            _dirty = true;
        }

        /// <summary>给系统提示用的一句话情绪描述。</summary>
        public static string MoodHint()
        {
            Load();
            string mood = _mood < -0.4 ? "你现在心情低落，说话偏闷" :
                          _mood < -0.1 ? "你有点疲惫" :
                          _mood > 0.5 ? "你现在心情不错，话会多一点" :
                          _mood > 0.1 ? "你状态平稳，语气轻松" : "你比较平静";
            string aff = _affinity >= 80 ? "你和用户已经很熟了，说话随意" :
                         _affinity >= 60 ? "你把用户当可靠同伴" :
                         _affinity >= 40 ? "你对用户还在观察" : "你对用户有点保留";
            return $"{mood}；{aff}。";
        }

        // ───────────────────────── 检索 ─────────────────────────

        /// <summary>给 prompt 用的记忆块：相关事实 + 最近 3 条日记 + 待核实。</summary>
        public static string BuildContext(string query, int recent = 3, int relevantFacts = 4)
        {
            Load();
            var sb = new StringBuilder();

            // 1) 相关事实
            if (_facts.Count > 0)
            {
                var scored = new List<(Fact f, int s)>();
                foreach (var f in _facts)
                {
                    int s = Score(query, f.key + f.value);
                    if (s > 0 || _facts.Count <= 6) scored.Add((f, s));
                }
                scored.Sort((a, b) => b.s.CompareTo(a.s));
                if (scored.Count > 0)
                {
                    sb.AppendLine("【关于用户】");
                    for (int i = 0; i < scored.Count && i < relevantFacts; i++)
                        sb.Append("- ").Append(scored[i].f.key).Append("：").Append(scored[i].f.value).AppendLine();
                }
            }

            // 2) 最近日记
            if (_entries.Count > 0)
            {
                sb.AppendLine("【最近发生】");
                int start = Math.Max(0, _entries.Count - recent);
                for (int i = start; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    sb.Append("- ");
                    if (e.tier == TierCritical) sb.Append("【重要】");
                    else if (e.tier == TierImportant) sb.Append("【记着】");
                    sb.AppendLine(e.text);
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static int Score(string query, string text)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text)) return 0;
            int score = 0;
            for (int i = 0; i + 1 < query.Length; i += 2)
            {
                var gram = query.Substring(i, 2);
                if (gram.Trim().Length == 0) continue;
                if (text.IndexOf(gram, StringComparison.Ordinal) >= 0) score++;
            }
            return score;
        }

        // ───────────────────────── 批量抽取（省 token 的关键） ─────────────────────────

        private static int _lastExtractCount;
        private static float _lastExtractAt;

        /// <summary>是否该做一次「记忆抽取」：攒够 6 条新记录或过了 180 秒。</summary>
        public static bool ShouldExtract()
        {
            Load();
            int newCount = _entries.Count - _lastExtractCount;
            if (newCount >= 6) return true;
            return newCount > 0 && UnityEngine.Time.unscaledTime - _lastExtractAt > 180f;
        }

        /// <summary>
        /// 一次 LLM 调用同时做三件事：抽事实、给最近聊天打重要级别、更新心情/好感。
        /// 只处理上次抽取之后的新条目，输入很短。
        /// </summary>
        public static void Extract(LlmSettings st, string npcName, string playerName)
        {
            Load();
            if (st == null || !st.IsConfigured) return;
            if (_entries.Count == 0) return;

            int startIdx = Math.Min(_lastExtractCount, _entries.Count);
            var recent = new StringBuilder();
            for (int i = Math.Max(0, startIdx); i < _entries.Count; i++)
                recent.AppendLine("- " + _entries[i].text);

            if (recent.Length == 0) return;

            _lastExtractCount = _entries.Count;
            _lastExtractAt = UnityEngine.Time.unscaledTime;

            var system =
                "你在帮一个末日游戏里的 AI 搭档整理记忆。读下面的对话/事件，输出一个 JSON：\n" +
                "{\"facts\":[{\"key\":\"玩家习惯|喜好|承诺|关系|其他\",\"value\":\"不超过20字\",\"confidence\":0.0}],\n" +
                " \"tiers\":[{\"index\":0,\"tier\":\"normal|important|critical\"}],\n" +
                " \"moodDelta\":0.0,\"affinityDelta\":0}\n" +
                "规则：只抽对以后有用的信息；没把握的 confidence 给 0.4 以下；tiers 只标注明显重要/关键的条目；" +
                "moodDelta 范围 -0.3~0.3，affinityDelta 范围 -3~3。没有可抽的就返回空数组。只输出 JSON。";

            var user = $"搭档：{npcName}；玩家：{playerName}。\n最近记录：\n{recent}";

            var r = LlmClient.Ask(st, system, user);
            if (!r.Ok)
            {
                DropOutlinePlugin.L.LogWarning($"[记忆] 抽取失败: {r.Error}");
                return;
            }

            try
            {
                var obj = MiniJson.Parse(r.Text ?? "");
                float delta = 0;
                var md = MiniJson.Get(obj, "moodDelta");
                if (md is double mdv) delta = (float)mdv;
                var ad = MiniJson.Get(obj, "affinityDelta");
                if (ad is double adv) NudgeAffinity((int)adv);
                NudgeMood(delta);

                var facts = MiniJson.AsList(MiniJson.Get(obj, "facts"));
                int added = 0;
                if (facts != null)
                {
                    foreach (var item in facts)
                    {
                        var key = MiniJson.Get(item, "key") as string;
                        var val = MiniJson.Get(item, "value") as string;
                        var conf = MiniJson.Get(item, "confidence");
                        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(val)) continue;
                        double c = conf is double cd ? cd : 0.5;
                        UpsertFact(key, val, c);
                        added++;
                    }
                }

                var tiers = MiniJson.AsList(MiniJson.Get(obj, "tiers"));
                if (tiers != null)
                {
                    foreach (var item in tiers)
                    {
                        var idxObj = MiniJson.Get(item, "index");
                        var tier = MiniJson.Get(item, "tier") as string;
                        if (idxObj is double didx && tier != null)
                        {
                            int idx = Math.Max(0, startIdx) + (int)didx;
                            if (idx >= 0 && idx < _entries.Count)
                            {
                                _entries[idx].tier = tier;
                                _entries[idx].expireAt = ExpireAt(tier, _entries[idx].ts);
                            }
                        }
                    }
                }

                _dirty = true;
                Save(true);
                DropOutlinePlugin.L.LogInfo(
                    $"[记忆] 抽取完成：新增/更新事实 {added} 条，心情 {_mood:0.00}，好感 {_affinity}");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[记忆] 抽取解析失败: {e.Message}");
            }
        }

        private static void UpsertFact(string key, string value, double confidence)
        {
            long now = Now();
            foreach (var f in _facts)
            {
                if (f.key == key && f.value == value)
                {
                    f.confidence = Math.Max(f.confidence, confidence);
                    f.ts = now;
                    f.expireAt = now + 30L * 24 * 3600;   // 再次确认 → 续命 30 天
                    return;
                }
            }
            _facts.Add(new Fact
            {
                key = key,
                value = value,
                confidence = confidence,
                ts = now,
                expireAt = now + 30L * 24 * 3600,
                hits = 0,
            });
            if (_facts.Count > 40) _facts.RemoveAt(0);
        }

        /// <summary>
        /// 导出人类可读版记忆（Obsidian / 记事本都能直接看和改）。
        /// 参考 Basic Memory 的思路：记忆必须是"文件"，不是只有程序能读的二进制。
        /// </summary>
        public static void ExportMarkdown()
        {
            if (string.IsNullOrEmpty(_saveId)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                Load();
                var sb = new StringBuilder();
                sb.AppendLine("# AI伙伴 记忆");
                sb.AppendLine();
                sb.AppendLine($"> 更新时间：{DateTime.Now:yyyy-MM-dd HH:mm}　心情 {_mood:0.00}　好感 {_affinity}");
                sb.AppendLine();
                sb.AppendLine("## 关于玩家（事实）");
                sb.AppendLine();
                sb.AppendLine("| 类别 | 内容 | 置信度 | 剩余有效期 |");
                sb.AppendLine("| --- | --- | --- | --- |");
                long now = Now();
                foreach (var f in _facts)
                {
                    string ttl = f.expireAt <= 0 ? "永久" : $"{(f.expireAt - now) / 86400} 天";
                    sb.AppendLine($"| {f.key} | {f.value} | {f.confidence:0.0} | {ttl} |");
                }
                sb.AppendLine();
                sb.AppendLine("## 最近记录");
                sb.AppendLine();
                int startIdx = Math.Max(0, _entries.Count - 40);
                for (int i = startIdx; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    string mark = e.tier == TierCritical ? "**[重要]** " : e.tier == TierImportant ? "*(记着)* " : "";
                    sb.AppendLine($"- {mark}{e.text}");
                }
                if (string.IsNullOrEmpty(_saveId)) return;
                var path = Path.Combine(Path.GetDirectoryName(FilePath), "ai-memory.md");
                File.WriteAllText(path, sb.ToString(), Utf8NoBom);
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[记忆] 导出 Markdown 失败: {e.Message}");
            }
        }

        /// <summary>置信度不足、还没被确认的事实（面板/日志里能看到）。</summary>
        public static string PendingFacts()
        {
            Load();
            var sb = new StringBuilder();
            foreach (var f in _facts)
                if (f.confidence < 0.5) sb.AppendLine($"{f.key}：{f.value}（{f.confidence:0.0}）");
            return sb.ToString().TrimEnd();
        }
    }
}



