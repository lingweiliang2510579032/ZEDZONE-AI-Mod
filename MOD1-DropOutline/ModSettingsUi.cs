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

        // 官方设置面板注入与导航守护状态
        private static Transform _pendingPanel;
        private static RectTransform _pendingHeader;
        private static float _pendingDeadline;
        private static bool _navPending;
        private static Transform _entryRow;
        private static Text _headerTitleText;
        private static Text _entryBtnLabel;
        private static string _entryStatusLast;
        private static float _lastStatusRefresh;
        private static SettingsNavigationBar _bar;
        private static SettingsNavigationItem _navItem;
        private static RectTransform _navHeader;
        private static bool _barLogged;
        private static float _lastEnsure;
        private static Font _cachedFont;
        private static string _lastRenderedLang;

        // 二级配置菜单弹窗状态
        private static GameObject _winRoot;
        private static RectTransform _winRows;
        private static Transform _winTabBar;
        private static Text _winTitleText;
        private static Text _winCloseBtnText;
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

                ModLocalization.Refresh(true);
                _lastRenderedLang = ModLocalization.Current;

                int idx = FindInsertIndex(content);
                var header = AddHeader(content, tpl.header, ref idx, ModLocalization.NavLabel());

                _entryRow = MakeEntryRow(content, tpl.row, idx,
                    ModLocalization.EntryRowTitle(),
                    ModLocalization.EntryRowDesc(),
                    ModLocalization.OpenSettingsBtn(),
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

            bool langChanged = ModLocalization.Refresh();
            if (langChanged || (!string.IsNullOrEmpty(_lastRenderedLang) && _lastRenderedLang != ModLocalization.Current))
            {
                _lastRenderedLang = ModLocalization.Current;
                RefreshLocalizedEntryTexts();
                if (IsConfigWindowOpen)
                    RenderConfigWindow();
            }

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
                    return ModLocalization.Pick(
                        "接管雇佣NPC：严格按游戏内置机制指挥你招募的队友（跟随/驻守/架枪/开火停火/搜刮资源/手雷与治疗烟雾），不可建造或合成。",
                        "Hired NPC Mode: Command your recruited teammates via built-in mechanics (follow/hold/overwatch/fire/loot/grenades & healing smoke); cannot build or craft.",
                        "Söldner-NPC: Steuert angeheuerte Kameraden per Spielmechanik (Folgen/Halten/Feuern/Plündern/Granaten & Rauch); kein Bauen oder Craften.",
                        "PNJ contratado: Controla a tus compañeros reclutados según las reglas del juego (seguir/mantener/fuego/saquear/granadas); sin construir ni fabricar.",
                        "PNJ recruté : Dirige vos équipiers recrutés selon les mécaniques du jeu (suivre/tenir/tirer/piller/grenades et soin) ; ni construction ni craft.",
                        "雇用NPCモード：雇った味方を公式仕様の範囲で指揮（追従/待機/射撃/探索/投擲物・回復煙幕）。建築・クラフトは不可。",
                        "고용 NPC 모드: 영입한 동료를 게임 기본 규칙 내에서 지휘(추종/대기/사격/수색/수류탄 및 치유 연막). 건설/제작 불가.",
                        "Najęty NPC: Dowódź najętymi towarzyszami zgodnie z mechaniką gry (podążaj/stój/ogień/łupienie/granaty); bez budowania i craftingu.",
                        "NPC contratado: Comande aliados recrutados pelas regras do jogo (seguir/manter/atirar/saquear/granadas e cura); sem construir ou criar.",
                        "Наёмный NPC: Управляйте нанятыми союзниками по правилам игры (следование/удержание/огонь/обыск/гранаты и дым); без стройки и крафта.");
                case "player":
                    return ModLocalization.Pick(
                        "玩家模式：为 AI 创建独立联机角色并作为正常玩家加入房间；单人下可自动或一键转为本地联机房让 AI 进房。",
                        "Player Mode: Creates a dedicated multiplayer character for the AI to join as a normal player; can auto-open a local co-op host from singleplayer.",
                        "Spieler-Modus: Erstellt einen eigenen Mehrspieler-Charakter für die KI; kann im Einzelspieler automatisch einen lokalen Koop-Host starten.",
                        "Modo Jugador: Crea un personaje multijugador propio para que la IA se una como jugador normal; puede abrir sala local desde un jugador.",
                        "Mode Joueur : Crée un personnage multijoueur dédié pour l'IA afin qu'elle rejoigne la partie comme un joueur normal (hôte local possible en solo).",
                        "プレイヤーモード：AI専用のマルチキャラを作成して通常のプレイヤーとして参加させます。ソロ時もローカル協力部屋へ自動切替可能。",
                        "플레이어 모드: AI 전용 멀티 캐릭터를 생성해 일반 플레이어로 합류시킵니다. 싱글에서도 로컬 협동 방을 자동으로 열 수 있습니다.",
                        "Tryb Gracza: Tworzy własną postać multiplayer dla AI, by dołączyła jak zwykły gracz; w trybie solo może otworzyć lokalny host ko-op.",
                        "Modo Jogador: Cria um personagem multiplayer para a IA entrar como jogador normal; no solo pode abrir sala local automaticamente.",
                        "Режим игрока: Создаёт отдельного сетевого персонажа для ИИ; в одиночной игре может автоматически открыть локальный кооп-хост.");
                default:
                    return ModLocalization.Pick(
                        "聊天陪伴：官方聊天框自然对话（防重复），主动提醒血量/电量/威胁，并基于已知信息与现有材料提供建议与辅助。",
                        "Chat Companion: Natural conversation via the game chat box (anti-repetition), proactive health/power/threat alerts, and advice based on known info & materials.",
                        "Chat-Begleiter: Natürliche Gespräche im Chat (ohne Wiederholung), proaktive Warnungen zu HP/Strom/Gefahr und Tipps auf Basis bekannter Materialien.",
                        "Compañero de chat: Diálogo natural en el chat oficial (sin repetición), avisos de salud/energía/peligro y consejos según lo conocido.",
                        "Compagnon de chat : Discussion naturelle dans le chat (anti-répétition), alertes santé/énergie/danger et conseils selon vos matériaux.",
                        "チャット相棒：公式チャット欄で自然に会話（重複防止）。HP・電力・脅威の注意喚起や手持ち資材に応じた助言・補助を行います。",
                        "채팅 동반자: 공식 채팅창에서 자연스럽게 대화하며(중복 방지), 체력·전력·위협 알림 및 보유 재료 기반 조언과 보조를 제공합니다.",
                        "Towarzysz czatu: Naturalna rozmowa na czacie gry (bez powtórek), alerty o HP/zasilaniu/zagrożeniach i porady na bazie posiadanych materiałów.",
                        "Companheiro de chat: Conversa natural no chat oficial (sem repetição), alertas de vida/energia/ameaça e dicas com base no que você tem.",
                        "Компаньон в чате: Естественное общение в чате (без повторов), подсказки по здоровью/питанию/угрозам и советы по доступным ресурсам.");
            }
        }

        private static void RefreshLocalizedEntryTexts()
        {
            try
            {
                if (_headerTitleText != null)
                    _headerTitleText.text = "──────────    " + ModLocalization.NavLabel() + "    ──────────";
                if (_entryRow != null && _entryRow.gameObject != null)
                {
                    SetText(_entryRow, "PropertyNameText", ModLocalization.EntryRowTitle());
                    SetText(_entryRow, "PropertyDescriptionText", ModLocalization.EntryRowDesc());
                }
                if (_entryBtnLabel != null)
                    _entryBtnLabel.text = ModLocalization.OpenSettingsBtn();
                if (_navItem != null && _navItem.labelText != null)
                    _navItem.labelText.text = ModLocalization.NavLabel();
                RefreshEntryStatus(true);
            }
            catch { }
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
                string summary = ModLocalization.CurrentPrefix() + CurrentRunModeLabel() + " | " + ModLocalization.StatusPrefix() + AiCompanion.StatusText();
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
                    string sub = ModLocalization.CurrentPrefix() + CurrentRunModeLabel() + " | " + ModLocalization.StatusPrefix() + AiCompanion.StatusText();
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
                    ? ModLocalization.ModalDefaultFooter()
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

            _winTitleText = NewTextAnchored(panel, "Title", ModLocalization.WinTitle(), 30,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(24f, -16f), new Vector2(880f, 40f), TextAnchor.MiddleLeft);
            _winTitleText.color = new Color(0.95f, 0.98f, 0.95f, 1f);
            ApplyBestFit(_winTitleText, 18, 30, false);

            _winSubTitle = NewTextAnchored(panel, "Subtitle", "", 16,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(24f, -58f), new Vector2(900f, 28f), TextAnchor.MiddleLeft);
            _winSubTitle.color = new Color(0.65f, 0.82f, 0.68f, 1f);
            ApplyBestFit(_winSubTitle, 11, 16, false);

            var closeBtn = NewButtonAnchored(panel, "Close", "✕",
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-20f, -18f), new Vector2(54f, 40f), 20, CloseConfigWindow);
            closeBtn.GetComponent<Image>().color = new Color(0.35f, 0.08f, 0.08f, 1f);

            // 顶部分类标签栏
            _winTabBar = NewRectAnchored(panel, "TabBar",
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -96f), new Vector2(-48f, 42f));

            // 列表主体区（无需滚动即可全览）
            _winRows = NewRectAnchored(panel, "Rows",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -150f), new Vector2(1032f, 520f));

            // 底部提示栏与关闭按钮
            _winStatusText = NewTextAnchored(panel, "Status", "", 16,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(24f, 20f), new Vector2(830f, 38f), TextAnchor.MiddleLeft);
            _winStatusText.color = new Color(0.65f, 0.85f, 0.68f, 1f);
            ApplyBestFit(_winStatusText, 11, 16, true);

            var bottomClose = NewButtonAnchored(panel, "BottomClose", ModLocalization.CloseText(),
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-24f, 18f), new Vector2(140f, 40f), 18, CloseConfigWindow);
            bottomClose.GetComponent<Image>().color = new Color(0.18f, 0.28f, 0.18f, 1f);
            _winCloseBtnText = bottomClose.GetComponentInChildren<Text>(true);
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

                if (_winTitleText != null) _winTitleText.text = ModLocalization.WinTitle();
                if (_winCloseBtnText != null) _winCloseBtnText.text = ModLocalization.CloseText();
                _lastRenderedLang = ModLocalization.Current;

                if (_winTab < 0 || _winTab > 3) _winTab = 0;
                string modeShort = AiCompanion.IsPlayerMode()
                    ? ModLocalization.Pick("2. 玩家与联机", "2. Player & Co-op", "2. Spieler & Koop", "2. Jugador y Coop", "2. Joueur & Coop", "2. プレイヤー協力", "2. 플레이어 협동", "2. Gracz i Coop", "2. Jogador e Coop", "2. Игрок и кооп")
                    : (AiCompanion.IsNpcMode()
                        ? ModLocalization.Pick("2. 雇佣NPC控制", "2. Hired NPC", "2. Söldner-NPC", "2. PNJ contratado", "2. PNJ recruté", "2. 雇用NPC操作", "2. 고용 NPC 제어", "2. Najęty NPC", "2. NPC contratado", "2. Наёмный NPC")
                        : ModLocalization.Pick("2. 陪伴与适配", "2. Companion", "2. Begleiter", "2. Compañero", "2. Compagnon", "2. コンパニオン", "2. 동반자 설정", "2. Towarzysz", "2. Companheiro", "2. Компаньон"));
                string[] tabTitles =
                {
                    ModLocalization.Pick("1. 接入与表现", "1. Mode & Setup", "1. Modus & Setup", "1. Modo y ajuste", "1. Mode & config", "1. 接続と形式", "1. 연결 및 형태", "1. Tryb i start", "1. Modo e ajuste", "1. Режим и вход"),
                    modeShort,
                    AiCompanion.IsDshMode
                        ? ModLocalization.Pick("3. DSH 桥接说明", "3. DSH Bridge", "3. DSH-Brücke", "3. Puente DSH", "3. Pont DSH", "3. DSH連携設定", "3. DSH 브리지", "3. Most DSH", "3. Ponte DSH", "3. Мост DSH")
                        : ModLocalization.Pick("3. API 接口配置", "3. API Config", "3. API-Config", "3. Config API", "3. Config API", "3. API接続設定", "3. API 연결 설정", "3. Konfig. API", "3. Config API", "3. Настройка API"),
                    ModLocalization.Pick("4. 辅助与权限", "4. Permissions", "4. Berechtigung", "4. Permisos", "4. Permissions", "4. 権限と安全", "4. 권한 및 안전", "4. Uprawnienia", "4. Permissões", "4. Права и лимиты"),
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
                    AddModalChoiceRow(ref rowIdx,
                        ModLocalization.Pick("界面语言", "Interface Language", "Sprache", "Idioma", "Langue", "表示言語", "표시 언어", "Język interfejsu", "Idioma", "Язык интерфейса"),
                        ModLocalization.Pick(
                            "默认自动跟随《隔离区》游戏语言；也可手动指定 10 种官方支持语言之一。",
                            "Follows ZED ZONE's game language by default, or manually pick any of the 10 supported languages.",
                            "Folgt standardmäßig der Spielsprache oder wählt manuell eine der 10 unterstützten Sprachen.",
                            "Sigue el idioma del juego por defecto, o elige manualmente cualquiera de los 10 idiomas.",
                            "Suit la langue du jeu par défaut, ou choisissez l'une des 10 langues manuellement.",
                            "既定ではゲームの言語に自動追従します。公式対応10言語から手動選択も可能です。",
                            "기본값은 게임 언어를 자동 추적하며, 공식 지원 10개 언어 중 직접 선택할 수도 있습니다.",
                            "Domyślnie śledzi język gry lub pozwala ręcznie wybrać jeden z 10 obsługiwanych języków.",
                            "Segue o idioma do jogo por padrão, ou escolha manualmente um dos 10 idiomas.",
                            "По умолчанию следует языку игры; можно выбрать любой из 10 языков вручную."),
                        ModLocalization.GetUiLangLabels(), ModLocalization.UiLangCodes, AiCompanion.CfgUiLanguage,
                        _ =>
                        {
                            ModLocalization.Refresh(true);
                            _lastRenderedLang = ModLocalization.Current;
                            RefreshLocalizedEntryTexts();
                            SetModalStatus(ModLocalization.Pick(
                                "界面语言已切换", "Interface language updated", "Sprache aktualisiert",
                                "Idioma actualizado", "Langue mise à jour", "表示言語を切り替えました",
                                "언어가 변경되었습니다", "Zaktualizowano język", "Idioma atualizado", "Язык изменён"));
                            RenderConfigWindow();
                        });

                    AddModalBoolRow(ref rowIdx,
                        ModLocalization.Pick("启用 AI 伙伴", "Enable AI Companion", "KI-Begleiter aktivieren", "Activar compañero IA", "Activer le compagnon IA", "AI相棒を有効化", "AI 동반자 활성화", "Włącz towarzysza AI", "Ativar companheiro IA", "Включить ИИ-напарника"),
                        ModLocalization.Pick(
                            $"开启后启用 AI 功能｜当前环境：{AiCompanion.GetNetworkRoleLabel()}（修改即时生效）",
                            $"Enables all AI features | Environment: {AiCompanion.GetNetworkRoleLabel()} (takes effect immediately).",
                            $"Aktiviert alle KI-Funktionen | Umgebung: {AiCompanion.GetNetworkRoleLabel()} (sofort wirksam).",
                            $"Activa la IA | Entorno actual: {AiCompanion.GetNetworkRoleLabel()} (aplicación inmediata).",
                            $"Active les fonctions IA | Environnement : {AiCompanion.GetNetworkRoleLabel()} (effet immédiat).",
                            $"AI機能を有効にします｜現在の環境：{AiCompanion.GetNetworkRoleLabel()}（即時反映）",
                            $"AI 기능을 켭니다 | 현재 환경: {AiCompanion.GetNetworkRoleLabel()} (즉시 적용).",
                            $"Włącza funkcje AI | Środowisko: {AiCompanion.GetNetworkRoleLabel()} (działa natychmiast).",
                            $"Ativa os recursos da IA | Ambiente: {AiCompanion.GetNetworkRoleLabel()} (efeito imediato).",
                            $"Включает функции ИИ | Среда: {AiCompanion.GetNetworkRoleLabel()} (сразу вступает в силу)."),
                        AiCompanion.CfgAiEnabled);

                    AddModalChoiceRow(ref rowIdx,
                        ModLocalization.Pick("接入模式（连接方式）", "Connection Mode", "Verbindungsmodus", "Modo de conexión", "Mode de connexion", "接続方式", "연결 방식", "Tryb połączenia", "Modo de conexão", "Режим подключения"),
                        ModLocalization.Pick(
                            "API 模式直连大模型接口；DSH 模式连接本地 DSH 桌面端。两种接入均可搭配下方三种表现形式。",
                            "API Mode connects directly to an LLM endpoint; DSH Mode connects to local DSH Desktop. Works with all 3 forms below.",
                            "API-Modus verbindet direkt mit einem LLM-Endpunkt; DSH-Modus mit lokalem DSH Desktop.",
                            "Modo API conecta directo a un LLM; Modo DSH conecta con DSH Desktop local.",
                            "Mode API se connecte directement à un LLM ; Mode DSH se connecte à DSH Desktop local.",
                            "APIモードはLLMに直接接続、DSHモードはローカルのDSHと連携します。下の3形式と併用可能です。",
                            "API 모드는 LLM API에 직접 연결하고, DSH 모드는 로컬 DSH와 연동합니다.",
                            "Tryb API łączy bezpośrednio z LLM; tryb DSH łączy z lokalnym DSH Desktop.",
                            "Modo API conecta direto a uma LLM; Modo DSH conecta ao DSH Desktop local.",
                            "Режим API подключается напрямую к LLM; режим DSH — к локальному DSH Desktop."),
                        AiCompanion.RunModeLabels, AiCompanion.RunModeValues, AiCompanion.CfgRunMode,
                        val =>
                        {
                            AiCompanion.SetRunMode(val);
                            SetModalStatus(ModLocalization.Pick(
                                $"已切换接入模式 → {CurrentRunModeLabel()}",
                                $"Connection mode switched -> {CurrentRunModeLabel()}",
                                $"Verbindungsmodus -> {CurrentRunModeLabel()}",
                                $"Modo de conexión -> {CurrentRunModeLabel()}",
                                $"Mode de connexion -> {CurrentRunModeLabel()}",
                                $"接続方式を切り替えました -> {CurrentRunModeLabel()}",
                                $"연결 방식 변경 -> {CurrentRunModeLabel()}",
                                $"Tryb połączenia -> {CurrentRunModeLabel()}",
                                $"Modo de conexão -> {CurrentRunModeLabel()}",
                                $"Режим подключения -> {CurrentRunModeLabel()}"));
                            RenderConfigWindow();
                        });

                    AddModalChoiceRow(ref rowIdx,
                        ModLocalization.Pick("表现形式（参与身份）", "Companion Form", "KI-Rolle", "Forma de la IA", "Forme du compagnon", "参加形態", "참여 형태", "Forma AI", "Forma da IA", "Роль ИИ"),
                        ModeExplainText(AiCompanion.NormalizedAiMode()),
                        AiCompanion.ModeLabels, AiCompanion.ModeValues, AiCompanion.CfgAiMode,
                        val =>
                        {
                            AiCompanion.OnModeChanged(val);
                            SetModalStatus(ModLocalization.Pick(
                                $"已切换表现形式 → {AiCompanion.AiModeDisplayName(val)}",
                                $"Companion form switched -> {AiCompanion.AiModeDisplayName(val)}",
                                $"KI-Rolle -> {AiCompanion.AiModeDisplayName(val)}",
                                $"Forma cambiada -> {AiCompanion.AiModeDisplayName(val)}",
                                $"Forme changée -> {AiCompanion.AiModeDisplayName(val)}",
                                $"参加形態を切り替えました -> {AiCompanion.AiModeDisplayName(val)}",
                                $"참여 형태 변경 -> {AiCompanion.AiModeDisplayName(val)}",
                                $"Zmieniono formę -> {AiCompanion.AiModeDisplayName(val)}",
                                $"Forma alterada -> {AiCompanion.AiModeDisplayName(val)}",
                                $"Роль изменена -> {AiCompanion.AiModeDisplayName(val)}"));
                            RenderConfigWindow();
                        });

                    AddModalBoolRow(ref rowIdx,
                        ModLocalization.Pick("使用游戏官方聊天框", "Use Game Chat Box", "Spiel-Chat nutzen", "Usar chat del juego", "Utiliser le chat du jeu", "公式チャット欄を使用", "게임 공식 채팅창 사용", "Używaj czatu gry", "Usar chat oficial do jogo", "Использовать чат игры"),
                        ModLocalization.Pick(
                            "开启后 AI 台词发进游戏官方聊天框并从这里接收消息（联机全房可见）；关闭则仅走 MOD 小窗。",
                            "Send AI messages into the game's chat box & read your chat there (synced in co-op); off uses mod overlay only.",
                            "Sendet KI-Nachrichten in den Spiel-Chat (im Koop synchron); deaktiviert nur über das Mod-Fenster.",
                            "Envía mensajes de la IA al chat del juego y lee allí tus mensajes (sincronizado en coop).",
                            "Envoie les réponses dans le chat du jeu et lit vos messages (synchronisé en coop).",
                            "ONで公式チャット欄へ発言し入力も受け取ります（マルチ同期）。OFFではMOD小窓のみ使用。",
                            "켜면 게임 공식 채팅창으로 대화하고(멀티 동기화), 끄면 MOD 미니창만 사용합니다.",
                            "Wysyła wiadomości AI na czat gry i odbiera stamtąd polecenia; wyłączone = tylko okno moda.",
                            "Envia falas da IA ao chat oficial do jogo e lê suas mensagens de lá (sincronizado no coop).",
                            "Отправляет реплики ИИ в игровой чат и читает ваши сообщения (синхронизируется в коопе)."),
                        AiCompanion.CfgChatViaOfficial);

                    AddModalTextRow(ref rowIdx,
                        ModLocalization.Pick("AI 名字", "AI Name", "KI-Name", "Nombre de IA", "Nom de l'IA", "AIの名前", "AI 이름", "Nazwa AI", "Nome da IA", "Имя ИИ"),
                        ModLocalization.Pick(
                            "AI 伙伴 / 雇佣队友 / 玩家模式下联机角色卡的显示名称",
                            "Display name for the AI companion, hired NPC, or co-op player character.",
                            "Anzeigename für KI-Begleiter, Söldner-NPC oder Koop-Charakter.",
                            "Nombre visible del compañero IA, PNJ contratado o personaje cooperativo.",
                            "Nom affiché pour le compagnon IA, PNJ recruté ou joueur coop.",
                            "相棒・雇用NPC・プレイヤーモード時のキャラ表示名。",
                            "AI 동반자 / 고용 동료 / 플레이어 모드 캐릭터의 표시 이름.",
                            "Wyświetlana nazwa towarzysza AI, najętego NPC lub postaci co-op.",
                            "Nome exibido para o companheiro IA, NPC contratado ou jogador coop.",
                            "Отображаемое имя ИИ-напарника, наёмного NPC или кооп-персонажа."),
                        AiCompanion.CfgNpcName, false, ModLocalization.DefaultAiName());

                    AddModalTextRow(ref rowIdx,
                        ModLocalization.Pick("玩家称呼", "Player Title", "Spielername", "Nombre del jugador", "Nom du joueur", "プレイヤーの呼び方", "플레이어 호칭", "Nazwa gracza", "Como chamar você", "Обращение к игроку"),
                        ModLocalization.Pick(
                            "AI 在对话中怎么称呼你",
                            "How the AI addresses you in chat",
                            "Wie die KI dich im Chat anspricht",
                            "Cómo te llama la IA en el chat",
                            "Comment l'IA vous appelle dans le chat",
                            "会話中にAIがあなたを呼ぶ名前",
                            "AI가 대화에서 당신을 부르는 호칭",
                            "Jak AI zwraca się do Ciebie na czacie",
                            "Como a IA chama você na conversa",
                            "Как ИИ обращается к вам в чате"),
                        AiCompanion.CfgPlayerName, false, ModLocalization.DefaultPlayerTitle());

                    AddModalTextRow(ref rowIdx,
                        ModLocalization.Pick("人设（性格与腔调）", "Persona & Tone", "Persönlichkeit & Ton", "Personalidad y tono", "Personnalité et ton", "性格・口調設定", "성격 및 말투", "Osobowość i styl", "Personalidade e tom", "Характер и стиль"),
                        ModLocalization.Pick(
                            "决定 AI 的性格与说话口吻（留空使用自然末世幸存者默认人设）",
                            "Defines the AI's personality and tone (leave blank for natural survivor default).",
                            "Bestimmt Charakter und Tonfall der KI (leer lassen für Überlebenden-Standard).",
                            "Define la personalidad y el tono de la IA (vacío = estilo superviviente natural).",
                            "Définit le caractère et le ton de l'IA (vide = survivant naturel par défaut).",
                            "AIの性格や口調を指定します（空欄時は自然なサバイバー風）。",
                            "AI의 성격과 말투를 설정합니다(비워두면 자연스러운 생존자 기본 설정).",
                            "Określa charakter i styl mowy AI (puste = naturalny styl ocalałego).",
                            "Define a personalidade e o tom da IA (vazio = sobrevivente natural).",
                            "Задаёт характер и манеру речи ИИ (пусто = естественный тон выжившего)."),
                        AiCompanion.CfgPersona, false,
                        ModLocalization.Pick("话少、谨慎、幽默（留空用默认设定）", "Concise, cautious, dry humor (blank = default)", "Wortkarg, vorsichtig, trocken (leer = Standard)", "Breve, cauto, humor seco (vacío = defecto)", "Concis, prudent, humour sec (vide = défaut)", "寡黙で慎重、少しユーモア（空欄＝既定）", "과묵하고 신중하며 유머러스함 (비우면 기본)", "Oszczędny w słowach, ostrożny (puste = domyślne)", "Direto, cauteloso, bom humor (vazio = padrão)", "Сдержанный, осторожный, с юмором (пусто = по умолчанию)"));
                }
                else if (_winTab == 1)
                {
                    if (AiCompanion.IsPlayerMode())
                    {
                        AddModalBoolRow(ref rowIdx,
                            ModLocalization.Pick("自动让 AI 玩家进入房间", "Auto-Join Co-op Room", "Automatisch Koop beitreten", "Unirse automático a sala", "Rejoindre la salle auto", "AIプレイヤーを自動参加", "AI 플레이어 자동 참가", "Automatycznie dołącz do pokoju", "Entrar automaticamente na sala", "Авто-вход ИИ в комнату"),
                            ModLocalization.Pick(
                                "进世界时自动创建 AI 专属联机角色并协同游玩（单人下若开下方选项将自动开联机房）。",
                                "Spawns a dedicated co-op character for the AI on world entry (opens local co-op host if enabled below).",
                                "Erstellt beim Betreten der Welt automatisch einen KI-Koop-Charakter.",
                                "Crea automáticamente un personaje cooperativo para la IA al entrar al mundo.",
                                "Crée automatiquement un personnage coop dédié pour l'IA à l'entrée du monde.",
                                "ワールド開始時にAI専用のマルチキャラを生成し同行させます（下項目ONで自動ホスト）。",
                                "월드 진입 시 AI 전용 멀티 캐릭터를 생성해 합류시킵니다.",
                                "Automatycznie tworzy postać co-op dla AI po wejściu do świata.",
                                "Cria automaticamente um personagem coop para a IA ao entrar no mundo.",
                                "Создаёт отдельного кооп-персонажа для ИИ при входе в мир."),
                            AiCompanion.CfgCoopAutoAvatar);

                        AddModalBoolRow(ref rowIdx,
                            ModLocalization.Pick("单机自动开启本地联机房", "Auto Host Co-op from Solo", "Koop-Host im Solo starten", "Abrir host local en solo", "Ouvrir hôte local en solo", "ソロ時にローカル協力部屋を自動開設", "싱글 시 로컬 협동방 자동 개설", "Auto-host co-op w solo", "Abrir sala local no solo", "Авто-хост коопа в соло"),
                            ModLocalization.Pick(
                                "处于单人游戏时，允许 AI 玩家把当前世界无缝开启为本地联机房主模式，以便作为正常玩家角色加入。",
                                "From singleplayer, opens your world as a local co-op host so the AI can join as a normal player.",
                                "Startet aus dem Einzelspieler einen lokalen Koop-Host, damit die KI beitreten kann.",
                                "Desde un jugador, abre tu partida como host local para que la IA entre como jugador.",
                                "En solo, ouvre votre partie en hôte local pour que l'IA rejoigne comme joueur.",
                                "ソロプレイ中、AIプレイヤーが参加できるようにローカル協力部屋としてシームレスに開きます。",
                                "싱글 플레이 중 AI가 일반 플레이어로 들어올 수 있도록 로컬 협동 방을 엽니다.",
                                "W trybie solo otwiera świat jako lokalny host co-op, by AI mogło dołączyć.",
                                "No modo solo, abre o mundo como host local para a IA entrar como jogador.",
                                "В одиночной игре открывает мир как локальный кооп-хост для входа ИИ."),
                            AiCompanion.CfgPlayerAutoHost);

                        _winAvatarDesc = AddModalActionRow(ref rowIdx,
                            ModLocalization.Pick("AI 玩家角色状态（召唤 / 传送）", "AI Player Status (Spawn / Teleport)", "KI-Spielerstatus (Rufen / Teleport)", "Estado del jugador IA (Invocar/TP)", "Statut du joueur IA (Invoquer/TP)", "AIプレイヤー状態（召喚・移動）", "AI 플레이어 상태 (소환 / 텔레포트)", "Status gracza AI (Przyzwij/TP)", "Status do jogador IA (Invocar/TP)", "Статус ИИ-игрока (Призыв / ТП)"),
                            AiCoopPlayer.GetAvatarSummary(),
                            ModLocalization.Pick("召唤 / 传送", "Spawn / Teleport", "Rufen / Teleport", "Invocar / TP", "Invoquer / TP", "召喚 / テレポート", "소환 / 텔레포트", "Przyzwij / TP", "Invocar / TP", "Призыв / ТП"),
                            () =>
                            {
                                string msg;
                                if (!AiCoopPlayer.IsNetPlayerActive && AiCoopPlayer.GetAvatarAgent() == null)
                                    AiCoopPlayer.EnsureOrFindAvatarNpc(true, out msg);
                                else
                                    AiCoopPlayer.ForceTeleportToPlayer(out msg);
                                if (_winAvatarDesc != null) _winAvatarDesc.text = msg;
                                SetModalStatus(msg);
                            });

                        AddModalChoiceRow(ref rowIdx,
                            ModLocalization.Pick("AI 玩家默认协同姿态", "Default Co-op Posture", "Standard-Koop-Verhalten", "Postura coop predeterminada", "Posture coop par défaut", "AIプレイヤー既定行動", "AI 플레이어 기본 자세", "Domyślna postawa co-op", "Postura coop padrão", "Поведение ИИ-игрока"),
                            ModLocalization.Pick(
                                "控制 AI 玩家进入房间后的行动逻辑：紧跟玩家 / 积极迎战 / 原地驻守 / 协同拾荒。",
                                "Controls AI player behavior after joining: Follow closely / Engage enemies / Hold position / Loot nearby.",
                                "Verhalten nach Beitritt: Eng folgen / Aktiv kämpfen / Position halten / Plündern.",
                                "Comportamiento al unirse: Seguir / Combatir / Mantener posición / Saquear.",
                                "Comportement en partie : Suivre / Engager / Tenir la position / Piller.",
                                "参加後の行動方針：追従 / 積極交戦 / その場待機 / 協力探索。",
                                "참가 후 행동 방침: 밀착 추종 / 적극 교전 / 위치 사수 / 협동 수색.",
                                "Zachowanie po dołączeniu: Podążaj / Walcz / Stój w miejscu / Szukaj łupów.",
                                "Comportamento na sala: Seguir / Combater / Manter posição / Saquear.",
                                "Тактика после входа: Следовать / Вступать в бой / Держать позицию / Искать лут."),
                            AiCoopPlayer.BehaviorLabels, AiCoopPlayer.BehaviorValues, AiCompanion.CfgCoopBehavior,
                            val =>
                            {
                                if (AiCompanion.CfgCoopBehavior != null) AiCompanion.CfgCoopBehavior.Value = val;
                                AiCoopPlayer.SetBehaviorMode(val);
                                SetModalStatus(ModLocalization.Pick(
                                    $"玩家模式姿态已切换为 {val}",
                                    $"Co-op posture set to {val}",
                                    $"Koop-Verhalten -> {val}",
                                    $"Postura cambiada a {val}",
                                    $"Posture définie sur {val}",
                                    $"行動方針を {val} に変更しました",
                                    $"플레이어 모드 자세 변경: {val}",
                                    $"Zmieniono postawę na {val}",
                                    $"Postura alterada para {val}",
                                    $"Тактика переключена на {val}"));
                            });

                        AddModalBoolRow(ref rowIdx,
                            ModLocalization.Pick("客户端自动协同房主", "Client Auto-Follow Host", "Als Client Host folgen", "Cliente sigue al host", "Client suit l'hôte auto", "クライアント時は自動追従", "클라이언트 시 방장 자동 추종", "Klient automatycznie podąża", "Cliente segue o host", "Клиент следует за хостом"),
                            ModLocalization.Pick(
                                "作为独立客户端连入他人房间且无手动按键时，由 AI 自动跟随房主并参与警戒。",
                                "When connected as a client with no manual input, AI automatically follows the host and watches for threats.",
                                "Folgt dem Host automatisch und sichert ab, wenn Sie als Client keine Eingaben machen.",
                                "Al unirte como cliente sin mover el teclado, la IA sigue al host y vigila peligros.",
                                "En tant que client sans saisie manuelle, l'IA suit l'hôte et surveille les menaces.",
                                "クライアント参加時、操作がない間は自動でホストに追従して警戒します。",
                                "클라이언트로 참가해 조작이 없을 때 AI가 방장을 자동 추종하며 경계합니다.",
                                "Jako klient bez ręcznego sterowania AI automatycznie podąża za hostem.",
                                "Conectado como cliente sem teclas manuais, a IA segue o host automaticamente.",
                                "При подключении клиентом без ручного ввода ИИ автоматически следует за хостом."),
                            AiCompanion.CfgCoopDriveLocalWhenClient);

                        AddModalTextRow(ref rowIdx,
                            ModLocalization.Pick("联机房间码（加入他人房间）", "Room Code (Join Room)", "Raumcode (Beitreten)", "Código de sala (Unirse)", "Code de salle (Rejoindre)", "ルームコード（参加用）", "멀티 방 코드 (참가용)", "Kod pokoju (Dołącz)", "Código da sala (Entrar)", "Код комнаты (Вход)"),
                            ModLocalization.Pick(
                                "填写要加入的房间码或 IP:端口（例如 ABCD12 或 127.0.0.1:7777）",
                                "Enter room code or IP:port to join (e.g. ABCD12 or 127.0.0.1:7777).",
                                "Raumcode oder IP:Port zum Beitreten (z. B. ABCD12 oder 127.0.0.1:7777).",
                                "Código de sala o IP:puerto para unirte (ej. ABCD12 o 127.0.0.1:7777).",
                                "Code de salle ou IP:port à rejoindre (ex. ABCD12 ou 127.0.0.1:7777).",
                                "参加先のルームコードまたは IP:ポートを入力（例: ABCD12 / 127.0.0.1:7777）",
                                "참가할 방 코드 또는 IP:포트 입력 (예: ABCD12 또는 127.0.0.1:7777)",
                                "Wpisz kod pokoju lub IP:port (np. ABCD12 lub 127.0.0.1:7777).",
                                "Digite o código da sala ou IP:porta (ex.: ABCD12 ou 127.0.0.1:7777).",
                                "Введите код комнаты или IP:порт (например ABCD12 или 127.0.0.1:7777)."),
                            AiCompanion.CfgCoopRoomCode, false,
                            ModLocalization.Pick("输入房间码（例如 ABCD12）", "Room code (e.g. ABCD12)", "Raumcode (z. B. ABCD12)", "Código (ej. ABCD12)", "Code (ex. ABCD12)", "コード入力 (例: ABCD12)", "방 코드 입력 (예: ABCD12)", "Kod pokoju (np. ABCD12)", "Código (ex.: ABCD12)", "Код (напр. ABCD12)"));

                        _winRoomDesc = AddModalActionRow(ref rowIdx,
                            ModLocalization.Pick("联机房间控制", "Co-op Room Control", "Koop-Raumsteuerung", "Control de sala Coop", "Contrôle de salle Coop", "マルチ部屋操作", "멀티 방 제어", "Sterowanie pokojem Co-op", "Controle da sala Coop", "Управление комнатой"),
                            $"{AiCoopPlayer.GetRoomSummary()} | " + ModLocalization.Pick(
                                "点右侧按码进房；如需当前档直接变房主请用“开启联机房”",
                                "Click to join by code; use 'Host Co-op Room' in Tab 3 to host the current save.",
                                "Klicken zum Beitreten per Code; in Tab 3 lokalen Host für den Spielstand starten.",
                                "Pulsa para entrar por código; en la pestaña 3 puedes abrir sala como host.",
                                "Cliquez pour rejoindre ; utilisez l'onglet 3 pour héberger la partie.",
                                "右ボタンでコード参加。現在セーブをホストするにはタブ3「協力部屋を開設」を使用。",
                                "오른쪽 버튼으로 코드 참가. 현재 세이브 호스트는 탭 3에서 개설.",
                                "Kliknij, aby dołączyć kodem; hostowanie obecnego zapisu w karcie 3.",
                                "Clique para entrar por código; use a aba 3 para hospedar este save.",
                                "Нажмите для входа по коду; открыть хост для текущего сохранения можно во вкладке 3."),
                            ModLocalization.Pick("按码进房", "Join by Code", "Per Code beitreten", "Entrar por código", "Rejoindre par code", "コードで参加", "코드로 참가", "Dołącz kodem", "Entrar por código", "Войти по коду"),
                            () =>
                            {
                                AiCoopPlayer.TryJoinRoomByCode(AiCompanion.CoopRoomCode, "", out string msg);
                                if (_winRoomDesc != null) _winRoomDesc.text = msg;
                                SetModalStatus(msg);
                            });
                    }
                    else if (AiCompanion.IsNpcMode())
                    {
                        AddModalActionRow(ref rowIdx,
                            ModLocalization.Pick("官方雇佣 NPC 能力与限制说明", "Hired NPC Scope & Limits", "Umfang & Grenzen Söldner-NPC", "Alcance y límites PNJ", "Capacités et limites PNJ", "雇用NPCの操作範囲", "고용 NPC 기능 및 제한", "Zakres najętego NPC", "Limites do NPC contratado", "Возможности наёмного NPC"),
                            ModLocalization.Pick(
                                "官方设计仅支持：跟随/驻守/架枪警戒/开火停火/远近战偏好/搜刮附近资源/拾取指定物品/手雷与治疗烟雾/自愈整理救人；不可建造/合成/开车。",
                                "Strictly follows built-in NPC mechanics: follow/hold/overwatch/fire/weapon range/loot nearby/pickup/grenades & healing smoke/heal & rescue; cannot build, craft, or drive.",
                                "Nutzt streng offizielle NPC-Befehle: Folgen/Halten/Feuern/Plündern/Granaten/Heilen & Retten; kein Bauen, Craften oder Fahren.",
                                "Solo usa funciones oficiales de PNJ: seguir/mantener/fuego/saquear/granadas/curar y rescatar; no construye, fabrica ni conduce.",
                                "Suit les règles officielles des PNJ : suivre/tenir/tirer/piller/grenades/soigner ; impossible de construire, crafter ou conduire.",
                                "公式仕様に準拠：追従・待機・警戒・射撃制御・探索・拾得・手榴弾・回復煙幕・応急手当のみ対応（建築・製作・運転は不可）。",
                                "공식 NPC 규격 준수: 추종/대기/경계/사격/수색/줍기/수류탄·치유연막/자체회복·구조만 가능(건설·제작·운전 불가).",
                                "Zgodnie z grą: podążaj/stój/ogień/łupienie/granaty/leczenie i ratunek; bez budowania, craftingu i jazdy.",
                                "Segue o sistema oficial de NPC: seguir/manter/atirar/saquear/granadas/curar; não constrói, cria ou dirige.",
                                "Строго по механике игры: следовать/держать/огонь/обыск/гранаты/лечение и помощь; без стройки, крафта и транспорта."),
                            ModLocalization.Pick("刷新状态", "Refresh", "Aktualisieren", "Actualizar", "Actualiser", "状態更新", "상태 새로고침", "Odśwież", "Atualizar", "Обновить"),
                            () =>
                            {
                                RefreshModalStatus(true);
                                SetModalStatus(ModLocalization.CurrentPrefix() + AiCompanion.StatusText());
                            });

                        AddModalActionRow(ref rowIdx,
                            ModLocalization.Pick("单人 vs 联机适配说明", "Singleplayer vs Co-op Note", "Einzel- vs. Mehrspieler", "Individual vs. Multijugador", "Solo vs Multijoueur", "ソロ / マルチ対応状況", "싱글 vs 멀티 호환 안내", "Tryb solo vs multiplayer", "Solo vs Multijogador", "Одиночная игра и кооп"),
                            ModLocalization.Pick(
                                $"当前环境：{AiCompanion.GetNetworkRoleLabel()}。仅单人或联机房主拥有雇佣 NPC 控制权；联机客机不会越权改写房主端 NPC。",
                                $"Environment: {AiCompanion.GetNetworkRoleLabel()}. Only Singleplayer or the Co-op Host has authority over hired NPCs; clients will not override host NPCs.",
                                $"Umgebung: {AiCompanion.GetNetworkRoleLabel()}. Nur Einzelspieler oder Host haben Kontrolle über angeheuerte NPCs.",
                                $"Entorno: {AiCompanion.GetNetworkRoleLabel()}. Solo un jugador o el anfitrión controlan a los PNJ contratados.",
                                $"Environnement : {AiCompanion.GetNetworkRoleLabel()}. Seuls le solo et l'hôte contrôlent les PNJ recrutés.",
                                $"現在の環境：{AiCompanion.GetNetworkRoleLabel()}。雇用NPCの操作権限はソロまたはホスト側のみに限定されます。",
                                $"현재 환경: {AiCompanion.GetNetworkRoleLabel()}. 고용 NPC 제어 권한은 싱글 또는 멀티 방장에게만 있습니다.",
                                $"Środowisko: {AiCompanion.GetNetworkRoleLabel()}. Tylko tryb solo lub host ma kontrolę nad najętymi NPC.",
                                $"Ambiente: {AiCompanion.GetNetworkRoleLabel()}. Apenas solo ou o host controla NPCs contratados.",
                                $"Среда: {AiCompanion.GetNetworkRoleLabel()}. Управление наёмным NPC доступно только в одиночной игре или у хоста."),
                            ModLocalization.Pick("立即检测", "Check Now", "Prüfen", "Comprobar", "Vérifier", "再検出", "즉시 확인", "Sprawdź", "Verificar", "Проверить"),
                            () =>
                            {
                                RefreshModalStatus(true);
                                SetModalStatus(AiCompanion.GetNetworkRoleLabel());
                            });

                        AddModalBoolRow(ref rowIdx,
                            ModLocalization.Pick("NPC 自主决策行动", "NPC Autonomous Decisions", "Autonome NPC-Aktionen", "Decisiones autónomas PNJ", "Décisions autonomes PNJ", "NPC自律行動", "NPC 자율 판단 행동", "Autonomiczne decyzje NPC", "Ações autônomas do NPC", "Автономные решения NPC"),
                            ModLocalization.Pick(
                                "没有玩家聊天指挥时，按计时与战局自主在官方可用行为与战术命令间切换。",
                                "When idle without chat orders, switches autonomously among built-in NPC tactical goals and stances based on combat state.",
                                "Wechselt ohne Chat-Befehle selbstständig zwischen offiziellen taktischen Zielen und Haltungen.",
                                "Sin órdenes por chat, alterna autónomamente entre posturas y objetivos tácticos oficiales del PNJ.",
                                "Sans ordre dans le chat, choisit seul parmi les objectifs et postures tactiques officiels du PNJ.",
                                "プレイヤーの指示がない時、戦況に応じて公式の行動目標や戦術コマンドを自律選択します。",
                                "채팅 지시가 없을 때 전황에 맞춰 게임 기본 전술 목표와 자세를 자율적으로 전환합니다.",
                                "Bez rozkazów na czacie samodzielnie przełącza taktyczne cele i postawy NPC.",
                                "Sem ordens no chat, alterna sozinho entre metas e posturas táticas oficiais do NPC.",
                                "Без команд в чате самостоятельно выбирает встроенные тактические цели и стойки NPC."),
                            AiCompanion.CfgAutonomous);

                        AddModalBoolRow(ref rowIdx,
                            ModLocalization.Pick("测试：临时接管最近友好NPC", "Test: Take Over Nearest Friendly NPC", "Test: Nächsten freundl. NPC steuern", "Prueba: Controlar PNJ cercano", "Test : Contrôler PNJ proche", "テスト：近くの友好NPCを仮操作", "테스트: 근처 우호 NPC 임시 제어", "Test: Przejmij najbliższego NPC", "Teste: Assumir NPC amigável próximo", "Тест: Взять ближайшего дружеств. NPC"),
                            ModLocalization.Pick(
                                "仅调试用（正常游玩建议关闭）：队伍里还没有正式招募队友时，临时接管附近友好人类 NPC。",
                                "Debug only (keep off for normal play): temporarily controls a nearby friendly human NPC when no teammate is hired yet.",
                                "Nur zum Testen (normal aus lassen): übernimmt vorübergehend einen nahen freundlichen NPC.",
                                "Solo pruebas (desactivar al jugar): controla temporalmente a un PNJ humano cercano si no reclutaste ninguno.",
                                "Test uniquement (désactiver en jeu) : contrôle un PNJ humain amical proche si aucun équipier n'est recruté.",
                                "デバッグ用（通常プレイ時はOFF推奨）：雇用済み仲間がいない場合に近くの友好NPCを仮制御します。",
                                "디버그 전용(일반 플레이 시 끄기 권장): 영입한 동료가 없을 때 근처 우호 NPC를 임시 제어합니다.",
                                "Tylko do testów (zalecane wył.): tymczasowo przejmuje pobliskiego przyjaznego NPC.",
                                "Apenas teste (desative ao jogar): assume um NPC humano amigável próximo sem recrutados.",
                                "Только для отладки (обычно выкл.): временно берёт дружелюбного NPC рядом, если никто не нанят."),
                            AiCompanion.CfgTakeoverTest);

                        AddModalBoolRow(ref rowIdx,
                            ModLocalization.Pick("允许有理由拒绝", "Allow Reasoned Refusal", "Begründete Ablehnung erlauben", "Permitir rechazo justificado", "Autoriser le refus motivé", "理由付き拒否を許可", "합리적 거절 허용", "Zezwalaj na odmowę z powodem", "Permitir recusa justificada", "Разрешить отказ с причиной"),
                            ModLocalization.Pick(
                                "当指令过于危险或会糟蹋关键物资时，允许 NPC 在聊天框说明理由并拒绝。",
                                "Allows the NPC to refuse overly reckless orders or commands that waste critical supplies, explaining why in chat.",
                                "Erlaubt dem NPC, zu gefährliche Befehle mit Begründung im Chat abzulehnen.",
                                "Permite al PNJ rechazar órdenes temerarias o desperdicio de recursos, explicando el motivo.",
                                "Permet au PNJ de refuser des ordres trop risqués en expliquant pourquoi dans le chat.",
                                "危険すぎる指示や重要物資の浪費を伴う場合、チャットで理由を述べて断ることを許可します。",
                                "지나치게 위험하거나 핵심 물자를 낭비하는 명령일 경우 이유를 말하고 거절할 수 있습니다.",
                                "Pozwala NPC odmówić zbyt ryzykownych rozkazów z podaniem powodu na czacie.",
                                "Permite que o NPC recuse ordens arriscadas ou desperdício de suprimentos explicando o motivo.",
                                "Позволяет NPC отказать в слишком опасном приказе и объяснить причину в чате."),
                            AiCompanion.CfgAllowRefuse);
                    }
                    else
                    {
                        AddModalActionRow(ref rowIdx,
                            ModLocalization.Pick("聊天陪伴模式功能说明", "Chat Companion Overview", "Chat-Begleiter Überblick", "Resumen del Compañero", "Aperçu du Compagnon", "チャット相棒モード概要", "채팅 동반자 기능 안내", "Opis towarzysza czatu", "Visão geral do Companheiro", "Обзор компаньона в чате"),
                            ModLocalization.Pick(
                                "通过官方聊天框陪你聊天、按多变口语提醒掉血/饥渴/口渴/电量/未加点数/视野内威胁（防机械重复），并在已有材料与已知信息内给建议。",
                                "Chats naturally in the game chat box, varies alerts for low HP/hunger/thirst/battery/unused points/visible threats (no spam), and advises using known info & materials.",
                                "Chattet im Spiel-Chat, warnt abwechslungsreich vor HP/Hunger/Durst/Akku/Punkten/Gefahr und berät im Rahmen bekannter Ressourcen.",
                                "Conversa en el chat oficial, alerta con variedad sobre salud/hambre/sed/batería/puntos/peligros y aconseja con lo conocido.",
                                "Discute dans le chat, varie les alertes santé/faim/soif/batterie/points/menaces et conseille selon vos ressources connues.",
                                "公式チャット欄で会話やHP・空腹・渇き・バッテリー・未振りPT・視界内の脅威を自然な口調で通知し、所持資材の範囲で助言します。",
                                "공식 채팅창으로 대화하며 체력/배고픔/갈증/배터리/미사용 포인트/시야 내 위협을 자연스럽게 알리고 보유 재료 기준으로 조언합니다.",
                                "Rozmawia na czacie gry, urozmaica alerty (HP/głód/pragnienie/bateria/punkty/zagrożenia) i doradza wg posiadanych zasobów.",
                                "Conversa no chat oficial, varia alertas de vida/fome/sede/bateria/pontos/ameaças e aconselha pelo que você já tem.",
                                "Общается в чате, разнообразно напоминает о HP/голоде/жажде/батарее/очках/угрозах и советует по доступным ресурсам."),
                            ModLocalization.Pick("查看当前状态", "Check Status", "Status prüfen", "Ver estado", "Voir statut", "状態確認", "현재 상태 확인", "Sprawdź status", "Ver status", "Проверить статус"),
                            () =>
                            {
                                RefreshModalStatus(true);
                                SetModalStatus(AiCompanion.GetNetworkRoleLabel());
                            });

                        AddModalActionRow(ref rowIdx,
                            ModLocalization.Pick("单人 vs 联机适配说明", "Singleplayer vs Co-op Note", "Einzel- vs. Mehrspieler", "Individual vs. Multijugador", "Solo vs Multijoueur", "ソロ / マルチ対応状況", "싱글 vs 멀티 호환 안내", "Tryb solo vs multiplayer", "Solo vs Multijogador", "Одиночная игра и кооп"),
                            ModLocalization.Pick(
                                $"当前：{AiCompanion.GetNetworkRoleLabel()}。单人模式开放完整背包整理/制造/丢弃/容器归并任务；联机下仅执行高亮指引、手电与只读制造规划。",
                                $"Current: {AiCompanion.GetNetworkRoleLabel()}. Singleplayer allows inventory sort/craft/discard/container tasks if enabled; Co-op runs highlight guides, flashlight & read-only craft plans.",
                                $"Aktuell: {AiCompanion.GetNetworkRoleLabel()}. Einzelspieler erlaubt Inventar-/Craft-/Container-Aufgaben (falls an); Koop bleibt sicher auf Hinweise & Licht beschränkt.",
                                $"Actual: {AiCompanion.GetNetworkRoleLabel()}. En un jugador permite tareas de mochila/fabricación/cajas si están activas; en coop solo guías, linterna y planes.",
                                $"Actuel : {AiCompanion.GetNetworkRoleLabel()}. Le solo autorise tri/craft/coffres (si activés) ; la coop limite aux repères visuels, lampe et plans.",
                                $"現在：{AiCompanion.GetNetworkRoleLabel()}。ソロでは権限ON時の整理・製作タスクが利用可能。マルチでは強調案内・ライト・読み取り専用プランのみ実行。",
                                $"현재: {AiCompanion.GetNetworkRoleLabel()}. 싱글에서는 허용 시 정리·제작 작업 가능, 멀티에서는 강조 안내·손전등·읽기 전용 제작 계획만 실행.",
                                $"Teraz: {AiCompanion.GetNetworkRoleLabel()}. Solo odblokowuje porządkowanie/crafting (po włączeniu); co-op ogranicza się do podświetleń, latarki i planów.",
                                $"Atual: {AiCompanion.GetNetworkRoleLabel()}. Solo libera tarefas de mochila/criação se ativadas; coop executa destaques, lanterna e planos somente leitura.",
                                $"Сейчас: {AiCompanion.GetNetworkRoleLabel()}. В соло доступны задачи инвентаря/крафта (если включены); в коопе — подсветка, фонарик и расчёт рецептов."),
                            ModLocalization.Pick("刷新检测", "Re-check", "Neu prüfen", "Reanalizar", "Revérifier", "再確認", "다시 감지", "Odśwież", "Reverificar", "Обновить"),
                            () =>
                            {
                                RefreshModalStatus(true);
                                SetModalStatus(AiCompanion.GetNetworkRoleLabel());
                            });

                        AddModalBoolRow(ref rowIdx,
                            ModLocalization.Pick("主动陪伴提醒（非对话触发）", "Proactive Alerts", "Proaktive Hinweise", "Alertas proactivas", "Alertes proactives", "能動的な状況リマインド", "능동적 동반 알림", "Aktywne przypomnienia", "Alertas proativos", "Проактивные подсказки"),
                            ModLocalization.Pick(
                                "关闭后仅在你发消息时回复；开启则会在掉血、电量低于两成、有未分配点数或遇敌时自然提醒。",
                                "When off, replies only when you chat; when on, gently warns on damage, battery < 20%, unspent points, or nearby enemies.",
                                "Aus = antwortet nur auf Chat; An = warnt natürlich bei Schaden, Akku < 20%, Punkten oder Feinden.",
                                "Desactivado = solo responde cuando hablas; activado = avisa de daño, batería baja, puntos libres o enemigos.",
                                "Désactivé = répond uniquement à vos messages ; activé = prévient en cas de dégâts, batterie < 20%, points ou ennemis.",
                                "OFF時は話しかけた時のみ返信。ON時はダメージ・バッテリー20%未満・未振りPT・敵接近などを自然に知らせます。",
                                "끄면 채팅 시에만 응답하고, 켜면 피해·배터리 20% 미만·미사용 포인트·적 출현 시 자연스럽게 알립니다.",
                                "Wył. = odpowiada tylko na czat; wł. = przypomina o obrażeniach, baterii < 20%, punktach i wrogach.",
                                "Desativado = só responde ao seu chat; ativado = avisa sobre dano, bateria < 20%, pontos ou inimigos.",
                                "Выкл. = отвечает только на ваши сообщения; вкл. = предупреждает об уроне, батарее < 20%, очках и врагах."),
                            AiCompanion.CfgCompanionMode);

                        AddModalBoolRow(ref rowIdx,
                            ModLocalization.Pick("允许有理由拒绝", "Allow Reasoned Refusal", "Begründete Ablehnung erlauben", "Permitir rechazo justificado", "Autoriser le refus motivé", "理由付き拒否を許可", "합리적 거절 허용", "Zezwalaj na odmowę z powodem", "Permitir recusa justificada", "Разрешить отказ с причиной"),
                            ModLocalization.Pick(
                                "材料不足、配方未解锁或超出已知信息边界时，直接如实拒绝或说明缺口，不假装完成。",
                                "Explains missing materials or locked recipes honestly instead of pretending a request succeeded.",
                                "Lehnt ehrlich ab oder nennt fehlende Materialien/Rezepte, statt Erfolg vorzutäuschen.",
                                "Explica con honestidad si faltan materiales o recetas sin fingir que lo completó.",
                                "Refuse honnêtement ou indique les manques si une recette est verrouillée ou des matériaux manquent.",
                                "素材不足・未解放レシピ・情報外の要求に対して、できたふりをせず正直に理由と不足分を伝えます。",
                                "재료 부족, 미해금 레시피 또는 정보 범위 밖 요청 시 완료한 척하지 않고 솔직히 이유와 부족분을 설명합니다.",
                                "Uczciwie wyjaśnia brakujące materiały lub zablokowane receptury zamiast udawać wykonanie.",
                                "Explica com honestidade materiais em falta ou receitas bloqueadas sem fingir conclusão.",
                                "Честно сообщает о нехватке материалов или закрытом рецепте вместо имитации выполнения."),
                            AiCompanion.CfgAllowRefuse);

                        AddModalBoolRow(ref rowIdx,
                            ModLocalization.Pick("自动关闭全屏 NPC 对话框", "Auto-Close Fullscreen NPC Dialogue", "Vollbild-NPC-Dialog schließen", "Cerrar diálogo NPC completo", "Fermer dialogue PNJ plein écran", "全画面NPC会話を自動で閉じる", "전체화면 NPC 대화창 자동 닫기", "Auto-zamykaj pełnoekr. dialog NPC", "Fechar diálogo NPC tela cheia", "Авто-закрытие диалога NPC"),
                            ModLocalization.Pick(
                                "仅当游戏全屏对话框打断操作时可开启；正常游玩建议保持关闭。",
                                "Enable only if fullscreen NPC dialogue blocks control; keep off during normal play.",
                                "Nur aktivieren, falls Vollbild-NPC-Dialoge die Steuerung blockieren; normal aus lassen.",
                                "Actívalo solo si los diálogos a pantalla completa bloquean el control.",
                                "À activer uniquement si les dialogues PNJ plein écran bloquent le jeu.",
                                "全画面NPC会話が操作を遮る場合のみONにしてください（通常プレイはOFF推奨）。",
                                "전체화면 NPC 대화창이 조작을 방해할 때만 켜세요(일반 플레이 시 끄기 권장).",
                                "Włącz tylko, gdy pełnoekranowy dialog NPC blokuje sterowanie.",
                                "Ative apenas se o diálogo NPC em tela cheia travar o controle.",
                                "Включайте только если полноэкранный диалог NPC мешает управлению."),
                            AiCompanion.CfgCloseDialoguePanel);
                    }
                }
                else if (_winTab == 2)
                {
                    if (AiCompanion.IsDshMode)
                    {
                        AddModalActionRow(ref rowIdx,
                            ModLocalization.Pick("当前使用：DSH 本地 Agent 模式", "Active: DSH Local Agent Mode", "Aktiv: DSH Lokaler Agent", "Activo: Modo Agente local DSH", "Actif : Mode Agent local DSH", "現在：DSH ローカル連携モード", "현재 사용: DSH 로컬 에이전트", "Aktywny: Tryb lokalny DSH", "Ativo: Modo Agente local DSH", "Активен: Локальный агент DSH"),
                            ModLocalization.Pick(
                                $"通过游戏目录桥接文件连接本地 DSH Agent（{AiCompanion.StateFilePath}），无需填 API Key，支持跨存档人物记忆。",
                                $"Connects to local DSH Desktop via bridge files ({AiCompanion.StateFilePath}); no API Key needed inside the mod.",
                                $"Verbindet sich über Bridge-Dateien mit DSH Desktop ({AiCompanion.StateFilePath}); kein API-Key im Mod nötig.",
                                $"Conecta con DSH Desktop mediante archivos puente ({AiCompanion.StateFilePath}); no requiere API Key aquí.",
                                $"Connecté à DSH Desktop via fichiers pont ({AiCompanion.StateFilePath}) ; aucune clé API requise ici.",
                                $"ゲーム内の連携ファイル経由でローカルのDSHと接続します（{AiCompanion.StateFilePath}）。APIキー入力不要。",
                                $"브리지 파일({AiCompanion.StateFilePath})을 통해 로컬 DSH와 연동합니다. API 키 입력이 필요 없습니다.",
                                $"Łączy z lokalnym DSH przez pliki mostu ({AiCompanion.StateFilePath}); klucz API zbędny.",
                                $"Conecta ao DSH Desktop via arquivos de ponte ({AiCompanion.StateFilePath}); dispensa API Key aqui.",
                                $"Подключается к DSH Desktop через файлы обмена ({AiCompanion.StateFilePath}); API-ключ не требуется."),
                            ModLocalization.Pick("切回 API 模式", "Switch to API Mode", "Zu API wechseln", "Cambiar a API", "Passer en mode API", "APIモードへ切替", "API 모드로 전환", "Przełącz na API", "Mudar para API", "Переключить на API"),
                            () =>
                            {
                                AiCompanion.SetRunMode("api");
                                SetModalStatus(ModLocalization.Pick(
                                    "已切换为 API 接入模式", "Switched to API connection mode", "Zu API-Modus gewechselt",
                                    "Cambiado a modo API", "Passé en mode API", "API接続モードに切り替えました",
                                    "API 연결 모드로 전환됨", "Przełączono na tryb API", "Alterado para modo API", "Переключено на режим API"));
                                RenderConfigWindow();
                            });

                        AddModalActionRow(ref rowIdx,
                            ModLocalization.Pick("开启联机房主（便捷入口）", "Host Co-op Room (Shortcut)", "Koop-Raum hosten (Schnellstart)", "Abrir sala Coop (Acceso rápido)", "Héberger salle Coop (Raccourci)", "協力部屋をホスト（ショートカット）", "멀티 방 호스트 열기 (바로가기)", "Hostuj pokój Co-op (Skrót)", "Hospedar sala Coop (Atalho)", "Создать кооп-хост (Быстро)"),
                            $"{AiCoopPlayer.GetRoomSummary()} | " + ModLocalization.Pick(
                                "无论选择哪种表现形式，都可在此将当前世界快速开启为联机房主。",
                                "Quickly open the current world as a co-op host regardless of which companion form you picked.",
                                "Öffnet die aktuelle Welt schnell als Koop-Host, unabhängig von der gewählten KI-Rolle.",
                                "Abre rápidamente la partida actual como sala cooperativa con cualquier forma de IA.",
                                "Ouvre rapidement la partie en cours comme salle coopérative quelle que soit la forme choisie.",
                                "どの参加形態でも、ここから現在のセーブを即座にマルチ部屋のホストに切り替えられます。",
                                "어떤 참여 형태를 쓰든 현재 세이브를 즉시 멀티플레이 방장 방으로 열 수 있습니다.",
                                "Szybko otwiera obecny świat jako host co-op niezależnie od wybranej formy AI.",
                                "Abre rapidamente o mundo atual como host coop em qualquer forma da IA.",
                                "Быстро открывает текущий мир как кооп-хост при любой выбранной роли ИИ."),
                            ModLocalization.Pick("开启联机房", "Host Co-op Room", "Raum hosten", "Abrir sala Coop", "Ouvrir salle", "部屋をホスト", "멀티 방 열기", "Otwórz pokój", "Abrir sala", "Открыть комнату"),
                            () =>
                            {
                                AiCoopPlayer.TryOpenHostRoom(7777, true, out string msg);
                                SetModalStatus(msg);
                                RenderConfigWindow();
                            });
                    }
                    else
                    {
                        AddModalChoiceRow(ref rowIdx,
                            ModLocalization.Pick("API 接口协议", "API Protocol", "API-Protokoll", "Protocolo API", "Protocole API", "APIプロトコル", "API 프로토콜", "Protokół API", "Protocolo API", "Протокол API"),
                            ModLocalization.Pick(
                                "选择大模型接口格式（Chat Completions / Anthropic Messages / OpenAI Responses / Ollama 本地）。",
                                "Select API format (OpenAI Chat Completions, Anthropic Messages, OpenAI Responses, or local Ollama).",
                                "API-Format wählen (OpenAI Chat Completions, Anthropic Messages, Responses oder Ollama).",
                                "Elige el formato de API (Chat Completions, Anthropic Messages, Responses u Ollama local).",
                                "Format d'API (OpenAI Chat Completions, Anthropic Messages, Responses ou Ollama local).",
                                "利用するAPI形式を選択（Chat Completions / Anthropic Messages / OpenAI Responses / ローカルOllama）。",
                                "사용할 API 형식을 선택하세요 (Chat Completions / Anthropic Messages / Responses / 로컬 Ollama).",
                                "Wybierz format API (Chat Completions, Anthropic Messages, Responses lub lokalna Ollama).",
                                "Escolha o formato da API (Chat Completions, Anthropic Messages, Responses ou Ollama local).",
                                "Выберите формат API (Chat Completions, Anthropic Messages, Responses или локальная Ollama)."),
                            AiCompanion.ApiProtocolLabels, AiCompanion.ApiProtocolValues, AiCompanion.CfgApiProtocol,
                            val =>
                            {
                                AiCompanion.SetApiProtocol(val);
                                SetModalStatus(ModLocalization.Pick(
                                    $"API 协议已切换为 {val}", $"API protocol set to {val}", $"API-Protokoll -> {val}",
                                    $"Protocolo API -> {val}", $"Protocole API -> {val}", $"API形式を変更 -> {val}",
                                    $"API 프로토콜 변경 -> {val}", $"Protokół API -> {val}", $"Protocolo API -> {val}", $"Протокол API -> {val}"));
                                RenderConfigWindow();
                            });

                        AddModalTextRow(ref rowIdx,
                            ModLocalization.Pick("API 根地址 (BaseUrl)", "API Base URL", "API Basis-URL", "URL base de API", "URL de base API", "API ベースURL", "API 기본 주소 (BaseUrl)", "Bazowy URL API", "URL base da API", "Базовый URL API"),
                            ModLocalization.Pick(
                                "接口根地址，不含具体路径，例：https://api.deepseek.com/v1",
                                "Root endpoint URL without the path suffix, e.g. https://api.deepseek.com/v1",
                                "Basis-URL ohne Pfad-Suffix, z. B. https://api.deepseek.com/v1",
                                "URL raíz sin sufijo de ruta, ej.: https://api.deepseek.com/v1",
                                "URL racine sans suffixe de route, ex. : https://api.deepseek.com/v1",
                                "エンドポイントのルートURL（末尾パス不要）。例：https://api.deepseek.com/v1",
                                "경로를 제외한 기본 API 주소. 예: https://api.deepseek.com/v1",
                                "Bazowy adres URL bez końcówki ścieżki, np. https://api.deepseek.com/v1",
                                "URL raiz sem o sufixo de rota, ex.: https://api.deepseek.com/v1",
                                "Корневой URL без пути эндпоинта, например: https://api.deepseek.com/v1"),
                            AiCompanion.CfgBaseUrl, false, "https://api.deepseek.com/v1");

                        AddModalTextRow(ref rowIdx,
                            ModLocalization.Pick("模型名称 (Model)", "Model Name", "Modellname", "Nombre del modelo", "Nom du modèle", "モデル名 (Model)", "모델 이름 (Model)", "Nazwa modelu", "Nome do modelo", "Имя модели (Model)"),
                            ModLocalization.Pick(
                                "例：deepseek-chat / gpt-4o-mini / claude-sonnet-4 / qwen2.5:14b",
                                "Example: deepseek-chat / gpt-4o-mini / claude-sonnet-4 / qwen2.5:14b",
                                "Beispiel: deepseek-chat / gpt-4o-mini / claude-sonnet-4 / qwen2.5:14b",
                                "Ejemplo: deepseek-chat / gpt-4o-mini / claude-sonnet-4 / qwen2.5:14b",
                                "Exemple : deepseek-chat / gpt-4o-mini / claude-sonnet-4 / qwen2.5:14b",
                                "例：deepseek-chat / gpt-4o-mini / claude-sonnet-4 / qwen2.5:14b",
                                "예: deepseek-chat / gpt-4o-mini / claude-sonnet-4 / qwen2.5:14b",
                                "Przykład: deepseek-chat / gpt-4o-mini / claude-sonnet-4 / qwen2.5:14b",
                                "Exemplo: deepseek-chat / gpt-4o-mini / claude-sonnet-4 / qwen2.5:14b",
                                "Пример: deepseek-chat / gpt-4o-mini / claude-sonnet-4 / qwen2.5:14b"),
                            AiCompanion.CfgModel, false, "deepseek-chat");

                        AddModalTextRow(ref rowIdx,
                            "API Key",
                            ModLocalization.Pick(
                                "本机 Windows DPAPI 加密保存，不回显；输入新值回车即可保存",
                                "Stored locally with Windows DPAPI encryption (hidden); enter a new key and press Enter to save.",
                                "Lokal mit Windows DPAPI verschlüsselt gespeichert (verdeckt); zum Überschreiben neuen Key eingeben.",
                                "Cifrada localmente con Windows DPAPI (oculta); escribe una nueva clave y pulsa Enter.",
                                "Chiffrée localement via Windows DPAPI (masquée) ; saisissez une nouvelle clé puis Entrée.",
                                "Windows DPAPIで暗号化しローカル保存（非表示）。新しい値を入力しEnterで上書き保存。",
                                "Windows DPAPI로 암호화하여 로컬 저장(미표시). 새 키 입력 후 Enter로 저장.",
                                "Zapisywany lokalnie z szyfrowaniem Windows DPAPI (ukryty); wpisz nowy i naciśnij Enter.",
                                "Criptografada localmente via Windows DPAPI (oculta); digite a nova chave e aperte Enter.",
                                "Шифруется локально через Windows DPAPI (скрыт); введите новый ключ и нажмите Enter."),
                            AiCompanion.CfgApiKeyEnc, true,
                            ModLocalization.Pick("sk-...（留空表示保持当前密钥）", "sk-... (leave empty to keep current key)", "sk-... (leer = aktuellen Key behalten)", "sk-... (vacío = mantener clave actual)", "sk-... (vide = garder la clé actuelle)", "sk-...（空欄で現在のキーを維持）", "sk-... (비우면 기존 키 유지)", "sk-... (puste = zachowaj obecny)", "sk-... (vazio = manter chave atual)", "sk-... (пусто = оставить текущий)"));

                        _winTestDesc = AddModalActionRow(ref rowIdx,
                            ModLocalization.Pick("测试 API 连通性", "Test API Connectivity", "API-Verbindung testen", "Probar conexión API", "Tester la connexion API", "API接続テスト", "API 연결 테스트", "Testuj połączenie API", "Testar conexão API", "Проверить подключение API"),
                            string.IsNullOrEmpty(AiCompanion.TestStatus)
                                ? ModLocalization.Pick("用当前 BaseUrl / 模型 / Key 发送一次最小测试请求", "Send a minimal test request using your current Base URL, Model, and Key.", "Sendet eine minimale Testanfrage mit aktueller URL, Modell und Key.", "Envía una petición de prueba mínima con la configuración actual.", "Envoie une requête de test minimale avec l'URL, le modèle et la clé actuels.", "現在のURL・モデル・キーで最小限の接続テストを送信します。", "현재 주소·모델·키로 최소 테스트 요청을 보냅니다.", "Wysyła minimalne żądanie testowe na obecnych ustawieniach.", "Envia uma requisição mínima de teste com URL, modelo e chave atuais.", "Отправляет минимальный тестовый запрос с текущими настройками.")
                                : AiCompanion.TestStatus,
                            ModLocalization.Pick("测试连接", "Test Connection", "Verbindung testen", "Probar", "Tester", "接続テスト", "연결 테스트", "Testuj", "Testar", "Проверить"),
                            () =>
                            {
                                AiCompanion.TestApiConnection();
                                SetModalStatus(ModLocalization.Pick(
                                    "已发起 API 连接测试...", "Started API connection test...", "API-Verbindungstest gestartet...",
                                    "Prueba de conexión iniciada...", "Test de connexion lancé...", "API接続テストを開始しました...",
                                    "API 연결 테스트 시작됨...", "Rozpoczęto test API...", "Teste de conexão iniciado...", "Запущен тест подключения API..."));
                                RefreshModalStatus(true);
                            });
                    }
                }
                else
                {
                    AddModalActionRow(ref rowIdx,
                        ModLocalization.Pick("反作弊边界与当前权限", "Anti-Cheat Rules & Permissions", "Fairness-Regeln & Rechte", "Límites antitrampas y permisos", "Limites anti-triche et droits", "公平性ルールと現在の権限", "공정성 경계 및 현재 권한", "Zasady uczciwości i uprawnienia", "Limites antitrapaça e permissões", "Честная игра и права"),
                        ModLocalization.Pick(
                            $"环境：{AiCompanion.GetNetworkRoleLabel()}。AI 严禁剧透未解锁配方、未检视箱子或视野外目标；下方写操作开关全部默认关闭，需你明确授权。",
                            $"Env: {AiCompanion.GetNetworkRoleLabel()}. AI never reveals locked recipes, uninspected containers, or out-of-sight targets; all write toggles default to Off.",
                            $"Umgebung: {AiCompanion.GetNetworkRoleLabel()}. KI verrät keine gesperrten Rezepte, ungeöffneten Kisten oder Ziele außerhalb der Sicht.",
                            $"Entorno: {AiCompanion.GetNetworkRoleLabel()}. La IA nunca revela recetas bloqueadas, cajas sin abrir ni objetivos fuera de vista.",
                            $"Env. : {AiCompanion.GetNetworkRoleLabel()}. L'IA ne révèle jamais recettes verrouillées, coffres non ouverts ou cibles hors vue.",
                            $"環境：{AiCompanion.GetNetworkRoleLabel()}。未解放レシピ・未調査コンテナ・視界外の情報は読取禁止。下の操作権限は既定OFFです。",
                            $"환경: {AiCompanion.GetNetworkRoleLabel()}. 미해금 레시피, 열지 않은 상자, 시야 밖 목표는 절대 누설하지 않으며 쓰기 권한은 기본 OFF입니다.",
                            $"Środowisko: {AiCompanion.GetNetworkRoleLabel()}. AI nie zdradza zablokowanych receptur, nieotwartych skrzyń ani celów poza wzrokiem.",
                            $"Ambiente: {AiCompanion.GetNetworkRoleLabel()}. A IA nunca revela receitas bloqueadas, caixas não abertas ou alvos fora de vista.",
                            $"Среда: {AiCompanion.GetNetworkRoleLabel()}. ИИ не видит закрытые рецепты, неоткрытые ящики и цели вне поля зрения; права записи по умолчанию выкл."),
                        ModLocalization.Pick("刷新环境", "Refresh Env", "Aktualisieren", "Actualizar", "Actualiser", "環境再取得", "환경 새로고침", "Odśwież", "Atualizar", "Обновить"),
                        () =>
                        {
                            RefreshModalStatus(true);
                            SetModalStatus(AiCompanion.GetNetworkRoleLabel());
                        });

                    AddModalBoolRow(ref rowIdx,
                        ModLocalization.Pick("AI 自主行动（定时决策）", "AI Timed Autonomy", "Zeitgesteuerte KI-Aktionen", "Autonomía por intervalo", "Autonomie périodique", "AI自律判断（定期チェック）", "AI 자율 행동 (주기적 판단)", "Okresowa autonomia AI", "Autonomia periódica da IA", "Автономные проверки ИИ"),
                        ModLocalization.Pick(
                            "无玩家新消息时按间隔自主检查战局（陪伴模式用上方「主动陪伴提醒」控制）。",
                            "Periodically checks game state when no new player messages arrive.",
                            "Prüft in Abständen selbstständig die Lage, wenn keine neuen Nachrichten eingehen.",
                            "Revisa el estado periódicamente cuando no hay mensajes nuevos del jugador.",
                            "Vérifie la situation périodiquement en l'absence de nouveaux messages.",
                            "プレイヤーの発言がない時も定期的に状況を判定します。",
                            "새 메시지가 없을 때 일정 간격으로 상황을 자체 점검합니다.",
                            "Okresowo sprawdza sytuację, gdy brak nowych wiadomości gracza.",
                            "Verifica a situação periodicamente quando não há novas mensagens.",
                            "Периодически оценивает обстановку при отсутствии новых сообщений игрока."),
                        AiCompanion.CfgAutonomous);

                    AddModalBoolRow(ref rowIdx,
                        ModLocalization.Pick("允许 AI 丢弃物品", "Allow AI Item Discard", "KI darf Items ablegen", "Permitir descartar objetos", "Autoriser le dépôt d'objets", "アイテム廃棄を許可", "아이템 버리기 허용", "Zezwalaj na wyrzucanie", "Permitir descartar itens", "Разрешить выброс предметов"),
                        ModLocalization.Pick(
                            "仅作用于随身背包未保护的多余物品（默认关闭，仅给整理建议；联机客机受保护）。",
                            "Applies only to unprotected surplus items in your backpack (Off by default = advice only; blocked for co-op clients).",
                            "Gilt nur für ungeschützte Überschuss-Items im Rucksack (Standard Aus = nur Tipps; im Koop für Clients gesperrt).",
                            "Solo afecta a objetos sobrantes sin proteger en tu mochila (apagado = solo consejos; bloqueado en cliente coop).",
                            "Uniquement sur le surplus non protégé du sac à dos (désactivé par défaut = conseils seuls).",
                            "バックパック内の保護されていない余剰品のみ対象（既定OFF＝助言のみ、マルチクライアント保護あり）。",
                            "배낭의 미보호 여분 아이템만 대상(기본 OFF=조언만 제공, 멀티 클라이언트 보호).",
                            "Dotyczy tylko niechronionych nadmiarów w plecaku (domyślnie wył. = tylko porady).",
                            "Aplica-se apenas a sobras desprotegidas na mochila (desativado por padrão = apenas dicas).",
                            "Только для незащищённых лишних вещей в рюкзаке (по умолчанию выкл. = только советы)."),
                        AiCompanion.CfgAllowDiscard);

                    AddModalBoolRow(ref rowIdx,
                        ModLocalization.Pick("允许 AI 直接合成", "Allow AI Direct Crafting", "KI darf direkt herstellen", "Permitir fabricación directa", "Autoriser le craft direct", "AI直接クラフトを許可", "AI 직접 제작 허용", "Zezwalaj na bezpośredni crafting", "Permitir criação direta", "Разрешить прямой крафт"),
                        ModLocalization.Pick(
                            "仅限已解锁配方与随身现有材料，核对缺口充足后加入官方制造队列（默认关闭，只规划缺口）。",
                            "Unlocked recipes & carried materials only; queues crafting if materials suffice (Off by default = plan & check gaps only).",
                            "Nur freigeschaltete Rezepte und vorhandene Materialien (Standard Aus = nur Bedarfsprüfung).",
                            "Solo recetas desbloqueadas y materiales propios (apagado = solo calcula lo que falta).",
                            "Recettes débloquées et matériaux en poche uniquement (désactivé = planification seule).",
                            "解放済みレシピと手持ち素材のみで製作キューへ追加（既定OFF＝不足確認と提案のみ）。",
                            "해금된 레시피와 보유 재료로만 제작 대기열 추가(기본 OFF=부족분 계산 및 제안만).",
                            "Tylko odblokowane receptury i posiadane materiały (domyślnie wył. = tylko planowanie).",
                            "Apenas receitas liberadas e materiais no inventário (desativado = apenas planeja faltas).",
                            "Только открытые рецепты и имеющиеся материалы (по умолчанию выкл. = только расчёт)."),
                        AiCompanion.CfgAllowDirectCraft);

                    AddModalBoolRow(ref rowIdx,
                        ModLocalization.Pick("允许 AI 归并容器（方案规划）", "Allow Container Sort Planning", "Kistensortierung planen", "Planificar orden de cajas", "Planifier tri des coffres", "収納整理プランニングを許可", "상자 정리 계획 허용", "Planuj sortowanie skrzyń", "Planejar organização de caixas", "Разрешить план сортировки ящиков"),
                        ModLocalization.Pick(
                            "仅限同楼层镜头视野内已检视过的容器，未打开检视的箱子绝不读取（默认关闭）。",
                            "Reads only containers already opened & inspected within camera view on the same floor (Off by default).",
                            "Liest nur bereits geöffnete Kisten in Kamerasichtweite auf derselben Etage (Standard Aus).",
                            "Solo lee contenedores ya abiertos e inspeccionados en vista de cámara y mismo piso (apagado por defecto).",
                            "Lit uniquement les conteneurs déjà ouverts et inspectés visibles à l'écran (désactivé par défaut).",
                            "同フロア・カメラ内の開封済みコンテナのみ対象。未確認の箱は絶対に読みません（既定OFF）。",
                            "같은 층 시야 내 이미 열어본 상자만 대상. 확인하지 않은 상자는 절대 읽지 않습니다(기본 OFF).",
                            "Czyta tylko otwarte wcześniej pojemniki w zasięgu kamery na tym samym piętrze (domyślnie wył.).",
                            "Lê apenas recipientes já abertos no campo de visão e mesmo andar (desativado por padrão).",
                            "Читает только уже открывавшиеся вами ящики в кадре на том же этаже (по умолчанию выкл.)."),
                        AiCompanion.CfgAllowContainerSort);

                    AddModalBoolRow(ref rowIdx,
                        ModLocalization.Pick("允许 AI 归并容器（实际搬运）", "Allow Container Item Moving", "Kisten-Items wirklich umlagern", "Mover objetos entre cajas", "Déplacer objets entre coffres", "収納間の自動アイテム移動を許可", "상자 간 실제 아이템 이동 허용", "Zezwalaj na przenoszenie w skrzyniach", "Mover itens entre caixas", "Разрешить перенос в ящиках"),
                        ModLocalization.Pick(
                            "实验开关（仅单机安全执行）：按数量守恒校验逐件搬运，发现差异立即停手。",
                            "Experimental (Singleplayer only): moves items one by one with strict item-count verification; halts immediately on mismatch.",
                            "Experimentell (nur Einzelspieler): verschiebt einzeln mit strenger Mengenprüfung.",
                            "Experimental (solo un jugador): mueve objeto por objeto verificando cantidades; se detiene ante cualquier diferencia.",
                            "Expérimental (solo uniquement) : déplace objet par objet avec contrôle strict des quantités.",
                            "実験機能（ソロ専用）：個数整合性を検証しながら1点ずつ移動し、不整合があれば即停止します。",
                            "실험 기능(싱글 전용): 수량 무결성을 검증하며 한 개씩 이동하고 오차 시 즉시 중단합니다.",
                            "Eksperymentalne (tylko solo): przenosi pojedynczo z kontrolą ilości.",
                            "Experimental (apenas solo): move item a item verificando a contagem exata.",
                            "Экспериментально (только соло): переносит по одному предмету с проверкой количества."),
                        AiCompanion.CfgAllowContainerSortWrite);

                    AddModalBoolRow(ref rowIdx,
                        ModLocalization.Pick("左下角 MOD 辅助聊天日志", "Bottom-Left Mod Chat Log", "Mod-Chatprotokoll unten links", "Registro de chat del mod", "Journal de chat du mod", "左下MODチャットログ表示", "좌하단 MOD 채팅 로그 표시", "Dziennik czatu moda (lewy dół)", "Log de chat do mod (inferior esq.)", "Лог чата мода (внизу слева)"),
                        ModLocalization.Pick(
                            "在左下角短暂显示辅助/指令回执提示；关闭则只看游戏官方聊天框。",
                            "Briefly shows command confirmations/notices at bottom-left; turn off to rely solely on the game's official chat box.",
                            "Zeigt unten links kurz Befehlsbestätigungen; deaktivieren für reinen Spiel-Chat.",
                            "Muestra avisos breves abajo a la izquierda; desactívalo para ver solo el chat del juego.",
                            "Affiche brièvement les confirmations en bas à gauche ; désactivez pour n'utiliser que le chat du jeu.",
                            "画面左下に補助メッセージや応答ログを一時表示します。OFFで公式チャット欄のみ表示。",
                            "좌측 하단에 보조/명령 확인 로그를 잠시 표시합니다. 끄면 공식 채팅창만 사용합니다.",
                            "Krótko wyświetla potwierdzenia w lewym dolnym rogu; wyłącz, by używać tylko czatu gry.",
                            "Exibe avisos breves no canto inferior esquerdo; desative para usar só o chat do jogo.",
                            "Кратко показывает уведомления внизу слева; выключите, чтобы видеть только игровой чат."),
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
            bool compact = _winTab == 0;
            float step = compact ? 64f : 72f;
            float rh = compact ? 58f : 64f;
            float descH = compact ? 25f : 30f;
            float y = -(rowIdx * step);
            rowIdx++;

            var row = NewRectAnchored(_winRows, "Row_" + rowIdx,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, y), new Vector2(0f, rh));
            var bg = row.gameObject.AddComponent<Image>();
            bg.color = rowIdx % 2 == 1
                ? new Color(0.10f, 0.108f, 0.10f, 0.94f)
                : new Color(0.085f, 0.092f, 0.085f, 0.94f);

            var titleTxt = NewTextAnchored(row, "Title", title, 18,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(16f, compact ? -4f : -6f), new Vector2(585f, compact ? 22f : 24f), TextAnchor.MiddleLeft);
            titleTxt.color = new Color(0.95f, 0.97f, 0.95f, 1f);
            ApplyBestFit(titleTxt, 13, 18, false);

            descText = NewTextAnchored(row, "Desc", desc ?? "", 13,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(16f, compact ? -28f : -31f), new Vector2(585f, descH), TextAnchor.UpperLeft);
            descText.color = new Color(0.68f, 0.72f, 0.68f, 1f);
            ApplyBestFit(descText, 10, 14, true);

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
                if (lbl != null) lbl.text = (on ? ModLocalization.BoolOn() : ModLocalization.BoolOff()) + " ▾";
                if (bg != null) bg.color = on ? new Color(0.18f, 0.38f, 0.20f, 1f) : new Color(0.28f, 0.20f, 0.18f, 1f);
            };
            refresh();

            btn.GetComponent<Button>().onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>((Action)(() =>
            {
                if (cfg == null) return;
                bool cur = cfg.Value;
                string onText = ModLocalization.BoolOn();
                string offText = ModLocalization.BoolOff();
                UiChoicePopup.Show(UiFont(), name, new[] { onText, offText }, cur ? 0 : 1, i =>
                {
                    cfg.Value = i == 0;
                    refresh();
                    SetModalStatus($"{name}: {(i == 0 ? onText : offText)}");
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

        private static string SecretSavedPlaceholder() => ModLocalization.Pick(
            "已加密保存（输入新值回车覆盖）",
            "Encrypted & saved (type new key & press Enter)",
            "Verschlüsselt gespeichert (neuen Key eingeben)",
            "Cifrada y guardada (escribe nueva clave y Enter)",
            "Chiffrée et sauvée (saisissez puis Entrée)",
            "暗号化保存済み（入力してEnterで上書き）",
            "암호화 저장됨 (새 값 입력 후 Enter)",
            "Zapisano szyfrowo (wpisz nowy i Enter)",
            "Criptografada e salva (digite e Enter)",
            "Зашифрован и сохранён (введите новый и Enter)");

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
                ? SecretSavedPlaceholder()
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
                    ph.text = SecretSavedPlaceholder();
                    SetModalStatus(ModLocalization.Pick(
                        "API Key 已加密保存", "API Key encrypted & saved", "API-Key verschlüsselt gespeichert",
                        "API Key cifrada y guardada", "Clé API chiffrée et enregistrée", "APIキーを暗号化保存しました",
                        "API 키가 암호화되어 저장되었습니다", "Klucz API zaszyfrowano i zapisano", "API Key criptografada e salva", "API-ключ зашифрован и сохранён"));
                    DropOutlinePlugin.L.LogInfo("[MOD设置] API Key 已加密保存");
                }
                else
                {
                    cfg.Value = val;
                    SetModalStatus($"{name}: " + ModLocalization.Pick(
                        "已保存", "Saved", "Gespeichert", "Guardado", "Enregistré",
                        "保存しました", "저장됨", "Zapisano", "Salvo", "Сохранено"));
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
                    catch (Exception e) { SetModalStatus($"{name}: {e.Message}"); }
                });
            btn.GetComponent<Image>().color = new Color(0.20f, 0.30f, 0.45f, 1f);
            return descTxt;
        }

        // ───────────────────────── 官方设置入口行 ─────────────────────────

        private static void StripLanguageText(Component c)
        {
            try
            {
                if (c == null || c.gameObject == null) return;
                foreach (var lt in c.gameObject.GetComponentsInChildren<LanguageText>(true))
                {
                    if (lt != null) lt.enabled = false;
                }
            }
            catch { }
        }

        private static Transform MakeEntryRow(Transform content, Transform tpl, int index,
            string name, string desc, string buttonText, Action onClick)
        {
            var row = UnityEngine.Object.Instantiate(tpl.gameObject, content).transform;
            row.name = Marker + "_Row";
            row.SetSiblingIndex(index);
            row.gameObject.SetActive(true);
            StripLanguageText(row);

            var toggleGroup = FindDeep(row, "ToggleGroup");
            if (toggleGroup != null) toggleGroup.gameObject.SetActive(false);
            var dropdown = FindDeep(row, "Dropdown");
            if (dropdown != null) dropdown.gameObject.SetActive(false);

            SetText(row, "PropertyNameText", name);
            SetText(row, "PropertyDescriptionText", desc);
            SetText(row, "OptionDescriptionText", "");

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
            _entryBtnLabel = label;

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
            StripLanguageText(header);
            var titleText = FindDeep(header, "TitleText");
            _headerTitleText = titleText != null ? titleText.GetComponent<Text>() : null;
            if (_headerTitleText != null)
                _headerTitleText.text = "──────────    " + title + "    ──────────";
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

        private static bool IsOwnHeader(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.StartsWith("AI 伙伴") || text.StartsWith("AI伙伴")) return true;
            var nav = ModLocalization.NavLabel();
            return !string.IsNullOrEmpty(nav) && text.Contains(nav);
        }

        private static RectTransform FindLiveHeader(Transform root)
        {
            try
            {
                var tagged = FindDeep(root, Marker + "_Header") as RectTransform;
                if (tagged != null) return tagged;
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

                string navLabel = ModLocalization.NavLabel();
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var it = items[i];
                    if (it == null) continue;
                    bool ours = false;
                    try
                    {
                        ours = (it.gameObject != null && it.gameObject.name == Marker + "_NavItem")
                            || (it.labelText != null && (it.labelText.text == navLabel || it.labelText.text == "AI 伙伴"));
                    }
                    catch { }
                    if (!ours) continue;
                    items.RemoveAt(i);
                    try { if (it.gameObject != null) UnityEngine.Object.Destroy(it.gameObject); } catch { }
                }

                var prefab = bar.itemPrefab;
                var parent = bar.itemParent;
                if (prefab == null || parent == null) return false;

                var go = UnityEngine.Object.Instantiate(prefab, parent);
                go.name = Marker + "_NavItem";
                StripLanguageText(go.transform);
                var item = go.GetComponent<SettingsNavigationItem>();
                if (item == null)
                {
                    UnityEngine.Object.Destroy(go);
                    return false;
                }
                go.transform.SetAsLastSibling();
                item.Init(navLabel, header, bar);
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
                    if (s.Contains("确认") || s.Contains("確定") || s.Contains("確認") || s.Contains("확인")
                        || s.Contains("取消") || s.Contains("キャンセル") || s.Contains("취소")) return true;
                    var up = s.ToUpperInvariant();
                    if (up == "APPLY" || up == "CONFIRM" || up == "CANNEL" || up == "CANCEL"
                        || up == "RESET" || up.Contains("CLEARINVALIDDATA")
                        || up == "BESTÄTIGEN" || up == "ANWENDEN" || up == "ABBRECHEN"
                        || up == "CONFIRMAR" || up == "APLICAR" || up == "CANCELAR"
                        || up == "CONFIRMER" || up == "APPLIQUER" || up == "ANNULER"
                        || up == "POTWIERDŹ" || up == "ZASTOSUJ" || up == "ANULUJ"
                        || up == "ПРИМЕНИТЬ" || up == "ПОДТВЕРДИТЬ" || up == "ОТМЕНА") return true;
                }
            }
            catch { }
            return false;
        }

        // ───────────────────────── UI 原语工具 ─────────────────────────

        internal static Font UiFont()
        {
            if (_cachedFont != null) return _cachedFont;
            try
            {
                _cachedFont = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Microsoft YaHei", "Malgun Gothic", "Yu Gothic UI", "Segoe UI", "微软雅黑", "SimHei", "Arial" }, 20);
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
