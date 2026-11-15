using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

namespace Dropoutline
{
    /// <summary>
    /// 内置 BepInEx 插件管理器（从「MOD 设置 → 插件管理器」按钮打开，不占用任何热键）。
    ///
    /// 游戏自带的 MOD管理器只认 .zedmod 内容包，不认代码插件；这里自己扫描
    /// BepInEx\plugins 下的 DLL：启用/禁用 = 改扩展名（下次启动生效），卸载 = 删文件。
    /// </summary>
    internal static class PluginManagerUi
    {
        private sealed class Entry
        {
            public string path;
            public string name;
            public long size;
            public bool enabled;
        }

        private static GameObject _root;
        private static RectTransform _list;
        private static Text _status;
        private static readonly List<Entry> _entries = new List<Entry>();
        private static int _page;
        private const int PageSize = 9;
        private static Font _font;

        public static bool IsOpen => _root != null && _root.activeInHierarchy;

        public static void Toggle()
        {
            if (IsOpen) { Close(); return; }
            Open();
        }

        public static void Open()
        {
            try
            {
                if (_root == null) Build();
                _root.SetActive(true);
                _page = 0;
                Refresh();
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[管理器] 打开失败: {e.Message}");
            }
        }

        public static void Close()
        {
            if (_root != null) _root.SetActive(false);
        }

        public static void Refresh()
        {
            _entries.Clear();
            try
            {
                var roots = new List<string>();
                try { roots.Add(Paths.PluginPath); } catch { }
                var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx", "plugins");
                if (Directory.Exists(local) && !roots.Contains(local)) roots.Add(local);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories))
                    {
                        var full = Path.GetFullPath(f);
                        if (seen.Add(full)) Add(full, true);
                    }
                    foreach (var f in Directory.GetFiles(root, "*.dll.disabled", SearchOption.AllDirectories))
                    {
                        var full = Path.GetFullPath(f);
                        if (seen.Add(full)) Add(full, false);
                    }
                }
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[管理器] 扫描失败: {e.Message}");
            }
            _entries.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            Render();
        }

        /// <summary>DLL 文件名 → 玩家能看懂的 MOD 名。</summary>
        private static string DisplayName(string fileName)
        {
            var lower = fileName.ToLowerInvariant();
            if (lower.Contains("zedai") || lower.Contains("dropoutline") || lower.Contains("yuyeai")) return "ZED · AI伙伴";
            if (lower.Contains("zedhighlight") || lower.Contains("yuyehighlight")) return "ZED · 持续高亮轮廓";
            if (lower.Contains("yuyepluginmanager")) return "插件管理器";
            return fileName;
        }

        private static void Add(string path, bool enabled)
        {
            var name = DisplayName(Path.GetFileName(path));
            if (!enabled && name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".disabled".Length);
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            _entries.Add(new Entry { path = path, name = name, size = size, enabled = enabled });
        }

        private static void ToggleEntry(Entry e)
        {
            try
            {
                if (e.enabled) { File.Move(e.path, e.path + ".disabled"); SetStatus($"已禁用 {e.name}（重启游戏生效）"); }
                else { File.Move(e.path, e.path.Substring(0, e.path.Length - ".disabled".Length)); SetStatus($"已启用 {e.name}（重启游戏生效）"); }
                Refresh();
            }
            catch (Exception ex) { SetStatus($"操作失败：{ex.Message}（请先退出游戏）"); }
        }

        private static void Uninstall(Entry e)
        {
            try { File.Delete(e.path); SetStatus($"已删除 {e.name}"); Refresh(); }
            catch (Exception ex) { SetStatus($"删除失败：{ex.Message}（请先退出游戏）"); }
        }

        private static void Build()
        {
            _root = new GameObject("YuyePluginManagerCanvas");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();

            var bg = NewRect(_root.transform, "BG", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            bg.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.85f);

            var panel = NewRect(_root.transform, "Panel", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(960, 720));
            panel.gameObject.AddComponent<Image>().color = new Color(0.07f, 0.07f, 0.07f, 0.98f);

            var title = NewText(panel, "Title", "BepInEx 插件管理器", 34, TextAnchor.MiddleLeft);
            title.rectTransform.anchoredPosition = new Vector2(24, 314);
            title.rectTransform.sizeDelta = new Vector2(600, 48);

            var hint = NewText(panel, "Hint", "禁用/启用：改扩展名，重启游戏生效；卸载：直接删文件", 20, TextAnchor.MiddleLeft);
            hint.color = new Color(0.7f, 0.75f, 0.7f);
            hint.rectTransform.anchoredPosition = new Vector2(24, 280);
            hint.rectTransform.sizeDelta = new Vector2(780, 28);

            var close = NewButton(panel, "Close", "✕", new Vector2(430, 314), new Vector2(64, 44), Close);
            close.GetComponent<Image>().color = new Color(0.35f, 0.08f, 0.08f);
            var refresh = NewButton(panel, "Refresh", "刷新", new Vector2(330, 314), new Vector2(96, 44), Refresh);
            refresh.GetComponent<Image>().color = new Color(0.1f, 0.28f, 0.1f);

            _list = NewRect(panel, "List", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -10), new Vector2(900, 480));

            NewButton(panel, "Prev", "▲", new Vector2(-430, -290), new Vector2(70, 44), () => { if (_page > 0) { _page--; Render(); } });
            NewButton(panel, "Next", "▼", new Vector2(-350, -290), new Vector2(70, 44), () => { _page++; Render(); });

            _status = NewText(panel, "Status", "", 20, TextAnchor.MiddleLeft);
            _status.color = new Color(0.55f, 0.9f, 0.6f);
            _status.rectTransform.anchoredPosition = new Vector2(24, -322);
            _status.rectTransform.sizeDelta = new Vector2(780, 28);
        }

        private static void Render()
        {
            if (_list == null) return;
            for (int i = _list.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_list.GetChild(i).gameObject);

            // 我们的两个 MOD：同一个 DLL，但对外是两个 MOD，各自独立开关
            // 本插件只代表 AI伙伴；「持续高亮轮廓」是另一个独立插件（YuyeHighlight.dll）
            RenderOwnMod("ZED · AI伙伴", "AI 搭档：读局面、说话、可接管队友", AiCompanion.CfgAiEnabled, 180);

            int total = _entries.Count;
            int pages = Mathf.Max(1, Mathf.CeilToInt(total / (float)PageSize));
            if (_page >= pages) _page = pages - 1;
            if (_page < 0) _page = 0;

            int start = _page * PageSize;
            for (int i = 0; i < PageSize && start + i < total; i++)
            {
                var e = _entries[start + i];
                if (e.name == "ZED · AI伙伴") continue;   // 已由上面 RenderOwnMod 代表
                float y = 100 - i * 50;

                var label = NewText(_list, "Name", e.name, 22, TextAnchor.MiddleLeft);
                label.rectTransform.anchoredPosition = new Vector2(-180, y);
                label.rectTransform.sizeDelta = new Vector2(420, 40);
                label.color = e.enabled ? new Color(0.92f, 0.92f, 0.92f) : new Color(0.55f, 0.55f, 0.55f);

                var size = NewText(_list, "Size", $"{e.size / 1024}KB", 18, TextAnchor.MiddleRight);
                size.rectTransform.anchoredPosition = new Vector2(120, y);
                size.rectTransform.sizeDelta = new Vector2(90, 40);
                size.color = new Color(0.6f, 0.6f, 0.6f);

                var state = NewText(_list, "State", e.enabled ? "启用中" : "已禁用", 19, TextAnchor.MiddleCenter);
                state.rectTransform.anchoredPosition = new Vector2(215, y);
                state.rectTransform.sizeDelta = new Vector2(90, 40);
                state.color = e.enabled ? new Color(0.5f, 0.95f, 0.55f) : new Color(0.9f, 0.7f, 0.35f);

                var entry = e;
                var toggle = NewButton(_list, "Toggle", e.enabled ? "禁用" : "启用", new Vector2(320, y), new Vector2(84, 38), () => ToggleEntry(entry));
                toggle.GetComponent<Image>().color = e.enabled ? new Color(0.35f, 0.22f, 0.05f) : new Color(0.1f, 0.3f, 0.12f);

                var del = NewButton(_list, "Del", "卸载", new Vector2(410, y), new Vector2(84, 38), () => Uninstall(entry));
                del.GetComponent<Image>().color = new Color(0.38f, 0.08f, 0.08f);
            }

            if (total == 0)
            {
                var empty = NewText(_list, "Empty", "（没有找到插件）", 22, TextAnchor.MiddleCenter);
                empty.rectTransform.anchoredPosition = Vector2.zero;
                empty.rectTransform.sizeDelta = new Vector2(600, 40);
                empty.color = new Color(0.6f, 0.6f, 0.6f);
            }

            SetStatus($"{total} 个插件 · 第 {_page + 1}/{pages} 页");
        }

        /// <summary>渲染我们自己的两个 MOD（共用一个 DLL，但各自有独立开关）。</summary>
        private static void RenderOwnMod(string name, string desc, BepInEx.Configuration.ConfigEntry<bool> flag, float y)
        {
            bool on = flag != null && flag.Value;

            var label = NewText(_list, "Own", name, 22, TextAnchor.MiddleLeft);
            label.rectTransform.anchoredPosition = new Vector2(-180, y);
            label.rectTransform.sizeDelta = new Vector2(420, 26);
            label.color = on ? new Color(0.95f, 0.95f, 0.95f) : new Color(0.55f, 0.55f, 0.55f);

            var sub = NewText(_list, "OwnDesc", desc, 16, TextAnchor.MiddleLeft);
            sub.rectTransform.anchoredPosition = new Vector2(-180, y - 18);
            sub.rectTransform.sizeDelta = new Vector2(420, 20);
            sub.color = new Color(0.62f, 0.66f, 0.62f);

            long size = 0;
            try
            {
                var dll = Path.Combine(Paths.PluginPath, "ZEDAI.dll");
                if (File.Exists(dll)) size = new FileInfo(dll).Length;
            }
            catch { }
            var sizeText = NewText(_list, "OwnSize", size > 0 ? $"{size / 1024}KB" : "-", 18, TextAnchor.MiddleRight);
            sizeText.rectTransform.anchoredPosition = new Vector2(120, y);
            sizeText.rectTransform.sizeDelta = new Vector2(90, 40);
            sizeText.color = new Color(0.6f, 0.6f, 0.6f);

            var state = NewText(_list, "OwnState", on ? "启用中" : "已禁用", 19, TextAnchor.MiddleCenter);
            state.rectTransform.anchoredPosition = new Vector2(215, y);
            state.rectTransform.sizeDelta = new Vector2(90, 40);
            state.color = on ? new Color(0.5f, 0.95f, 0.55f) : new Color(0.9f, 0.7f, 0.35f);

            var toggle = NewButton(_list, "OwnToggle", on ? "禁用" : "启用", new Vector2(320, y), new Vector2(84, 38), () =>
            {
                if (flag != null) flag.Value = !flag.Value;
                SetStatus($"{(flag != null && flag.Value ? "已启用" : "已禁用")} {name}（立即生效）");
                Render();
            });
            toggle.GetComponent<Image>().color = on ? new Color(0.35f, 0.22f, 0.05f) : new Color(0.1f, 0.3f, 0.12f);

            var del = NewButton(_list, "OwnDel", "卸载", new Vector2(410, y), new Vector2(84, 38), () =>
            {
                ModManagerActions.MakeUninstaller();
                SetStatus("已生成卸载脚本（退出游戏后双击运行）");
            });
            del.GetComponent<Image>().color = new Color(0.38f, 0.08f, 0.08f);
        }
        private static void SetStatus(string text)
        {
            if (_status != null) _status.text = text ?? "";
        }

        private static Font Font()
        {
            if (_font != null) return _font;
            try
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<Text>())
                    if (t != null && t.font != null) { _font = t.font; return _font; }
            }
            catch { }
            try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            return _font;
        }

        private static RectTransform NewRect(Transform parent, string name, Vector2 amin, Vector2 amax, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        private static Text NewText(Transform parent, string name, string content, int size, TextAnchor anchor)
        {
            var rt = NewRect(parent, name, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(600, 40));
            var t = rt.gameObject.AddComponent<Text>();
            t.text = content;
            t.font = ChatInput.UiFont;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = Color.white;
            t.raycastTarget = false;
            return t;
        }

        private static GameObject NewButton(Transform parent, string name, string label, Vector2 pos, Vector2 size, Action onClick)
        {
            var rt = NewRect(parent, name, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0.18f, 0.18f, 0.18f, 1f);
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(new Action(onClick));
            var txt = NewText(rt, "Text", label, 20, TextAnchor.MiddleCenter);
            txt.rectTransform.anchorMin = Vector2.zero;
            txt.rectTransform.anchorMax = Vector2.one;
            txt.rectTransform.sizeDelta = Vector2.zero;
            txt.rectTransform.anchoredPosition = Vector2.zero;
            return rt.gameObject;
        }
    }
}








