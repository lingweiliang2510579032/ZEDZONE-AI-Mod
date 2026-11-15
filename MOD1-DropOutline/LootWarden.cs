using System;
using System.Collections.Generic;
using BepInEx;
using UnityEngine;

namespace Dropoutline
{
    /// <summary>
    /// 掉落物守卫：盯着玩家脚边一直没被捡走的东西，躺够久了就提醒一句。
    ///
    /// 防呆规则（用户反馈"有点呆"后加的）：
    ///   · 按**物品 id** 跟踪，不按坐标——同一件东西换个位置不会再提醒一次；
    ///   · 同一件最多提醒 2 次，之后静默；
    ///   · 玩家说"不要/不用"时，附近的东西直接标记为"已放弃"，不再提；
    ///   · 战斗中不打扰；全局提醒间隔 ≥ 90 秒。
    /// </summary>
    internal static class LootWarden
    {
        private sealed class Tracked
        {
            public float firstSeen;
            public int reminders;
            public bool dismissed;
            public string name;
        }

        private static readonly Dictionary<int, Tracked> _items = new Dictionary<int, Tracked>();
        private static readonly List<int> _seen = new List<int>();
        private static float _lastScan;
        private static float _lastRemind;

        public static void ResetWorld()
        {
            _items.Clear(); _seen.Clear(); _lastScan = 0f; _lastRemind = 0f;
        }

        private const float RangeMeters = 12f;
        private const float RemindAfterSeconds = 75f;
        private const float GlobalCooldown = 90f;
        private const int MaxReminders = 2;

        /// <summary>每 2 秒扫一次；返回一句提醒（没有就返回 null）。</summary>
        /// <summary>给外部大脑看的线索：不消耗提醒次数、不触发冷却。</summary>
        public static string PendingSummary() => Scan(0, false);

        public static string Scan(int threatCount, bool allowSpeak = true)
        {
            // 进世界 8 秒内不扫：此时 MapController 的地面物品列表还没稳定，读它会崩
            if (DropOutlinePlugin.WorldSince <= 0f || Time.unscaledTime - DropOutlinePlugin.WorldSince < 8f) return null;
            if (allowSpeak && Time.unscaledTime - _lastScan < 2f) return null;
            if (allowSpeak) _lastScan = Time.unscaledTime;

            MapController mc = null;
            try
            {
                foreach (var m in Resources.FindObjectsOfTypeAll<MapController>())
                {
                    if (m != null && m.gameObject != null && m.gameObject.activeInHierarchy) { mc = m; break; }
                }
            }
            catch { }
            if (mc == null) return null;

            Vector2 player;
            try
            {
                var gc = GameController.instance;
                if (gc == null || gc.playerCharacter == null) return null;
                var pw = gc.playerWorldPosition;
                player = new Vector2(pw.x, pw.y);
            }
            catch { return null; }

            Il2CppSystem.Collections.Generic.List<BasicItem> items;
            try { items = mc.GetAllGroundItems(); }
            catch (Exception e) { DropOutlinePlugin.L.LogWarning($"[掉落提示] 取地面物品失败: {e.Message}"); return null; }
            if (items == null) return null;

            float now = Time.unscaledTime;
            _seen.Clear();

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it == null || it.itemData == null || it.gameObject == null || !it.gameObject.activeInHierarchy || !it.isOnScreen || it.standHiddenFlag || it.submergedFlag) continue;
                if (!mc.IsInSightByRaycast(it.transform.position)) continue;
                int id = it.itemData.itemId;

                Vector2 p;
                try { p = new Vector2(it.transform.position.x, it.transform.position.y); }
                catch { continue; }
                if (Vector2.Distance(p, player) > RangeMeters) continue;

                _seen.Add(id);
                if (!_items.TryGetValue(id, out var t))
                {
                    string nm = null;
                    try { nm = it.itemData.GetItemName(); } catch { }
                    _items[id] = new Tracked { firstSeen = now, name = nm };
                }
            }

            // 离开视野很久的东西就忘掉，回来重新计时
            if (_items.Count > _seen.Count + 30)
            {
                var stale = new List<int>();
                foreach (var kv in _items)
                    if (!_seen.Contains(kv.Key) && now - kv.Value.firstSeen > 300f) stale.Add(kv.Key);
                foreach (var k in stale) _items.Remove(k);
            }

            if (allowSpeak && threatCount > 0) return null;
            if (allowSpeak && now - _lastRemind < GlobalCooldown) return null;

            int bestId = 0;
            float bestAge = 0f;
            foreach (var kv in _items)
            {
                var t = kv.Value;
                if (t.dismissed) continue;
                if (allowSpeak && t.reminders >= MaxReminders) continue;
                float age = now - t.firstSeen;
                if (age < RemindAfterSeconds) continue;
                if (age > bestAge) { bestAge = age; bestId = kv.Key; }
            }
            if (bestId == 0) return null;

            var chosen = _items[bestId];
            if (allowSpeak) { chosen.reminders++; _lastRemind = now; }

            string name = string.IsNullOrEmpty(chosen.name)
                ? ModLocalization.Pick("东西", "item", "Gegenstand", "objeto", "objet", "アイテム", "아이템", "przedmiot", "item", "предмет")
                : chosen.name;
            bool longWait = bestAge > 150f;
            return longWait
                ? ModLocalization.Pick(
                    $"脚边那件「{name}」躺了好一会儿了，不捡？",
                    $"\"{name}\" has been sitting by your feet for a while — pick it up?",
                    $"„{name}“ liegt schon eine Weile vor deinen Füßen — mitnehmen?",
                    $"\"{name}\" lleva rato en el suelo — ¿lo recoges?",
                    $"« {name} » traîne à vos pieds depuis un moment — on le prend ?",
                    $"足元の「{name}」、ずっと落ちたままですよ。拾いますか？",
                    $"발밑의 \"{name}\"이(가) 한참째 떨어져 있어요. 안 주울 건가요?",
                    $"„{name}” leży pod nogami już od dłuższego czasu — podniesiesz?",
                    $"\"{name}\" está no chão há um tempinho — vai pegar?",
                    $"«{name}» уже давно лежит под ногами — подберёшь?")
                : ModLocalization.Pick(
                    $"脚边那件「{name}」躺了一分多钟了，不捡？",
                    $"\"{name}\" has been on the ground for over a minute — pick it up?",
                    $"„{name}“ liegt schon über eine Minute am Boden — mitnehmen?",
                    $"\"{name}\" lleva más de un minuto en el suelo — ¿lo recoges?",
                    $"« {name} » est au sol depuis plus d'une minute — on le prend ?",
                    $"足元の「{name}」、1分ほど落ちたままですよ。拾いますか？",
                    $"발밑의 \"{name}\"이(가) 1분 넘게 떨어져 있어요. 안 주울 건가요?",
                    $"„{name}” leży na ziemi ponad minutę — podniesiesz?",
                    $"\"{name}\" está no chão há mais de um minuto — vai pegar?",
                    $"«{name}» лежит на земле уже больше минуты — подберёшь?");
        }

        /// <summary>附近（可感知范围内）物品清单：名字×数量，给外部大脑看，不消耗提醒次数。</summary>
        public static string NearbySummary(float range = 10f)
        {
            try
            {
                MapController mc = null;
                foreach (var m in Resources.FindObjectsOfTypeAll<MapController>())
                    if (m != null && m.gameObject != null && m.gameObject.activeInHierarchy) { mc = m; break; }
                if (mc == null) return "";
                var gc = GameController.instance;
                if (gc == null || gc.playerCharacter == null) return "";
                var pw = gc.playerWorldPosition;
                var player = new Vector2(pw.x, pw.y);
                var items = mc.GetAllGroundItems();
                if (items == null) return "";

                var counts = new Dictionary<string, int>();
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it == null || it.itemData == null || it.gameObject == null || !it.gameObject.activeInHierarchy || !it.isOnScreen || it.standHiddenFlag || it.submergedFlag) continue;
                if (!mc.IsInSightByRaycast(it.transform.position)) continue;
                    Vector2 p;
                    try { p = new Vector2(it.transform.position.x, it.transform.position.y); } catch { continue; }
                    if (Vector2.Distance(p, player) > range) continue;
                    string name = null;
                    try { name = it.itemData.GetItemName(); } catch { }
                    if (string.IsNullOrEmpty(name)) continue;
                    counts.TryGetValue(name, out int n);
                    counts[name] = n + 1;
                }
                if (counts.Count == 0) return "";
                var sb = new System.Text.StringBuilder();
                int shown = 0;
                foreach (var kv in counts)
                {
                    if (shown++ > 0) sb.Append('、');
                    sb.Append(kv.Key);
                    if (kv.Value > 1) sb.Append('x').Append(kv.Value);
                    if (shown >= 8) break;
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>玩家说"不要"时调用：把附近的物品标记为放弃，别再念叨。</summary>
        public static void DismissNearby()
        {
            foreach (var kv in _items) kv.Value.dismissed = true;
            DropOutlinePlugin.L.LogInfo($"[掉落提示] 玩家表示不要，{_items.Count} 件物品不再提醒");
        }

        public static void Reset()
        {
            _items.Clear();
            _seen.Clear();
            _lastScan = 0f;
            _lastRemind = 0f;
        }
    }
}

