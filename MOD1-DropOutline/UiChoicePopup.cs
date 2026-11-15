using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Dropoutline
{
    /// <summary>
    /// 自绘的「下拉选择」弹窗：显示标题 + 选项列表 + 当前项，点选后回调。
    /// 不用 Unity 的 Dropdown 组件——官方模板的布局在克隆行里会错位。
    /// 只用 Canvas / Image / Text / Button 这些最基础的东西，全部运行时构建。
    /// </summary>
    internal static class UiChoicePopup
    {
        private static GameObject _root;
        private static float _openedAt;

        public static bool IsOpen => _root != null;

        public static void Show(Font font, string title, string[] labels, int current, Action<int> onPick)
        {
            Close();
            if (labels == null || labels.Length == 0) return;

            try
            {
                var go = new GameObject("DropOutline_ChoicePopup");
                UnityEngine.Object.DontDestroyOnLoad(go);
                var canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32500;
                var scaler = go.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                go.AddComponent<GraphicRaycaster>();
                _root = go;
                _openedAt = Time.unscaledTime;

                // 点空白处关闭
                var blocker = Stretch(go.transform, "Blocker", 0f, 0f, 0f, 0f);
                var blockerImg = blocker.gameObject.AddComponent<Image>();
                blockerImg.color = new Color(0f, 0f, 0f, 0.55f);
                var blockerBtn = blocker.gameObject.AddComponent<Button>();
                blockerBtn.targetGraphic = blockerImg;
                blockerBtn.transition = Selectable.Transition.None;
                blockerBtn.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>((Action)(() =>
                {
                    if (Time.unscaledTime - _openedAt < 0.35f) return;   // 防止打开当帧就被这次点击关掉
                    Close();
                })));

                float rowH = labels.Length > 8 ? 46f : 54f;
                float w = 680f;
                float h = 70f + rowH * labels.Length + 10f;

                var panel = Fixed(go.transform, "Panel", new Vector2(0.5f, 0.5f), new Vector2(w, h), Vector2.zero);
                var panelImg = panel.gameObject.AddComponent<Image>();
                panelImg.color = new Color(0.08f, 0.08f, 0.09f, 0.98f);

                var titleRt = TopBar(panel, "Title", 0f, 56f);
                var titleText = titleRt.gameObject.AddComponent<Text>();
                titleText.font = font;
                titleText.fontSize = 24;
                titleText.color = new Color(1f, 0.85f, 0.4f, 1f);
                titleText.alignment = TextAnchor.MiddleCenter;
                titleText.text = title ?? "";
                titleText.raycastTarget = false;
                titleText.horizontalOverflow = HorizontalWrapMode.Wrap;
                titleText.verticalOverflow = VerticalWrapMode.Truncate;
                titleText.resizeTextForBestFit = true;
                titleText.resizeTextMinSize = 14;
                titleText.resizeTextMaxSize = 24;

                for (int i = 0; i < labels.Length; i++)
                {
                    int idx = i;
                    var itemRt = TopBar(panel, "Item" + i, -(62f + rowH * i), rowH - 6f);
                    itemRt.offsetMin = new Vector2(14f, itemRt.offsetMin.y);
                    itemRt.offsetMax = new Vector2(-14f, itemRt.offsetMax.y);

                    var bg = itemRt.gameObject.AddComponent<Image>();
                    bool isCur = idx == current;
                    bg.color = isCur ? new Color(0.20f, 0.40f, 0.20f, 1f) : new Color(0.14f, 0.14f, 0.15f, 1f);

                    var btn = itemRt.gameObject.AddComponent<Button>();
                    btn.targetGraphic = bg;
                    var colors = btn.colors;
                    colors.normalColor = Color.white;
                    colors.highlightedColor = new Color(0.75f, 1f, 0.75f, 1f);
                    colors.pressedColor = new Color(0.6f, 0.9f, 0.6f, 1f);
                    btn.colors = colors;

                    var labelRt = Stretch(itemRt, "Label", 16f, 4f, 16f, 4f);
                    var labelText = labelRt.gameObject.AddComponent<Text>();
                    labelText.font = font;
                    labelText.fontSize = 20;
                    labelText.color = Color.white;
                    labelText.alignment = TextAnchor.MiddleLeft;
                    labelText.text = (isCur ? "✓ " : "   ") + labels[i];
                    labelText.raycastTarget = false;
                    labelText.horizontalOverflow = HorizontalWrapMode.Wrap;
                    labelText.verticalOverflow = VerticalWrapMode.Truncate;
                    labelText.resizeTextForBestFit = true;
                    labelText.resizeTextMinSize = 13;
                    labelText.resizeTextMaxSize = 20;

                    var cb = onPick;
                    btn.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>((Action)(() =>
                    {
                        if (Time.unscaledTime - _openedAt < 0.35f) return;
                        Close();
                        try { cb?.Invoke(idx); }
                        catch (Exception e) { DropOutlinePlugin.L.LogWarning("[选择弹窗] 回调异常: " + e.Message); }
                    })));
                }

                DropOutlinePlugin.L.LogInfo($"[选择弹窗] 打开「{title}」共 {labels.Length} 项，当前={current}");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[选择弹窗] 创建失败: {e.Message}");
                Close();
            }
        }

        public static void Close()
        {
            if (_root == null) return;
            try { UnityEngine.Object.Destroy(_root); } catch { }
            _root = null;
        }

        private static RectTransform Stretch(Transform parent, string name, float left, float bottom, float right, float top)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        private static RectTransform Fixed(Transform parent, string name, Vector2 anchor, Vector2 size, Vector2 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            return rt;
        }

        /// <summary>贴顶的一条：宽度撑满父级，高度固定，向下偏移 y。</summary>
        private static RectTransform TopBar(Transform parent, string name, float y, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, height);
            rt.anchoredPosition = new Vector2(0f, y);
            return rt;
        }
    }
}


