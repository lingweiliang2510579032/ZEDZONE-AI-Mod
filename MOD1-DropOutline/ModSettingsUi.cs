using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Dropoutline
{
    /// <summary>
    /// AI 伙伴设置 UI：
    ///   · 官方设置面板（ESC → 游戏选项）的「AI 伙伴」分区仅保留 1 个标题 + 1 个紧凑入口按钮行，
    ///     右侧官方导航栏点击「AI 伙伴」直接滚到该入口，不拉长官方设置列表；
    ///   · 点击「打开 AI 伙伴设置」弹出独立二级菜单弹窗（全屏遮罩 + 居中面板 + 3 个分页），
    ///     参照 ZedModManager 居中弹窗风格设计，并对所有按钮与选项文字加内边距和自适应缩放，避免文字出框。
    /// </summary>
    internal static class ModSettingsUi
    {
        private const string Marker = "[ZEDAI]";
        private const string LegacyMarker = "[DropOutline]";
        private const string NavLabel = "AI 伙伴";

        // 官方设置面板注入与导航守护状态
        private static Transform _pendingPanel;
        private static RectTransform _pendingHeader;
        private static float _pendingDeadline;
        private static bool _navPending;
        private static Transform _entryRow;
        private static string _entryStatusLast;
        private static float _lastStatusRefresh;
        private static SettingsNavigationBar _bar;
        private static SettingsNavigationItem _navItem;
        private static RectTransform _navHeader;
        private static bool _barLogged;
        private static float _lastEnsure;
        private static Font _cachedFont;

        // 二级配置菜单弹窗状态
        private static GameObject _winRoot;
        private static RectTransform _winRows;
        private static Transform _winTabBar;
        private static Text _winSubTitle;
        private static Text _winStatusText;
        private static Text _winTestDesc;
        private static Text _winAvatarDesc;
        private static Text _winRoomDesc;
        private static string _winTestLast;
        private static string _winAvatarLast;
        private static string _winRoomLast;
        private static string _winStatusLast;
        private static string _winActionMessage = "";
        private static int _winTab;
        private static bool _buildingModal;

        public static bool IsConfigWindowOpen => _winRoot != null && _winRoot.activeInHierarchy;

        private static bool IsInjectedName(string objName) =>
            !string.IsNullOrEmpty(objName) &&
            (objName.StartsWith(Marker, StringComparison.Ordinal) ||
             objName.StartsWith(LegacyMarker, StringComparison.Ordinal));

        public static void Inject(GameOptionsPanel panel)
        {
            try
            {
                var content = FindDeep(panel.transform, "ConfigProperties");
                if (content == null)
                {
                    DropOutlinePlugin.L.LogWarning("[MOD设置] 未找到 ConfigProperties 容器");
                    return;
                }

                // 幂等：先移除上次注入的节点（面板每次打开重建）
                for (int i = content.childCount - 1; i >= 0; i--)
                {
                    var c = content.GetChild(i);
                    if (c != null && IsInjectedName(c.name))
                        UnityEngine.Object.Destroy(c.gameObject);
                }

                var tpl = PickTemplates(content);
                if (tpl.header == null || tpl.row == null)
                {
                    DropOutlinePlugin.L.LogWarning(
                        $"[MOD设置] 模板缺失 header={tpl.header != null} row={tpl.row != null}");
                    return;
                }

                int idx = FindInsertIndex(content);
                var header = AddHeader(content, tpl.header, ref idx, "AI 伙伴");

                _entryRow = MakeEntryRow(content, tpl.row, idx,
                    "AI 伙伴设置",
                    "点击打开独立二级菜单，配置 API 直连 / DSH 联机陪玩、AI 人设、联机房间与辅助任务权限",
                    "打开 AI 伙伴设置",
                    OpenConfigWindow);

                _entryStatusLast = null;
                RefreshEntryStatus(true);

                try
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(content.GetComponent<RectTransform>());
                    var page = content.parent;
                    if (page != null)
                        LayoutRebuilder.ForceRebuildLayoutImmediate(page.GetComponent<RectTransform>());
                }
                catch (Exception e)
                {
                    DropOutlinePlugin.L.LogWarning($"[MOD设置] 布局刷新失败: {e.Message}");
                }

                ScheduleNav(panel.transform, header.GetComponent<RectTransform>());
                DropOutlinePlugin.L.LogInfo($"[MOD设置] 已注入紧凑入口（运行模式={AiCompanion.CurrentRunMode}）");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[MOD设置] 注入失败: {e.Message}");
            }
        }

        /// <summary>每帧由 ProbeBehaviour 调用。</summary>
        public static void Tick()
        {
            HandleModalEsc();

            if (Time.unscaledTime - _lastEnsure < 0.4f) return;
            _lastEnsure = Time.unscaledTime;

            if (_navPending)
            {
                if (_pendingPanel == null)
                {
                    _navPending = false;
                }
                else if (Time.unscaledTime > _pendingDeadline)
                {
                    _navPending = false;
                    DropOutlinePlugin.L.LogWarning("[MOD设置] 超时仍未找到官方导航栏，跳过导航项注入");
                    _pendingPanel = null;
                }
                else
                {
                    try
                    {
                        if (EnsureNavItem(_pendingPanel, _pendingHeader))
                            _navPending = false;
                    }
                    catch (Exception e)
                    {
                        DropOutlinePlugin.L.LogWarning($"[MOD设置] 导航项注入异常: {e.Message}");
                    }
                }
            }
            else
            {
                EnsureNavAlive();
            }

            RefreshStatusTexts();
        }

        private static void HandleModalEsc()
        {
            try
            {
                if (!Input.GetKeyDown(KeyCode.Escape)) return;
                if (UiChoicePopup.IsOpen)
                {
                    UiChoicePopup.Close();
                    return;
                }
                if (IsConfigWindowOpen)
                    CloseConfigWindow();
            }
            catch { }
        }

        // ───────────────────────── 状态刷新 ─────────────────────────

        private static string CurrentRunModeLabel() =>
            AiCompanion.IsDshMode ? AiCompanion.RunModeLabels[1] : AiCompanion.RunModeLabels[0];

        private static string ModeExplainText(string aiMode)
        {
            switch ((aiMode ?? "").Trim().ToLowerInvariant())
            {
                case "npc":
                    return "接管雇佣NPC：严格按游戏内置机制指挥你招募的队友（跟随/驻守/架枪/开火停火/搜刮资源/手雷与治疗烟雾），不可建造或合成。";
                case "player":
                    return "玩家模式：为 AI 创建独立联机角色卡并作为正常玩家加入房间；单机下可自动或一键转为本地联机房让 AI 进房。";
                default:
                    return "聊天陪伴：官方聊天框自然对话（防重复），主动提醒血量/电量/威胁，并基于已知信息与现有材料提供建议与辅助。";
            }
        }

        private static void RefreshStatusTexts()
        {
            if (Time.unscaledTime - _lastStatusRefresh < 1.2f) return;
            _lastStatusRefresh = Time.unscaledTime;

            RefreshEntryStatus(false);
            RefreshModalStatus(false);
        }

        private static void RefreshEntryStatus(bool force)
        {
            try
            {
                if (_entryRow == null || _entryRow.gameObject == null) return;
                string summary = "当前：" + CurrentRunModeLabel() + " ｜ 状态：" + AiCompanion.StatusText();
                if (!force && summary == _entryStatusLast) return;
                _entryStatusLast = summary;
                SetText(_entryRow, "OptionDescriptionText", summary);
            }
            catch { }
        }

        private static void RefreshModalStatus(bool force)
        {
            if (!IsConfigWindowOpen) return;
            try
            {
                if (_winSubTitle != null)
                {
                    string sub = "当前：" + CurrentRunModeLabel() + " ｜ 状态：" + AiCompanion.StatusText();
                    if (force || sub != _winStatusLast)
                    {
                        _winStatusLast = sub;
                        _winSubTitle.text = sub;
                    }
                }

                if (_winTestDesc != null)
                {
                    string ts = AiCompanion.TestStatus;
                    if (!string.IsNullOrEmpty(ts) && (force || ts != _winTestLast))
                    {
                        _winTestLast = ts;
                        _winTestDesc.text = ts;
                    }
                }

                if (_winAvatarDesc != null && (AiCompanion.IsPlayerMode() || AiCompanion.IsDshMode))
                {
                    string av = AiCoopPlayer.GetAvatarSummary();
                    if (!string.IsNullOrEmpty(av) && (force || av != _winAvatarLast))
                    {
                        _winAvatarLast = av;
                        _winAvatarDesc.text = av;
                    }
                }

                if (_winRoomDesc != null && (AiCompanion.IsPlayerMode() || AiCompanion.IsDshMode))
                {
                    string rm = AiCoopPlayer.GetRoomSummary();
                    if (!string.IsNullOrEmpty(rm) && (force || rm != _winRoomLast))
                    {
                        _winRoomLast = rm;
                        _winRoomDesc.text = rm;
                    }
                }
            }
            catch { }
        }

        private static void SetModalStatus(string msg)
        {
            _winActionMessage = msg ?? "";
            if (_winStatusText != null)
                _winStatusText.text = string.IsNullOrEmpty(_winActionMessage)
                    ? "修改实时自动保存（BepInEx zed.ai.cfg）· 按 ESC 或点右上角关闭"
                    : _winActionMessage;
        }

        // ───────────────────────── 二级配置菜单 ─────────────────────────

        public static void OpenConfigWindow()
        {
            try
            {
                if (_winRoot == null) BuildConfigWindow();
                _winRoot.SetActive(true);
                _winRoot.transform.SetAsLastSibling();
                RenderConfigWindow();
                RefreshModalStatus(true);
                DropOutlinePlugin.L.LogInfo("[MOD设置] 已打开 AI 伙伴二级设置窗口");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[MOD设置] 打开二级设置失败: {e.Message}");
            }
        }

        public static void CloseConfigWindow()
        {
            try
            {
                UiChoicePopup.Close();
                if (_winRoot != null) _winRoot.SetActive(false);
                RefreshEntryStatus(true);
            }
            catch { }
        }

        private static void BuildConfigWindow()
        {
            _winRoot = new GameObject("ZedAiSettingsCanvas");
            UnityEngine.Object.DontDestroyOnLoad(_winRoot);

            var canvas = _winRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = _winRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _winRoot.AddComponent<GraphicRaycaster>();

            var bg = NewRect(_winRoot.transform, "BG", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var bgImg = bg.gameObject.AddComponent<Image>();
            bgImg.color = new Color(0f, 0f, 0f, 0.86f);
            bgImg.raycastTarget = true;

            var panel = NewRectAnchored(_winRoot.transform, "Panel",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(1080f, 760f));
            var panelImg = panel.gameObject.AddComponent<Image>();
            panelImg.color = new Color(0.055f, 0.06f, 0.055f, 0.99f);

            var title = NewTextAnchored(panel, "Title", "ZED · AI 伙伴设置", 30,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(24f, -16f), new Vector2(760f, 40f), TextAnchor.MiddleLeft);
            title.color = new Color(0.95f, 0.98f, 0.95f, 1f);

            _winSubTitle = NewTextAnchored(panel, "Subtitle", "", 16,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(24f, -58f), new Vector2(900f, 28f), TextAnchor.MiddleLeft);
            _winSubTitle.color = new Color(0.65f, 0.82f, 0.68f, 1f);

            var closeBtn = NewButtonAnchored(panel, "Close", "✕",
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-20f, -18f), new Vector2(54f, 40f), 20, CloseConfigWindow);
            closeBtn.GetComponent<Image>().color = new Color(0.35f, 0.08f, 0.08f, 1f);

            // 顶部分类标签栏
            _winTabBar = NewRectAnchored(panel, "TabBar",
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -96f), new Vector2(-48f, 42f));

            // 列表主体区（每页最多 7 行，无需滚动即可全览）
            _winRows = NewRectAnchored(panel, "Rows",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -150f), new Vector2(1032f, 520f));

            // 底部提示栏与关闭按钮
            _winStatusText = NewTextAnchored(panel, "Status", "", 16,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(24f, 20f), new Vector2(830f, 38f), TextAnchor.MiddleLeft);
            _winStatusText.color = new Color(0.65f, 0.85f, 0.68f, 1f);

            var bottomClose = NewButtonAnchored(panel, "BottomClose", "关闭",
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-24f, 18f), new Vector2(140f, 40f), 18, CloseConfigWindow);
            bottomClose.GetComponent<Image>().color = new Color(0.18f, 0.28f, 0.18f, 1f);
        }

        private static void RenderConfigWindow()
        {
            if (_winRows == null || _winTabBar == null) return;
            _buildingModal = true;
            try
            {
                _winTestDesc = null;
                _winAvatarDesc = null;
                _winRoomDesc = null;
                _winTestLast = null;
                _winAvatarLast = null;
                _winRoomLast = null;
                _winStatusLast = null;

                ClearChildren(_winTabBar);
                ClearChildren(_winRows);

                if (_winTab < 0 || _winTab > 3) _winTab = 0;
                string modeShort = AiCompanion.IsPlayerMode()
                    ? "2. 玩家模式与联机"
                    : (AiCompanion.IsNpcMode() ? "2. 雇佣NPC控制" : "2. 陪伴模式与适配");
                string[] tabTitles =
                {
                    "1. 接入与表现形式",
                    modeShort,
                    AiCompanion.IsDshMode ? "3. DSH 桥接说明" : "3. API 接口配置",
                    "4. 辅助与权限边界",
                };
                for (int i = 0; i < tabTitles.Length; i++)
                {
                    int tabIdx = i;
                    var tb = NewButtonAnchored(_winTabBar, "Tab" + i, tabTitles[i],
                        new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                        new Vector2(i * 252f, 0f), new Vector2(244f, 40f), 16, () =>
                        {
                            _winTab = tabIdx;
                            RenderConfigWindow();
                        });
                    tb.GetComponent<Image>().color = i == _winTab
                        ? new Color(0.20f, 0.38f, 0.22f, 1f)
                        : new Color(0.13f, 0.14f, 0.14f, 1f);
                }

                int rowIdx = 0;
                if (_winTab == 0)
                {
                    AddModalBoolRow(ref rowIdx, "启用 AI 伙伴",
                        $"开启后启用 AI 功能｜当前环境：{AiCompanion.GetNetworkRoleLabel()}（修改即时生效）",
                        AiCompanion.CfgAiEnabled);

                    AddModalChoiceRow(ref rowIdx, "接入模式（大脑连接方式）",
                        "API 模式直连大模型接口；DSH 模式连接本地大肥鱼 Agent（支持文件桥实时联动）。两种接入均可搭配下方三种表现形式。",
                        AiCompanion.RunModeLabels, AiCompanion.RunModeValues, AiCompanion.CfgRunMode,
                        val =>
                        {
                            AiCompanion.SetRunMode(val);
                            SetModalStatus($"已切换接入模式 → {CurrentRunModeLabel()}");
                            RenderConfigWindow();
                        });

                    AddModalChoiceRow(ref rowIdx, "表现形式（AI 以何种身份参与）",
                        ModeExplainText(AiCompanion.NormalizedAiMode()),
                        AiCompanion.ModeLabels, AiCompanion.ModeValues, AiCompanion.CfgAiMode,
                        val =>
                        {
                            AiCompanion.OnModeChanged(val);
                            SetModalStatus($"已切换表现形式 → {AiCompanion.AiModeDisplayName(val)}");
                            RenderConfigWindow();
                        });

                    AddModalBoolRow(ref rowIdx, "使用游戏官方聊天框",
                        "开启后 AI 台词直接发进游戏官方聊天框并从这里接收你的消息（联机可全房同步）；关闭则仅走 MOD 小窗。",
                        AiCompanion.CfgChatViaOfficial);

                    AddModalTextRow(ref rowIdx, "AI 名字",
                        "AI 伙伴 / 雇佣队友 / 玩家模式下联机角色卡的显示名称",
                        AiCompanion.CfgNpcName, false, "大肥鱼");

                    AddModalTextRow(ref rowIdx, "玩家称呼",
                        "AI 在对话中怎么称呼你",
                        AiCompanion.CfgPlayerName, false, "用户");

                    AddModalTextRow(ref rowIdx, "人设（性格与腔调）",
                        "决定 AI 的性格与说话口吻（留空使用自然末世幸存者默认人设）",
                        AiCompanion.CfgPersona, false, "话少、谨慎、幽默（留空用默认设定）");
                }
                else if (_winTab == 1)
                {
                    if (AiCompanion.IsPlayerMode())
                    {
                        AddModalBoolRow(ref rowIdx, "自动让 AI 玩家进入房间",
                            "进世界时自动以 AI 专属 MpCharacters 角色卡生成 AI 玩家并协同游玩（单人下若开下方选项将自动开联机房）。",
                            AiCompanion.CfgCoopAutoAvatar);

                        AddModalBoolRow(ref rowIdx, "单机自动开启本地联机房",
                            "处于单人游戏时，允许 AI 玩家把当前世界无缝开启为本地联机房主模式，以便作为正常玩家角色加入。",
                            AiCompanion.CfgPlayerAutoHost);

                        _winAvatarDesc = AddModalActionRow(ref rowIdx, "AI 玩家角色状态（召唤 / 传送）",
                            AiCoopPlayer.GetAvatarSummary(),
                            "召唤 / 传送", () =>
                            {
                                string msg;
                                if (!AiCoopPlayer.IsNetPlayerActive && AiCoopPlayer.GetAvatarAgent() == null)
                                    AiCoopPlayer.EnsureOrFindAvatarNpc(true, out msg);
                                else
                                    AiCoopPlayer.ForceTeleportToPlayer(out msg);
                                if (_winAvatarDesc != null) _winAvatarDesc.text = msg;
                                SetModalStatus(msg);
                            });

                        AddModalChoiceRow(ref rowIdx, "AI 玩家默认协同姿态",
                            "控制 AI 玩家进入房间后的行动逻辑：紧跟玩家 / 积极迎战 / 原地驻守 / 协同拾荒。",
                            AiCoopPlayer.BehaviorLabels, AiCoopPlayer.BehaviorValues, AiCompanion.CfgCoopBehavior,
                            val =>
                            {
                                if (AiCompanion.CfgCoopBehavior != null) AiCompanion.CfgCoopBehavior.Value = val;
                                AiCoopPlayer.SetBehaviorMode(val);
                                SetModalStatus($"玩家模式姿态已切换为 {val}");
                            });

                        AddModalBoolRow(ref rowIdx, "客户端自动协同房主",
                            "作为独立客户端连入他人房间且无手动按键时，由 AI 自动跟随房主并参与警戒。",
                            AiCompanion.CfgCoopDriveLocalWhenClient);

                        AddModalTextRow(ref rowIdx, "联机房间码（加入他人房间）",
                            "填写要加入的房间码或 IP:端口（例如 ABCD12 或 127.0.0.1:7777）",
                            AiCompanion.CfgCoopRoomCode, false, "输入房间码（例如 ABCD12）");

                        _winRoomDesc = AddModalActionRow(ref rowIdx, "联机房间控制",
                            $"{AiCoopPlayer.GetRoomSummary()}｜点右侧按码进房；如需当前档直接变房主请用“开启联机房”",
                            "按码进房", () =>
                            {
                                AiCoopPlayer.TryJoinRoomByCode(AiCompanion.CoopRoomCode, "", out string msg);
                                if (_winRoomDesc != null) _winRoomDesc.text = msg;
                                SetModalStatus(msg);
                            });
                    }
                    else if (AiCompanion.IsNpcMode())
                    {
                        AddModalActionRow(ref rowIdx, "官方雇佣 NPC 能力与限制说明",
                            "官方设计仅支持：跟随/驻守/架枪警戒/开火停火/远近战偏好/搜刮附近资源/拾取指定物品/手雷与治疗烟雾/自愈整理救人；不可建造/合成/开车。",
                            "刷新状态", () =>
                            {
                                RefreshModalStatus(true);
                                SetModalStatus($"当前：{AiCompanion.StatusText()}");
                            });

                        AddModalActionRow(ref rowIdx, "单人 vs 联机适配说明",
                            $"当前环境：{AiCompanion.GetNetworkRoleLabel()}。仅单人或联机房主拥有雇佣 NPC 权威控制权；联机客户端不会越权改写房主端 NPC。",
                            "立即检测", () =>
                            {
                                RefreshModalStatus(true);
                                SetModalStatus($"已检测网络环境：{AiCompanion.GetNetworkRoleLabel()}");
                            });

                        AddModalBoolRow(ref rowIdx, "NPC 自主决策行动",
                            "没有玩家聊天指挥时，按计时与战局自主在官方可用 GOAP 目标与命令间切换。",
                            AiCompanion.CfgAutonomous);

                        AddModalBoolRow(ref rowIdx, "测试：临时接管最近友好NPC",
                            "仅调试用（正常游玩建议关闭）：队伍里还没有正式招募队友时，临时接管附近友好人类 NPC。",
                            AiCompanion.CfgTakeoverTest);

                        AddModalBoolRow(ref rowIdx, "允许有理由拒绝",
                            "当指令过于危险或会糟蹋关键物资时，允许 NPC 在聊天框说明理由并拒绝。",
                            AiCompanion.CfgAllowRefuse);
                    }
                    else
                    {
                        AddModalActionRow(ref rowIdx, "聊天陪伴模式功能说明",
                            "通过官方聊天框陪你聊天、按多变口语提醒掉血/饥渴/口渴/电量/未加点数/视野内威胁（防机械重复），并在已有材料与已知信息内给建议。",
                            "查看当前状态", () =>
                            {
                                RefreshModalStatus(true);
                                SetModalStatus($"陪伴模式运行中：{AiCompanion.GetNetworkRoleLabel()}");
                            });

                        AddModalActionRow(ref rowIdx, "单人 vs 联机适配说明",
                            $"当前：{AiCompanion.GetNetworkRoleLabel()}。单人模式开放完整背包整理/制造/丢弃/容器归并任务；联机下仅执行高亮指引、手电与只读制造规划。",
                            "刷新检测", () =>
                            {
                                RefreshModalStatus(true);
                                SetModalStatus($"当前网络：{AiCompanion.GetNetworkRoleLabel()}");
                            });

                        AddModalBoolRow(ref rowIdx, "主动陪伴提醒（非对话触发）",
                            "关闭后仅在你发消息时回复；开启则会在掉血、电量低于两成、有未分配点数或遇敌时自然提醒。",
                            AiCompanion.CfgCompanionMode);

                        AddModalBoolRow(ref rowIdx, "允许有理由拒绝",
                            "材料不足、配方未解锁或超出已知信息边界时，直接如实拒绝或说明缺口，不假装完成。",
                            AiCompanion.CfgAllowRefuse);

                        AddModalBoolRow(ref rowIdx, "自动关闭全屏 NPC 对话框",
                            "仅当游戏全屏对话框打断操作时可开启；正常游玩建议保持关闭。",
                            AiCompanion.CfgCloseDialoguePanel);
                    }
                }
                else if (_winTab == 2)
                {
                    if (AiCompanion.IsDshMode)
                    {
                        AddModalActionRow(ref rowIdx, "当前使用：DSH 本地 Agent 模式",
                            $"通过游戏目录桥接文件连接本地大肥鱼 Agent（{AiCompanion.StateFilePath}），无需填 API Key，支持跨存档人物记忆。",
                            "切回 API 模式", () =>
                            {
                                AiCompanion.SetRunMode("api");
                                SetModalStatus("已切换为 API 接入模式");
                                RenderConfigWindow();
                            });

                        AddModalActionRow(ref rowIdx, "开启联机房主（便捷入口）",
                            $"{AiCoopPlayer.GetRoomSummary()}｜无论选择哪种表现形式，都可在此将当前世界快速开启为联机房主。",
                            "开启联机房", () =>
                            {
                                AiCoopPlayer.TryOpenHostRoom(7777, true, out string msg);
                                SetModalStatus(msg);
                                RenderConfigWindow();
                            });
                    }
                    else
                    {
                        AddModalChoiceRow(ref rowIdx, "API 接口协议",
                            "选择大模型接口格式（Chat Completions / Anthropic Messages / OpenAI Responses / Ollama 本地）。",
                            AiCompanion.ApiProtocolLabels, AiCompanion.ApiProtocolValues, AiCompanion.CfgApiProtocol,
                            val =>
                            {
                                AiCompanion.SetApiProtocol(val);
                                SetModalStatus($"API 协议已切换为 {val}");
                                RenderConfigWindow();
                            });

                        AddModalTextRow(ref rowIdx, "API 根地址 (BaseUrl)",
                            "接口根地址，不含具体路径，例：https://api.deepseek.com/v1",
                            AiCompanion.CfgBaseUrl, false, "https://api.deepseek.com/v1");

                        AddModalTextRow(ref rowIdx, "模型名称 (Model)",
                            "例：deepseek-chat / gpt-4o-mini / claude-sonnet-4 / qwen2.5:14b",
                            AiCompanion.CfgModel, false, "deepseek-chat");

                        AddModalTextRow(ref rowIdx, "API Key",
                            "本机 Windows DPAPI 加密保存，不回显；输入新值回车即可保存",
                            AiCompanion.CfgApiKeyEnc, true, "sk-...（留空表示保持当前密钥）");

                        _winTestDesc = AddModalActionRow(ref rowIdx, "测试 API 连通性",
                            string.IsNullOrEmpty(AiCompanion.TestStatus) ? "用当前 BaseUrl / 模型 / Key 发送一次最小测试请求" : AiCompanion.TestStatus,
                            "测试连接", () =>
                            {
                                AiCompanion.TestApiConnection();
                                SetModalStatus("已发起 API 连接测试...");
                                RefreshModalStatus(true);
                            });
                    }
                }
                else
                {
                    AddModalActionRow(ref rowIdx, "反作弊边界与当前权限",
                        $"环境：{AiCompanion.GetNetworkRoleLabel()}。AI 严禁剧透未解锁配方、未检视箱子或视野外目标；下方写操作开关全部默认关闭，需你明确授权。",
                        "刷新环境", () =>
                        {
                            RefreshModalStatus(true);
                            SetModalStatus($"权限检查完毕：{AiCompanion.GetNetworkRoleLabel()}");
                        });

                    AddModalBoolRow(ref rowIdx, "AI 自主行动（定时决策）",
                        "无玩家新消息时按间隔自主检查战局（陪伴模式用上方「主动陪伴提醒」控制）。",
                        AiCompanion.CfgAutonomous);

                    AddModalBoolRow(ref rowIdx, "允许 AI 丢弃物品",
                        "仅作用于随身背包未保护的多余物品（默认关闭，仅给整理建议；联机客机受保护）。",
                        AiCompanion.CfgAllowDiscard);

                    AddModalBoolRow(ref rowIdx, "允许 AI 直接合成",
                        "仅限已解锁配方与随身现有材料，核对缺口充足后加入官方制造队列（默认关闭，只规划缺口）。",
                        AiCompanion.CfgAllowDirectCraft);

                    AddModalBoolRow(ref rowIdx, "允许 AI 归并容器（方案规划）",
                        "仅限同楼层镜头视野内已检视过的容器，未打开检视的箱子绝不读取（默认关闭）。",
                        AiCompanion.CfgAllowContainerSort);

                    AddModalBoolRow(ref rowIdx, "允许 AI 归并容器（实际搬运）",
                        "实验开关（仅单机安全执行）：按数量守恒校验逐件搬运，发现差异立即停手。",
                        AiCompanion.CfgAllowContainerSortWrite);

                    AddModalBoolRow(ref rowIdx, "左下角 MOD 辅助聊天日志",
                        "在左下角短暂显示辅助/指令回执提示；关闭则只看游戏官方聊天框。",
                        AiCompanion.CfgShowChatLog);
                }

                SetModalStatus(_winActionMessage);
                RefreshModalStatus(true);
            }
            finally
            {
                _buildingModal = false;
            }
        }

        private static RectTransform CreateModalRowBase(ref int rowIdx, string title, string desc, out Text descText)
        {
            float y = -(rowIdx * 72f);
            rowIdx++;

            var row = NewRectAnchored(_winRows, "Row_" + rowIdx,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, y), new Vector2(0f, 64f));
            var bg = row.gameObject.AddComponent<Image>();
            bg.color = rowIdx % 2 == 1
                ? new Color(0.10f, 0.108f, 0.10f, 0.94f)
                : new Color(0.085f, 0.092f, 0.085f, 0.94f);

            var titleTxt = NewTextAnchored(row, "Title", title, 18,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(16f, -6f), new Vector2(585f, 24f), TextAnchor.MiddleLeft);
            titleTxt.color = new Color(0.95f, 0.97f, 0.95f, 1f);
            ApplyBestFit(titleTxt, 14, 18, false);

            descText = NewTextAnchored(row, "Desc", desc ?? "", 13,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(16f, -31f), new Vector2(585f, 30f), TextAnchor.UpperLeft);
            descText.color = new Color(0.68f, 0.72f, 0.68f, 1f);
            ApplyBestFit(descText, 11, 14, true);

            return row;
        }

        private static void AddModalBoolRow(ref int rowIdx, string name, string desc, ConfigEntry<bool> cfg)
        {
            var row = CreateModalRowBase(ref rowIdx, name, desc, out _);
            var btn = NewButtonAnchored(row, "Toggle", "",
                new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-16f, 0f), new Vector2(180f, 40f), 17, null);
            var bg = btn.GetComponent<Image>();
            var lbl = btn.GetComponentInChildren<Text>(true);

            Action refresh = () =>
            {
                bool on = cfg != null && cfg.Value;
                if (lbl != null) lbl.text = on ? "开启 ▾" : "关闭 ▾";
                if (bg != null) bg.color = on ? new Color(0.18f, 0.38f, 0.20f, 1f) : new Color(0.28f, 0.20f, 0.18f, 1f);
            };
            refresh();

            btn.GetComponent<Button>().onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>((Action)(() =>
            {
                if (cfg == null) return;
                bool cur = cfg.Value;
                UiChoicePopup.Show(UiFont(), name, new[] { "开启", "关闭" }, cur ? 0 : 1, i =>
                {
                    cfg.Value = i == 0;
                    refresh();
                    SetModalStatus($"「{name}」已设为 {(i == 0 ? "开启" : "关闭")}");
                    RefreshModalStatus(true);
                });
            })));
        }

        private static void AddModalChoiceRow(ref int rowIdx, string name, string desc,
            string[] labels, string[] values, ConfigEntry<string> cfg, Action<string> onChanged)
        {
            var row = CreateModalRowBase(ref rowIdx, name, desc, out _);
            int cur = 0;
            if (cfg != null && values != null)
            {
                int found = Array.IndexOf(values, (cfg.Value ?? "").Trim());
                if (found >= 0) cur = found;
            }
            if (labels == null || labels.Length == 0) return;
            if (cur >= labels.Length) cur = 0;

            var btn = NewButtonAnchored(row, "Choice", labels[cur] + "  ▾",
                new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-16f, 0f), new Vector2(380f, 42f), 16, null);
            btn.GetComponent<Image>().color = new Color(0.17f, 0.28f, 0.17f, 0.96f);
            var lbl = btn.GetComponentInChildren<Text>(true);
            if (lbl != null) ApplyBestFit(lbl, 12, 16, true);

            int currentIndex = cur;
            btn.GetComponent<Button>().onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>((Action)(() =>
            {
                UiChoicePopup.Show(UiFont(), name, labels, currentIndex, i =>
                {
                    currentIndex = i;
                    string val = values != null && i < values.Length ? values[i] : "";
                    if (cfg != null) cfg.Value = val;
                    if (lbl != null) lbl.text = labels[i] + "  ▾";
                    try { onChanged?.Invoke(val); }
                    catch (Exception e) { DropOutlinePlugin.L.LogWarning($"[MOD设置] 切换「{name}」异常: {e.Message}"); }
                    RefreshModalStatus(true);
                });
            })));
        }

        private static void AddModalTextRow(ref int rowIdx, string name, string desc,
            ConfigEntry<string> cfg, bool secret, string placeholder)
        {
            var row = CreateModalRowBase(ref rowIdx, name, desc, out _);
            var box = NewRectAnchored(row, "Input",
                new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-16f, 0f), new Vector2(380f, 40f));
            var bg = box.gameObject.AddComponent<Image>();
            bg.color = new Color(0.14f, 0.145f, 0.15f, 1f);
            box.gameObject.AddComponent<RectMask2D>();

            var font = UiFont();
            var phRt = NewRect(box, "Placeholder", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            phRt.offsetMin = new Vector2(12f, 4f);
            phRt.offsetMax = new Vector2(-12f, -4f);
            var ph = phRt.gameObject.AddComponent<Text>();
            ph.font = font;
            ph.fontSize = 15;
            ph.color = new Color(1f, 1f, 1f, 0.35f);
            ph.alignment = TextAnchor.MiddleLeft;
            ph.text = secret && cfg != null && !string.IsNullOrEmpty(cfg.Value)
                ? "已加密保存（输入新值回车覆盖）"
                : placeholder;
            ph.raycastTarget = false;
            ApplyBestFit(ph, 11, 15, true);

            var txtRt = NewRect(box, "Text", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            txtRt.offsetMin = new Vector2(12f, 4f);
            txtRt.offsetMax = new Vector2(-12f, -4f);
            var txt = txtRt.gameObject.AddComponent<Text>();
            txt.font = font;
            txt.fontSize = 16;
            txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.supportRichText = false;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;

            var input = box.gameObject.AddComponent<InputField>();
            input.targetGraphic = bg;
            input.textComponent = txt;
            input.placeholder = ph;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 300;
            if (!secret && cfg != null) input.text = cfg.Value ?? "";

            if (!secret)
            {
                input.onValueChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<string>>((Action<string>)(v =>
                {
                    if (_buildingModal || cfg == null) return;
                    cfg.Value = (v ?? "").Trim();
                })));
            }

            input.onEndEdit.AddListener(DelegateSupport.ConvertDelegate<UnityAction<string>>((Action<string>)(v =>
            {
                if (_buildingModal || cfg == null) return;
                var val = (v ?? "").Trim();
                if (secret)
                {
                    if (val.Length == 0) return;
                    cfg.Value = LlmClient.ProtectKey(val);
                    input.text = "";
                    ph.text = "已加密保存（输入新值回车覆盖）";
                    SetModalStatus("API Key 已加密保存");
                    DropOutlinePlugin.L.LogInfo("[MOD设置] API Key 已加密保存");
                }
                else
                {
                    cfg.Value = val;
                    SetModalStatus($"「{name}」已保存");
                    DropOutlinePlugin.L.LogInfo($"[MOD设置] {name} 已更新");
                }
            })));
        }

        private static Text AddModalActionRow(ref int rowIdx, string name, string desc, string btnText, Action onClick)
        {
            var row = CreateModalRowBase(ref rowIdx, name, desc, out var descTxt);
            var btn = NewButtonAnchored(row, "Action", btnText,
                new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-16f, 0f), new Vector2(210f, 40f), 17, () =>
                {
                    try { onClick?.Invoke(); }
                    catch (Exception e) { SetModalStatus($"「{name}」执行失败：{e.Message}"); }
                });
            btn.GetComponent<Image>().color = new Color(0.20f, 0.30f, 0.45f, 1f);
            return descTxt;
        }

        // ───────────────────────── 官方设置入口行 ─────────────────────────

        private static Transform MakeEntryRow(Transform content, Transform tpl, int index,
            string name, string desc, string buttonText, Action onClick)
        {
            var row = UnityEngine.Object.Instantiate(tpl.gameObject, content).transform;
            row.name = Marker + "_Row";
            row.SetSiblingIndex(index);
            row.gameObject.SetActive(true);

            var toggleGroup = FindDeep(row, "ToggleGroup");
            if (toggleGroup != null) toggleGroup.gameObject.SetActive(false);
            var dropdown = FindDeep(row, "Dropdown");
            if (dropdown != null) dropdown.gameObject.SetActive(false);

            SetText(row, "PropertyNameText", name);
            SetText(row, "PropertyDescriptionText", desc);
            SetText(row, "OptionDescriptionText", "");

            var nameTf = FindDeep(row, "PropertyNameText");
            var nameText = nameTf != null ? nameTf.GetComponent<Text>() : null;
            if (nameText != null && nameText.font != null) _cachedFont = nameText.font;

            TightenRowLayout(row);

            var btnGo = new GameObject(Marker + "_Btn");
            btnGo.transform.SetParent(row, false);
            var rt = btnGo.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(240f, 40f);
            var bg = btnGo.AddComponent<Image>();
            bg.color = new Color(0.18f, 0.32f, 0.20f, 1f);
            var le = btnGo.AddComponent<LayoutElement>();
            le.minHeight = 40f;
            le.preferredHeight = 40f;
            le.preferredWidth = 240f;
            le.flexibleWidth = 0f;
            var btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = bg;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(btnGo.transform, false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(12f, 4f);
            labelRt.offsetMax = new Vector2(-12f, -4f);
            var label = labelGo.AddComponent<Text>();
            label.font = UiFont();
            label.fontSize = 18;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.text = buttonText;
            label.raycastTarget = false;
            ApplyBestFit(label, 12, 18, true);

            btn.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>((Action)(() =>
            {
                try { onClick?.Invoke(); }
                catch (Exception e) { DropOutlinePlugin.L.LogWarning($"[MOD设置] 入口按钮异常: {e.Message}"); }
            })));

            return row;
        }

        private static void TightenRowLayout(Transform row)
        {
            try
            {
                var vlg = row.GetComponent<VerticalLayoutGroup>();
                if (vlg == null) return;
                vlg.childForceExpandHeight = false;
                vlg.childControlHeight = true;
                vlg.childControlWidth = true;
                vlg.childForceExpandWidth = true;
                vlg.spacing = 4f;
            }
            catch { }
        }

        private static Transform AddHeader(Transform content, Transform tplHeader, ref int index, string title)
        {
            var header = UnityEngine.Object.Instantiate(tplHeader.gameObject, content).transform;
            header.name = Marker + "_Header";
            header.gameObject.SetActive(true);
            var titleText = FindDeep(header, "TitleText");
            if (titleText != null)
            {
                var t = titleText.GetComponent<Text>();
                if (t != null) t.text = "──────────    " + title + "    ──────────";
            }
            header.SetSiblingIndex(index);
            index++;
            return header;
        }

        // ───────────────────────── 官方导航栏 ─────────────────────────

        public static void ScheduleNav(Transform panel, RectTransform header)
        {
            _pendingPanel = panel;
            _pendingHeader = header;
            _pendingDeadline = Time.unscaledTime + 10f;
            _lastEnsure = 0f;
            _navPending = true;
        }

        private static SettingsNavigationBar FindLiveBar()
        {
            try
            {
                foreach (var b in Resources.FindObjectsOfTypeAll<SettingsNavigationBar>())
                {
                    if (b == null) continue;
                    var go = b.gameObject;
                    if (go != null && go.activeInHierarchy) return b;
                }
            }
            catch { }
            return null;
        }

        private static bool IsOwnHeader(string text) =>
            !string.IsNullOrEmpty(text) && (text.StartsWith("AI 伙伴") || text.StartsWith("AI伙伴"));

        private static RectTransform FindLiveHeader(Transform root)
        {
            try
            {
                foreach (var t in root.GetComponentsInChildren<Text>(true))
                {
                    if (t == null || string.IsNullOrEmpty(t.text)) continue;
                    if (!IsOwnHeader(t.text)) continue;
                    var rt = t.rectTransform;
                    var parent = rt.parent as RectTransform;
                    return parent != null ? parent : rt;
                }
            }
            catch { }
            return null;
        }

        private static void EnsureNavAlive()
        {
            try
            {
                var bar = FindLiveBar();
                if (bar == null) return;
                var panel = bar.transform.root;
                if (!ReferenceEquals(bar, _bar))
                {
                    _bar = bar;
                    _navItem = null;
                }

                if (_navHeader == null || _navHeader.gameObject == null || !_navHeader.gameObject.activeInHierarchy)
                    _navHeader = FindLiveHeader(panel);
                if (_navHeader == null) return;

                bool alive = false;
                try
                {
                    alive = _navItem != null && _bar.items != null && _bar.items.Contains(_navItem) &&
                            _navItem.gameObject != null && _navItem.gameObject.activeInHierarchy;
                }
                catch { alive = false; }

                if (alive) return;
                AddNavItem(_bar, _navHeader);
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[MOD设置] 导航守护异常: {e.Message}");
            }
        }

        private static bool EnsureNavItem(Transform panel, RectTransform header)
        {
            Transform root = panel;
            while (root.parent != null) root = root.parent;

            SettingsNavigationBar bar = null;
            var navMenu = FindByName(root, "NavigationMenu", 0, 8);
            if (navMenu != null)
                bar = navMenu.GetComponentInChildren<SettingsNavigationBar>(true);
            if (bar == null)
                bar = root.GetComponentInChildren<SettingsNavigationBar>(true);

            if (bar == null) return false;

            if (!_barLogged)
            {
                _barLogged = true;
                DropOutlinePlugin.L.LogInfo(
                    $"[MOD设置] 找到官方导航栏组件：节点={bar.gameObject.name}");
            }

            _bar = bar;
            _navHeader = header;
            return AddNavItem(bar, header);
        }

        private static bool AddNavItem(SettingsNavigationBar bar, RectTransform header)
        {
            try
            {
                var items = bar.items;
                if (items == null) return false;

                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var it = items[i];
                    if (it == null) continue;
                    bool ours = false;
                    try { ours = it.labelText != null && it.labelText.text == NavLabel; } catch { }
                    if (!ours) continue;
                    items.RemoveAt(i);
                    try { if (it.gameObject != null) UnityEngine.Object.Destroy(it.gameObject); } catch { }
                }

                var prefab = bar.itemPrefab;
                var parent = bar.itemParent;
                if (prefab == null || parent == null) return false;

                var go = UnityEngine.Object.Instantiate(prefab, parent);
                var item = go.GetComponent<SettingsNavigationItem>();
                if (item == null)
                {
                    UnityEngine.Object.Destroy(go);
                    return false;
                }
                go.transform.SetAsLastSibling();
                item.Init(NavLabel, header, bar);
                if (!items.Contains(item)) items.Add(item);

                try
                {
                    var btn = go.GetComponent<Button>() ?? go.GetComponentInChildren<Button>(true);
                    if (btn != null)
                    {
                        btn.onClick.AddListener(new Action(() => { try { bar.ScrollToSection(header); } catch { } }));
                        btn.interactable = true;
                    }
                }
                catch { }
                try { if (item.labelText != null) item.labelText.color = new Color(0.85f, 0.85f, 0.85f); }
                catch { }

                _navItem = item;
                return true;
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[MOD设置] 导航项注入失败: {e.Message}");
                return false;
            }
        }

        // ───────────────────────── 模板选择 ─────────────────────────

        private readonly struct Templates
        {
            public readonly Transform header;
            public readonly Transform row;

            public Templates(Transform h, Transform r) { header = h; row = r; }
        }

        private static Templates PickTemplates(Transform content)
        {
            Transform header = null, fallbackRow = null, boolRow = null;

            for (int i = 0; i < content.childCount; i++)
            {
                var c = content.GetChild(i);
                if (c == null || c.name == null || IsInjectedName(c.name)) continue;

                if (header == null && c.name.StartsWith("SettingsSectionHeader", StringComparison.Ordinal))
                    header = c;

                if (!c.name.StartsWith("ConfigPropertyUI", StringComparison.Ordinal)) continue;

                var group = FindDeep(c, "ToggleGroup");
                var toggles = group != null ? group.GetComponentsInChildren<Toggle>(true) : null;
                if (toggles == null || toggles.Length != 2) continue;

                if (fallbackRow == null) fallbackRow = c;
                string l0 = ReadToggleLabel(toggles[0]);
                string l1 = ReadToggleLabel(toggles[1]);
                if (IsOffLabel(l0) && IsOnLabel(l1))
                {
                    boolRow = c;
                    break;
                }
            }

            return new Templates(header, boolRow ?? fallbackRow);
        }

        private static string ReadToggleLabel(Toggle t)
        {
            try
            {
                var label = FindDeep(t.transform, "Label");
                var txt = label != null ? label.GetComponent<Text>() : null;
                return txt != null ? txt.text : "";
            }
            catch { return ""; }
        }

        private static bool IsOffLabel(string s) =>
            !string.IsNullOrEmpty(s) && (s.Contains("关") || s.ToLowerInvariant().Contains("off"));

        private static bool IsOnLabel(string s) =>
            !string.IsNullOrEmpty(s) && (s.Contains("开") || s.ToLowerInvariant().Contains("on"));

        private static int FindInsertIndex(Transform content)
        {
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                var c = content.GetChild(i);
                if (c == null || c.name == null || IsInjectedName(c.name)) continue;
                if (c.name.StartsWith("GameSettingsExtend", StringComparison.OrdinalIgnoreCase)) return i;
                if (HasFooterButton(c)) return i;
            }

            for (int i = content.childCount - 1; i >= 0; i--)
            {
                var c = content.GetChild(i);
                if (c == null || c.name == null || IsInjectedName(c.name)) continue;
                return i + 1;
            }
            return content.childCount;
        }

        private static bool HasFooterButton(Transform t)
        {
            try
            {
                foreach (var txt in t.GetComponentsInChildren<Text>(true))
                {
                    if (txt == null || string.IsNullOrEmpty(txt.text)) continue;
                    var s = txt.text.Replace(" ", "").Trim();
                    if (s.Contains("确认") || s.Contains("確定") || s.Contains("取消")) return true;
                    if (s.Equals("APPLY", StringComparison.OrdinalIgnoreCase)
                        || s.Equals("CANNEL", StringComparison.OrdinalIgnoreCase)
                        || s.Equals("CANCEL", StringComparison.OrdinalIgnoreCase)
                        || s.Contains("CLEAR INVALID DATA")) return true;
                }
            }
            catch { }
            return false;
        }

        // ───────────────────────── UI 原语工具 ─────────────────────────

        private static Font UiFont()
        {
            if (_cachedFont != null) return _cachedFont;
            try
            {
                _cachedFont = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Microsoft YaHei", "微软雅黑", "SimHei", "Arial" }, 20);
                if (_cachedFont != null) return _cachedFont;
            }
            catch { }
            try
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<Text>())
                {
                    if (t != null && t.font != null) { _cachedFont = t.font; return _cachedFont; }
                }
            }
            catch { }
            try { _cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            return _cachedFont;
        }

        private static void ApplyBestFit(Text t, int minSize, int maxSize, bool wrap)
        {
            if (t == null) return;
            t.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.resizeTextForBestFit = true;
            t.resizeTextMinSize = minSize;
            t.resizeTextMaxSize = maxSize;
        }

        private static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
            {
                var ch = t.GetChild(i);
                if (ch == null) continue;
                ch.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(ch.gameObject);
            }
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

        private static RectTransform NewRectAnchored(Transform parent, string name,
            Vector2 amin, Vector2 amax, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        private static Text NewTextAnchored(Transform parent, string name, string content, int size,
            Vector2 amin, Vector2 amax, Vector2 pivot, Vector2 pos, Vector2 sz, TextAnchor anchor)
        {
            var rt = NewRectAnchored(parent, name, amin, amax, pivot, pos, sz);
            var text = rt.gameObject.AddComponent<Text>();
            text.text = content ?? "";
            text.font = UiFont();
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.raycastTarget = false;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.resizeTextForBestFit = false;
            return text;
        }

        private static GameObject NewButtonAnchored(Transform parent, string name, string label,
            Vector2 amin, Vector2 amax, Vector2 pivot, Vector2 pos, Vector2 size, int fontSize, Action onClick)
        {
            var rt = NewRectAnchored(parent, name, amin, amax, pivot, pos, size);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0.18f, 0.18f, 0.18f, 1f);
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            if (onClick != null)
            {
                btn.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>((Action)(() =>
                {
                    try { onClick(); }
                    catch (Exception e) { DropOutlinePlugin.L.LogWarning($"[MOD设置] 按钮「{name}」异常: {e.Message}"); }
                })));
            }

            var txtRt = NewRect(rt, "Label", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            txtRt.offsetMin = new Vector2(12f, 4f);
            txtRt.offsetMax = new Vector2(-12f, -4f);
            var text = txtRt.gameObject.AddComponent<Text>();
            text.font = UiFont();
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = label ?? "";
            text.raycastTarget = false;
            ApplyBestFit(text, 12, fontSize, true);
            return rt.gameObject;
        }

        private static void SetText(Transform row, string childName, string text)
        {
            var t = FindDeep(row, childName);
            if (t == null) return;
            var txt = t.GetComponent<Text>();
            if (txt != null) txt.text = text;
        }

        private static Transform FindDeep(Transform root, string childName)
        {
            if (root == null) return null;
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c == null) continue;
                if (c.name == childName) return c;
                var r = FindDeep(c, childName);
                if (r != null) return r;
            }
            return null;
        }

        private static Transform FindByName(Transform root, string name, int depth, int maxDepth)
        {
            if (root == null || depth > maxDepth) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindByName(root.GetChild(i), name, depth + 1, maxDepth);
                if (r != null) return r;
            }
            return null;
        }
    }
}
