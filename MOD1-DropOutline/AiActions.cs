using System;
using System.Collections.Generic;

namespace Dropoutline
{
    /// <summary>
    /// 直接操作类动作（手电开关等）。原则同丢弃/合成：只调游戏自己的方法，
    /// 不模拟键鼠；动作后用可观察状态校验，没生效就如实说，不谎报。
    /// </summary>
    internal static class AiActions
    {
        internal static AiTaskStep Flashlight(HumanCharacterController player, AiTaskRequest request)
        {
            if (player == null) return new AiTaskStep("failed", "玩家角色不可操作");
            bool? want = ParseWanted(request.Target);
            bool before;
            if (!TryReadLight(player, out before))
                return new AiTaskStep("failed", "读不到手电状态；这次没有操作，也没有假装成功");

            bool target = want ?? !before;
            if (target == before)
                return new AiTaskStep("done", target ? "手电本来就是开着的，不用再开" : "手电本来就是关着的", true);

            try
            {
                if (target) player.TurnOnLight();
                else player.TurnOffLight();
            }
            catch (Exception error)
            {
                return new AiTaskStep("failed", "调用手电开关失败：" + error.Message);
            }

            bool after;
            if (!TryReadLight(player, out after) || after != target)
                return new AiTaskStep("failed", "手电没有按预期切换（当前" + (after ? "开" : "关") + "）；可能是手持设备槽没有手电，请先装备手电");
            return new AiTaskStep("done", target ? "手电已打开" : "手电已关闭", true);
        }

        private static bool? ParseWanted(string target)
        {
            var text = (target ?? "").Trim().ToLowerInvariant();
            if (text.Length == 0) return null;
            if (text == "on" || text == "开" || text == "打开" || text == "亮") return true;
            if (text == "off" || text == "关" || text == "关闭" || text == "灭") return false;
            return null;
        }

        /// <summary>手电是否亮着：优先看 Light2D.enabled，读不到再看强度。</summary>
        private static bool TryReadLight(HumanCharacterController player, out bool on)
        {
            on = false;
            try
            {
                var light = player.handFlashLight;
                if (light == null) return false;
                try { on = light.enabled; return true; } catch { }
                try { on = light.intensity > 0.001f; return true; } catch { }
                return false;
            }
            catch { return false; }
        }

        // ── 轻量目标高亮（供 API 基础模式使用：只标一个目标，10 秒自动回收） ──
        private static UnityEngine.GameObject _activeHighlight;
        private static float _highlightUntil;

        internal static void ClearHighlight()
        {
            if (_activeHighlight == null) return;
            try
            {
                var gc = GameController.instance;
                if (gc != null) gc.PushItemHighlightObject(_activeHighlight);
                else _activeHighlight.SetActive(false);
            }
            catch { }
            _activeHighlight = null;
            _highlightUntil = 0f;
        }

        internal static void TickHighlight()
        {
            if (_activeHighlight == null) return;
            if (DropOutlinePlugin.WorldTearingDown || UnityEngine.Time.unscaledTime >= _highlightUntil)
                ClearHighlight();
        }

        internal static bool HighlightTarget(string kind, out string summary)
        {
            summary = "";
            if (DropOutlinePlugin.WorldTearingDown) return false;

            var k = (kind ?? "").Trim().ToLowerInvariant();
            if (k == "clear" || k == "none" || k == "cancel")
            {
                ClearHighlight();
                summary = "已取消目标标记";
                return true;
            }

            try
            {
                var gc = GameController.instance;
                var player = gc != null ? gc.playerCharacter : null;
                if (gc == null || player == null) return false;

                var mc = MapController.instance;
                var cam = UnityEngine.Camera.main;
                UnityEngine.Vector2 pPos = player.transform.position;
                bool wantThreat = k == "nearest_threat" || k == "threat" || k == "enemy";
                UnityEngine.Vector3? found = null;
                string label = "";

                if (wantThreat)
                {
                    float best = 36f;
                    int pCamp = player.characterData != null ? player.characterData.camp : 0;
                    int? pFloor = player.characterData != null ? (int?)player.characterData.zLevel : null;
                    var list = gc.characterList;
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            var c = list[i];
                            if (c == null || c == player || c.deadFlag || c.characterData == null) continue;
                            if (c.characterData.deadFlag || c.characterData.camp == pCamp) continue;
                            if (pFloor.HasValue && c.characterData.zLevel != pFloor.Value) continue;
                            UnityEngine.Vector3 cp = c.transform.position;
                            if (cam != null)
                            {
                                var vp = cam.WorldToViewportPoint(cp);
                                if (vp.z <= 0f || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) continue;
                            }
                            if (mc != null && !mc.IsInSightByRaycast(cp)) continue;
                            float d = UnityEngine.Vector2.Distance(new UnityEngine.Vector2(cp.x, cp.y), pPos);
                            if (d < best)
                            {
                                best = d;
                                found = cp;
                                label = string.IsNullOrEmpty(c.characterData.characterName) ? "敌对目标" : c.characterData.characterName;
                            }
                        }
                    }
                    if (!found.HasValue) { summary = "周围 36 米内暂无可见威胁"; return false; }
                    label = $"{label}（约 {best:F0} 米）";
                }
                else
                {
                    float best = 32f;
                    var items = mc != null ? mc.allGroundItems : null;
                    if (items != null)
                    {
                        for (int i = 0; i < items.Count; i++)
                        {
                            var it = items[i];
                            if (it == null || it.itemData == null || it.gameObject == null || !it.gameObject.activeInHierarchy) continue;
                            if (!it.isOnScreen || !it.isInRange || it.standHiddenFlag || it.submergedFlag) continue;
                            UnityEngine.Vector3 ip = it.transform.position;
                            if (!mc.IsInSightByRaycast(ip)) continue;
                            float d = UnityEngine.Vector2.Distance(new UnityEngine.Vector2(ip.x, ip.y), pPos);
                            if (d < best)
                            {
                                best = d;
                                found = ip;
                                try { label = it.itemData.GetItemName(); } catch { }
                                if (string.IsNullOrEmpty(label)) label = "地面物资";
                            }
                        }
                    }
                    if (!found.HasValue) { summary = "附近暂无视线内可标记的地面物资"; return false; }
                    label = $"{label}（约 {best:F0} 米）";
                }

                ClearHighlight();
                var go = gc.PullItemHighlightObject();
                if (go == null) { summary = "当前无法创建高亮标记"; return false; }
                go.transform.position = found.Value;
                go.SetActive(true);
                _activeHighlight = go;
                _highlightUntil = UnityEngine.Time.unscaledTime + 10f;
                summary = "已标出：" + label;
                return true;
            }
            catch (Exception e)
            {
                summary = "标记失败：" + e.Message;
                return false;
            }
        }
    }
}
