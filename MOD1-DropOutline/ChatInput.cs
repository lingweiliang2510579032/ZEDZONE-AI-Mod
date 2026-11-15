using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Dropoutline
{
    /// <summary>
    /// MOD2 的游戏内聊天输入框：按 Enter 打开，输入玩家要说的话，再按 Enter 发送给 AI 队友。
    /// 全部在运行时构建（不依赖游戏内部 UI 预制体），只借用场景里已有 Text 的字体。
    /// </summary>
    internal static class ChatInput
    {
        private static GameObject _root;
        private static InputField _field;
        private static Text _hint;
        private static bool _open;
        private static float _closedAt;

        private static Text[] _logLines;
        private static int _logCount;
        private const int LogCapacity = 5;

        public static bool IsOpen => _open;

        public static void ResetWorld()
        {
            _open = false; _logCount = 0; _logShowUntil = 0f;
            History.Clear();
            if (_logText != null) _logText.text = "";
            if (_field != null) _field.text = "";
            if (_root != null) _root.SetActive(false);
            if (_logRoot != null) _logRoot.SetActive(false);
            if (_logLines != null) foreach (var line in _logLines) if (line != null) line.text = "";
        }

        /// <summary>创建界面（插件加载时调用一次）。</summary>
        public static void Create()
        {
            try
            {
                var go = new GameObject("DropOutline_ChatInput");
                UnityEngine.Object.DontDestroyOnLoad(go);

                var canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32000;
                var scaler = go.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                go.AddComponent<GraphicRaycaster>();

                // 背板
                var panel = new GameObject("Panel");
                panel.transform.SetParent(go.transform, false);
                var panelRt = panel.AddComponent<RectTransform>();
                panelRt.anchorMin = new Vector2(0.5f, 0f);
                panelRt.anchorMax = new Vector2(0.5f, 0f);
                panelRt.pivot = new Vector2(0.5f, 0f);
                panelRt.anchoredPosition = new Vector2(0f, 60f);
                panelRt.sizeDelta = new Vector2(760f, 56f);
                var bg = panel.AddComponent<Image>();
                bg.color = new Color(0.05f, 0.05f, 0.05f, 0.92f);

                // 提示文字（占位符）
                var placeholderGo = new GameObject("Placeholder");
                placeholderGo.transform.SetParent(panel.transform, false);
                var phRt = placeholderGo.AddComponent<RectTransform>();
                phRt.anchorMin = Vector2.zero;
                phRt.anchorMax = Vector2.one;
                phRt.offsetMin = new Vector2(16f, 6f);
                phRt.offsetMax = new Vector2(-16f, -6f);
                var phText = placeholderGo.AddComponent<Text>();
                phText.font = GetGameFont();
                phText.fontSize = 22;
                phText.color = new Color(1f, 1f, 1f, 0.35f);
                phText.text = "对队友说点什么…（Enter 发送，Esc 取消）";
                phText.alignment = TextAnchor.MiddleLeft;
                phText.raycastTarget = false;

                // 文本
                var textGo = new GameObject("Text");
                textGo.transform.SetParent(panel.transform, false);
                var textRt = textGo.AddComponent<RectTransform>();
                textRt.anchorMin = Vector2.zero;
                textRt.anchorMax = Vector2.one;
                textRt.offsetMin = new Vector2(16f, 6f);
                textRt.offsetMax = new Vector2(-16f, -6f);
                var text = textGo.AddComponent<Text>();
                text.font = phText.font;
                text.fontSize = 22;
                text.color = Color.white;
                text.alignment = TextAnchor.MiddleLeft;
                text.supportRichText = false;

                var input = panel.AddComponent<InputField>();
                input.targetGraphic = bg;
                input.textComponent = text;
                input.placeholder = phText;
                input.lineType = InputField.LineType.SingleLine;
                input.characterLimit = 200;
                input.onEndEdit.AddListener(DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<string>>((Action<string>)OnEndEdit));

                _root = go;
                _field = input;
                _hint = phText;

                BuildLog(go.transform, phText.font);

                go.SetActive(false);
                DropOutlinePlugin.L.LogInfo("[聊天] 聊天输入框已创建（Enter 打开）");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[聊天] 创建输入框失败: {e.Message}");
            }
        }

        private static GameObject _logRoot;
        private static float _logShowUntil;
        private static Text _logText;
        private static readonly System.Collections.Generic.List<string> History =
            new System.Collections.Generic.List<string>();

        /// <summary>左下角的对话记录（独立画布，不受输入框开关影响）。</summary>
        private static void BuildLog(Transform _, Font font)
        {
            var go = new GameObject("DropOutline_ChatLog");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 31999;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            // 半透明底板：压住背后的场景，字才看得清
            var bgGo = new GameObject("BG");
            bgGo.transform.SetParent(go.transform, false);
            var bgRt = bgGo.AddComponent<RectTransform>();
            bgRt.anchorMin = new Vector2(0f, 0f);
            bgRt.anchorMax = new Vector2(0f, 0f);
            bgRt.pivot = new Vector2(0f, 0f);
            bgRt.anchoredPosition = new Vector2(16f, 108f);
            bgRt.sizeDelta = new Vector2(844f, 174f);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.color = new Color(0f, 0f, 0f, 0.55f);
            bgImg.raycastTarget = false;

            var textGo = new GameObject("Log");
            textGo.transform.SetParent(go.transform, false);
            var rt = textGo.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(28f, 120f);
            rt.sizeDelta = new Vector2(820f, 150f);

            var text = textGo.AddComponent<Text>();
            text.font = font;
            text.fontSize = 20;
            text.color = new Color(1f, 1f, 1f, 0.92f);
            text.alignment = TextAnchor.LowerLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = true;
            text.raycastTarget = false;
            text.text = "";

            _logRoot = go;
            _logText = text;
            go.SetActive(false);   // 默认隐藏：只有聊天时短暂出现
        }

        /// <summary>往左下角记录一行（who 用于着色）。</summary>
        public static void Log(string who, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var line = who == "玩家"
                ? "<color=#7fd1ff>" + who + "：</color>" + text
                : "<color=#ffd479>" + who + "：</color>" + text;
            History.Add(line);
            while (History.Count > LogCapacity) History.RemoveAt(0);
            if (_logText == null) return;
            try
            {
                _logText.text = string.Join("\n", History.ToArray());
                if (AiCompanion.CfgShowChatLog != null && !AiCompanion.CfgShowChatLog.Value) return;
                if ((AiCompanion.CfgChatViaOfficial == null || AiCompanion.CfgChatViaOfficial.Value) && AiNetChat.Available)
                {
                    if (_logRoot != null && _logRoot.activeSelf) _logRoot.SetActive(false);
                    return;
                }
                _logShowUntil = Time.unscaledTime + 7f;   // 出现 7 秒后自动收起
                if (_logRoot != null) _logRoot.SetActive(true);
            }
            catch { }
        }

        internal static Font UiFont => GetGameFont();

        private static Font GetGameFont()
        {
            try
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<Text>())
                {
                    if (t != null && t.font != null) return t.font;
                }
            }
            catch { }
            try { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            return null;
        }

        /// <summary>每帧调用：处理打开/关闭快捷键。</summary>
        public static void Tick()
        {
            try
            {
                if (_root == null) return;

                bool official = AiCompanion.CfgChatViaOfficial == null || AiCompanion.CfgChatViaOfficial.Value;
                if (official && AiNetChat.Available)
                {
                    if (_open) Close();
                    if (_logRoot != null && _logRoot.activeSelf) _logRoot.SetActive(false);
                    return;
                }

                // 聊天记录浮层：输入框打开时一直显示，否则只在有新消息后短暂显示
                if (_logRoot != null)
                {
                    bool wantShow = _open || Time.unscaledTime < _logShowUntil;
                    if (_logRoot.activeSelf != wantShow) _logRoot.SetActive(wantShow);
                }

                // 正式版走官方聊天时，回车归游戏自己的聊天框，不再抢键
                if (!_open && !official && Input.GetKeyDown(KeyCode.Return) && Time.unscaledTime - _closedAt > 0.35f)
                {
                    Open();
                    return;
                }
                if (_open && Input.GetKeyDown(KeyCode.Escape))
                {
                    Close();
                }
            }
            catch { }
        }

        private static void Open()
        {
            if (_root == null) return;
            _open = true;
            _root.SetActive(true);
            _field.text = "";
            _field.Select();
            _field.ActivateInputField();
            DropOutlinePlugin.L.LogInfo("[聊天] 输入框打开");
        }

        private static void Close()
        {
            _open = false;
            _closedAt = Time.unscaledTime;
            if (_field != null) _field.DeactivateInputField();
            if (_root != null) _root.SetActive(false);
        }

        private static void OnEndEdit(string text)
        {
            // Esc / 失焦 会走进来；只在有内容时发送
            if (!_open) return;
            if (!string.IsNullOrEmpty(text) && text.Trim().Length > 0)
            {
                Log("玩家", text.Trim());
                AiCompanion.SubmitPlayerChat(text);
            }
            Close();
        }
    }
}







