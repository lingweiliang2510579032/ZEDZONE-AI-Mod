using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace Dropoutline
{
    /// <summary>
    /// MOD2：AI 操控队友 NPC。
    ///
    /// 分工：
    ///   · 决策层 = AI（自主判断 + 听从玩家聊天里的意图，可以拒绝并说明理由）；
    ///   · 执行层 = 游戏原生 GOAP —— 只调用官方接口 GOAPAgent.ForceGoal / ClearForcedGoal；
    ///   · 表达层 = 游戏原生对话框 DialoguePanel.AddDialogueLog。
    ///
    /// 决策后端：
    ///   api        直接调 LLM（Chat Completions / Anthropic Messages / Responses / Ollama 四选一）
    ///   bridge     外部 agent（如 DSH）读写 JSON 文件当大脑，用于联调
    ///   heuristic  本地规则兜底，保证断网也有行为
    /// </summary>
    internal static class AiCompanion
    {
        // ── 配置 ──
        internal static ConfigEntry<bool> CfgAiEnabled;
        internal static ConfigEntry<string> CfgRunMode;          // api | dsh
        internal static ConfigEntry<string> CfgApiProtocol;      // chat | anthropic | responses | ollama
        internal static ConfigEntry<string> CfgMode;
        internal static ConfigEntry<string> CfgBridgeFile;
        internal static ConfigEntry<string> CfgBaseUrl;
        internal static ConfigEntry<string> CfgProtocol;
        internal static ConfigEntry<string> CfgModel;
        internal static ConfigEntry<string> CfgApiKeyEnc;
        internal static ConfigEntry<int> CfgIntervalSec;
        internal static ConfigEntry<bool> CfgSayToChat;
        internal static ConfigEntry<string> CfgNpcName;
        internal static ConfigEntry<string> CfgPlayerName;
        internal static ConfigEntry<bool> CfgAllowRefuse;
        internal static ConfigEntry<bool> CfgAutonomous;
        internal static ConfigEntry<bool> CfgTakeoverTest;
        internal static ConfigEntry<bool> CfgShowChatLog;
        internal static ConfigEntry<bool> CfgAllowDiscard;
        internal static ConfigEntry<bool> CfgAllowDirectCraft;
        internal static ConfigEntry<bool> CfgAllowContainerSort;
        internal static ConfigEntry<bool> CfgAllowContainerSortWrite;
        /// <summary>正式版官方聊天：她的台词进游戏自己的聊天框，并从那里接收指令。</summary>
        internal static ConfigEntry<bool> CfgChatViaOfficial;
        internal static ConfigEntry<bool> CfgCompanionMode;
        internal static ConfigEntry<int> CfgCompanionInterval;
        internal static ConfigEntry<string> CfgAiMode;   // companion | npc | player
        internal static ConfigEntry<string> CfgPersona;  // 人设：性格/说话风格
        internal static ConfigEntry<bool> CfgCoopAutoAvatar;
        internal static ConfigEntry<string> CfgCoopBehavior;
        internal static ConfigEntry<bool> CfgCoopDriveLocalWhenClient;
        internal static ConfigEntry<string> CfgCoopRoomCode;
        internal static ConfigEntry<bool> CfgPlayerAutoHost;

        // ── 运行时 ──
        private static float _nextTick;
        private static bool _discoveryDone;
        private static bool _allAgentsLogged;
        private static bool _inactiveAgentsLogged;
        private static float _lastNoTeammateLog = -999f;
        private static string _lastCommandId;
        private static float _lastDecisionTime;
        private static string _pendingPlayerChat;
        private static string _pendingChatId = "";
        private static string _lastAppliedCommandId = "";
        private static string _memoryScope = "";
        private static float _nextBridgeSnapshot;
        private static long _requestSerial;
        private static readonly List<string> Memory = new List<string>();
        private static readonly List<string> RecentAiLines = new List<string>();
        private static readonly System.Random SpeechRng = new System.Random();
        private static readonly AiSpeechQueue Speech = new AiSpeechQueue();
        private static string _speechRequestId = "";
        private static bool _llmBusy;
        private static LlmResult _llmResult;
        private static readonly object _llmLock = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static string _lastGoalApplied;
        private static bool _lastGoalOk;
        private static string _forcedGoalName;   // 需要持续保持的目标（官方 AI 可能中途改回自己的判断）
        private static float _lastReassert;
        private static float _stateWriteTime;
        private static float _lastCommandTime = -999f;
        private static float _nextMenuDshTick;

        /// <summary>接入模式：API 直连大模型 / DSH 本地大肥鱼 Agent。</summary>
        public static readonly string[] RunModeLabels =
        {
            "API 模式（模组直连大模型接口）",
            "DSH 模式（连接本地大肥鱼 Agent）",
        };
        public static readonly string[] RunModeValues = { "api", "dsh" };

        /// <summary>API 模式下的协议下拉（仅 API 模式可见）。</summary>
        public static readonly string[] ApiProtocolLabels =
        {
            "Chat Completions (/chat/completions)",
            "Anthropic Messages (/v1/messages)",
            "Responses (/responses)",
            "Ollama 本地 (/api/chat)",
        };
        public static readonly string[] ApiProtocolValues = { "chat", "anthropic", "responses", "ollama" };

        /// <summary>表现形式：聊天陪伴 / 接管雇佣 NPC / 玩家模式（作为正常联机玩家加入）。</summary>
        public static readonly string[] ModeLabels =
        {
            "聊天陪伴模式（官方聊天框对话 · 动态提醒 · 建议与辅助）",
            "接管雇佣NPC模式（按官方机制指挥游戏内已招募队友）",
            "玩家模式（创建独立AI角色 · 作为正常玩家加入联机）",
        };
        public static readonly string[] ModeValues = { "companion", "npc", "player" };

        public static readonly string[] ProtocolLabels =
        {
            "Chat Completions (/chat/completions)",
            "Anthropic Messages (/v1/messages)",
            "Responses (/responses)",
            "Ollama 本地 (/api/chat)",
            "本地 Agent · DSH（文件桥）",
        };

        public static readonly string[] ProtocolValues =
        {
            "chat", "anthropic", "responses", "ollama", "agent-dsh",
        };

        private static bool _syncingConfig;
        private static string Protocol => (CfgProtocol != null ? (CfgProtocol.Value ?? "chat") : "chat").Trim().ToLowerInvariant();
        public static string CurrentRunMode => IsDshMode ? "dsh" : "api";
        public static bool IsDshMode
        {
            get
            {
                var rm = (CfgRunMode?.Value ?? "").Trim().ToLowerInvariant();
                if (rm == "dsh") return true;
                if (rm == "api") return false;
                return IsAgentProtocol(Protocol);
            }
        }
        public static bool IsApiMode => !IsDshMode;
        public static string GetNpcName() => NpcName();
        public static bool CoopAutoAvatarEnabled => CfgCoopAutoAvatar == null || CfgCoopAutoAvatar.Value;
        public static bool CoopDriveLocalWhenClient => CfgCoopDriveLocalWhenClient != null && CfgCoopDriveLocalWhenClient.Value;
        public static bool PlayerAutoHostEnabled => CfgPlayerAutoHost == null || CfgPlayerAutoHost.Value;
        public static string CoopRoomCode => CfgCoopRoomCode != null ? (CfgCoopRoomCode.Value ?? "").Trim() : "";

        /// <summary>当前规范化的表现形式：companion | npc | player。</summary>
        public static string NormalizedAiMode()
        {
            var raw = (CfgAiMode != null ? (CfgAiMode.Value ?? "") : "companion").Trim().ToLowerInvariant();
            switch (raw)
            {
                case "npc":
                case "teammate":
                case "hired":
                    return "npc";
                case "player":
                case "coop":
                case "mp":
                    return "player";
                default:
                    return "companion";
            }
        }

        public static bool IsCompanionMode() => NormalizedAiMode() == "companion";
        public static bool IsNpcMode() => NormalizedAiMode() == "npc";
        public static bool IsPlayerMode() => NormalizedAiMode() == "player";

        public static string AiModeDisplayName(string mode = null)
        {
            switch ((mode ?? NormalizedAiMode()).Trim().ToLowerInvariant())
            {
                case "npc": return "接管雇佣NPC模式";
                case "player": return "玩家模式（联机AI玩家）";
                default: return "聊天陪伴模式";
            }
        }

        /// <summary>当前网络角色标签（区分单人 / 联机房主 / 联机客户端 / 大厅）。</summary>
        public static string GetNetworkRoleLabel()
        {
            try
            {
                var nm = ZedZone.Net.NetworkManager.instance;
                if (!AiSaveContext.Active)
                {
                    if (ZedZone.Net.NetLobby.inLobbyAsClient) return "联机大厅（客户端）";
                    if (ZedZone.Net.NetLobby.isOpenAsHost) return "联机大厅（房主）";
                    return "主菜单";
                }
                if (nm != null && ZedZone.Net.NetworkManager.isOnline)
                {
                    if (ZedZone.Net.NetworkManager.isHost) return "联机房主（Host）";
                    if (ZedZone.Net.NetworkManager.isClient) return "联机客户端（Client）";
                    return "联机模式";
                }
            }
            catch { }
            return "单人游戏（Offline）";
        }

        public static bool IsMultiplayerClientOnly()
        {
            try
            {
                return ZedZone.Net.NetworkManager.instance != null &&
                       ZedZone.Net.NetworkManager.isOnline &&
                       ZedZone.Net.NetworkManager.isClient &&
                       !ZedZone.Net.NetworkManager.isHost;
            }
            catch { return false; }
        }

        public static void SetRunMode(string mode)
        {
            if (_syncingConfig) return;
            _syncingConfig = true;
            try
            {
                bool dsh = string.Equals((mode ?? "").Trim(), "dsh", StringComparison.OrdinalIgnoreCase);
                if (CfgRunMode != null) CfgRunMode.Value = dsh ? "dsh" : "api";

                if (dsh)
                {
                    if (CfgProtocol != null) CfgProtocol.Value = "agent-dsh";
                    if (CfgMode != null) CfgMode.Value = "bridge";
                }
                else
                {
                    string apiProto = (CfgApiProtocol?.Value ?? "chat").Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(apiProto) || IsAgentProtocol(apiProto)) apiProto = "chat";
                    if (CfgApiProtocol != null) CfgApiProtocol.Value = apiProto;
                    if (CfgProtocol != null) CfgProtocol.Value = apiProto;
                    if (CfgMode != null) CfgMode.Value = "api";
                    OnProtocolChanged(apiProto);
                }

                DropOutlinePlugin.L?.LogInfo($"[AI队友] 运行模式切换 → {(dsh ? "DSH 联机陪玩" : "API 基础模式")}");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L?.LogWarning($"[AI队友] 运行模式切换失败: {e.Message}");
            }
            finally
            {
                _syncingConfig = false;
            }
        }

        public static void SetApiProtocol(string proto)
        {
            if (_syncingConfig) return;
            _syncingConfig = true;
            try
            {
                var p = (proto ?? "chat").Trim().ToLowerInvariant();
                if (IsAgentProtocol(p) || string.IsNullOrEmpty(p)) p = "chat";
                if (CfgApiProtocol != null) CfgApiProtocol.Value = p;
                if (IsApiMode)
                {
                    if (CfgProtocol != null) CfgProtocol.Value = p;
                    if (CfgMode != null) CfgMode.Value = "api";
                    OnProtocolChanged(p);
                }
            }
            finally
            {
                _syncingConfig = false;
            }
        }

        /// <summary>选择协议时自动带出该协议的默认 BaseUrl / 模型（仅在当前值还是默认值时替换）。</summary>
        public static void OnProtocolChanged(string value)
        {
            try
            {
                string baseUrl = null, model = null;
                switch (value)
                {
                    case "anthropic": baseUrl = "https://api.anthropic.com"; model = "claude-sonnet-4"; break;
                    case "responses": baseUrl = "https://api.openai.com/v1"; model = "gpt-4o-mini"; break;
                    case "ollama": baseUrl = "http://127.0.0.1:11434"; model = "qwen2.5:14b"; break;
                    case "agent-dsh": baseUrl = ""; model = ""; break;
                    default: baseUrl = "https://api.deepseek.com/v1"; model = "deepseek-chat"; break;
                }

                if (CfgBaseUrl != null && IsDefaultBaseUrl(CfgBaseUrl.Value)) CfgBaseUrl.Value = baseUrl;
                if (CfgModel != null && IsDefaultModel(CfgModel.Value)) CfgModel.Value = model;

                DropOutlinePlugin.L.LogInfo(
                    $"[AI队友] 协议切换 → {value}（BaseUrl={CfgBaseUrl?.Value} 模型={CfgModel?.Value}）");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[AI队友] 协议切换处理失败: {e.Message}");
            }
        }

        private static readonly string[] KnownBaseUrls =
        {
            "https://api.deepseek.com/v1", "https://api.anthropic.com",
            "https://api.openai.com/v1", "http://127.0.0.1:11434", "",
        };

        private static readonly string[] KnownModels =
        {
            "deepseek-chat", "claude-sonnet-4", "gpt-4o-mini", "qwen2.5:14b", "",
        };

        private static bool IsDefaultBaseUrl(string v)
        {
            v = (v ?? "").Trim();
            foreach (var k in KnownBaseUrls) if (k == v) return true;
            return v.Length == 0;
        }

        private static bool IsDefaultModel(string v)
        {
            v = (v ?? "").Trim();
            foreach (var k in KnownModels) if (k == v) return true;
            return v.Length == 0;
        }

        public static bool IsAgentMode() => IsAgentProtocol(Protocol);

        /// <summary>切换表现形式（聊天陪伴 / 接管雇佣NPC / 玩家模式）并同步内部状态。</summary>
        public static void OnModeChanged(string mode)
        {
            if (_syncingConfig) return;
            _syncingConfig = true;
            try
            {
                var raw = (mode ?? "companion").Trim().ToLowerInvariant();
                string norm = raw == "npc" || raw == "teammate" || raw == "hired"
                    ? "npc"
                    : (raw == "player" || raw == "coop" || raw == "mp" ? "player" : "companion");
                if (CfgAiMode != null && !string.Equals(CfgAiMode.Value, norm, StringComparison.OrdinalIgnoreCase))
                    CfgAiMode.Value = norm;
                if (CfgCompanionMode != null) CfgCompanionMode.Value = (norm == "companion");
                if (CfgAiEnabled != null && !CfgAiEnabled.Value) CfgAiEnabled.Value = true;
                DropOutlinePlugin.L?.LogInfo($"[AI伙伴] 表现形式切换 → {AiModeDisplayName(norm)}（当前网络：{GetNetworkRoleLabel()}）");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L?.LogWarning($"[AI伙伴] 表现形式切换失败: {e.Message}");
            }
            finally
            {
                _syncingConfig = false;
            }
        }

        private static bool IsAgentProtocol(string p) => !string.IsNullOrEmpty(p) && p.StartsWith("agent", StringComparison.OrdinalIgnoreCase);

        private static string AgentName(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            int i = p.IndexOf('-');
            return i >= 0 ? p.Substring(i + 1) : "agent";
        }

        /// <summary>
        /// 决策后端解析：
        ///   · 显式 heuristic → 本地规则；
        ///   · 协议选了「本地 Agent」 → bridge（外部 agent 当大脑）；
        ///   · 其余协议 → api（MOD 自己调 LLM）。
        /// </summary>
        private static string ResolveMode()
        {
            var mode = (CfgMode != null ? (CfgMode.Value ?? "") : "").Trim().ToLowerInvariant();
            if (mode == "heuristic") return "heuristic";
            return IsAgentProtocol(Protocol) ? "bridge" : "api";
        }

        public static void Init(ConfigFile cfg)
        {
            CfgAiEnabled = cfg.Bind("AI队友", "启用", false,
                "开启后由 AI 接管队友 NPC 的决策（底层行为仍由游戏 GOAP 执行）");
            CfgMode = cfg.Bind("AI队友", "决策后端", "bridge",
                "api=直接调 LLM / bridge=外部 agent 读写命令文件 / heuristic=本地规则");
            CfgProtocol = cfg.Bind("AI队友", "接口协议", "chat",
                "chat=Chat Completions(/chat/completions) / anthropic=Anthropic Messages(/v1/messages) / " +
                "responses=OpenAI Responses(/responses) / ollama=本地 Ollama(/api/chat)");
            CfgBaseUrl = cfg.Bind("AI队友", "API BaseUrl", "https://api.deepseek.com/v1",
                "接口根地址，例如 https://api.deepseek.com/v1");
            CfgModel = cfg.Bind("AI队友", "API 模型", "deepseek-chat", "模型名，例如 deepseek-chat / gpt-4o-mini / claude-sonnet-4");
            CfgApiKeyEnc = cfg.Bind("AI队友", "API Key(加密)", "",
                "DPAPI 加密后的 Base64（只对当前 Windows 用户可解），由 MOD 设置面板写入，不落明文");
            CfgIntervalSec = cfg.Bind("AI队友", "自主决策间隔(秒)", 8,
                "没有玩家消息时，AI 多久自己判断一次局面（同时限制请求频率）");
            CfgSayToChat = cfg.Bind("AI队友", "对话输出到对话框", true, "AI 的话是否显示在游戏对话界面");
            CfgNpcName = cfg.Bind("AI队友", "队友名字", "阿岳", "AI 队友/搭档的名字，写进提示词");
            CfgPersona = cfg.Bind("AI队友", "人设", "",
                "性格与说话风格，例如：话少、谨慎、爱吐槽；对玩家忠诚但会顶嘴。留空则用默认设定");
            CfgPlayerName = cfg.Bind("AI队友", "玩家称呼", "队长", "AI 怎么称呼玩家");
            CfgAllowRefuse = cfg.Bind("AI队友", "允许拒绝", true,
                "允许 AI 拒绝不合理要求，但必须在对话里说明理由");
            CfgAllowDiscard = cfg.Bind("AI辅助任务", "允许AI丢弃物品", false,
                "默认关闭，只给建议。开启后明确未保护单堆可复用官方库存移出、落地及失败放回逻辑，无需打开背包，不模拟鼠标、不直接删除或自行生成物品。保护装备快捷栏和任务物品，玩家操作优先。");
            CfgAllowDirectCraft = cfg.Bind("AI辅助任务", "允许AI直接合成", false,
                "默认关闭，只做制造规划。开启后 AI 会先核对配方与随身材料：材料不够就先说清缺什么；材料够则短暂打开官方合成面板、按官方流程加入制造队列再自动关闭（会闪现一次界面并短暂占用暂停），不模拟鼠标、不自行扣除材料或生成物品。未指定数量时只做一个。");
            CfgAllowContainerSort = cfg.Bind("AI辅助任务", "允许AI归并容器", false,
                "默认关闭，只给分区建议。开启后 AI 会按分区（电源/武器/医疗/食物/材料/工具/穿戴/容器/家具）规划视野内已检视容器的归并方案，并用空容器当缓冲区腾位置；装备槽和背包不动。");
            CfgAllowContainerSortWrite = cfg.Bind("AI辅助任务", "允许AI归并容器（写入）", false,
                "实验开关，默认关闭。开启后才会真的搬动物品：每件按数量守恒校验（该物品在源+目标的总量搬前搬后必须一致），一有差异立刻停手且不做任何补偿写入。建议先用 target=test 单件试搬验证。");
            CfgChatViaOfficial = cfg.Bind("AI队友", "使用游戏官方聊天", true,
                "正式版自带联机聊天。开启后她的台词会出现在游戏自己的聊天框里，玩家的发言也从那里接收指令；关闭则退回 MOD 自带的小输入框。");
            CfgAutonomous = cfg.Bind("AI队友", "自主行动", true,
                "玩家不指挥时，AI 也会自己判断该做什么");
            CfgAiMode = cfg.Bind("AI队友", "模式", "companion",
                new ConfigDescription(
                    "companion=聊天陪伴模式（官方聊天框对话、动态提醒与辅助）/ npc=接管雇佣NPC模式（按官方机制指挥已招募队友）/ player=玩家模式（创建独立AI角色作为正常联机玩家加入房间）",
                    new AcceptableValueList<string>("companion", "npc", "player")));

            CfgCloseDialoguePanel = cfg.Bind("AI队友", "自动关闭官方对话框", false,
                "官方全屏对话面板被打开时自动关掉（可能打断你和 NPC 的对话，不需要就关掉这个开关）");
            CfgCompanionMode = cfg.Bind("AI队友", "陪伴模式", false,
                "没有队友时也能用：AI 定期读取你的血量/饥渴/疲劳和周围威胁，用一两句话汇报并给建议（只走聊天框）");
            CfgCompanionInterval = cfg.Bind("AI队友", "陪伴汇报间隔(秒)", 25,
                "陪伴模式下多久主动汇报一次；局势有明显变化（掉血/出现威胁）会提前汇报");
            CfgShowChatLog = cfg.Bind("AI队友", "显示聊天记录", true,
                "在左下角短暂显示玩家与队友的对话（7 秒后自动收起）；关闭则完全不显示");
            CfgTakeoverTest = cfg.Bind("AI队友", "测试：接管最近友好NPC", false,
                "没有招募队友时，接管最近的非敌对人类 NPC 用于联调（正常游戏请关闭）");

            string defaultApiProto = IsAgentProtocol(Protocol) || string.IsNullOrEmpty(Protocol)
                ? "chat"
                : (Array.IndexOf(new[] { "chat", "anthropic", "responses", "ollama" }, Protocol) >= 0 ? Protocol : "chat");
            string defaultRunMode = IsAgentProtocol(Protocol) || string.Equals((CfgMode.Value ?? "").Trim(), "bridge", StringComparison.OrdinalIgnoreCase)
                ? "dsh"
                : "api";

            CfgRunMode = cfg.Bind("AI队友", "运行模式", defaultRunMode,
                new ConfigDescription(
                    "api=API 接入模式（直连大模型接口）/ dsh=DSH 接入模式（连接本地大肥鱼 Agent）。两种接入均可搭配陪伴/雇佣NPC/玩家三种表现形式。",
                    new AcceptableValueList<string>("api", "dsh")));
            CfgApiProtocol = cfg.Bind("AI队友", "API协议", defaultApiProto,
                new ConfigDescription(
                    "API 模式使用的接口协议：chat / anthropic / responses / ollama",
                    new AcceptableValueList<string>("chat", "anthropic", "responses", "ollama")));
            CfgCoopAutoAvatar = cfg.Bind("AI联机陪玩", "自动召唤化身", true,
                "处于「玩家模式」且进入世界时，自动以 AI 联机角色卡生成 AI 玩家并加入房间");
            CfgCoopBehavior = cfg.Bind("AI联机陪玩", "陪玩行为模式", "follow",
                new ConfigDescription(
                    "follow=紧跟玩家 / combat=积极迎战 / guard=原地驻守 / scavenge=协同拾荒",
                    new AcceptableValueList<string>("follow", "combat", "guard", "scavenge")));
            CfgCoopDriveLocalWhenClient = cfg.Bind("AI联机陪玩", "客户端自动跟房主", false,
                "当 AI 作为独立客户端连入他人房间时，无手动键鼠操作下自动协同房主行动与警戒");
            CfgCoopRoomCode = cfg.Bind("AI联机陪玩", "联机房间码", "",
                "最近使用或将要加入的多人联机房间码");
            CfgPlayerAutoHost = cfg.Bind("AI联机陪玩", "单机自动开启本地联机房", true,
                "处于「玩家模式」但当前处于单人游戏时，自动将当前存档开启为本地联机房主模式，以便 AI 作为正常玩家角色加入");

            // 兼容旧配置迁移：首次升级自动从 接口协议 / 决策后端 派生运行模式与 API 协议
            if (string.IsNullOrEmpty(CfgApiProtocol.Value))
                CfgApiProtocol.Value = defaultApiProto;
            if (string.IsNullOrEmpty(CfgRunMode.Value))
                CfgRunMode.Value = defaultRunMode;
            CfgAiMode.Value = NormalizedAiMode();
            SetRunMode(CfgRunMode.Value);
            AiCoopPlayer.SetBehaviorMode(CfgCoopBehavior.Value);

            CfgRunMode.SettingChanged += (_, __) => { if (!_syncingConfig) SetRunMode(CfgRunMode.Value); };
            CfgApiProtocol.SettingChanged += (_, __) => { if (!_syncingConfig) SetApiProtocol(CfgApiProtocol.Value); };
            CfgAiMode.SettingChanged += (_, __) => { if (!_syncingConfig) OnModeChanged(CfgAiMode.Value); };
            CfgCoopBehavior.SettingChanged += (_, __) => { if (!_syncingConfig) AiCoopPlayer.SetBehaviorMode(CfgCoopBehavior.Value); };

            if (IsCompanionMode() && CfgCompanionMode != null) CfgCompanionMode.Value = true;
            EnsureDataDir();
            EnsureReadme();
            WriteInactiveState();

            DropOutlinePlugin.L.LogInfo(
                $"[AI伙伴] 初始化 接入={CurrentRunMode} 表现={NormalizedAiMode()} 启用={CfgAiEnabled.Value} 协议={CfgProtocol.Value} " +
                $"自主={CfgAutonomous.Value} 允许拒绝={CfgAllowRefuse.Value}");
        }

        /// <summary>AI 搭档的全部资料目录：游戏根目录下的 ZED AI。</summary>
        public static string DataDir
        {
            get
            {
                try { return Path.Combine(Paths.GameRootPath, "ZED AI"); }
                catch { return Path.Combine(Paths.ConfigPath, "DropOutline"); }
            }
        }

        /// <summary>建 ZED AI 目录；首次升级时把旧的 BepInEx/config/DropOutline 资料复制过来，不覆盖已有文件。</summary>
        private static void EnsureDataDir()
        {
            try
            {
                var dir = DataDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var legacy = Path.Combine(Paths.ConfigPath, "DropOutline");
                if (Directory.Exists(legacy) && !string.Equals(legacy, dir, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var f in Directory.GetFiles(legacy))
                    {
                        var dest = Path.Combine(dir, Path.GetFileName(f));
                        if (!File.Exists(dest))
                        {
                            try { File.Copy(f, dest); } catch { }
                        }
                    }
                }
            }
            catch (Exception e) { DropOutlinePlugin.L.LogWarning($"[AI队友] 初始化数据目录失败: {e.Message}"); }
        }

        public static string CommandFilePath
        {
            get
            {
                var p = CfgBridgeFile != null ? CfgBridgeFile.Value : null;
                if (!string.IsNullOrEmpty(p)) return p;
                return Path.Combine(DataDir, "ai-command.json");
            }
        }

        /// <summary>给卸载脚本用：BepInEx 配置文件路径。</summary>
        internal static string ConfigFilePathForUninstall()
        {
            try { return Path.Combine(Paths.ConfigPath, "yuye.dropoutline.cfg"); }
            catch { return ""; }
        }

        public static string StateFilePath
        {
            get
            {
                var dir = Path.GetDirectoryName(CommandFilePath);
                return Path.Combine(dir ?? Paths.ConfigPath, "ai-state.json");
            }
        }
        /// <summary>首次运行写一份接入说明（带本机绝对路径），方便别人对接自己的 DSH / agent。</summary>
        private static void EnsureReadme()
        {
            try
            {
                var dir = Path.GetDirectoryName(StateFilePath) ?? Paths.ConfigPath;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "接入说明.md");
                if (File.Exists(path)) return;

                var mcp = Path.Combine(dir, "zed_mcp_server.py");
                var text =
                    "# ZED ZONE AI 搭档 · 接入说明（本机自动生成）\n\n" +
                    "本目录：" + dir + "\n\n" +
                    "## 方式一：API Key 直连\n" +
                    "游戏内 ESC → 游戏选项 → MOD 设置 → MOD2 · AI 搭档 → 接口协议，选 Chat Completions / Anthropic Messages / " +
                    "Responses / Ollama 之一，再填 API 地址、模型名、API Key（DPAPI 加密保存，配置里只有密文）。\n\n" +
                    "## 方式二：本地 Agent（DSH 等，文件桥）\n" +
                    "接口协议选「本地 Agent · DSH（文件桥）」。MOD 会持续写：\n" +
                    "- ai-state.json：当前局面（血量/饥渴/疲劳、威胁数、可用目标 goals、待回应的 pendingChat、最近对话）\n" +
                    "- ai-prompt.md：拼好的完整提示词（系统提示 + 本次输入 + 输出格式）\n\n" +
                    "agent 把决策写回：\n" +
                    "- ai-command.json：{\"id\":\"任意\",\"goal\":\"目标名或clear\",\"say\":\"对玩家说的话\",\"reason\":\"理由\"}\n\n" +
                    "### DSH 自动化任务（推荐，不需要 API Key）\n" +
                    "新建「自动化任务」，间隔 8 秒左右，提示词：\n\n" +
                    "```\n" +
                    "读取 " + Path.Combine(dir, "ai-prompt.md") + " ，按里面的【系统提示】和【本次输入】给出决策，\n" +
                    "只输出一个 JSON 对象，写入 " + CommandFilePath + " （覆盖写）。\n" +
                    "如果 pendingChat 为空且局面没有变化，就写 {\"goal\":\"clear\",\"say\":\"\"}。\n" +
                    "```\n\n" +
                    "### MCP 方式（DSH 或任意 MCP 客户端）\n" +
                    "把 zed_mcp_server.py 注册成 MCP 服务器：\n\n" +
                    "```\n" +
                    "command: python\n" +
                    "args: [\"" + mcp + "\", \"--dir\", \"" + dir + "\"]\n" +
                    "```\n\n" +
                    "工具：zed_state（读局面）、zed_order（下指令）、zed_chat（当玩家说话）。\n\n" +
                    "### ZED 会话开场词（在这个对话里粘一次，之后它就知道该干什么）\n" +
                    "```\n" +
                    "你是《隔离区》里的 AI 搭档「" + NpcName() + "」，称呼玩家为「" + PlayerName() + "」。\n" +
                    "每当我让你看局面，就用 zed_state 读一次；需要让它说话或行动，就用 zed_order 下一条决策。\n" +
                    "说话风格按人设来，别报流水账，也别解释你在调用工具。\n" +
                    "```\n\n" +
                    "> DSH 桌面端的本地 HTTP 接口用一次性随机 token 鉴权（dsh-desktop-* 头），外部进程直接请求会被 403；\n" +
                    "> 所以 MOD 走文件桥 / MCP，而不是直接调 DSH 的 HTTP。\n";

                File.WriteAllText(path, text, Utf8NoBom);
                DropOutlinePlugin.L.LogInfo($"[AI队友] 已生成接入说明：{path}");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[AI队友] 生成接入说明失败: {e.Message}");
            }
        }

        // ───────────────────────── 主循环 ─────────────────────────

        public static bool RefreshWorld(bool worldPresent)
        {
            string gate = Protocol + "|" + ResolveMode() + "|" + (CfgAiEnabled?.Value == true) + "|" + (CfgAiMode?.Value ?? "");
            bool changed = AiSaveContext.Refresh(worldPresent, gate);
            if (changed)
            {
                // Fence every API worker and cancel task-owned actions before world reset.
                Speech.Clear(); _speechRequestId = "";
                AiTaskRuntime.WorldChanged();
                AiCoopPlayer.ResetOnWorldChange();
                AiActions.ClearHighlight();
                System.Threading.Interlocked.Increment(ref _requestSerial);
                bool newSave = _memoryScope != AiSaveContext.SaveId;
                AiMemory.Save(true);
                AiMemory.SelectSave(AiSaveContext.SaveId);
                AiMemory.AdoptLegacyScope(AiSaveContext.LegacyId);
                _memoryScope = AiSaveContext.SaveId;
                if (newSave || !AiSaveContext.Active)
                {
                    Memory.Clear(); RecentAiLines.Clear(); _pendingPlayerChat = null; _pendingChatId = "";
                    LootWarden.ResetWorld(); ChatInput.ResetWorld(); AiNetChat.ResetWorld();
                }
                _lastCommandId = null; _lastAppliedCommandId = "";
                _forcedGoalName = null; _lastGoalApplied = ""; _lastGoalOk = false;
                _lastScanTime = -999f; _cachedTeammates.Clear();
                _allAgentsLogged = false; _discoveryDone = false; _inactiveAgentsLogged = false;
                _lastPlayerHp = -1f; _lastHunger = -1f; _lastThirst = -1f; _lastFatigue = -1f;
                _lastThreatCount = 0; _lastPos = UnityEngine.Vector2.zero;
                _lastSignature = ""; _nextCompanionReport = 0f; _stateWriteTime = -999f;
                _companionChatAt = 0f; _lastCompressAt = 0f; _knowledgeAt = 0f;
                lock (_llmLock) { _llmResult = null; _companionResult = null; _llmBusy = false; _companionLlmPending = false; }
                DropOutlinePlugin.WorldSince = AiSaveContext.Active ? Time.unscaledTime : 0f;
                _nextBridgeSnapshot = 0f;
                DropOutlinePlugin.L.LogInfo($"[AI存档] active={AiSaveContext.Active} save={AiSaveContext.SaveId} epoch={AiSaveContext.Epoch}");
            }
            if (Time.unscaledTime >= _nextBridgeSnapshot || changed)
            {
                _nextBridgeSnapshot = Time.unscaledTime + 2f;
                if (AiSaveContext.Active)
                {
                    GOAPAgent snapshotAgent = null;
                    if (IsPlayerMode())
                    {
                        snapshotAgent = AiCoopPlayer.GetAvatarAgent();
                    }
                    else if (IsNpcMode())
                    {
                        var teammates = FindTeammates();
                        if (teammates.Count > 0) snapshotAgent = teammates[0];
                    }
                    WriteStateFile(snapshotAgent, _pendingPlayerChat);
                }
                else WriteInactiveState();
            }
            return AiSaveContext.Active;
        }

        private static string CurrentSceneTag()
        {
            if (AiSaveContext.Active) return "world";
            try
            {
                if (ZedZone.Net.NetLobby.inLobbyAsClient || ZedZone.Net.NetLobby.isOpenAsHost)
                    return "lobby";
            }
            catch { }
            return "menu";
        }

        private static string ScopeJson()
        {
            return "\"schemaVersion\":2,\"worldActive\":" + (AiSaveContext.Active ? "true" : "false") +
                ",\"runMode\":\"" + CurrentRunMode + "\",\"scene\":\"" + CurrentSceneTag() + "\"" +
                ",\"saveId\":\"" + MiniJson.Escape(AiSaveContext.SaveId) + "\",\"saveName\":\"" + MiniJson.Escape(AiSaveContext.SaveName) +
                "\",\"worldEpoch\":\"" + AiSaveContext.Epoch + "\",\"worldIsNew\":" + (AiSaveContext.IsNew ? "true" : "false") +
                // The conversation follows the character card. Progress and map are
                // disposable: both are published only as context, plus the older
                // progress identity so an existing conversation can be adopted once.
                ",\"characterId\":\"" + MiniJson.Escape(AiSaveContext.CharacterId) +
                "\",\"mapId\":\"" + MiniJson.Escape(AiSaveContext.MapId) +
                "\",\"mapName\":\"" + MiniJson.Escape(AiSaveContext.MapName) +
                "\",\"legacySaveId\":\"" + MiniJson.Escape(AiSaveContext.LegacyId) +
                // Exactly one card means every retired progress conversation is
                // this character's, so the bridge may adopt the newest of them.
                "\",\"singleCharacter\":" + (AiSaveLifecycle.SingleCharacterCard ? "true" : "false") +
                // 面板顶上那三个可分配点数：有点数时 AI 要主动给加点建议。
                ",\"progress\":\"" + MiniJson.Escape(AiSaveContext.Active ? GameFacts.ProgressSummary() : "") + "\"" +
                // 手电等手持设备的电量：低于两成时 AI 要提醒（手电变暗就是没电）。
                ",\"battery\":\"" + MiniJson.Escape(AiSaveContext.Active ? GameFacts.BatterySummary() : "") + "\"" +
                // 附近「已检视」容器里装了什么（未检视一律不读，玩家自己定的边界）。
                ",\"containers\":\"" + MiniJson.Escape(AiSaveContext.Active ? GameFacts.ContainerSummary() : "") + "\"" +
                ",\"network\":" + AiCoopPlayer.GetNetworkJson() +
                ",\"avatar\":" + AiCoopPlayer.GetAvatarJson() +
                ",\"writtenAtUtc\":\"" + DateTime.UtcNow.ToString("O") + "\",\"aiEnabled\":" + (CfgAiEnabled?.Value == true ? "true" : "false") +
                ",\"backend\":\"" + ResolveMode() + "\",\"pendingChatId\":\"" + MiniJson.Escape(_pendingChatId) +
                "\",\"lastAppliedCommandId\":\"" + MiniJson.Escape(_lastAppliedCommandId) + "\",\"context\":{" +
                "\"npc\":\"" + MiniJson.Escape(NpcName()) + "\",\"player\":\"" + MiniJson.Escape(PlayerName()) +
                "\",\"persona\":\"" + MiniJson.Escape(CfgPersona?.Value ?? "") + "\",\"mode\":\"" + MiniJson.Escape(NormalizedAiMode()) +
                "\",\"modeLabel\":\"" + MiniJson.Escape(AiModeDisplayName()) + "\",\"networkRole\":\"" + MiniJson.Escape(GetNetworkRoleLabel()) +
                "\",\"hasHiredTeammate\":" + (FindTeammates(true).Count > 0 ? "true" : "false") +
                ",\"memoryContext\":\"" + MiniJson.Escape(AiSaveContext.Active ? AiMemory.BuildContext(_pendingPlayerChat ?? "", 12) : "") +
                "\",\"moodHint\":\"" + MiniJson.Escape(AiSaveContext.Active ? AiMemory.MoodHint() : "") + "\"}";
        }

        private static void AtomicText(string path, string text)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Utf8NoBom);
            File.Move(tmp, path, true);
        }

        private static void WriteInactiveState()
        {
            try { AtomicText(StateFilePath, "{" + ScopeJson() + ",\"protocol\":\"" + MiniJson.Escape(Protocol) + "\",\"pendingChat\":\"\",\"memory\":[],\"goals\":[]}"); }
            catch (Exception e) { DropOutlinePlugin.L?.LogWarning("[AI状态] " + e.Message); }
        }

        /// <summary>在主菜单 / 联机大厅等待进房时，处理玩家模式自动准备与 DSH 房间指令。</summary>
        public static void TickMenuDsh()
        {
            if (DropOutlinePlugin.WorldTearingDown || AiSaveContext.Active) return;
            if (Time.unscaledTime - _stateWriteTime >= 3f)
            {
                _stateWriteTime = Time.unscaledTime;
                WriteInactiveState();
            }
            if ((!IsDshMode && !IsPlayerMode()) || CfgAiEnabled == null || !CfgAiEnabled.Value) return;
            if (Time.unscaledTime < _nextMenuDshTick) return;
            _nextMenuDshTick = Time.unscaledTime + 0.35f;

            try
            {
                AiCoopPlayer.TickLobbyAutoReady();
                if (IsDshMode) HandleCommandFile(null);
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L?.LogWarning("[AI菜单联机] " + e.Message);
            }
        }

        public static void Tick()
        {
            if (!AiSaveContext.Active || CfgAiEnabled == null || !CfgAiEnabled.Value)
            { Speech.Clear(); return; }
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + 0.5f;

            try
            {
                if (!HasActiveMapController()) { Speech.Clear(); return; }   // 主菜单/加载中不跑 AI 逻辑

                if (CfgChatViaOfficial == null || CfgChatViaOfficial.Value)
                    AiNetChat.EnsureOfficialChatReady();

                if (IsPlayerMode())
                    AiCoopPlayer.Tick();

                // 陪伴模式（及队友 NPC / 玩家模式分支）：读玩家状态、定期/异常时汇报
                CloseOfficialDialogue();
                DrainCompanionResult();
                bool companionMode = IsCompanionMode();
                if (companionMode)
                {
                    CompanionTick();

                    // 诊断（只读，15 秒采样一次，仅 DSH 模式写入重型容器调试）
                    if (IsDshMode && Time.unscaledTime - _deviceDumpAt >= 15f)
                    {
                        _deviceDumpAt = Time.unscaledTime;
                        try
                        {
                            var device = GameFacts.EquippedDeviceDump();
                            if (device != _lastDeviceDump) { _lastDeviceDump = device; DropOutlinePlugin.L.LogInfo("[诊断·手持设备] " + device); }
                        }
                        catch { }
                        try
                        {
                            var boxes = GameFacts.ContainerDebugDump();
                            if (boxes != _lastContainerDump) { _lastContainerDump = boxes; DropOutlinePlugin.L.LogInfo("[诊断·容器] " + boxes); }
                        }
                        catch { }
                    }

                    // 陪伴模式下持续向 ai-state.json 同步最新只读状态（DSH 用于决策，API 模式用于 DSH 面板只读展示）
                    if (Time.unscaledTime - _stateWriteTime >= Mathf.Max(2f, CfgIntervalSec.Value))
                    {
                        _stateWriteTime = Time.unscaledTime;
                        WriteStateFile(null, _pendingPlayerChat);
                    }

                    HandleCommandFile(null);   // 外部 agent 仍可让“搭档”说话或下辅助任务
                    DrainSpeech(); // Process a new bridge chat before emitting any old continuation.

            // 每 60 秒刷新一次知识文件（仅 DSH 模式全量导出，API 模式保持轻量）
            if (IsDshMode && Time.unscaledTime - _knowledgeAt > 60f)
            {
                _knowledgeAt = Time.unscaledTime;
                try { GameFacts.WriteKnowledge(System.IO.Path.GetDirectoryName(StateFilePath)); }
                catch (Exception e) { DropOutlinePlugin.L.LogWarning($"[知识] 刷新失败: {e.Message}"); }
                // 全物品分类表：遇到新物品先查表，不靠运行期猜
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(StateFilePath);
                    var summary = GameFacts.WriteItemCategories(dir);
                    DropOutlinePlugin.L.LogInfo("[物品分类] " + summary);
                    var catalog = GameFacts.WriteContainerCatalog(dir);
                    DropOutlinePlugin.L.LogInfo("[容器清单] " + catalog);
                }
                catch (Exception e) { DropOutlinePlugin.L.LogWarning($"[物品分类] 导出失败: {e.Message}"); }
            }
                    return;
                }

                if (!_allAgentsLogged && IsNpcMode())
                {
                    _allAgentsLogged = true;
                    DumpAllAgents();
                }

                var agents = IsNpcMode() ? FindTeammates() : new List<GOAPAgent>();
                GOAPAgent agent = IsPlayerMode()
                    ? AiCoopPlayer.GetAvatarAgent()
                    : (agents.Count > 0 && IsAlive(agents[0]) ? agents[0] : null);
                if (IsNpcMode() && agents.Count > 0 && agent == null) _lastScanTime = -999f;

                if (agent != null && !_discoveryDone && IsNpcMode())
                {
                    _discoveryDone = true;
                    DumpDiscovery(agents);
                }

                // 1) 外部 agent 的命令文件（也是玩家聊天的入口）
                HandleCommandFile(agent);
                DrainSpeech(); // New player input/commands may have cleared old queued lines.

                var mode = ResolveMode();

                // 2) LLM 结果回收（主线程应用）
                var done = TakeLlmResult();
                if (done != null)
                {
                    if (!done.Ok)
                    {
                        DropOutlinePlugin.L.LogWarning($"[AI伙伴] LLM 失败({done.LatencyMs}ms): {done.Error}");
                    }
                    else
                    {
                        DropOutlinePlugin.L.LogInfo(
                            $"[AI伙伴] LLM[{done.LatencyMs}ms] mode={NormalizedAiMode()} goal={done.Goal} npcCmd={done.NpcCmd} coop={done.CoopOp} room={done.RoomOp} say={done.Say}");
                        if (done.ReplyTo != _pendingChatId) return;
                        string opNote = "";
                        if (!string.IsNullOrEmpty(done.RoomOp))
                        {
                            AiCoopPlayer.ExecuteRoomOp(done.RoomOp, done.RoomArg, out string roomNote);
                            if (!string.IsNullOrEmpty(roomNote)) opNote = roomNote;
                        }
                        if (IsPlayerMode() && !string.IsNullOrEmpty(done.CoopOp))
                        {
                            AiCoopPlayer.ExecuteCoopOp(done.CoopOp, done.CoopArg, out string coopNote);
                            if (!string.IsNullOrEmpty(coopNote))
                                opNote = string.IsNullOrEmpty(opNote) ? coopNote : opNote + "；" + coopNote;
                        }
                        if (IsNpcMode() && !string.IsNullOrEmpty(done.NpcCmd))
                        {
                            ExecuteHiredNpcCommand(agent, done.NpcCmd, out string npcNote);
                            if (!string.IsNullOrEmpty(npcNote))
                                opNote = string.IsNullOrEmpty(opNote) ? npcNote : opNote + "；" + npcNote;
                        }
                        if (done.Task != null)
                        {
                            try { AiTaskRuntime.Accept(done.Task, Guid.NewGuid().ToString("N"), done.ReplyTo); }
                            catch (Exception error) { done.Say = "任务没有执行：" + error.Message; }
                        }
                        string hlNote = ApplyHighlightGoal(done.Goal, false);
                        if (!string.IsNullOrEmpty(hlNote))
                        {
                            if (string.IsNullOrEmpty(done.Say)) done.Say = hlNote;
                            done.Goal = "clear";
                        }
                        string finalSay = !string.IsNullOrEmpty(done.Say) ? done.Say : opNote;
                        ApplyDecision(agent, done.Goal, finalSay, done.Reason, done.ReplyTo);
                        _pendingPlayerChat = null; _pendingChatId = "";
                    }
                }

                // 3) 目标保持：仅在雇佣 NPC 模式下把被官方 AI 改掉的强制目标压回
                if (IsNpcMode() && !IsMultiplayerClientOnly() && agent != null && !string.IsNullOrEmpty(_forcedGoalName) &&
                    Time.unscaledTime - _lastReassert > 2f)
                {
                    _lastReassert = Time.unscaledTime;
                    var cur = CurrentGoalName(agent);
                    if (!string.Equals(cur, _forcedGoalName, StringComparison.OrdinalIgnoreCase))
                    {
                        DropOutlinePlugin.L.LogInfo($"[AI队友] 目标被改回「{cur}」，重新压回「{_forcedGoalName}」");
                        ApplyGoal(agent, _forcedGoalName);
                    }
                }

                // 4) 触发决策
                if (mode == "api")
                {
                    if (Time.unscaledTime - _stateWriteTime >= Mathf.Max(2f, CfgIntervalSec.Value))
                    {
                        _stateWriteTime = Time.unscaledTime;
                        WriteStateFile(agent, _pendingPlayerChat);
                    }
                    bool needByChat = !string.IsNullOrEmpty(_pendingPlayerChat);
                    bool needByTimer = CfgAutonomous.Value &&
                                       Time.unscaledTime - _lastDecisionTime >= Mathf.Max(3f, CfgIntervalSec.Value);
                    if (!_llmBusy && (needByChat || needByTimer))
                    {
                        var chat = _pendingPlayerChat;
                        _lastDecisionTime = Time.unscaledTime;
                        var st = BuildSettings();
                        if (!st.IsConfigured)
                        {
                            DropOutlinePlugin.L.LogWarning("[AI队友] LLM 未配置（BaseUrl/模型/Key），跳过本次决策");
                        }
                        else
                        {
                            RequestLlmAsync(agent, st, chat);
                        }
                    }
                }
                else if (mode == "heuristic")
                {
                    bool needByChat = !string.IsNullOrEmpty(_pendingPlayerChat);
                    bool needByTimer = CfgAutonomous.Value &&
                                       Time.unscaledTime - _lastDecisionTime >= Mathf.Max(3f, CfgIntervalSec.Value);
                    if (needByChat || needByTimer)
                    {
                        _lastDecisionTime = Time.unscaledTime;
                        if (needByChat)
                        {
                            Say(agent, "我这边信号断了，先按老规矩来。");
                            _pendingPlayerChat = null; _pendingChatId = "";
                        }
                        ApplyHeuristic(agent);
                    }
                }
                else // bridge：把局面写成文件，等外部 agent 回命令
                {
                    if (Time.unscaledTime - _stateWriteTime >= Mathf.Max(2f, CfgIntervalSec.Value))
                    {
                        _stateWriteTime = Time.unscaledTime;
                        if (_lastCommandTime < 0f) _lastCommandTime = Time.unscaledTime;
                        WriteStateFile(agent, _pendingPlayerChat);
                    }

                    // agent 长时间没回话（DSH 没开/卡住）时，别让队友呆站着：超过 3 个间隔就用本地规则兜底
                    if (agent != null && CfgAutonomous.Value &&
                        Time.unscaledTime - _lastCommandTime > Mathf.Max(20f, CfgIntervalSec.Value * 3f))
                    {
                        DropOutlinePlugin.L.LogInfo("[AI队友] 外部 agent 超时未响应，临时切本地规则");
                        ApplyHeuristic(agent);
                        _lastCommandTime = Time.unscaledTime;   // 避免每帧都刷
                    }
                }
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[AI队友] Tick 异常: {e.Message}");
            }
        }

        /// <summary>给设置面板显示的实时状态。</summary>
        public static string StatusText()
        {
            try
            {
                if (CfgAiEnabled == null || !CfgAiEnabled.Value) return "已关闭";
                string run = IsDshMode ? "DSH本地Agent" : $"API({Protocol})";
                string aiMode = NormalizedAiMode();
                string netRole = GetNetworkRoleLabel();
                if (aiMode == "companion")
                    return $"{run} · 聊天陪伴模式 · {netRole}";
                if (aiMode == "player")
                    return $"{run} · 玩家模式 · {AiCoopPlayer.GetAvatarSummary()} · {AiCoopPlayer.GetRoomSummary()}";

                var agents = FindTeammates(true);
                if (agents.Count == 0) return $"{run} · 雇佣NPC模式 · 暂无已招募队友 · {netRole}";
                var a = agents[0];
                string npcName = a.npcData?.characterData?.characterName;
                if (string.IsNullOrEmpty(npcName))
                    npcName = a.npcData != null && !string.IsNullOrEmpty(a.npcData.npcGUID)
                        ? a.npcData.npcGUID.Substring(0, Math.Min(6, a.npcData.npcGUID.Length)) : "雇佣队友";
                return $"{run} · 雇佣NPC({agents.Count}名) · {npcName}:{CurrentGoalName(a)} · {netRole}";
            }
            catch { return "状态未知"; }
        }

        // ───────────────────────── 连接自检 ─────────────────────────

        /// <summary>设置面板「测试连接」用：最近一次自检的结果文字。</summary>
        public static string TestStatus = "";
        private static bool _testBusy;

        /// <summary>用当前配置发一次最小请求，结果写进 TestStatus（面板会显示）。</summary>
        public static void TestApiConnection()
        {
            if (_testBusy) return;
            _testBusy = true;
            TestStatus = "测试中…";

            LlmSettings st = BuildSettings();
            if (!st.IsConfigured)
            {
                TestStatus = "未配置完整：需要 BaseUrl / 模型 / Key";
                _testBusy = false;
                return;
            }

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    string proto = LlmClient.ProtocolName(st.Protocol);
                    var r = LlmClient.Ask(st, "你是连通性测试助手。", "只回复两个字：正常");
                    if (r.Ok) TestStatus = $"成功（{proto}，{r.LatencyMs}ms）：{Trim(r.Say ?? r.Text, 40)}";
                    else TestStatus = $"失败（{proto}）：{Trim(r.Error, 120)}";
                }
                catch (Exception e)
                {
                    TestStatus = "异常：" + e.Message;
                }
                finally
                {
                    _testBusy = false;
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ").Replace("\r", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
        // ───────────────────────── 官方对话面板 ─────────────────────────

        internal static ConfigEntry<bool> CfgCloseDialoguePanel;
        private static float _lastDialogClose;

        /// <summary>
        /// 官方 DialoguePanel 一旦被打开就是全屏大面板，而且没有对外关闭接口，
        /// 只能走 DialogueController.EndDialogue()。这里做兜底自动关闭（可关）。
        /// </summary>
        private static void CloseOfficialDialogue()
        {
            if (CfgCloseDialoguePanel == null || !CfgCloseDialoguePanel.Value) return;
            if (Time.unscaledTime - _lastDialogClose < 3f) return;
            try
            {
                var panel = DialoguePanel.instance;
                if (panel == null || panel.gameObject == null || !panel.gameObject.activeInHierarchy) return;
                _lastDialogClose = Time.unscaledTime;
                var ctrl = DialogueController.instance;
                if (ctrl != null) ctrl.EndDialogue();
                else panel.gameObject.SetActive(false);
                DropOutlinePlugin.L.LogInfo("[对话面板] 已自动关闭官方对话框");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[对话面板] 关闭失败: {e.Message}");
            }
        }
        // ───────────────────────── 陪伴模式 ─────────────────────────

        private static float _nextCompanionReport;
        private static float _lastPlayerHp = -1f;
        private static float _lastHunger = -1f, _lastThirst = -1f, _lastFatigue = -1f;
        private static int _lastThreatCount;
        private static float _companionChatAt;
        private static bool _companionLlmPending;
        private static string _companionRequestChatId = "";
        private static LlmResult _companionResult;
        private static UnityEngine.Vector2 _lastPos;
        private static float _lastMoveAt;
        private static float _lastMinorAt;
        private static float _lastCompressAt;
        private static string _lastSignature = "";
        private static float _knowledgeAt;
        /// <summary>最近一次"环境事件"本地开口。bridge 模式也只靠它避免哑巴。</summary>
        private static float _lastLocalEventAt;
        /// <summary>手持设备电量诊断节流。</summary>
        private static float _deviceDumpAt = -999f;
        private static string _lastDeviceDump = "", _lastContainerDump = "";

        /// <summary>
        /// 陪伴模式：事件驱动 + 定期播报。
        /// 省 token 的三条规则：
        ///   1) 没玩家消息、没事件、没到播报间隔 → 完全不请求；
        ///   2) 到点了但局面和上次一样 → 不说话；
        ///   3) 无聊小事（发呆/乱走）只走本地模板，不花 token。
        /// </summary>
        private static void CompanionTick()
        {
            float hp, hunger, thirst, fatigue;
            int threats;
            UnityEngine.Vector2 pos;
            var snap = BuildPlayerSnapshot(out hp, out hunger, out thirst, out fatigue, out threats, out pos);
            if (string.IsNullOrEmpty(snap)) return;

            if (_lastPos == UnityEngine.Vector2.zero) _lastPos = pos;
            if (UnityEngine.Vector2.Distance(pos, _lastPos) > 1.5f)
            {
                _lastPos = pos;
                _lastMoveAt = Time.unscaledTime;
            }

            bool hasChat = !string.IsNullOrEmpty(_pendingPlayerChat);
            if (!hasChat && (CfgCompanionMode == null || !CfgCompanionMode.Value)) return;
            if (hasChat && _companionChatAt <= 0f) _companionChatAt = Time.unscaledTime;

            string evt = DetectEvent(hp, hunger, thirst, fatigue, threats);

            // 掉落物提醒：只有本地大脑时才自己开口；bridge 模式把线索写进状态，交给外部大脑决定
            if (!hasChat && ResolveMode() != "bridge")
            {
                var lootLine = LootWarden.Scan(threats);
                if (!string.IsNullOrEmpty(lootLine))
                {
                    SayReport(lootLine);
                    _nextCompanionReport = Time.unscaledTime + 20f;
                }
            }

            float interval = Mathf.Max(20f, CfgCompanionInterval != null ? CfgCompanionInterval.Value : 60);
            bool due = Time.unscaledTime >= _nextCompanionReport;

            if (!hasChat && evt == null && !due) return;

            string signature = $"{hp:F2}|{hunger:F2}|{thirst:F2}|{threats}|{pos.x:F0},{pos.y:F0}";
            if (!hasChat && evt == null)
            {
                _nextCompanionReport = Time.unscaledTime + interval;
                if (signature == _lastSignature) return;   // 局面没变，省一次请求
            }
            _lastSignature = signature;

            // 记忆攒够了就批量抽取（低频，一次调用同时抽事实/打重要级别/调心情）
            if (ResolveMode() == "api" && AiMemory.ShouldExtract() && Time.unscaledTime - _lastCompressAt > 30f)
            {
                _lastCompressAt = Time.unscaledTime;
                try { AiMemory.Extract(BuildSettings(), NpcName(), PlayerName()); }
                catch (Exception e) { DropOutlinePlugin.L.LogWarning($"[记忆] 抽取异常: {e.Message}"); }
            }

            var mode = ResolveMode();
            if (mode == "api" && !_companionLlmPending && !_llmBusy)
            {
                var st = BuildSettings();
                if (st.IsConfigured)
                {
                    RequestCompanionLlmAsync(st, snap, _pendingPlayerChat, evt);
                    return;
                }
            }

            // 文件桥 = 大脑在外部（DSH / Agent）。玩家聊天由 DSH 回复，这里不抢话；
            // 但环境事件（遇敌、受伤、饿渴、脱险）必须当场有反应，否则玩家会觉得
            // 她哑了。这类反应是本地短句，不产生 DSH 回合、不写进对话记录。
            if (mode == "bridge")
            {
                _companionChatAt = 0f;
                if (!hasChat && evt != null && evt != "idle" && Time.unscaledTime - _lastLocalEventAt > 12f)
                {
                    _lastLocalEventAt = Time.unscaledTime;
                    SayReport(LocalEventLine(evt));
                }
                return;
            }
            if (mode == "api")
            {
                _companionChatAt = 0f;
                return;
            }

            SayReport(evt != null ? LocalEventLine(evt) : BuildLocalCompanionLine(hp, threats));
        }

        /// <summary>
        /// 没有外部大脑时的本地回应：先看玩家说的是什么，再决定怎么接，避免"答非所问"。
        /// 明确拒绝捡东西时顺手把该物品标记为"不要了"，不再重复提醒。
        /// </summary>
        private static string LocalReply(string said)
        {
            if (string.IsNullOrEmpty(said)) return "嗯，我在听。";
            var s = said.Trim();

            bool deny = s.Contains("不用") || s.Contains("不要") || s.Contains("算了") || s.Contains("别捡") || s.Contains("不需要");
            bool askPick = s.Contains("捡") || s.Contains("拿") || s.Contains("搜");
            bool askMove = s.Contains("走") || s.Contains("去") || s.Contains("回");
            bool greet = s.Contains("在吗") || s.Contains("大肥鱼") || s.Contains("喂");

            if (deny)
            {
                LootWarden.DismissNearby();
                return "行，那就不捡了。";
            }
            if (askPick) return "我去看看能拿什么。";
            if (askMove) return "好，跟着你走。";
            if (greet) return "在呢，怎么了？";

            // 兜底：重复玩家一句关键词，表示听到了
            var tail = s.Length > 12 ? s.Substring(0, 12) + "…" : s;
            return $"「{tail}」——我记着了。";
        }

        /// <summary>比较上一次的状态，产出事件键；没有事件返回 null。</summary>
        private static string DetectEvent(float hp, float hunger, float thirst, float fatigue, int threats)
        {
            string evt = null;

            if (_lastPlayerHp >= 0f && hp < _lastPlayerHp - 0.08f) evt = "hurt";
            else if (_lastPlayerHp >= 0f && hp > _lastPlayerHp + 0.15f) evt = "heal";
            else if (_lastHunger >= 0f && _lastHunger > 0.3f && hunger <= 0.3f) evt = "hungry";
            else if (_lastThirst >= 0f && _lastThirst > 0.3f && thirst <= 0.3f) evt = "thirsty";
            else if (_lastFatigue >= 0f && _lastFatigue < 0.85f && fatigue >= 0.85f) evt = "tired";
            else if (threats > 0 && _lastThreatCount == 0) evt = "threat";
            else if (threats == 0 && _lastThreatCount > 0) evt = "safe";

            _lastPlayerHp = hp;
            _lastHunger = hunger;
            _lastThirst = thirst;
            _lastFatigue = fatigue;
            _lastThreatCount = threats;

            // 长时间没动：偶尔贫一句（本地模板，不花 token）
            if (evt == null && Time.unscaledTime - _lastMoveAt > 240f && Time.unscaledTime - _lastMinorAt > 120f)
            {
                _lastMinorAt = Time.unscaledTime;
                _lastMoveAt = Time.unscaledTime;
                evt = "idle";
            }
            return evt;
        }

        private static void RecordRecentAiLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            string clean = line.Trim();
            RecentAiLines.RemoveAll(s => string.Equals(s, clean, StringComparison.Ordinal));
            RecentAiLines.Add(clean);
            while (RecentAiLines.Count > 12) RecentAiLines.RemoveAt(0);
        }

        private static string PickNonRepeatingLine(string[] candidates)
        {
            if (candidates == null || candidates.Length == 0) return "";
            var unused = new List<string>();
            for (int i = 0; i < candidates.Length; i++)
            {
                string c = candidates[i];
                if (!RecentAiLines.Contains(c)) unused.Add(c);
            }
            if (unused.Count > 0)
                return unused[SpeechRng.Next(unused.Count)];
            return candidates[SpeechRng.Next(candidates.Length)];
        }

        private static string LocalEventLine(string evt)
        {
            switch (evt)
            {
                case "hurt":
                    return PickNonRepeatingLine(new[]
                    {
                        "嘶，刚才那下蹭到血了，先拉开点打药？",
                        "你掉血了，别硬扛着换血，兜里有绷带赶紧缠上。",
                        "注意血条！先退半步喘口气，把伤口止住。",
                        "挨了这一下有点痛，找个拐角先处理伤口吧。"
                    });
                case "heal":
                    return PickNonRepeatingLine(new[]
                    {
                        "血线拉回来点了，总算稳住。",
                        "看着精神多啦，继续推进的时候留意侧翼。",
                        "缓过来就好，省着点用剩下的药。"
                    });
                case "hungry":
                    return PickNonRepeatingLine(new[]
                    {
                        "肚子咕噜响了吧，翻翻包里垫一口干粮？",
                        "再饿下去体力上限要掉，找点吃的塞一塞。",
                        "先找个安全角落吃点东西，空着肚子跑不动。"
                    });
                case "thirsty":
                    return PickNonRepeatingLine(new[]
                    {
                        "嘴皮都干了，记得补一口水。",
                        "该喝水了，包里有水壶或饮料的话抿两口。",
                        "别光顾着搜东西，口渴见底耐力会掉得很快。"
                    });
                case "tired":
                    return PickNonRepeatingLine(new[]
                    {
                        "眼皮直打架了，找张床或安全屋眯一会儿吧。",
                        "太乏了反应会慢半拍，天黑前看能不能找地方歇歇脚。",
                        "体力条有点沉，别再拖着满包重物长途奔袭啦。"
                    });
                case "threat":
                    return PickNonRepeatingLine(new[]
                    {
                        "前面视野里冒出动静了，架好家伙别踩脸。",
                        "有东西靠过来了，听声音先清视线内这一波。",
                        "小心点，附近晃出敌对目标了，留意掩体和弹匣。"
                    });
                case "safe":
                    return PickNonRepeatingLine(new[]
                    {
                        "周围消停了，趁这会儿摸摸包、喘口气。",
                        "威胁清干净啦，顺便检查下枪里还剩几发。",
                        "暂时安全，正好环顾一下有没有漏捡的资源。"
                    });
                case "idle":
                    return PickNonRepeatingLine(new[]
                    {
                        "站这儿发呆呢？要不要顺手清点下背包或者挑个方向搜？",
                        "风声倒是挺安静，要我帮你高亮点附近的物资吗？",
                        "趁着还没天黑刷怪，看看有没有手电电池或配方能搓两件。"
                    });
                default:
                    return "";
            }
        }

        /// <summary>采集玩家状态文本；输出血量/饥饿/口渴/疲劳比例、威胁数与坐标。</summary>
        private static string BuildPlayerSnapshot(out float hpRatio, out float hungerRatio, out float thirstRatio,
            out float fatigueRatio, out int threats, out UnityEngine.Vector2 position)
        {
            hpRatio = 1f; hungerRatio = 0f; thirstRatio = 0f; fatigueRatio = 0f; threats = 0;
            position = UnityEngine.Vector2.zero;
            var sb = new StringBuilder();
            try
            {
                var gc = GameController.instance;
                if (gc == null) return "";
                var pc = gc.playerCharacter;
                if (pc == null || pc.characterData == null) return "";
                var st = pc.characterData.characterStatusData;
                if (st == null) return "";

                hpRatio = st.maxhp > 0 ? st.hp / st.maxhp : 1f;
                hungerRatio = st.maxHunger > 0 ? st.hunger / st.maxHunger : 0f;
                thirstRatio = st.maxThirst > 0 ? st.thirst / st.maxThirst : 0f;
                fatigueRatio = st.maxFatigue > 0 ? st.fatigue / st.maxFatigue : 0f;

                sb.Append($"血量 {st.hp:F0}/{st.maxhp:F0}（{hpRatio * 100:F0}%）");
                sb.Append($"，饥饿 {hungerRatio * 100:F0}%，口渴 {thirstRatio * 100:F0}%，疲劳 {fatigueRatio * 100:F0}%");
                try { sb.Append($"，体温 {st.bodyTemperature:F1}℃"); } catch { }

                var pos = gc.playerWorldPosition;
                position = new UnityEngine.Vector2(pos.x, pos.y);

                try
                {
                    var mc = MapController.instance;
                    var playerCamp = pc.characterData.camp;
                    var playerFloor = pc.characterData.zLevel;
                    var list = gc.characterList;
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            var c = list[i];
                            if (c == null || c.characterData == null) continue;
                            if (c == pc) continue;
                            if (c.characterData.deadFlag) continue;
                            if (c.characterData.camp == playerCamp) continue;
                            if (c.characterData.zLevel != playerFloor) continue;
                            var cp = c.transform.position;
                            float d = UnityEngine.Vector2.Distance(new UnityEngine.Vector2(cp.x, cp.y), position);
                            if (d > 25f) continue;
                            if (d > 5f && mc != null && !mc.IsInSightByRaycast(cp)) continue;
                            threats++;
                        }
                    }
                    sb.Append($"，附近 25 米视线/近身威胁 {threats} 个");
                }
                catch { }

                sb.Append($"，位置 ({pos.x:F0},{pos.y:F0})");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[陪伴] 读取玩家状态失败: {e.Message}");
                return "";
            }
            return sb.ToString();
        }

        private static string BuildLocalCompanionLine(float hpRatio, int threats)
        {
            try
            {
                string bat = GameFacts.BatterySummary();
                if (!string.IsNullOrEmpty(bat) && (bat.Contains("低") || bat.Contains("没电")))
                    return PickNonRepeatingLine(new[]
                    {
                        bat + "，黑夜摸索前记得换电池或备用手电。",
                        bat + "，别等真灭了才摸黑找路。"
                    });
                string prog = GameFacts.ProgressSummary();
                if (!string.IsNullOrEmpty(prog) && threats == 0 && hpRatio >= 0.7f)
                    return PickNonRepeatingLine(new[]
                    {
                        $"你还有未分配点数（{prog}），趁周围安全打开面板加一下战力吧。",
                        $"包里边先缓一缓，{prog} 留着也能早点点出趁手技能。"
                    });
            }
            catch { }

            if (hpRatio < 0.35f)
            {
                return PickNonRepeatingLine(new[]
                {
                    threats > 0 ? $"血量见底而且周围还有 {threats} 个家伙，赶紧撤出视线打药！" : "血量不到三成五了，趁没怪先把伤包严实。",
                    "这血条太悬了，先吃药回口血再考虑硬刚。"
                });
            }
            if (hpRatio < 0.65f)
            {
                return PickNonRepeatingLine(new[]
                {
                    threats > 0 ? $"身上带着伤，边上有 {threats} 个目标别硬拼，卡视野点射。" : "血只剩一半多点，搜完这片顺便找点医疗补给。",
                    "不算满状态，摸房间记得先敲窗看一眼里面。"
                });
            }
            if (threats > 0)
            {
                return PickNonRepeatingLine(new[]
                {
                    $"视线范围内有 {threats} 个目标在晃，先确认退路和弹匣余量。",
                    $"附近盯上 {threats} 个家伙，需要我帮你高亮最近威胁就说一声。"
                });
            }
            return PickNonRepeatingLine(new[]
            {
                "状态稳着呢，需要看背包能搓什么配方或者整理格子都可以喊我。",
                "周围没见到威胁，手电电量和弹匣记得隔阵子瞄一眼。",
                "这片还算安稳，可以打开视线内的箱子检视下物资，我按你看到的给你搭把手。"
            });
        }

        private static string BuildKnownFactsSummary()
        {
            var lines = new List<string>();
            void Add(string label, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    lines.Add(string.IsNullOrEmpty(label) ? value.Trim() : label + "：" + value.Trim());
            }
            try { Add("", GameFacts.InventorySummary()); } catch { }
            try { Add("当前材料可制造（已解锁配方）", GameFacts.CraftableSummary()); } catch { }
            try { Add("接近可制造（材料缺口）", GameFacts.CraftDeficitSummary()); } catch { }
            try { Add("", GameFacts.ContainerSummary()); } catch { }
            try { Add("附近可见地面物品", LootWarden.NearbySummary()); } catch { }
            try { Add("手持设备电量", GameFacts.BatterySummary()); } catch { }
            try { Add("", GameFacts.ProgressSummary()); } catch { }
            return lines.Count == 0 ? "" : string.Join("\n", lines);
        }

        private static void RequestCompanionLlmAsync(LlmSettings st, string snapshot, string playerChat, string evt)
        {
            _companionLlmPending = true;
            _companionRequestChatId = _pendingChatId;
            var requestEpoch = AiSaveContext.Epoch;
            var requestSave = AiSaveContext.SaveId;
            var serial = System.Threading.Interlocked.Increment(ref _requestSerial);

            string persona = CfgPersona != null ? (CfgPersona.Value ?? "").Trim() : "";
            string memory = AiMemory.BuildContext((playerChat ?? "") + " " + (evt ?? ""));
            string knownFacts = BuildKnownFactsSummary();
            string networkRole = GetNetworkRoleLabel();

            var system = new StringBuilder();
            system.Append(AiPrompt.BuildCompanionSystem(NpcName(), PlayerName(), persona, networkRole));
            system.Append("\n当前情绪：").Append(AiMemory.MoodHint());
            if (!string.IsNullOrEmpty(memory)) system.Append("\n你记得这些事：\n").Append(memory);

            var user = new StringBuilder();
            user.Append("当前状态：").Append(snapshot);
            if (RecentAiLines.Count > 0)
                user.Append("\n【recent_ai_lines（避免复读）】").Append(string.Join(" / ", RecentAiLines));
            if (!string.IsNullOrEmpty(knownFacts)) user.Append("\n【已知信息与现有材料】\n").Append(knownFacts);
            if (!string.IsNullOrEmpty(evt)) user.Append("\n刚发生：").Append(EventText(evt));
            if (!string.IsNullOrEmpty(playerChat)) user.Append("\n玩家说：").Append(playerChat).Append("\n先回应这句话。");
            user.Append("\n给出自然不重复的一句回应。");

            var thread = new System.Threading.Thread(() =>
            {
                LlmResult r;
                try { r = LlmClient.Ask(st, system.ToString(), user.ToString()); }
                catch (Exception e) { r = new LlmResult { Error = e.GetType().Name + ": " + e.Message }; }
                lock (_llmLock)
                {
                    if (serial == _requestSerial && AiSaveContext.Matches(requestSave, requestEpoch))
                        _companionResult = r;
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private static string EventText(string evt)
        {
            switch (evt)
            {
                case "hurt": return "玩家受伤掉血了";
                case "heal": return "玩家血量恢复了不少";
                case "hungry": return "玩家饿了";
                case "thirsty": return "玩家渴了";
                case "tired": return "玩家很疲劳";
                case "threat": return "附近出现了敌对目标";
                case "safe": return "附近的威胁清掉了";
                case "idle": return "玩家很久没动";
                default: return evt;
            }
        }

        /// <summary>取回陪伴模式的 LLM 结果（主线程调用）。</summary>
        private static void DrainCompanionResult()
        {
            if (!_companionLlmPending) return;
            LlmResult r = null;
            lock (_llmLock)
            {
                if (_companionResult != null) { r = _companionResult; _companionResult = null; }
            }
            if (r == null) return;
            _companionLlmPending = false;
            if (!r.Ok)
            {
                DropOutlinePlugin.L.LogWarning($"[陪伴] LLM 失败: {r.Error}");
                return;   // API failure is not an in-character fabricated reply.
            }
            if (_companionRequestChatId != _pendingChatId) return;
            string hlNote = ApplyHighlightGoal(r.Goal, true);
            if (!string.IsNullOrEmpty(hlNote) && string.IsNullOrEmpty(r.Say)) r.Say = hlNote;
            if (r.Task != null)
            {
                try { AiTaskRuntime.Accept(r.Task, Guid.NewGuid().ToString("N"), _companionRequestChatId); }
                catch (Exception error) { r.Say = "任务没有执行：" + error.Message; }
            }
            if (!string.IsNullOrEmpty(r.Say)) SayReport(r.Say, _companionRequestChatId);
            if (_companionRequestChatId == _pendingChatId)
            { _pendingPlayerChat = null; _pendingChatId = ""; }
            _nextCompanionReport = Time.unscaledTime + Mathf.Max(20f, CfgCompanionInterval.Value);
        }

        private static string ApplyHighlightGoal(string goal, bool includeClear)
        {
            if (string.IsNullOrEmpty(goal)) return "";
            var g = goal.Trim().ToLowerInvariant();
            if (g == "clear" || g == "clear_highlight")
            {
                if (includeClear) AiActions.ClearHighlight();
                return "";
            }
            if (g == "nearest_loot" || g == "highlight_loot" || g == "nearest_threat" || g == "highlight_threat")
            {
                AiActions.HighlightTarget(g.Replace("highlight_", "nearest_"), out string hSum);
                return hSum ?? "";
            }
            return "";
        }

        /// <summary>只排有限短句；记忆在真正显示时写入。</summary>
        private static void SayReport(string text, string requestId = null)
        {
            QueueSpeech(text, true, requestId);
        }
        // ───────────────────────── 玩家聊天入口 ─────────────────────────

        /// <summary>玩家在游戏内聊天框说的话（由 ChatInput 调用，主线程）。</summary>
        public static void SubmitPlayerChat(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!AiSaveContext.Active || CfgAiEnabled?.Value != true) return;
            text = text.Trim();
            if (text.Length == 0) return;

            Speech.Clear();
            System.Threading.Interlocked.Increment(ref _requestSerial);
            lock (_llmLock) { _llmResult = null; _companionResult = null; _llmBusy = false; _companionLlmPending = false; }
            Memory.Add($"{PlayerName()}：{text}");
            TrimMemory();
            AiMemory.AddChat(PlayerName(), text, AiMemory.TierImportant);
            AiMemory.Save();
            _pendingPlayerChat = text;
            _pendingChatId = Guid.NewGuid().ToString("N");
            _speechRequestId = _pendingChatId;
            AiTaskRuntime.OnPlayerChat(_pendingChatId, text);
            if (IsPlayerMode() || IsDshMode)
            {
                if (AiCoopPlayer.TryHandleDirectCoopChat(text, out string directReply) && !string.IsNullOrEmpty(directReply))
                {
                    bool shownOfficial = false;
                    if (CfgChatViaOfficial == null || CfgChatViaOfficial.Value)
                    {
                        try { shownOfficial = AiNetChat.Say(directReply); } catch { }
                    }
                    if (!shownOfficial)
                        ChatInput.Log(IsPlayerMode() ? "玩家模式指令" : "联机指令", directReply);
                    AiCoopPlayer.ShowAvatarBubble(directReply, 3.5f);
                }
            }
            DropOutlinePlugin.L.LogInfo($"[AI队友] 玩家说：{text}");

            // bridge 模式：立刻把带问题的新状态写给外部 agent
            try
            {
                GOAPAgent snapAgent = IsPlayerMode()
                    ? AiCoopPlayer.GetAvatarAgent()
                    : (IsNpcMode() && FindTeammates().Count > 0 ? FindTeammates()[0] : null);
                WriteStateFile(snapAgent, text);
            }
            catch { }
        }

        /// <summary>当前是否有待处理的玩家消息（供 UI 显示状态）。</summary>
        public static bool HasPendingChat => !string.IsNullOrEmpty(_pendingPlayerChat);

        // ───────────────────────── 队友发现 ─────────────────────────

        private static void DumpAllAgents()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<GOAPAgent>();
                DropOutlinePlugin.L.LogInfo($"[AI队友] 场景内 GOAPAgent 总数={all.Length}（含未招募 NPC）");
                int shown = 0;
                foreach (var a in all)
                {
                    if (a == null || a.gameObject == null || !a.gameObject.activeInHierarchy) continue;
                    if (shown++ >= 25) { DropOutlinePlugin.L.LogInfo("[AI队友] …（超过 25 个，略）"); break; }
                    var sb = new StringBuilder("[AI队友] ");
                    try { sb.Append("type=").Append(a.GetType().Name).Append(' '); } catch { }
                    GOAPNPCData d = null;
                    try { d = a.npcData; } catch { }
                    if (d == null) { sb.Append("npcData=null"); DropOutlinePlugin.L.LogInfo(sb.ToString()); continue; }
                    try { sb.Append("guid=").Append(d.npcGUID).Append(' '); } catch { }
                    try { sb.Append("leader=").Append(d.leaderCharacterGUID ?? "null").Append(' '); } catch { }
                    try { sb.Append("wanderer=").Append(d.isWanderer).Append(' '); } catch { }
                    try { sb.Append("bandit=").Append(d.isBandit).Append(' '); } catch { }
                    try { sb.Append("pet=").Append(d.petData != null).Append(' '); } catch { }
                    try { sb.Append("hp=").Append(d.health.ToString("F0")).Append(' '); } catch { }
                    try
                    {
                        var lc = a.leaderCharacter;
                        sb.Append("leaderChar=").Append(lc != null ? "有" : "无").Append(' ');
                    }
                    catch { }
                    try
                    {
                        var cur = a.GetCurrentGoal();
                        sb.Append("goal=").Append(DescribeGoal(cur));
                    }
                    catch { }
                    DropOutlinePlugin.L.LogInfo(sb.ToString());
                }
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[AI队友] 全量 NPC 探查失败: {e.Message}");
            }
        }

        private static bool _mapActiveCache;
        private static float _lastMapCheck = -999f;

        private static bool HasActiveMapController()
        {
            if (Time.unscaledTime - _lastMapCheck < 2f) return _mapActiveCache;
            _lastMapCheck = Time.unscaledTime;
            _mapActiveCache = false;
            try
            {
                foreach (var mc in Resources.FindObjectsOfTypeAll<MapController>())
                    if (mc != null && mc.gameObject != null && mc.gameObject.activeInHierarchy)
                    { _mapActiveCache = true; break; }
            }
            catch { }
            return _mapActiveCache;
        }

        /// <summary>对已缓存的实例做存活检查（被销毁/卸载的对象再访问会原生崩溃）。</summary>
        private static bool IsAlive(GOAPAgent a)
        {
            try { return a != null && a.gameObject != null && a.gameObject.activeInHierarchy; }
            catch { return false; }
        }

        private static List<GOAPAgent> FindTeammates() => FindTeammates(false);

        // 全场景扫描很贵，IL2CPP 下频繁调用还可能触发原生崩溃（实测 0xC0000005 @0x3254xx），
        // 所以结果缓存 4 秒，Tick / 状态显示都走缓存。
        private static List<GOAPAgent> _cachedTeammates = new List<GOAPAgent>();
        private static float _lastScanTime = -999f;

        private static List<GOAPAgent> FindTeammates(bool quiet)
        {
            if (Time.unscaledTime - _lastScanTime < 4f) return _cachedTeammates;
            _lastScanTime = Time.unscaledTime;

            var list = new List<GOAPAgent>();
            var all = new List<GOAPAgent>();
            foreach (var a in Resources.FindObjectsOfTypeAll<GOAPAgent>())
            {
                if (a == null) continue;
                var go = a.gameObject;
                if (go == null) continue;
                GOAPNPCData data = null;
                try { data = a.npcData; } catch { }
                if (data == null) continue;

                // 未激活的实例（不在当前区块 / 预制体）不参与接管，但要记一笔，方便排查"为什么没有队友"
                if (!go.activeInHierarchy)
                {
                    if (!_inactiveAgentsLogged)
                    {
                        _inactiveAgentsLogged = true;
                        DropOutlinePlugin.L.LogInfo(
                            $"[AI队友] 未激活实例：{a.GetType().Name} leader={(string.IsNullOrEmpty(data.leaderCharacterGUID) ? "空" : data.leaderCharacterGUID)}");
                    }
                    continue;
                }

                all.Add(a);
                if (!string.IsNullOrEmpty(data.leaderCharacterGUID)) list.Add(a);
            }

            if (list.Count == 0 && CfgTakeoverTest != null && CfgTakeoverTest.Value)
            {
                // 联调用：没有招募队友时，接管最近的友好 NPC（非强盗、非宠物）
                GOAPAgent best = null;
                float bestDist = float.MaxValue;
                var origin = Camera.main != null ? (Vector2)Camera.main.transform.position : Vector2.zero;
                foreach (var a in all)
                {
                    try
                    {
                        var d = a.npcData;
                        if (d.isBandit || d.petData != null) continue;
                        if (a.character == null) continue;
                        var p = a.character.transform.position;
                        float dist = Vector2.Distance(new Vector2(p.x, p.y), origin);
                        if (dist < bestDist) { bestDist = dist; best = a; }
                    }
                    catch { }
                }
                if (best != null)
                {
                    list.Add(best);
                    DropOutlinePlugin.L.LogInfo($"[AI队友] 测试接管：选中最近友好 NPC {best.npcData.npcGUID}");
                }
            }

            if (!quiet && list.Count == 0 && Time.unscaledTime - _lastNoTeammateLog > 30f)
            {
                _lastNoTeammateLog = Time.unscaledTime;
                DropOutlinePlugin.L.LogInfo($"[AI队友] 当前没有跟随队友（场景内可用 NPC={all.Count}）");
            }
            _cachedTeammates = list;
            return list;
        }

        private static void DumpDiscovery(List<GOAPAgent> agents)
        {
            DropOutlinePlugin.L.LogInfo($"[AI队友] 发现 {agents.Count} 个跟随队友");
            foreach (var a in agents)
            {
                var d = a.npcData;
                var sb = new StringBuilder();
                sb.Append($"[AI队友] NPC={d.npcGUID} HP={d.health:F0}/{d.maxHealth:F0} ");
                sb.Append($"饥饿={d.hunger:F0} 口渴={d.thirst:F0} 疲劳={d.fatigue:F0} ");
                sb.Append($"basic={d.basicCommand} combat={d.combatCommand} one={d.onetimeCommand} ");
                try { sb.Append($"当前={DescribeGoal(a.GetCurrentGoal())} "); } catch { }
                DropOutlinePlugin.L.LogInfo(sb.ToString());

                try
                {
                    var goals = a.availableGoals;
                    if (goals != null)
                    {
                        var names = new StringBuilder();
                        for (int i = 0; i < goals.Count; i++)
                        {
                            var g = goals[i];
                            if (g == null) continue;
                            names.Append(DescribeGoal(g)).Append(' ');
                        }
                        DropOutlinePlugin.L.LogInfo($"[AI队友] 可用目标[{goals.Count}]: {names}");
                    }
                }
                catch (Exception e)
                {
                    DropOutlinePlugin.L.LogWarning($"[AI队友] 读取目标失败: {e.Message}");
                }
            }

            try
            {
                DropOutlinePlugin.L.LogInfo($"[AI队友] DialoguePanel={(DialoguePanel.instance != null ? "存在" : "null")}");
                var fields = Resources.FindObjectsOfTypeAll<UnityEngine.UI.InputField>();
                DropOutlinePlugin.L.LogInfo($"[AI队友] 场景内 InputField={fields.Length}");
            }
            catch { }
        }

        private static string DescribeGoal(GOAPGoal g)
        {
            if (g == null) return "null";
            string name = null, type = null;
            try { name = g.goalName; } catch { }
            try { type = g.GetType().Name; } catch { }
            if (string.IsNullOrEmpty(name)) return type ?? "?";
            return $"{name}<{type}>";
        }

        private static List<string> GetGoalNames(GOAPAgent agent)
        {
            var names = new List<string>();
            if (agent == null) return names;
            try
            {
                var goals = agent.availableGoals;
                if (goals == null) return names;
                for (int i = 0; i < goals.Count; i++)
                {
                    var g = goals[i];
                    if (g == null) continue;
                    string n = null;
                    try { n = g.goalName; } catch { }
                    if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
                }
            }
            catch { }
            return names;
        }

        // ───────────────────────── 命令文件 / 状态文件 ─────────────────────────

        private sealed class AiCommand
        {
            public string id, goal, npcCmd, say, reason, chat, saveId, worldEpoch, replyTo, memoryNote, writtenAtUtc;
            public string coopOp, coopArg, roomOp, roomArg;
            public AiTaskRequest task;
        }

        private static void HandleCommandFile(GOAPAgent agent)
        {
            var path = CommandFilePath;
            if (!File.Exists(path)) return;
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                File.Move(path, path + ".done-" + Guid.NewGuid().ToString("N"));
                var cmd = ParseCommand(json);
                if (cmd == null) return;
                bool isMenuRoomOp = !AiSaveContext.Active && !string.IsNullOrEmpty(cmd.roomOp) &&
                    cmd.task == null && string.IsNullOrEmpty(cmd.saveId) &&
                    string.Equals(cmd.worldEpoch ?? "", AiSaveContext.Epoch, StringComparison.Ordinal);
                bool scopeOk = AiSaveContext.Matches(cmd.saveId, cmd.worldEpoch) || isMenuRoomOp;
                if (ResolveMode() != "bridge" || Protocol != "agent-dsh" || CfgAiEnabled?.Value != true ||
                    !scopeOk ||
                    !string.Equals(cmd.replyTo ?? "", _pendingChatId ?? "", StringComparison.Ordinal) ||
                    !DateTimeOffset.TryParse(cmd.writtenAtUtc, out var sentAt) ||
                    DateTimeOffset.UtcNow - sentAt > TimeSpan.FromMinutes(2) || sentAt > DateTimeOffset.UtcNow.AddSeconds(10))
                {
                    DropOutlinePlugin.L.LogWarning("[AI队友] 拒绝无作用域、过期、跨档/跨后端或旧消息命令");
                    return;
                }
                if (!string.IsNullOrEmpty(cmd.id) && cmd.id == _lastCommandId) return;
                _lastCommandId = cmd.id;

                if (!string.IsNullOrEmpty(cmd.chat))
                {
                    // 新玩家消息立即丢弃尚未展示的旧分句，不能串话。
                    Speech.Clear();
                    // 外部把玩家的话送进来：进记忆，并在 api 模式下立刻请求 LLM
                    Memory.Add($"{PlayerName()}：{cmd.chat}");
                    TrimMemory();
                    _pendingPlayerChat = cmd.chat;
                    _pendingChatId = Guid.NewGuid().ToString("N");
                    _speechRequestId = _pendingChatId;
                    AiTaskRuntime.OnPlayerChat(_pendingChatId, cmd.chat);
                    AiMemory.AddChat(PlayerName(), cmd.chat, AiMemory.TierImportant);
                    AiMemory.Save(true);
                    if (!((CfgChatViaOfficial == null || CfgChatViaOfficial.Value) && AiNetChat.Available))
                        ChatInput.Log(PlayerName(), cmd.chat);
                    DropOutlinePlugin.L.LogInfo($"[AI队友] 玩家说：{cmd.chat}");
                }

                string opNote = "";
                if (!string.IsNullOrEmpty(cmd.roomOp))
                {
                    AiCoopPlayer.ExecuteRoomOp(cmd.roomOp, cmd.roomArg, out string roomMsg);
                    if (!string.IsNullOrEmpty(roomMsg)) opNote = roomMsg;
                    if (!AiSaveContext.Active)
                    {
                        _lastAppliedCommandId = cmd.id;
                        if (!string.IsNullOrEmpty(opNote)) DropOutlinePlugin.L.LogInfo("[AI联机·房间] " + opNote);
                        WriteInactiveState();
                        return;
                    }
                }
                if (!string.IsNullOrEmpty(cmd.coopOp))
                {
                    AiCoopPlayer.ExecuteCoopOp(cmd.coopOp, cmd.coopArg, out string coopMsg);
                    if (!string.IsNullOrEmpty(coopMsg))
                        opNote = string.IsNullOrEmpty(opNote) ? coopMsg : opNote + "；" + coopMsg;
                    if (IsPlayerMode()) agent = AiCoopPlayer.GetAvatarAgent() ?? agent;
                }
                if (IsNpcMode() && !string.IsNullOrEmpty(cmd.npcCmd))
                {
                    ExecuteHiredNpcCommand(agent, cmd.npcCmd, out string npcMsg);
                    if (!string.IsNullOrEmpty(npcMsg))
                        opNote = string.IsNullOrEmpty(opNote) ? npcMsg : opNote + "；" + npcMsg;
                }

                if (cmd.task != null)
                {
                    try { AiTaskRuntime.Accept(cmd.task, cmd.id, cmd.replyTo); }
                    catch (Exception error)
                    {
                        ApplyDecision(agent, "clear", "任务没有执行：" + error.Message, "", cmd.replyTo ?? cmd.id);
                        if (cmd.replyTo == _pendingChatId) { _pendingPlayerChat = null; _pendingChatId = ""; }
                        WriteStateFile(agent, _pendingPlayerChat); return;
                    }
                }
                if (!string.IsNullOrEmpty(cmd.goal) || !string.IsNullOrEmpty(cmd.say) || !string.IsNullOrEmpty(opNote))
                {
                    string finalSay = !string.IsNullOrEmpty(cmd.say) ? cmd.say : opNote;
                    DropOutlinePlugin.L.LogInfo($"[AI队友] 收到决策 goal={cmd.goal} say={finalSay} reason={cmd.reason} coop={cmd.coopOp} room={cmd.roomOp}");
                    ApplyDecision(agent, cmd.goal, finalSay, cmd.reason, string.IsNullOrEmpty(cmd.replyTo) ? cmd.id : cmd.replyTo);
                    _lastAppliedCommandId = cmd.id;
                    if (!string.IsNullOrEmpty(cmd.memoryNote))
                    {
                        var note = cmd.memoryNote.Length > 500 ? cmd.memoryNote.Substring(0, 500) : cmd.memoryNote;
                        AiMemory.AddEvent("AI整理：" + note, AiMemory.TierImportant);
                    }
                    if (!string.IsNullOrEmpty(cmd.replyTo) && cmd.replyTo == _pendingChatId)
                    { _pendingPlayerChat = null; _pendingChatId = ""; }
                    AiMemory.Save(true);
                }
                WriteStateFile(agent, _pendingPlayerChat);
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[AI队友] 处理命令文件失败: {e.Message}");
            }
        }

        private static void ParseOp(object raw, out string op, out string arg)
        {
            op = null;
            arg = "";
            if (raw is string s)
            {
                op = s.Trim();
                return;
            }
            var obj = MiniJson.AsObj(raw);
            if (obj == null) return;
            op = (MiniJson.Get(obj, "op") as string ?? "").Trim();
            arg = (MiniJson.Get(obj, "arg") as string ?? MiniJson.Get(obj, "code") as string ?? MiniJson.Get(obj, "target") as string ?? "").Trim();
        }

        private static AiCommand ParseCommand(string json)
        {
            var obj = MiniJson.Parse(json);
            if (obj == null) return null;
            ParseOp(MiniJson.Get(obj, "coop_op"), out string coopOp, out string coopArg);
            ParseOp(MiniJson.Get(obj, "room_op"), out string roomOp, out string roomArg);
            var cmd = new AiCommand
            {
                id = MiniJson.Get(obj, "id") as string,
                goal = MiniJson.Get(obj, "goal") as string,
                npcCmd = (MiniJson.Get(obj, "npc_cmd") as string ?? MiniJson.Get(obj, "npcCommand") as string ?? "").Trim(),
                say = MiniJson.Get(obj, "say") as string,
                reason = MiniJson.Get(obj, "reason") as string,
                chat = MiniJson.Get(obj, "chat") as string,
                saveId = MiniJson.Get(obj, "saveId") as string,
                worldEpoch = MiniJson.Get(obj, "worldEpoch") as string,
                replyTo = MiniJson.Get(obj, "replyTo") as string,
                memoryNote = MiniJson.Get(obj, "memoryNote") as string,
                writtenAtUtc = MiniJson.Get(obj, "writtenAtUtc") as string,
                coopOp = coopOp,
                coopArg = coopArg,
                roomOp = roomOp,
                roomArg = roomArg,
                task = AiTaskRuntime.Parse(MiniJson.Get(obj, "task"))
            };
            if (string.IsNullOrEmpty(cmd.goal) && string.IsNullOrEmpty(cmd.npcCmd) && string.IsNullOrEmpty(cmd.say) && string.IsNullOrEmpty(cmd.chat)
                && string.IsNullOrEmpty(cmd.coopOp) && string.IsNullOrEmpty(cmd.roomOp) && cmd.task == null)
                return null;
            return cmd;
        }

        /// <summary>把当前局面写给外部 agent（bridge 模式的大脑输入）。</summary>
        private static void WriteStateFile(GOAPAgent agent, string pendingChat)
        {
            try
            {
                var situation = BuildSituation(agent);
                var goals = GetGoalNames(agent);
                var sb = new StringBuilder();
                sb.Append("{").Append(ScopeJson()).Append(",\"tasks\":").Append(AiTaskRuntime.TasksJson()).Append(",\"taskCapabilities\":").Append(AiTaskRuntime.CapabilitiesJson()).Append(",\"taskFacts\":").Append(AiTaskRuntime.FactsJson()).Append(",\"ts\":\"").Append(DateTime.Now.ToString("HH:mm:ss")).Append("\",");
                sb.Append("\"agent\":\"").Append(MiniJson.Escape(AgentName(Protocol))).Append("\",");
                sb.Append("\"protocol\":\"").Append(MiniJson.Escape(Protocol)).Append("\",");
                sb.Append("\"npc\":\"").Append(MiniJson.Escape(NpcName())).Append("\",");
                sb.Append("\"player\":\"").Append(MiniJson.Escape(PlayerName())).Append("\",");
                sb.Append("\"replyFile\":\"").Append(MiniJson.Escape(CommandFilePath)).Append("\",");
                sb.Append("\"replyFormat\":\"{\\\"id\\\":\\\"任意\\\",\\\"goal\\\":\\\"目标名或clear\\\",\\\"say\\\":\\\"对玩家说的话\\\",\\\"reason\\\":\\\"理由（可空）\\\"}\",");
                sb.Append("\"situation\":\"").Append(MiniJson.Escape(situation)).Append("\",");
                sb.Append("\"currentGoal\":\"").Append(MiniJson.Escape(CurrentGoalName(agent))).Append("\",");
                sb.Append("\"lastApplied\":\"").Append(MiniJson.Escape(_lastGoalApplied ?? "")).Append("\",");
                sb.Append("\"lastAppliedOk\":").Append(_lastGoalOk ? "true" : "false").Append(",");
                sb.Append("\"pendingChat\":\"").Append(MiniJson.Escape(pendingChat ?? "")).Append("\",");
                sb.Append("\"memory\":[");
                for (int i = 0; i < Memory.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(MiniJson.Escape(Memory[i])).Append('"');
                }
                sb.Append("],\"goals\":[");
                for (int i = 0; i < goals.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(MiniJson.Escape(goals[i])).Append('"');
                }
                sb.Append("],\"loot\":\"").Append(MiniJson.Escape(LootWarden.PendingSummary() ?? "")).Append("\",");
                sb.Append("\"inventory\":\"").Append(MiniJson.Escape(GameFacts.InventorySummary() ?? "")).Append("\",");
                sb.Append("\"craftable\":\"").Append(MiniJson.Escape(GameFacts.CraftableSummary() ?? "")).Append("\",");
                sb.Append("\"nearby\":\"").Append(MiniJson.Escape(LootWarden.NearbySummary() ?? "")).Append("\"}");

                var path = StateFilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // 偶发"拒绝访问"（多为切档/杀毒/索引器短暂占用），重试几次再放弃，
                // 避免一帧写不进去就把整轮状态丢掉。
                for (int attempt = 0; ; attempt++)
                {
                    try { AtomicText(path, sb.ToString()); break; }
                    catch (Exception writeError) when (attempt < 3 &&
                        (writeError is System.IO.IOException || writeError is UnauthorizedAccessException))
                    {
                        System.Threading.Thread.Sleep(20);
                    }
                }

                WritePromptFile(agent, situation, pendingChat);
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[AI队友] 写状态文件失败: {e.Message}");
            }
        }

        private static void WritePromptFile(GOAPAgent agent, string situation, string pendingChat)
        {
            try
            {
                bool hasMate = FindTeammates(true).Count > 0;
                var system = AiPrompt.BuildSystem(NpcName(), GetGoalNames(agent), PlayerName(),
                    CfgPersona != null ? CfgPersona.Value : null, GetNetworkRoleLabel(), NormalizedAiMode(), hasMate);
                var user = AiPrompt.BuildUser(situation, AiMemory.BuildContext(pendingChat ?? "", 12) + "\n" + BuildMemory(), pendingChat);
                var sb = new StringBuilder();
                sb.AppendLine("# ZED ZONE AI 队友 · 决策提示词（MOD 自动生成，供本地 Agent 直接使用）");
                sb.AppendLine();
                sb.AppendLine("把下面【系统提示】和【本次输入】一起交给模型，然后把模型输出的 JSON");
                sb.AppendLine("写入：");
                sb.AppendLine("`" + CommandFilePath + "`");
                sb.AppendLine();
                sb.AppendLine("写入示例：{\"id\":\"" + DateTime.Now.ToString("HHmmss") + "\",\"goal\":\"FollowLeader\",\"say\":\"我跟着你。\",\"reason\":\"\"}");
                sb.AppendLine();
                sb.AppendLine("## 【系统提示】");
                sb.AppendLine(system);
                sb.AppendLine();
                sb.AppendLine("## 【本次输入】");
                sb.AppendLine(user);

                var path = Path.Combine(Path.GetDirectoryName(StateFilePath) ?? Paths.ConfigPath, "ai-prompt.md");
                AtomicText(path, sb.ToString());
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[AI队友] 写提示词文件失败: {e.Message}");
            }
        }

        // ───────────────────────── 决策执行 ─────────────────────────

        private static readonly HashSet<string> OfficialNpcCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "Basic.FollowPlayer", "Basic.StayAtPosition", "Basic.CollectResource", "Basic.CollectSpecifiedItem",
            "Combat.FireAtWill", "Combat.ProtectPlayer", "Combat.StopFire",
            "WeaponPreference.NoWeaponPreference", "WeaponPreference.RangedWeaponOnly", "WeaponPreference.MeleeWeaponOnly",
            "OneTime.HealPlayer", "OneTime.ThrowGrenade",
        };

        /// <summary>严格按官方设计范围对已招募雇佣 NPC 下达内置指令。</summary>
        private static bool ExecuteHiredNpcCommand(GOAPAgent agent, string rawCmd, out string note)
        {
            note = "";
            if (string.IsNullOrWhiteSpace(rawCmd)) return false;
            if (IsMultiplayerClientOnly())
            {
                note = "当前为联机客户端，雇佣 NPC 指令由房主端权威执行。";
                return false;
            }
            if (agent == null || agent.npcData == null)
            {
                note = "当前未招募雇佣 NPC（请先在游戏内招募幸存者，或改用「玩家模式」）。";
                return false;
            }
            string matched = null;
            foreach (var c in OfficialNpcCommands)
            {
                if (string.Equals(c, rawCmd.Trim(), StringComparison.OrdinalIgnoreCase)) { matched = c; break; }
            }
            if (matched == null)
            {
                note = $"不支持的非官方 NPC 指令：{rawCmd}";
                return false;
            }
            try
            {
                if (matched.StartsWith("OneTime.", StringComparison.Ordinal))
                    agent.ExecuteOneTimeNPCCommand(matched);
                else
                    agent.npcData.ExecuteNPCCommand(matched);
                DropOutlinePlugin.L.LogInfo($"[雇佣NPC] 执行官方指令 → {matched}");
                note = $"已执行雇佣NPC指令：{matched}";
                return true;
            }
            catch (Exception e)
            {
                note = $"雇佣NPC指令执行失败：{e.Message}";
                DropOutlinePlugin.L.LogWarning($"[雇佣NPC] {note}");
                return false;
            }
        }

        private static void ApplyDecision(GOAPAgent agent, string goal, string say, string reason, string requestId = null)
        {
            _lastCommandTime = Time.unscaledTime;
            // Gameplay actions remain immediate; only presentation is paced.
            if (!string.IsNullOrEmpty(goal))
            {
                if (IsPlayerMode())
                    AiCoopPlayer.ApplyPlayerModeGoal(goal);
                else if (IsNpcMode() && !IsMultiplayerClientOnly())
                    ApplyGoal(agent, goal);
            }
            string line = string.IsNullOrEmpty(say) ? reason : say;
            if (!string.IsNullOrEmpty(line)) QueueSpeech(line, true, requestId);
        }

        private static bool ApplyGoal(GOAPAgent agent, string goalName)
        {
            if (agent == null)
            {
                DropOutlinePlugin.L.LogWarning($"[AI队友] 当前没有可操控的队友，忽略目标「{goalName}」");
                return false;
            }
            try
            {
                if (goalName.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                    goalName.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    agent.ClearForcedGoal();
                    _lastGoalApplied = "clear";
                    _forcedGoalName = null;
                    DropOutlinePlugin.L.LogInfo("[AI队友] 清除强制目标（维持现状）");
                    return true;
                }

                var goal = FindGoal(agent, goalName);
                if (goal == null)
                {
                    DropOutlinePlugin.L.LogWarning($"[AI队友] 找不到目标「{goalName}」，保持原行为");
                    return false;
                }
                bool ok = agent.ForceGoal(goal);
                _lastGoalApplied = goalName;
                _forcedGoalName = goalName;
                _lastGoalOk = ok;
                DropOutlinePlugin.L.LogInfo($"[AI队友] 强制目标 → {DescribeGoal(goal)} 结果={ok}");
                return ok;
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[AI队友] 设置目标失败: {e.Message}");
                return false;
            }
        }

        private static GOAPGoal FindGoal(GOAPAgent agent, string goalName)
        {
            var goals = agent.availableGoals;
            if (goals == null) return null;

            GOAPGoal partial = null;
            for (int i = 0; i < goals.Count; i++)
            {
                var g = goals[i];
                if (g == null) continue;
                string n = null;
                try { n = g.goalName; } catch { }
                if (string.IsNullOrEmpty(n)) continue;
                if (n.Equals(goalName, StringComparison.OrdinalIgnoreCase)) return g;
                if (partial == null && n.IndexOf(goalName, StringComparison.OrdinalIgnoreCase) >= 0) partial = g;
            }
            if (partial != null) return partial;

            for (int i = 0; i < goals.Count; i++)
            {
                var g = goals[i];
                if (g == null) continue;
                string t = null;
                try { t = g.GetType().Name; } catch { }
                if (t != null && t.IndexOf(goalName, StringComparison.OrdinalIgnoreCase) >= 0) return g;
            }
            return null;
        }

        private static string CurrentGoalName(GOAPAgent agent)
        {
            if (agent == null) return "";
            try
            {
                var g = agent.GetCurrentGoal();
                if (g == null) return "";
                var n = g.goalName;
                return string.IsNullOrEmpty(n) ? g.GetType().Name : n;
            }
            catch { return ""; }
        }

        private static void ApplyHeuristic(GOAPAgent agent)
        {
            if (agent == null) return;
            var data = agent.npcData;
            if (data == null) return;

            float hp = 1f;
            try { hp = data.maxHealth > 0 ? data.health / data.maxHealth : 1f; } catch { }

            string pick;
            if (hp < 0.35f) pick = "HealSelf";
            else if (HasNearbyThreat(agent)) pick = "EliminateThreat";
            else pick = "FollowLeader";

            if (FindGoal(agent, pick) == null)
            {
                pick = null;
                foreach (var fallback in new[] { "EliminateThreat", "FollowLeader", "StayAlive", "LootResource" })
                {
                    if (FindGoal(agent, fallback) != null) { pick = fallback; break; }
                }
            }
            if (pick != null) ApplyGoal(agent, pick);
        }

        private static bool HasNearbyThreat(GOAPAgent agent)
        {
            try { return agent.mostThreateningCharacter != null; }
            catch { return false; }
        }

        // ───────────────────────── 局面描述 ─────────────────────────

        private static string BuildSituation(GOAPAgent agent)
        {
            var sb = new StringBuilder();
            sb.Append($"【环境与模式】{AiModeDisplayName()} | {GetNetworkRoleLabel()}。");
            if (IsPlayerMode())
            {
                sb.Append($"玩家化身：{AiCoopPlayer.GetAvatarSummary()} | {AiCoopPlayer.GetRoomSummary()}。");
                float hpRatio, hunger, thirst, fatigue; int threats; UnityEngine.Vector2 pos;
                var snap = BuildPlayerSnapshot(out hpRatio, out hunger, out thirst, out fatigue, out threats, out pos);
                if (!string.IsNullOrEmpty(snap)) sb.Append('\n').Append("【本地玩家状态】").Append(snap);
            }
            else if (agent == null)
            {
                float hpRatio, hunger, thirst, fatigue; int threats; UnityEngine.Vector2 pos;
                var snap = BuildPlayerSnapshot(out hpRatio, out hunger, out thirst, out fatigue, out threats, out pos);
                if (IsNpcMode())
                    sb.Append(string.IsNullOrEmpty(snap) ? "当前还没有已招募的雇佣 NPC。" : $"（当前还没有已招募的雇佣 NPC，只能给玩家建议。）\n{snap}");
                else
                    sb.Append(string.IsNullOrEmpty(snap) ? "（暂无可读玩家状态。）" : snap);
            }
            else
            {
                var d = agent.npcData;
                try
                {
                    sb.Append($"血量 {d.health:F0}/{d.maxHealth:F0}，饥饿 {d.hunger:F0}，口渴 {d.thirst:F0}，疲劳 {d.fatigue:F0}。");
                    if (IsNpcMode())
                        sb.Append($"官方指令[基础={d.basicCommand ?? "默认"},战斗={d.combatCommand ?? "默认"},武器={d.weaponPreference ?? "默认"}]。");
                }
                catch { }
                try
                {
                    var cur = CurrentGoalName(agent);
                    sb.Append($"正在做：{(string.IsNullOrEmpty(cur) ? "空闲" : cur)}。");
                }
                catch { }
                try
                {
                    sb.Append(HasNearbyThreat(agent) ? "附近有威胁。" : "附近暂时安全。");
                }
                catch { }
                try
                {
                    var leader = agent.leaderCharacter;
                    var self = agent.character;
                    if (leader != null && self != null)
                    {
                        float dist = Vector2.Distance(
                            new Vector2(self.transform.position.x, self.transform.position.y),
                            new Vector2(leader.transform.position.x, leader.transform.position.y));
                        sb.Append($"离{PlayerName()}{dist:F0}米。");
                    }
                }
                catch { }
            }
            if (RecentAiLines.Count > 0)
            {
                sb.Append('\n').Append("【recent_ai_lines（最近已说台词，不要复读）】").Append(string.Join(" / ", RecentAiLines));
            }
            var knownFacts = BuildKnownFactsSummary();
            if (!string.IsNullOrEmpty(knownFacts))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("【已知信息与现有材料】\n").Append(knownFacts);
            }
            return sb.ToString();
        }

        private static string BuildMemory()
        {
            if (Memory.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var m in Memory) sb.AppendLine(m);
            return sb.ToString().TrimEnd();
        }

        private static void TrimMemory()
        {
            while (Memory.Count > 12) Memory.RemoveAt(0);
        }

        private static string NpcName() => CfgNpcName != null && !string.IsNullOrEmpty(CfgNpcName.Value) ? CfgNpcName.Value : "阿岳";
        /// <summary>供其他模块（官方聊天接入层）读取她的显示名。</summary>
        internal static string AiDisplayName() => NpcName();
        /// <summary>供官方聊天单人发送回显使用的玩家名称。</summary>
        internal static string PlayerDisplayName() => PlayerName();
        private static string PlayerName() => CfgPlayerName != null && !string.IsNullOrEmpty(CfgPlayerName.Value) ? CfgPlayerName.Value : "队长";

        // ───────────────────────── LLM ─────────────────────────

        private static LlmSettings BuildSettings()
        {
            return new LlmSettings
            {
                Protocol = LlmClient.ParseProtocol(CfgProtocol != null ? CfgProtocol.Value : "chat"),
                BaseUrl = (CfgBaseUrl != null ? CfgBaseUrl.Value : "").Trim(),
                Model = (CfgModel != null ? CfgModel.Value : "").Trim(),
                ApiKey = LlmClient.UnprotectKey(CfgApiKeyEnc != null ? CfgApiKeyEnc.Value : ""),
                TimeoutSec = 45
            };
        }

        private static LlmResult TakeLlmResult()
        {
            lock (_llmLock)
            {
                var r = _llmResult;
                _llmResult = null;
                return r;
            }
        }

        private static void RequestLlmAsync(GOAPAgent agent, LlmSettings st, string playerChat)
        {
            string situation = BuildSituation(agent);
            string memory = BuildMemory();
            bool hasMate = agent != null || FindTeammates(true).Count > 0;
            string system = AiPrompt.BuildSystem(NpcName(), GetGoalNames(agent), PlayerName(),
                CfgPersona != null ? CfgPersona.Value : null, GetNetworkRoleLabel(), NormalizedAiMode(), hasMate);
            if (CfgAllowRefuse != null && !CfgAllowRefuse.Value)
                system += "\n补充规则：不要拒绝玩家，尽量配合。";
            string user = AiPrompt.BuildUser(situation, memory, playerChat);

            _llmBusy = true;
            var requestChatId = _pendingChatId;
            var requestEpoch = AiSaveContext.Epoch;
            var requestSave = AiSaveContext.SaveId;
            var serial = System.Threading.Interlocked.Increment(ref _requestSerial);
            var thread = new System.Threading.Thread(() =>
            {
                LlmResult r;
                try { r = LlmClient.Ask(st, system, user); }
                catch (Exception e) { r = new LlmResult { Error = e.GetType().Name + ": " + e.Message }; }
                lock (_llmLock)
                {
                    if (serial != _requestSerial || !AiSaveContext.Matches(requestSave, requestEpoch)) return;
                    r.ReplyTo = requestChatId;
                    _llmResult = r;
                    _llmBusy = false;
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        // ───────────────────────── 对话输出 ─────────────────────────

        private static string SpeechScope => AiSaveContext.SaveId + "|" + AiSaveContext.Epoch + "|" + Protocol + "|" + ResolveMode() + "|" + CurrentRunMode + "|" + (CfgAiMode?.Value ?? "");

        private static void QueueSpeech(string text, bool remember, string requestId = null)
        {
            if (!AiSaveContext.Active || CfgAiEnabled?.Value != true || string.IsNullOrWhiteSpace(text)) return;
            Speech.Enqueue(SpeechScope, requestId ?? _speechRequestId, text, Time.unscaledTime, remember);
            DrainSpeech(); // First short line can be immediate; no delayed task/goal.
        }
        private static void DrainSpeech()
        {
            if (!AiSaveContext.Active || CfgAiEnabled?.Value != true) { Speech.Clear(); return; }
            if (!Speech.TryTake(SpeechScope, Time.unscaledTime, out var line)) return;
            // Only the actual displayed text enters chat/short-term/long-term memory.
            // Never use the original unshown long model response as a memory fact.
            RecordRecentAiLine(line.Text);
            bool useOfficial = CfgChatViaOfficial == null || CfgChatViaOfficial.Value;
            bool shownInOfficial = false;
            // 正式版：她的台词进左侧官方聊天框，不再同时弹出左下角重复浮层；仅在官方聊天未启用或不可用时回退
            if (useOfficial)
            {
                try { shownInOfficial = AiNetChat.Say(line.Text); } catch { }
            }
            if (!shownInOfficial)
                ChatInput.Log(NpcName(), line.Text);
            if (IsPlayerMode() || IsDshMode)
            {
                try { AiCoopPlayer.ShowAvatarBubble(line.Text, 4.5f); } catch { }
            }
            DropOutlinePlugin.L.LogInfo($"[AI队友·说] {line.Text}");
            if (line.Remember)
            {
                AiMemory.AddChat(NpcName(), line.Text);
                AiMemory.Save();
                Memory.Add($"我：{line.Text}");
                TrimMemory();
            }
        }
        private static void Say(GOAPAgent agent, string text)
        {
            QueueSpeech(text, false);
        }
    }
}






















































