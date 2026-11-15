using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LiteNetLib;
using UnityEngine;
using ZedZone.HordeMode;
using ZedZone.Net;

namespace Dropoutline
{
    /// <summary>
    /// 玩家模式（联机 AI 玩家）与联机房间控制层：
    /// 1) 联机房间进退/建房与大厅自动准备（单人游戏可自动或一键开启本地联机 Host）；
    /// 2) 为 AI 创建独立联机角色卡（MpCharacters），通过 NetworkManager / NetLobby / NetPlayerSync
    ///    以正式 playerId 加入房间并生成真实联机玩家实体（带玩家头顶名牌、小地图玩家图标与网络同步）；
    /// 3) 若未开联机且关闭自动开房，则平滑兜底为单机跟随实体；支持紧跟/迎战/驻守/拾荒与客户端协同。
    /// </summary>
    public static class AiCoopPlayer
    {
        private static string _avatarGuid = "";
        private static GOAPAgent _avatarAgent;
        private static int _aiNetPlayerId;
        private static string _aiMpProfileId = "";
        private static HumanCharacterController _netPlayerCtrl;
        private static CharacterData _aiPlayerCharData;
        private static Vector2 _netPos;
        private static Vector2 _netVel;
        private static Vector2 _netLook = Vector2.right;
        private static uint _netTick;
        private static float _nextNetPushAt;
        private static float _nextNetFireAt;
        private static float _nextNetHealAt;
        private static float _nextNetReloadAt;
        private static float _netLastFrameTime;
        private static int _lastHoldItemId = int.MinValue;
        private static readonly HashSet<int> _spawnSentPeers = new HashSet<int>();

        private static string _behaviorMode = "follow"; // follow | combat | guard | scavenge
        private static Vector2 _guardPos;
        private static bool _hasGuardPos;
        private static float _nextSpawnAttemptAt;
        private static float _nextFollowTickAt;
        private static float _nextCombatTickAt;
        private static float _nextNetDriveAt;
        private static float _nextClientReadyAt;
        private static float _nextNetworkSyncAt;
        private static bool _lobbyProfileSent;
        private static float _lastJoinAttemptAt = -999f;
        private static string _lastJoinResult = "";
        private static string _lastCoopStatus = "idle";

        public static int AiNetPlayerId => _aiNetPlayerId;
        public static bool IsNetPlayerActive => _aiNetPlayerId > 0 && IsValidController(_netPlayerCtrl);

        public static readonly string[] BehaviorValues = { "follow", "combat", "guard", "scavenge" };
        public static readonly string[] BehaviorLabels =
        {
            "紧跟玩家（自动清怪/传送兜底）",
            "积极迎战（主动索敌/自动换弹）",
            "原地驻守（守卫指定位置）",
            "协同拾荒（跟随并警戒周边）",
        };

        public static string BehaviorMode => _behaviorMode;
        public static string AvatarGuid => _avatarGuid ?? "";

        public static void SetBehaviorMode(string mode)
        {
            var m = NormalizeBehavior(mode);
            _behaviorMode = m;
            if (m == "guard")
            {
                var p = GameController.instance != null ? GameController.instance.playerCharacter : null;
                if (IsNetPlayerActive)
                {
                    _guardPos = new Vector2(_netPlayerCtrl.transform.position.x, _netPlayerCtrl.transform.position.y);
                    _hasGuardPos = true;
                }
                else
                {
                    var ag = GetAvatarAgent();
                    if (ag != null && ag.character != null)
                    {
                        _guardPos = new Vector2(ag.character.transform.position.x, ag.character.transform.position.y);
                        _hasGuardPos = true;
                    }
                    else if (p != null)
                    {
                        _guardPos = new Vector2(p.transform.position.x, p.transform.position.y);
                        _hasGuardPos = true;
                    }
                }
            }
            else
            {
                _hasGuardPos = false;
            }
        }

        /// <summary>在玩家模式下，把通用 goal 名映射为玩家模式的行为姿态。</summary>
        public static void ApplyPlayerModeGoal(string goal)
        {
            if (string.IsNullOrWhiteSpace(goal)) return;
            var g = goal.Trim().ToLowerInvariant();
            if (g == "clear" || g == "none") return;
            if (g.Contains("eliminate") || g.Contains("attack") || g.Contains("combat"))
            {
                SetBehaviorMode("combat");
            }
            else if (g.Contains("overwatch") || g.Contains("guard") || g.Contains("stay"))
            {
                SetBehaviorMode("guard");
            }
            else if (g.Contains("loot") || g.Contains("collect") || g.Contains("search") || g.Contains("scavenge"))
            {
                SetBehaviorMode("scavenge");
            }
            else if (g.Contains("follow"))
            {
                SetBehaviorMode("follow");
            }
            else if (g.Contains("heal"))
            {
                ExecuteCoopOp("heal", "", out _);
            }
        }

        public static string NormalizeBehavior(string mode)
        {
            var m = (mode ?? "").Trim().ToLowerInvariant();
            switch (m)
            {
                case "combat":
                case "attack":
                case "fight":
                case "cover":
                    return "combat";
                case "guard":
                case "stay":
                case "hold":
                    return "guard";
                case "scavenge":
                case "loot":
                case "search":
                    return "scavenge";
                default:
                    return "follow";
            }
        }

        public static void ResetOnWorldChange()
        {
            _avatarGuid = "";
            _avatarAgent = null;
            _aiNetPlayerId = 0;
            _netPlayerCtrl = null;
            _spawnSentPeers.Clear();
            _lastHoldItemId = int.MinValue;
            _hasGuardPos = false;
            _nextSpawnAttemptAt = 0f;
            _nextFollowTickAt = 0f;
            _nextCombatTickAt = 0f;
            _nextNetDriveAt = 0f;
            _nextNetPushAt = 0f;
            _nextNetFireAt = 0f;
            _nextNetHealAt = 0f;
            _nextNetReloadAt = 0f;
            _nextNetworkSyncAt = 0f;
            _lobbyProfileSent = false;
            _lastCoopStatus = "world_reset";
        }

        // ─────────────────────────────────────────────
        // 1. 联机状态、进退房间、建房与大厅自动准备
        // ─────────────────────────────────────────────

        public static string GetNetworkJson()
        {
            if (DropOutlinePlugin.WorldTearingDown)
                return "{\"online\":false,\"isHost\":false,\"isClient\":false,\"mode\":\"offline\",\"clientState\":\"disconnected\",\"roomCode\":\"\",\"joinPhase\":\"idle\",\"joinStatus\":\"\",\"memberCount\":0,\"remotePlayers\":0,\"inLobbyAsClient\":false,\"isOpenAsHost\":false,\"lastAction\":\"\"}";

            bool online = false, isHost = false, isClient = false;
            string mode = "offline", clientState = "disconnected", roomCode = "", joinPhase = "idle", joinStatus = "";
            int members = 0, remotePlayers = 0;
            bool inLobbyAsClient = false, isOpenAsHost = false;

            try
            {
                var nm = NetworkManager.instance;
                if (nm != null)
                {
                    online = NetworkManager.isOnline;
                    isHost = NetworkManager.isHost;
                    isClient = NetworkManager.isClient;
                    mode = nm.mode.ToString().ToLowerInvariant();
                    clientState = nm.clientState.ToString().ToLowerInvariant();
                    members = nm.members != null ? nm.members.Count : 0;
                }
            }
            catch { }

            try
            {
                inLobbyAsClient = NetLobby.inLobbyAsClient;
                isOpenAsHost = NetLobby.isOpenAsHost;
                roomCode = NetLobby.roomCode ?? "";
            }
            catch { }

            try
            {
                var ncc = NetCoordClient.instance;
                if (ncc != null)
                {
                    if (string.IsNullOrEmpty(roomCode)) roomCode = ncc.roomCode ?? ncc.LocalRoomCode ?? "";
                    joinPhase = ncc.joinPhase.ToString().ToLowerInvariant();
                    joinStatus = ncc.joinStatusLocalized ?? ncc.joinStatus ?? "";
                }
            }
            catch { }

            try
            {
                if (NetPlayerSync.instance != null) remotePlayers = NetPlayerSync.remotePlayerCount;
            }
            catch { }

            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"online\":").Append(online ? "true" : "false");
            sb.Append(",\"isHost\":").Append(isHost ? "true" : "false");
            sb.Append(",\"isClient\":").Append(isClient ? "true" : "false");
            sb.Append(",\"mode\":\"").Append(MiniJson.Escape(mode)).Append('"');
            sb.Append(",\"clientState\":\"").Append(MiniJson.Escape(clientState)).Append('"');
            sb.Append(",\"roomCode\":\"").Append(MiniJson.Escape(roomCode)).Append('"');
            sb.Append(",\"joinPhase\":\"").Append(MiniJson.Escape(joinPhase)).Append('"');
            sb.Append(",\"joinStatus\":\"").Append(MiniJson.Escape(joinStatus)).Append('"');
            sb.Append(",\"memberCount\":").Append(members);
            sb.Append(",\"remotePlayers\":").Append(remotePlayers);
            sb.Append(",\"inLobbyAsClient\":").Append(inLobbyAsClient ? "true" : "false");
            sb.Append(",\"isOpenAsHost\":").Append(isOpenAsHost ? "true" : "false");
            sb.Append(",\"lastAction\":\"").Append(MiniJson.Escape(_lastJoinResult ?? "")).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        public static string GetRoomSummary()
        {
            try
            {
                var nm = NetworkManager.instance;
                var ncc = NetCoordClient.instance;
                string code = "";
                try { code = NetLobby.roomCode; } catch { }
                if (string.IsNullOrEmpty(code) && ncc != null) code = ncc.roomCode ?? ncc.LocalRoomCode ?? "";

                if (nm == null || !NetworkManager.isOnline)
                {
                    if (NetLobby.inLobbyAsClient) return $"大厅准备中 房间码:{code}";
                    if (NetLobby.isOpenAsHost) return $"大厅等待玩家 房间码:{code}";
                    if (ncc != null && ncc.joinPhase != NetCoordClient.JoinPhase.Idle)
                        return $"连线阶段:{ncc.joinPhase} {ncc.joinStatusLocalized ?? ncc.joinStatus}";
                    return string.IsNullOrEmpty(_lastJoinResult) ? "单机 / 未连线" : _lastJoinResult;
                }

                string role = NetworkManager.isHost ? "房主" : "客户端";
                int mem = nm.members != null ? nm.members.Count : 1;
                return $"{role} ({nm.clientState}) 成员:{mem} 房间:{(string.IsNullOrEmpty(code) ? "-" : code)}";
            }
            catch
            {
                return "联机状态未知";
            }
        }

        public static bool TryJoinRoomByCode(string code, string password, out string message)
        {
            message = "";
            if (DropOutlinePlugin.WorldTearingDown)
            {
                message = "场景切换中，请稍后再试。";
                return false;
            }
            var clean = (code ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(clean))
            {
                message = "请先填写房间码，再执行加入房间。";
                _lastJoinResult = message;
                return false;
            }

            if (Time.unscaledTime - _lastJoinAttemptAt < 1.5f)
            {
                message = "正在发起连线，请稍候。";
                return false;
            }
            _lastJoinAttemptAt = Time.unscaledTime;

            try
            {
                var ncc = NetCoordClient.instance;
                if (ncc == null)
                {
                    message = "联机协调器未就绪（NetCoordClient.instance 为空）。";
                    _lastJoinResult = message;
                    return false;
                }

                _lobbyProfileSent = false;
                bool ok = ncc.JoinByCode(clean, password ?? "", false);
                message = ok
                    ? $"已发起房间码连线：{clean}（{ncc.joinPhase}）"
                    : $"加入房间失败：{ncc.joinStatusLocalized ?? ncc.joinStatus ?? "协调服务拒绝或格式无效"}";
                _lastJoinResult = message;
                DropOutlinePlugin.L.LogInfo($"[AI联机] JoinByCode({clean}) -> {ok} / {message}");
                return ok;
            }
            catch (Exception e)
            {
                message = "加入房间异常：" + e.Message;
                _lastJoinResult = message;
                DropOutlinePlugin.L.LogWarning($"[AI联机] JoinByCode 异常: {e.Message}");
                return false;
            }
        }

        public static bool TryJoinRoomDirect(string hostAndPort, out string message)
        {
            message = "";
            if (DropOutlinePlugin.WorldTearingDown)
            {
                message = "场景切换中，请稍后再试。";
                return false;
            }
            var raw = (hostAndPort ?? "").Trim();
            if (string.IsNullOrEmpty(raw))
            {
                message = "请提供 IP 或 IP:端口。";
                _lastJoinResult = message;
                return false;
            }

            string host = raw;
            int port = 7777;
            int colon = raw.LastIndexOf(':');
            if (colon > 0 && colon < raw.Length - 1)
            {
                if (int.TryParse(raw.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPort) && parsedPort > 0 && parsedPort <= 65535)
                {
                    port = parsedPort;
                    host = raw.Substring(0, colon);
                }
            }

            try
            {
                var nm = NetworkManager.instance;
                if (nm == null)
                {
                    message = "NetworkManager 未就绪。";
                    _lastJoinResult = message;
                    return false;
                }
                _lobbyProfileSent = false;
                bool ok = nm.StartClient(host, port);
                message = ok ? $"正在直连服务器 {host}:{port}" : $"直连 {host}:{port} 失败";
                _lastJoinResult = message;
                DropOutlinePlugin.L.LogInfo($"[AI联机] StartClient({host}:{port}) -> {ok}");
                return ok;
            }
            catch (Exception e)
            {
                message = "直连异常：" + e.Message;
                _lastJoinResult = message;
                return false;
            }
        }

        public static bool TryOpenHostRoom(int port, bool registerRoomCode, out string message)
        {
            message = "";
            if (DropOutlinePlugin.WorldTearingDown)
            {
                message = "场景切换中，请稍后再试。";
                return false;
            }
            if (port <= 0 || port > 65535) port = 7777;

            try
            {
                var nm = NetworkManager.instance;
                if (nm == null)
                {
                    message = "NetworkManager 未就绪，无法开房。";
                    _lastJoinResult = message;
                    return false;
                }

                if (NetworkManager.isHost)
                {
                    if (registerRoomCode)
                    {
                        try { NetCoordClient.instance?.BeginHostRegisterChecked(); } catch { }
                    }
                    string existingCode = NetLobby.roomCode ?? NetCoordClient.instance?.roomCode ?? NetCoordClient.instance?.LocalRoomCode ?? "";
                    message = string.IsNullOrEmpty(existingCode)
                        ? $"当前已是房主（端口 {nm.hostPort}）。"
                        : $"当前已开启联机房间，房间码：{existingCode}";
                    _lastJoinResult = message;
                    return true;
                }

                // 若玩家已经在游戏世界中：直接将当前世界切换为 Host 房间并注册房间码
                var gc = GameController.instance;
                if (gc != null && gc.playerCharacter != null)
                {
                    bool started = nm.StartHost(port);
                    if (started && registerRoomCode)
                    {
                        try { NetCoordClient.instance?.BeginHostRegisterChecked(); } catch { }
                    }
                    string code = NetCoordClient.instance?.roomCode ?? NetCoordClient.instance?.LocalRoomCode ?? NetLobby.roomCode ?? "";
                    message = started
                        ? (string.IsNullOrEmpty(code) ? $"已开启联机房主模式（端口 {port}），等待分配房间码" : $"已开启联机房间，房间码：{code}")
                        : "开启房主模式失败（请确认端口未被占用）";
                    _lastJoinResult = message;
                    DropOutlinePlugin.L.LogInfo($"[AI联机] In-world StartHost({port}) -> {started}, code={code}");
                    return started;
                }

                // 若在主菜单/大厅：打开多人准备大厅
                var hostChar = LoadOrCreateMpCharacter();
                bool opened = NetLobby.HostOpen(hostChar, 0, HordeRunMode.Standard);
                if (opened && registerRoomCode)
                {
                    try { NetCoordClient.instance?.BeginHostRegisterChecked(); } catch { }
                }
                string lobbyCode = NetLobby.roomCode ?? NetCoordClient.instance?.roomCode ?? "";
                message = opened
                    ? $"已在大厅创建联机房间{(string.IsNullOrEmpty(lobbyCode) ? "" : " 房间码:" + lobbyCode)}"
                    : "大厅开房失败";
                _lastJoinResult = message;
                DropOutlinePlugin.L.LogInfo($"[AI联机] Lobby HostOpen -> {opened}, code={lobbyCode}");
                return opened;
            }
            catch (Exception e)
            {
                message = "开房异常：" + e.Message;
                _lastJoinResult = message;
                DropOutlinePlugin.L.LogWarning($"[AI联机] TryOpenHostRoom 异常: {e.Message}");
                return false;
            }
        }

        public static bool TryLeaveRoom(out string message)
        {
            message = "";
            try
            {
                var nm = NetworkManager.instance;
                if (nm == null)
                {
                    message = "联机模块未启动。";
                    return false;
                }
                nm.Shutdown();
                _lobbyProfileSent = false;
                message = "已退出当前联机房间。";
                _lastJoinResult = message;
                DropOutlinePlugin.L.LogInfo("[AI联机] Shutdown()");
                return true;
            }
            catch (Exception e)
            {
                message = "退出房间失败：" + e.Message;
                _lastJoinResult = message;
                return false;
            }
        }

        public static void TickLobbyAutoReady()
        {
            if (DropOutlinePlugin.WorldTearingDown) return;
            float now = Time.unscaledTime;
            if (now < _nextClientReadyAt) return;
            _nextClientReadyAt = now + 1.2f;

            try
            {
                if (!NetLobby.inLobbyAsClient)
                {
                    _lobbyProfileSent = false;
                    return;
                }
                if (_lobbyProfileSent) return;

                var lobby = NetLobby.instance;
                var nm = NetworkManager.instance;
                if (lobby == null || nm == null || nm.localPlayerId <= 0) return;

                var chr = LoadOrCreateMpCharacter();
                if (chr == null) return;

                var npcName = AiCompanion.GetNpcName();
                if (string.IsNullOrEmpty(chr.characterName)) chr.characterName = npcName;

                NetLobby.SetLocalProfile(nm.localPlayerId, chr);
                lobby.SendLocalClientProfile();
                _lobbyProfileSent = true;
                _lastJoinResult = $"大厅角色已就绪：{chr.characterName}";
                DropOutlinePlugin.L.LogInfo($"[AI联机] 客户端大厅自动提交角色卡: {chr.characterName} (id={nm.localPlayerId})");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[AI联机] TickLobbyAutoReady 异常: {e.Message}");
            }
        }

        private static CharacterData LoadOrCreateMpCharacter()
        {
            return EnsureAiMpCharacterProfile(AiCompanion.GetNpcName());
        }

        private static string SanitizeProfileId(string name)
        {
            var sb = new StringBuilder();
            foreach (char ch in (name ?? "ai").Trim())
            {
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
            }
            if (sb.Length == 0) sb.Append("partner");
            return "zedai_" + sb.ToString().ToLowerInvariant();
        }

        /// <summary>在官方联机角色目录（MpCharacters）加载或创建 AI 专属玩家角色卡。</summary>
        private static CharacterData EnsureAiMpCharacterProfile(string aiName, int preferredCamp = 0)
        {
            string cleanName = string.IsNullOrWhiteSpace(aiName) ? "大肥鱼" : aiName.Trim();
            string profileId = SanitizeProfileId(cleanName);
            _aiMpProfileId = profileId;

            try
            {
                var existing = NetCharacterSave.LoadProfileCharacterForDisplay(profileId);
                if (existing != null)
                {
                    existing.characterName = cleanName;
                    existing.deadFlag = false;
                    if (preferredCamp >= 0) existing.camp = preferredCamp;
                    _aiPlayerCharData = existing;
                    return existing;
                }
            }
            catch { }

            try
            {
                CharacterData created = null;
                try { created = InGameController.instance?.GenerateRandomHumanCharacterData(); } catch { }
                if (created == null) created = CharacterData.InitBasicHumanCharacterData();
                if (created != null)
                {
                    created.characterName = cleanName;
                    created.characterId = "mp_" + profileId;
                    created.deadFlag = false;
                    if (preferredCamp >= 0) created.camp = preferredCamp;
                    _aiPlayerCharData = created;

                    try
                    {
                        var bytes = NetCharacterSave.SerializeCharacterForArchive(created);
                        if (bytes != null)
                        {
                            var env = new MpCharacterProfile
                            {
                                version = 1,
                                characterName = cleanName,
                                characterId = created.characterId,
                                savedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                origin = 0,
                                characterDataBytes = bytes
                            };
                            NetCharacterSave.WriteProfileFile(profileId, env);
                            DropOutlinePlugin.L.LogInfo($"[玩家模式] 已创建并保存 AI 联机角色卡：{profileId}（{cleanName}）");
                        }
                    }
                    catch { }
                    return created;
                }
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[玩家模式] 创建 AI 角色卡失败: {e.Message}");
            }

            try
            {
                var selected = NetCharacterSave.selectedProfile ?? "";
                if (!string.IsNullOrEmpty(selected))
                    return NetCharacterSave.LoadProfileCharacterForDisplay(selected);
            }
            catch { }
            return null;
        }

        private static bool IsValidController(HumanCharacterController ctrl)
        {
            try { return ctrl != null && ctrl.gameObject != null && ctrl.gameObject.activeInHierarchy && !ctrl.deadFlag; }
            catch { return false; }
        }

        private static int AllocateAiPlayerId(NetworkManager nm)
        {
            int candidate = 901;
            try
            {
                var members = nm?.members;
                if (members != null)
                {
                    var used = new HashSet<int>();
                    for (int i = 0; i < members.Count; i++)
                    {
                        var m = members[i];
                        if (m != null) used.Add(m.playerId);
                    }
                    while (used.Contains(candidate)) candidate++;
                }
            }
            catch { }
            return candidate;
        }

        // ─────────────────────────────────────────────
        // 2. AI 专属化身 NPC（大肥鱼）生成与队伍注册
        // ─────────────────────────────────────────────

        public static GOAPAgent GetAvatarAgent()
        {
            if (DropOutlinePlugin.WorldTearingDown) return null;
            if (IsValidAvatar(_avatarAgent)) return _avatarAgent;

            _avatarAgent = null;
            var mgr = GOAPNPCManager.instance;
            if (mgr == null || mgr.activeNPCs == null) return null;

            string wantName = AiCompanion.GetNpcName();
            try
            {
                if (!string.IsNullOrEmpty(_avatarGuid) && mgr.activeNPCs.ContainsKey(_avatarGuid))
                {
                    var cached = mgr.activeNPCs[_avatarGuid];
                    if (IsValidAvatar(cached))
                    {
                        _avatarAgent = cached;
                        return cached;
                    }
                }

                foreach (var kv in mgr.activeNPCs)
                {
                    var ag = kv.Value;
                    if (!IsValidAvatar(ag)) continue;
                    string n = "";
                    try { n = ag.npcData?.characterData?.characterName ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(n) && string.Equals(n.Trim(), wantName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        _avatarAgent = ag;
                        _avatarGuid = ag.npcData.npcGUID ?? kv.Key;
                        return ag;
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool IsValidAvatar(GOAPAgent ag)
        {
            try
            {
                if (ag == null || ag.gameObject == null || !ag.gameObject.activeInHierarchy) return false;
                if (ag.character == null || ag.character.deadFlag) return false;
                if (ag.npcData == null) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool EnsureOrFindAvatarNpc(bool forceSpawn, out string message)
        {
            message = "";
            if (DropOutlinePlugin.WorldTearingDown)
            {
                message = "场景切换中，暂无法生成 AI 玩家。";
                return false;
            }

            if (IsNetPlayerActive)
            {
                message = $"AI 玩家 {AiCompanion.GetNpcName()} 已在房间中（playerId={_aiNetPlayerId}）。";
                return true;
            }

            var existing = GetAvatarAgent();
            if (existing != null && !AiCompanion.IsPlayerMode())
            {
                SyncAvatarSquadRegistration(existing, GameController.instance?.playerCharacter);
                message = $"实体化身 {AiCompanion.GetNpcName()} 已在队伍中。";
                return true;
            }

            try
            {
                if (NetworkManager.instance != null && NetworkManager.isOnline && NetworkManager.isClient && !NetworkManager.isHost)
                {
                    message = "当前是联机客户端，请由房主生成远端 AI 玩家，或开启「客户端自动跟房主」。";
                    return false;
                }
            }
            catch { }

            if (!forceSpawn && !AiCompanion.CoopAutoAvatarEnabled)
            {
                message = "自动生成 AI 玩家已关闭。";
                return false;
            }

            float now = Time.unscaledTime;
            if (!forceSpawn && now < _nextSpawnAttemptAt)
                return false;
            _nextSpawnAttemptAt = now + 6f;

            var gc = GameController.instance;
            var player = gc != null ? gc.playerCharacter : null;
            if (gc == null || player == null || player.characterData == null)
            {
                message = "尚未进入可操控存档，无法生成 AI 玩家。";
                return false;
            }

            // 玩家模式：优先以正式联机玩家（MpCharacters 角色卡 + NetworkManager.members + NetPlayerSync）加入房间
            if (AiCompanion.IsPlayerMode())
            {
                bool onlineHost = false;
                try { onlineHost = NetworkManager.instance != null && NetworkManager.isOnline && NetworkManager.isHost; } catch { }
                if (!onlineHost && (forceSpawn || AiCompanion.PlayerAutoHostEnabled))
                {
                    if (TryOpenHostRoom(7777, false, out string hostMsg))
                    {
                        try { onlineHost = NetworkManager.instance != null && NetworkManager.isOnline && NetworkManager.isHost; } catch { }
                        DropOutlinePlugin.L.LogInfo($"[玩家模式] 自动切换为本地联机房主：{hostMsg}");
                    }
                }
                if (onlineHost && NetPlayerSync.instance != null)
                {
                    if (TrySpawnAsNetPlayer(player, out message))
                        return true;
                }
                // 如果已存在兜底化身且未开启联机，先沿用兜底化身
                if (existing != null)
                {
                    SyncAvatarSquadRegistration(existing, player);
                    message = $"AI 伙伴 {AiCompanion.GetNpcName()} 已在本地小队中（未开联机房）。";
                    return true;
                }
            }

            if (existing != null)
            {
                SyncAvatarSquadRegistration(existing, player);
                message = $"AI 化身 {AiCompanion.GetNpcName()} 已在队伍中。";
                return true;
            }

            var igc = InGameController.instance;
            var mgr = GOAPNPCManager.instance;
            if (igc == null || mgr == null)
            {
                message = "游戏 NPC 管理器尚未就绪，无法生成实体。";
                return false;
            }

            try
            {
                HumanCharacterController human = igc.GenerateNPC();
                if (human == null)
                {
                    var chr = igc.GenerateRandomHumanCharacterData() ?? CharacterData.InitBasicHumanCharacterData();
                    if (chr != null)
                    {
                        var data = new GOAPNPCData();
                        data.npcGUID = "zedai_" + Guid.NewGuid().ToString("N").Substring(0, 12);
                        data.characterData = chr;
                        human = igc.CreateHumanNPC(data);
                    }
                }

                if (human == null)
                {
                    message = "游戏生成实体实例失败。";
                    return false;
                }

                var ag = human.GetComponent<GOAPAgent>();
                if (ag == null || ag.npcData == null)
                {
                    message = "生成的实体缺少 GOAPAgent/npcData 组件。";
                    return false;
                }

                string wantName = AiCompanion.GetNpcName();
                if (human.characterData != null)
                {
                    human.characterData.characterName = wantName;
                    human.characterData.camp = player.characterData.camp;
                    human.characterData.deadFlag = false;
                }

                var npcData = ag.npcData;
                if (npcData.characterData != null)
                {
                    npcData.characterData.characterName = wantName;
                    npcData.characterData.camp = player.characterData.camp;
                }
                if (string.IsNullOrEmpty(npcData.npcGUID))
                    npcData.npcGUID = "zedai_" + Guid.NewGuid().ToString("N").Substring(0, 12);

                npcData.isBandit = false;
                npcData.hireAffinity = 100f;
                npcData.leaderCharacterGUID = player.characterData.characterId;
                npcData.basicCommand = "FollowLeader";

                Vector2 spawnPos = new Vector2(player.transform.position.x + 1.8f, player.transform.position.y - 0.8f);
                try { human.TeleportTo(spawnPos, false); } catch { human.transform.position = new Vector3(spawnPos.x, spawnPos.y, player.transform.position.z); }

                SyncAvatarSquadRegistration(ag, player);
                SyncAvatarToNetworkClients(human);

                _avatarAgent = ag;
                _avatarGuid = npcData.npcGUID;
                _lastCoopStatus = "spawned";
                message = $"已召唤 {wantName}（单机本地实体）并加入小队！";
                ShowAvatarBubble($"{player.characterData.characterName}，{wantName}来陪你玩啦！", 4.5f);
                DropOutlinePlugin.L.LogInfo($"[AI陪玩] 召唤本地实体成功 name={wantName} guid={_avatarGuid}");
                return true;
            }
            catch (Exception e)
            {
                message = "召唤 AI 实体失败：" + e.Message;
                DropOutlinePlugin.L.LogWarning($"[AI陪玩] EnsureOrFindAvatarNpc 异常: {e.Message}");
                return false;
            }
        }

        /// <summary>将 AI 作为正式联机玩家（MpCharacters + NetworkManager.members + NetPlayerSync）加入房间。</summary>
        private static bool TrySpawnAsNetPlayer(HumanCharacterController hostPlayer, out string message)
        {
            message = "";
            try
            {
                var nm = NetworkManager.instance;
                var nps = NetPlayerSync.instance;
                if (nm == null || nps == null || !NetworkManager.isHost)
                {
                    message = "联机房主模块未就绪。";
                    return false;
                }

                string wantName = AiCompanion.GetNpcName();
                int pid = _aiNetPlayerId > 0 ? _aiNetPlayerId : AllocateAiPlayerId(nm);
                int camp = hostPlayer.characterData != null ? hostPlayer.characterData.camp : 0;
                var charData = EnsureAiMpCharacterProfile(wantName, camp);
                if (charData == null)
                {
                    message = "初始化 AI 联机角色卡失败。";
                    return false;
                }
                charData.zLevel = hostPlayer.characterData != null ? hostPlayer.characterData.zLevel : (sbyte)0;

                // 1) 注册到房间成员列表并通知全房
                NetMemberInfo mem = null;
                if (nm.members != null)
                {
                    for (int i = 0; i < nm.members.Count; i++)
                    {
                        if (nm.members[i] != null && nm.members[i].playerId == pid)
                        {
                            mem = nm.members[i];
                            mem.playerName = wantName;
                            break;
                        }
                    }
                    if (mem == null)
                    {
                        mem = new NetMemberInfo { playerId = pid, playerName = wantName };
                        nm.members.Add(mem);
                        try { nm.BroadcastToJoined(new MsgMemberUpdate { joined = true, member = mem }, null, DeliveryMethod.ReliableOrdered, 0); } catch { }
                    }
                    try { nm.NotifyMembersChanged(); } catch { }
                }
                try { NetLobby.SetLocalProfile(pid, charData); } catch { }

                // 2) 生成官方联机玩家实体（RemotePlayer）
                Vector2 spawnPos = (Vector2)hostPlayer.transform.position + new Vector2(1.8f, -0.8f);
                nps.SpawnRemotePlayer(pid, wantName, charData, spawnPos);
                if (!nps.TryGetRemoteController(pid, out var remoteCtrl) || remoteCtrl == null)
                {
                    message = "NetPlayerSync 生成联机玩家失败。";
                    return false;
                }

                if (remoteCtrl.characterData != null)
                {
                    remoteCtrl.characterData.camp = camp;
                    remoteCtrl.characterData.deadFlag = false;
                    remoteCtrl.characterData.zLevel = charData.zLevel;
                }

                _aiNetPlayerId = pid;
                _netPlayerCtrl = remoteCtrl;
                _aiPlayerCharData = charData;
                _netPos = spawnPos;
                _netVel = Vector2.zero;
                _netLook = Vector2.right;
                _netLastFrameTime = Time.unscaledTime;
                _spawnSentPeers.Clear();
                _lastHoldItemId = int.MinValue;
                _lastCoopStatus = "net_player_active";

                PushNetPlayerSnapshot(remoteCtrl, Vector2.zero, _netLook, false);
                SyncNetPlayerToPeers(forceAll: true);

                message = $"已创建 AI 联机角色「{wantName}」(玩家ID:{pid}) 并以正常联机玩家身份加入房间！";
                ShowAvatarBubble($"{hostPlayer.characterData?.characterName ?? "队长"}，{wantName}已上线进房！", 4.5f);
                DropOutlinePlugin.L.LogInfo($"[玩家模式] {message}");
                return true;
            }
            catch (Exception e)
            {
                message = "生成联机 AI 玩家失败：" + e.Message;
                DropOutlinePlugin.L.LogWarning($"[玩家模式] TrySpawnAsNetPlayer 异常: {e.Message}");
                return false;
            }
        }

        private static void SyncAvatarSquadRegistration(GOAPAgent ag, HumanCharacterController player)
        {
            if (ag == null || ag.npcData == null || player == null || player.characterData == null) return;
            var npcData = ag.npcData;
            var mgr = GOAPNPCManager.instance;
            var pData = player.characterData;

            try
            {
                string wantName = AiCompanion.GetNpcName();
                if (npcData.characterData != null && !string.Equals(npcData.characterData.characterName, wantName, StringComparison.Ordinal))
                    npcData.characterData.characterName = wantName;
                if (npcData.characterData != null)
                    npcData.characterData.camp = pData.camp;
                npcData.isBandit = false;
                npcData.hireAffinity = 100f;
                npcData.leaderCharacterGUID = pData.characterId;

                if (mgr != null)
                {
                    if (mgr.activeNPCs != null && !string.IsNullOrEmpty(npcData.npcGUID) && !mgr.activeNPCs.ContainsKey(npcData.npcGUID))
                        mgr.AddNPC(npcData);
                }

                if (!GOAPNPCManager.IsPlayerTeammateData(npcData, pData))
                    pData.AddTeamNPC(npcData);

                if (ag.leaderCharacter != player)
                {
                    ag.SetLeader(player);
                    ag.OnJoinedPlayerTeam();
                    ag.EnableWithLeaderGoals();
                }

                try { pData.SyncTeamUI(); } catch { }
                try { TeamNPCPanel.instance?.AddNPC(npcData); } catch { }
                try { TeamNPCOverheadUI.NotifyAdd(npcData); } catch { }
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[AI陪玩] SyncAvatarSquadRegistration 警告: {e.Message}");
            }
        }

        private static void SyncAvatarToNetworkClients(HumanCharacterController human)
        {
            if (human == null) return;
            try
            {
                if (NetworkManager.instance == null || !NetworkManager.isOnline || !NetworkManager.isHost) return;
                var nms = NetMonsterSync.instance;
                if (nms != null)
                {
                    nms.HostResendSpawn(human);
                    nms.HostSendEquipment(human);
                }
            }
            catch { }
        }

        public static bool DespawnAvatar(out string message)
        {
            message = "";
            bool removedAny = false;
            try
            {
                if (_aiNetPlayerId > 0)
                {
                    int pid = _aiNetPlayerId;
                    try { NetPlayerSync.instance?.Despawn(pid); } catch { }
                    try
                    {
                        var nm = NetworkManager.instance;
                        if (nm != null && nm.members != null)
                        {
                            NetMemberInfo removed = null;
                            for (int i = nm.members.Count - 1; i >= 0; i--)
                            {
                                var m = nm.members[i];
                                if (m != null && m.playerId == pid)
                                {
                                    removed = m;
                                    nm.members.RemoveAt(i);
                                }
                            }
                            if (removed != null)
                            {
                                if (NetworkManager.isHost)
                                {
                                    try { nm.BroadcastToJoined(new MsgPlayerDespawn { playerId = pid }, null, DeliveryMethod.ReliableOrdered, 0); } catch { }
                                    try { nm.BroadcastToJoined(new MsgMemberUpdate { joined = false, member = removed }, null, DeliveryMethod.ReliableOrdered, 0); } catch { }
                                }
                                try { nm.NotifyMembersChanged(); } catch { }
                            }
                        }
                    }
                    catch { }
                    _aiNetPlayerId = 0;
                    _netPlayerCtrl = null;
                    _spawnSentPeers.Clear();
                    removedAny = true;
                }

                var ag = GetAvatarAgent();
                if (ag != null)
                {
                    string guid = ag.npcData?.npcGUID ?? _avatarGuid;
                    var player = GameController.instance?.playerCharacter;
                    if (player != null && player.characterData != null && ag.npcData != null)
                        player.characterData.RemoveTeamNPC(ag.npcData);
                    if (!string.IsNullOrEmpty(guid))
                        GOAPNPCManager.instance?.DespawnNPCInstance(guid);
                    _avatarAgent = null;
                    _avatarGuid = "";
                    removedAny = true;
                }

                _lastCoopStatus = "despawned";
                message = removedAny ? "已让 AI 玩家/化身离队退房。" : "当前没有在场的 AI 玩家或化身。";
                return true;
            }
            catch (Exception e)
            {
                message = "收回 AI 角色失败：" + e.Message;
                return false;
            }
        }

        public static bool ForceTeleportToPlayer(out string message)
        {
            message = "";
            var player = GameController.instance?.playerCharacter;
            if (player == null)
            {
                message = "玩家角色不在场景中。";
                return false;
            }

            if (IsNetPlayerActive)
            {
                try
                {
                    Vector2 target = new Vector2(player.transform.position.x + 1.6f, player.transform.position.y - 0.6f);
                    _netPos = target;
                    _netVel = Vector2.zero;
                    if (_netPlayerCtrl.characterData != null && player.characterData != null)
                        _netPlayerCtrl.characterData.zLevel = player.characterData.zLevel;
                    _netPlayerCtrl.TeleportTo(target, false);
                    PushNetPlayerSnapshot(_netPlayerCtrl, Vector2.zero, _netLook, false);
                    ShowAvatarBubble("我过来了！", 3f);
                    message = $"已将 AI 玩家 {AiCompanion.GetNpcName()} 传送到你身边。";
                    return true;
                }
                catch (Exception e)
                {
                    message = "传送 AI 玩家失败：" + e.Message;
                    return false;
                }
            }

            var ag = GetAvatarAgent();
            if (ag == null || ag.character == null)
            {
                if (!EnsureOrFindAvatarNpc(true, out string spawnMsg))
                {
                    message = spawnMsg;
                    return false;
                }
                if (IsNetPlayerActive)
                    return ForceTeleportToPlayer(out message);
                ag = GetAvatarAgent();
                if (ag == null || ag.character == null)
                {
                    message = "无法定位 AI 玩家位置。";
                    return false;
                }
            }

            try
            {
                Vector2 target = new Vector2(player.transform.position.x + 1.6f, player.transform.position.y - 0.6f);
                ag.character.TeleportTo(target, false);
                SyncAvatarToNetworkClients(ag.humanCharacter);
                ShowAvatarBubble("到你身边啦！", 3f);
                message = $"已将 {AiCompanion.GetNpcName()} 传送到你身边。";
                return true;
            }
            catch (Exception e)
            {
                message = "传送失败：" + e.Message;
                return false;
            }
        }

        public static void ShowAvatarBubble(string text, float duration = 4.5f)
        {
            if (DropOutlinePlugin.WorldTearingDown || string.IsNullOrEmpty(text)) return;
            try
            {
                if (IsNetPlayerActive)
                {
                    _netPlayerCtrl.ShowDialogueBubble(text, duration);
                    return;
                }
                var ag = GetAvatarAgent();
                var h = ag?.humanCharacter ?? ag?.character as HumanCharacterController;
                if (h != null && !h.deadFlag)
                    h.ShowDialogueBubble(text, duration);
            }
            catch { }
        }

        public static string GetAvatarSummary()
        {
            if (IsNetPlayerActive)
            {
                try
                {
                    float dist = -1f;
                    var player = GameController.instance?.playerCharacter;
                    if (player != null)
                        dist = Vector2.Distance(_netPlayerCtrl.transform.position, player.transform.position);
                    var st = _netPlayerCtrl.characterData?.characterStatusData;
                    int pct = st != null && st.maxhp > 0f ? Mathf.Clamp(Mathf.RoundToInt(st.hp * 100f / st.maxhp), 0, 100) : 100;
                    string distTxt = dist >= 0f ? $" 距{dist:F0}m" : "";
                    return $"{AiCompanion.GetNpcName()}[联机玩家ID:{_aiNetPlayerId}] · {_behaviorMode} · HP {pct}%{distTxt}";
                }
                catch
                {
                    return $"{AiCompanion.GetNpcName()}[联机玩家ID:{_aiNetPlayerId}] · {_behaviorMode}";
                }
            }

            var ag = GetAvatarAgent();
            if (ag == null || ag.npcData == null || ag.character == null)
            {
                if (NetworkManager.instance != null && NetworkManager.isOnline && NetworkManager.isClient && !NetworkManager.isHost)
                    return AiCompanion.CoopDriveLocalWhenClient ? $"客户端自主协同 ({_behaviorMode})" : "联机客户端等待中";
                return "AI 玩家未上线";
            }

            try
            {
                float dist = -1f;
                var player = GameController.instance?.playerCharacter;
                if (player != null)
                    dist = Vector2.Distance(ag.character.transform.position, player.transform.position);
                float hp = ag.npcData.health;
                float maxHp = Math.Max(1f, ag.npcData.maxHealth);
                int pct = Mathf.Clamp(Mathf.RoundToInt(hp * 100f / maxHp), 0, 100);
                string distTxt = dist >= 0f ? $" 距{dist:F0}m" : "";
                return $"{AiCompanion.GetNpcName()} · {_behaviorMode} · HP {pct}%{distTxt}";
            }
            catch
            {
                return $"{AiCompanion.GetNpcName()} · {_behaviorMode}";
            }
        }

        public static string GetAvatarJson()
        {
            if (DropOutlinePlugin.WorldTearingDown)
                return "{\"present\":false,\"isNetPlayer\":false,\"netPlayerId\":0,\"name\":\"\",\"guid\":\"\",\"behavior\":\"follow\",\"hpRatio\":0,\"distToPlayer\":-1,\"inCombat\":false,\"goal\":\"\",\"flashlight\":false,\"status\":\"teardown\"}";

            if (IsNetPlayerActive)
            {
                float hpRatio = 1f, dist = -1f;
                bool lightOn = false;
                string name = AiCompanion.GetNpcName();
                try
                {
                    var st = _netPlayerCtrl.characterData?.characterStatusData;
                    if (st != null && st.maxhp > 0f) hpRatio = Mathf.Clamp01(st.hp / st.maxhp);
                }
                catch { }
                try
                {
                    var player = GameController.instance?.playerCharacter;
                    if (player != null) dist = Vector2.Distance(_netPlayerCtrl.transform.position, player.transform.position);
                }
                catch { }
                try { lightOn = _netPlayerCtrl.isFlashLightOn; } catch { }
                try { name = _netPlayerCtrl.characterData?.characterName ?? name; } catch { }
                var threat = FindNearestHostile(_netPlayerCtrl.transform.position, _netPlayerCtrl.characterData, 16f);

                var sbNet = new StringBuilder(256);
                sbNet.Append('{');
                sbNet.Append("\"present\":true,\"isNetPlayer\":true,\"netPlayerId\":").Append(_aiNetPlayerId);
                sbNet.Append(",\"name\":\"").Append(MiniJson.Escape(name)).Append('"');
                sbNet.Append(",\"guid\":\"").Append(MiniJson.Escape(_aiPlayerCharData?.characterId ?? "")).Append('"');
                sbNet.Append(",\"behavior\":\"").Append(MiniJson.Escape(_behaviorMode)).Append('"');
                sbNet.Append(",\"hpRatio\":").Append(hpRatio.ToString("F2", CultureInfo.InvariantCulture));
                sbNet.Append(",\"distToPlayer\":").Append(dist.ToString("F1", CultureInfo.InvariantCulture));
                sbNet.Append(",\"inCombat\":").Append(threat != null ? "true" : "false");
                sbNet.Append(",\"goal\":\"player_").Append(MiniJson.Escape(_behaviorMode)).Append('"');
                sbNet.Append(",\"flashlight\":").Append(lightOn ? "true" : "false");
                sbNet.Append(",\"status\":\"").Append(MiniJson.Escape(_lastCoopStatus ?? "net_player_active")).Append('"');
                sbNet.Append('}');
                return sbNet.ToString();
            }

            var ag = GetAvatarAgent();
            if (ag == null || ag.npcData == null || ag.character == null)
            {
                var sb0 = new StringBuilder(176);
                sb0.Append("{\"present\":false,\"isNetPlayer\":false,\"netPlayerId\":0,\"name\":\"").Append(MiniJson.Escape(AiCompanion.GetNpcName())).Append('"');
                sb0.Append(",\"guid\":\"\",\"behavior\":\"").Append(MiniJson.Escape(_behaviorMode)).Append('"');
                sb0.Append(",\"hpRatio\":0,\"distToPlayer\":-1,\"inCombat\":false,\"goal\":\"\",\"flashlight\":false");
                sb0.Append(",\"status\":\"").Append(MiniJson.Escape(_lastCoopStatus ?? "idle")).Append("\"}");
                return sb0.ToString();
            }

            float hpR = 1f, d = -1f;
            bool inCombat = false, lOn = false;
            string goal = "", n = AiCompanion.GetNpcName();
            try { hpR = ag.npcData.maxHealth > 0f ? Mathf.Clamp01(ag.npcData.health / ag.npcData.maxHealth) : 1f; } catch { }
            try
            {
                var player = GameController.instance?.playerCharacter;
                if (player != null) d = Vector2.Distance(ag.character.transform.position, player.transform.position);
            }
            catch { }
            try { inCombat = ag.mostThreateningCharacter != null; } catch { }
            try { goal = ag.GetCurrentGoal()?.goalName ?? ""; } catch { }
            try { lOn = ag.humanCharacter != null && ag.humanCharacter.isFlashLightOn; } catch { }
            try { n = ag.npcData.characterData?.characterName ?? n; } catch { }

            var sb = new StringBuilder(240);
            sb.Append('{');
            sb.Append("\"present\":true,\"isNetPlayer\":false,\"netPlayerId\":0");
            sb.Append(",\"name\":\"").Append(MiniJson.Escape(n)).Append('"');
            sb.Append(",\"guid\":\"").Append(MiniJson.Escape(ag.npcData.npcGUID ?? "")).Append('"');
            sb.Append(",\"behavior\":\"").Append(MiniJson.Escape(_behaviorMode)).Append('"');
            sb.Append(",\"hpRatio\":").Append(hpR.ToString("F2", CultureInfo.InvariantCulture));
            sb.Append(",\"distToPlayer\":").Append(d.ToString("F1", CultureInfo.InvariantCulture));
            sb.Append(",\"inCombat\":").Append(inCombat ? "true" : "false");
            sb.Append(",\"goal\":\"").Append(MiniJson.Escape(goal)).Append('"');
            sb.Append(",\"flashlight\":").Append(lOn ? "true" : "false");
            sb.Append(",\"status\":\"").Append(MiniJson.Escape(_lastCoopStatus ?? "active")).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        // ─────────────────────────────────────────────
        // 3. 持续陪玩主循环
        // ─────────────────────────────────────────────

        public static void Tick()
        {
            if (DropOutlinePlugin.WorldTearingDown) return;
            if (!AiCompanion.IsPlayerMode() && !AiCompanion.IsDshMode) return;

            TickLobbyAutoReady();

            var gc = GameController.instance;
            var player = gc != null ? gc.playerCharacter : null;
            if (player == null || player.deadFlag) return;

            // 仅在表现形式为「玩家模式」时，自动维护/加入 AI 玩家实体
            bool isClient = false;
            try { isClient = NetworkManager.instance != null && NetworkManager.isOnline && NetworkManager.isClient && !NetworkManager.isHost; } catch { }
            if (isClient)
            {
                if (AiCompanion.CoopDriveLocalWhenClient || AiCompanion.IsPlayerMode())
                    TickClientFollowRemote(player);
                return;
            }

            if (AiCompanion.IsPlayerMode() && AiCompanion.CoopAutoAvatarEnabled && !IsNetPlayerActive && GetAvatarAgent() == null)
                EnsureOrFindAvatarNpc(false, out _);

            if (IsNetPlayerActive)
            {
                TickNetPlayer(player);
                return;
            }

            var ag = GetAvatarAgent();
            if (ag == null || ag.npcData == null || ag.character == null) return;

            float now = Time.unscaledTime;

            // 确保队伍关系稳固（例如切图、进出建筑或加载存档后）
            if (ag.leaderCharacter != player)
                SyncAvatarSquadRegistration(ag, player);

            // 与玩家同步手电筒
            if (ag.humanCharacter != null)
            {
                try
                {
                    if (player.isFlashLightOn && !ag.humanCharacter.isFlashLightOn)
                        ag.humanCharacter.TurnOnLight();
                    else if (!player.isFlashLightOn && ag.humanCharacter.isFlashLightOn)
                        ag.humanCharacter.TurnOffLight();
                }
                catch { }
            }

            // 定期同步给联机客户端，避免远端玩家看不到 AI 队友
            if (now >= _nextNetworkSyncAt && NetworkManager.instance != null && NetworkManager.isOnline && NetworkManager.isHost)
            {
                _nextNetworkSyncAt = now + 8f;
                SyncAvatarToNetworkClients(ag.humanCharacter);
            }

            // 行为调度
            if (now >= _nextFollowTickAt)
            {
                _nextFollowTickAt = now + 0.65f;
                TickAvatarMovement(ag, player);
            }

            if (now >= _nextCombatTickAt)
            {
                _nextCombatTickAt = now + 0.45f;
                TickAvatarCombatAndCare(ag);
            }
        }

        private static void TickAvatarMovement(GOAPAgent ag, HumanCharacterController player)
        {
            try
            {
                Vector2 myPos = ag.character.transform.position;
                Vector2 targetPos;

                if (_behaviorMode == "guard" && _hasGuardPos)
                {
                    targetPos = _guardPos;
                    ag.npcData.basicCommand = "StayAtPosition";
                    ag.npcData.npcStayPosition = new Vector3(_guardPos.x, _guardPos.y, player.transform.position.z);
                    float guardDist = Vector2.Distance(myPos, targetPos);
                    if (guardDist > 3.0f)
                        ag.MoveTo_PathFinding(targetPos, true);
                    _lastCoopStatus = "guarding";
                    return;
                }

                ag.npcData.basicCommand = "FollowLeader";
                Vector2 playerPos = player.transform.position;
                float dist = Vector2.Distance(myPos, playerPos);

                // 距离太远时自动传送跟上，防止卡地形掉队
                float tpThreshold = _behaviorMode == "scavenge" ? 42f : 30f;
                if (dist > tpThreshold)
                {
                    Vector2 behind = playerPos + new Vector2(1.6f, -0.8f);
                    ag.character.TeleportTo(behind, false);
                    SyncAvatarToNetworkClients(ag.humanCharacter);
                    _lastCoopStatus = "teleported_catchup";
                    return;
                }

                // 战斗模式若有明确威胁且不算过远，优先让 GOAP 交战
                BasicCharacterController threat = null;
                try { threat = ag.mostThreateningCharacter ?? ag.GetMostThreatCharacter(); } catch { }
                if (threat != null && !threat.deadFlag && dist < (_behaviorMode == "combat" ? 20f : 12f))
                {
                    _lastCoopStatus = "engaging";
                    return;
                }

                float stopDist = _behaviorMode == "scavenge" ? 7.5f : 4.2f;
                if (dist > stopDist)
                {
                    Vector2 sideOffset = playerPos + new Vector2(-1.5f, -0.9f);
                    ag.MoveTo_PathFinding(sideOffset, true);
                    _lastCoopStatus = "following";
                }
                else
                {
                    _lastCoopStatus = "near_player";
                }
            }
            catch { }
        }

        private static void TickAvatarCombatAndCare(GOAPAgent ag)
        {
            try
            {
                if (ag.npcData.maxHealth > 0f && ag.npcData.health < ag.npcData.maxHealth * 0.45f)
                {
                    try { ag.humanCharacter?.HealAllBodyPartInjuries(); } catch { }
                }
                ag.CheckAndReloadMagazine();

                var threat = ag.mostThreateningCharacter ?? ag.GetMostThreatCharacter();
                if (threat != null && !threat.deadFlag)
                {
                    ag.LookAtPositionSmoothCompat(threat.transform.position, 14f);
                }
            }
            catch { }
        }

        private static void TickClientFollowRemote(HumanCharacterController localPlayer)
        {
            float now = Time.unscaledTime;
            if (now < _nextNetDriveAt) return;
            _nextNetDriveAt = now + 0.15f;

            try
            {
                // 若本地用户正在手动 WASD 操作，则立刻让出控制权
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D))
                    return;

                var nps = NetPlayerSync.instance;
                if (nps == null || NetPlayerSync.remotePlayerCount <= 0) return;

                Vector2 myPos = localPlayer.transform.position;
                sbyte z = localPlayer.characterData != null ? localPlayer.characterData.zLevel : (sbyte)0;
                if (!NetPlayerSync.TryGetNearestRemotePlayer(myPos, z, out Vector2 nearest, out float sqrDist))
                    return;

                float dist = Mathf.Sqrt(Mathf.Max(0f, sqrDist));
                if (dist > 34f)
                {
                    localPlayer.TeleportTo(nearest + new Vector2(1.4f, -0.6f), false);
                    localPlayer.movementInput = Vector2.zero;
                    _lastCoopStatus = "client_tp_catchup";
                }
                else if (dist > 4.2f && _behaviorMode != "guard")
                {
                    Vector2 dir = (nearest - myPos).normalized;
                    localPlayer.movementInput = dir;
                    localPlayer.LookAtPositionSmooth(nearest, 10f);
                    _lastCoopStatus = "client_following";
                }
                else
                {
                    localPlayer.movementInput = Vector2.zero;
                    _lastCoopStatus = "client_synced";
                }
            }
            catch { }
        }

        private static void TickNetPlayer(HumanCharacterController hostPlayer)
        {
            var ctrl = _netPlayerCtrl;
            if (!IsValidController(ctrl) || hostPlayer == null) return;

            float now = Time.unscaledTime;
            float dt = _netLastFrameTime > 0f ? Mathf.Clamp(now - _netLastFrameTime, 0.01f, 0.2f) : 0.05f;
            _netLastFrameTime = now;

            try
            {
                Vector2 hostPos = hostPlayer.transform.position;
                Vector2 myPos = _netPos;
                if (Vector2.Distance(myPos, ctrl.transform.position) > 6f)
                    myPos = ctrl.transform.position;

                // 跨楼层或掉队太远自动瞬移跟上
                sbyte hostZ = hostPlayer.characterData != null ? hostPlayer.characterData.zLevel : (sbyte)0;
                if (ctrl.characterData != null && ctrl.characterData.zLevel != hostZ)
                    ctrl.characterData.zLevel = hostZ;

                float tpThreshold = _behaviorMode == "scavenge" ? 42f : 30f;
                if (_behaviorMode != "guard" && Vector2.Distance(myPos, hostPos) > tpThreshold)
                {
                    Vector2 behind = hostPos + new Vector2(1.6f, -0.8f);
                    myPos = behind;
                    _netPos = behind;
                    ctrl.TeleportTo(behind, false);
                    _lastCoopStatus = "net_teleported_catchup";
                }

                // 手电同步
                try
                {
                    if (hostPlayer.isFlashLightOn && !ctrl.isFlashLightOn) ctrl.TurnOnLight();
                    else if (!hostPlayer.isFlashLightOn && ctrl.isFlashLightOn) ctrl.TurnOffLight();
                }
                catch { }

                // 低血自愈
                try
                {
                    var st = ctrl.characterData?.characterStatusData;
                    if (st != null && st.maxhp > 0f && st.hp < st.maxhp * 0.45f && now >= _nextNetHealAt)
                    {
                        _nextNetHealAt = now + 8f;
                        ctrl.HealAllBodyPartInjuries();
                        st.hp = Math.Min(st.maxhp, st.hp + st.maxhp * 0.35f);
                    }
                }
                catch { }

                // 索敌与战斗
                BasicCharacterController threat = FindNearestHostile(myPos, ctrl.characterData, _behaviorMode == "combat" ? 18f : 14f);
                bool running = false;
                Vector2 moveDir = Vector2.zero;

                if (threat != null)
                {
                    Vector2 threatPos = threat.transform.position;
                    Vector2 toThreat = threatPos - myPos;
                    float td = toThreat.magnitude;
                    if (td > 0.05f) _netLook = toThreat / td;

                    bool hasRanged = false;
                    try { hasRanged = ctrl.rangedWeapon != null; } catch { }

                    if (hasRanged)
                    {
                        if (td < 4.5f) moveDir = -_netLook;
                        else if (td > 10.5f && _behaviorMode == "combat") moveDir = _netLook;
                    }
                    else
                    {
                        if (td > 1.5f) moveDir = _netLook;
                        if (td > 4.5f) running = true;
                    }

                    if (now >= _nextNetFireAt)
                    {
                        try
                        {
                            ctrl.LookAtPosition(threatPos, true);
                            if (hasRanged && td <= 15.5f)
                            {
                                _nextNetFireAt = now + 0.38f;
                                ctrl.Fire(threat);
                            }
                            else if (td <= 2.3f)
                            {
                                _nextNetFireAt = now + 0.55f;
                                ctrl.MeleeAttack(threat);
                            }
                        }
                        catch { }
                    }
                    _lastCoopStatus = "net_engaging";
                }
                else
                {
                    Vector2 anchor = (_behaviorMode == "guard" && _hasGuardPos)
                        ? _guardPos
                        : (hostPos + new Vector2(-1.6f, -0.8f));
                    Vector2 toAnchor = anchor - myPos;
                    float ad = toAnchor.magnitude;
                    float stopRadius = _behaviorMode == "guard" ? 1.5f : (_behaviorMode == "scavenge" ? 5.5f : 2.6f);
                    if (ad > stopRadius)
                    {
                        moveDir = toAnchor / ad;
                        _netLook = moveDir;
                        running = ad > 6.5f;
                        _lastCoopStatus = _behaviorMode == "guard" ? "net_guarding" : "net_following";
                    }
                    else
                    {
                        _lastCoopStatus = _behaviorMode == "guard" ? "net_guarding" : "net_synced";
                    }
                }

                float moveSpeed = moveDir.sqrMagnitude > 0.01f ? (running ? 5.2f : 3.2f) : 0f;
                _netVel = moveDir * moveSpeed;
                _netPos = myPos + _netVel * dt;

                try { ctrl.LookAtPosition(_netPos + _netLook * 3f, true); } catch { }

                if (now >= _nextNetPushAt)
                {
                    _nextNetPushAt = now + 0.05f;
                    PushNetPlayerSnapshot(ctrl, _netVel, _netLook, running);
                }
                if (now >= _nextNetworkSyncAt)
                {
                    _nextNetworkSyncAt = now + 3f;
                    SyncNetPlayerToPeers(forceAll: false);
                }
            }
            catch { }
        }

        private static BasicCharacterController FindNearestHostile(Vector2 origin, CharacterData selfData, float maxDist)
        {
            BasicCharacterController best = null;
            float bestDist = maxDist;
            try
            {
                var gc = GameController.instance;
                if (gc == null || gc.characterList == null) return null;
                int selfCamp = selfData != null ? selfData.camp : 0;
                sbyte selfZ = selfData != null ? selfData.zLevel : (sbyte)0;
                for (int i = 0; i < gc.characterList.Count; i++)
                {
                    var c = gc.characterList[i];
                    if (c == null || c.characterData == null || c.deadFlag) continue;
                    if (c.characterData.camp == selfCamp) continue;
                    if (c.characterData.zLevel != selfZ) continue;
                    if (c == gc.playerCharacter || c == _netPlayerCtrl) continue;
                    float d = Vector2.Distance(origin, c.transform.position);
                    if (d < bestDist) { bestDist = d; best = c; }
                }
            }
            catch { }
            return best;
        }

        private static void PushNetPlayerSnapshot(HumanCharacterController ctrl, Vector2 vel, Vector2 look, bool running)
        {
            try
            {
                var nps = NetPlayerSync.instance;
                if (nps == null || _aiNetPlayerId <= 0 || ctrl == null) return;

                float hp = 100f;
                sbyte z = 0;
                try
                {
                    var st = ctrl.characterData?.characterStatusData;
                    if (st != null) hp = st.hp;
                    if (ctrl.characterData != null) z = ctrl.characterData.zLevel;
                }
                catch { }

                int flags = 0;
                try
                {
                    if (ctrl.isFlashLightOn) flags |= NetPlayerSync.flagFlashlight;
                    if (ctrl.reloadingMagazineFlag) flags |= NetPlayerSync.flagReloading;
                    if (running) flags |= NetPlayerSync.flagRunning;
                }
                catch { }

                var msg = new MsgPlayerState
                {
                    playerId = _aiNetPlayerId,
                    posX = _netPos.x,
                    posY = _netPos.y,
                    moveX = vel.x,
                    moveY = vel.y,
                    lookX = look.x,
                    lookY = look.y,
                    hp = hp,
                    speed = vel.magnitude,
                    stateFlags = flags,
                    zLevel = z,
                    tick = ++_netTick,
                };
                try { msg.Pack(); } catch { }
                try { nps.PushSnap(_aiNetPlayerId, msg); } catch { }

                try
                {
                    if (NetworkManager.isHost)
                    {
                        nps.ApplyPuppetHp(_aiNetPlayerId, hp);
                        int holdId = ctrl.characterData?.characterEquipmentData?.currentWeaponItemData?.itemId ?? 0;
                        if (holdId != _lastHoldItemId)
                        {
                            _lastHoldItemId = holdId;
                            nps.ApplyPuppetHoldItem(_aiNetPlayerId, holdId);
                        }
                        if (nps.m_latestStates != null) nps.m_latestStates[_aiNetPlayerId] = msg;
                        if (nps.m_dirtyStates != null) nps.m_dirtyStates.Add(_aiNetPlayerId);
                        if (nps.m_stateReceivedAt != null) nps.m_stateReceivedAt[_aiNetPlayerId] = Time.unscaledTime;
                    }
                }
                catch { }
            }
            catch { }
        }

        private static void SyncNetPlayerToPeers(bool forceAll)
        {
            try
            {
                var nm = NetworkManager.instance;
                var nps = NetPlayerSync.instance;
                if (nm == null || nps == null || !NetworkManager.isHost || _aiNetPlayerId <= 0 || _aiPlayerCharData == null) return;
                var members = nm.members;
                if (members == null) return;
                string aiName = AiCompanion.GetNpcName();
                for (int i = 0; i < members.Count; i++)
                {
                    var m = members[i];
                    if (m == null || m.playerId == 0 || m.playerId == _aiNetPlayerId || m.playerId == nm.localPlayerId) continue;
                    if (!forceAll && _spawnSentPeers.Contains(m.playerId)) continue;
                    var peer = nm.GetPeerByPlayerId(m.playerId);
                    if (peer == null) continue;
                    nps.SendSpawnTo(peer, _aiNetPlayerId, aiName, _aiPlayerCharData, _netPos);
                    _spawnSentPeers.Add(m.playerId);
                }
            }
            catch { }
        }

        // ─────────────────────────────────────────────
        // 4. 命令分发（DSH command / 游戏内对话 / UI）
        // ─────────────────────────────────────────────

        public static bool ExecuteCoopOp(string op, string arg, out string message)
        {
            message = "";
            if (!AiCompanion.IsPlayerMode() && !AiCompanion.IsDshMode)
            {
                message = "请先在设置中将「表现形式」切换为玩家模式（或使用 DSH 模式）。";
                return false;
            }

            var verb = (op ?? "").Trim().ToLowerInvariant();
            switch (verb)
            {
                case "spawn":
                case "summon":
                case "join_squad":
                    return EnsureOrFindAvatarNpc(true, out message);

                case "despawn":
                case "dismiss":
                case "leave_squad":
                    return DespawnAvatar(out message);

                case "tp":
                case "come":
                case "teleport":
                    return ForceTeleportToPlayer(out message);

                case "follow":
                    SetBehaviorMode("follow");
                    EnsureOrFindAvatarNpc(true, out _);
                    ShowAvatarBubble("好嘞，我紧跟着你！", 3f);
                    message = "AI 玩家已切换为紧跟模式。";
                    return true;

                case "combat":
                case "attack":
                case "fight":
                case "cover":
                    SetBehaviorMode("combat");
                    EnsureOrFindAvatarNpc(true, out _);
                    ShowAvatarBubble("交给我来掩护开火！", 3f);
                    message = "AI 玩家已切换为积极迎战模式。";
                    return true;

                case "guard":
                case "stay":
                case "hold":
                    SetBehaviorMode("guard");
                    ShowAvatarBubble("收到，我守在这里！", 3f);
                    message = "AI 玩家已切换为原地驻守模式。";
                    return true;

                case "scavenge":
                case "loot":
                    SetBehaviorMode("scavenge");
                    EnsureOrFindAvatarNpc(true, out _);
                    ShowAvatarBubble("边走边帮你盯着物资！", 3f);
                    message = "AI 玩家已切换为协同拾荒模式。";
                    return true;

                case "heal":
                    if (IsNetPlayerActive)
                    {
                        _netPlayerCtrl.HealAllBodyPartInjuries();
                        var st = _netPlayerCtrl.characterData?.characterStatusData;
                        if (st != null) st.hp = st.maxhp;
                        ShowAvatarBubble("状态拉满了！", 2.5f);
                        message = "已为 AI 玩家恢复伤势与生命。";
                        return true;
                    }
                    var agH = GetAvatarAgent();
                    if (agH?.humanCharacter != null)
                    {
                        agH.humanCharacter.FullyHealInjuries();
                        ShowAvatarBubble("状态拉满了！", 2.5f);
                        message = "已为 AI 角色恢复伤势。";
                        return true;
                    }
                    message = "AI 玩家尚未上场。";
                    return false;

                case "reload":
                    if (IsNetPlayerActive)
                    {
                        _netPlayerCtrl.ReloadButtonDown();
                        message = "已让 AI 玩家换弹。";
                        return true;
                    }
                    var agR = GetAvatarAgent();
                    if (agR != null)
                    {
                        agR.CheckAndReloadMagazine();
                        agR.humanCharacter?.ReloadButtonDown();
                        message = "已让 AI 角色检查并换弹。";
                        return true;
                    }
                    message = "AI 玩家尚未上场。";
                    return false;

                case "light":
                case "flashlight":
                    if (IsNetPlayerActive)
                    {
                        if (_netPlayerCtrl.isFlashLightOn) _netPlayerCtrl.TurnOffLight();
                        else _netPlayerCtrl.TurnOnLight();
                        message = $"已切换 AI 玩家手电：{(_netPlayerCtrl.isFlashLightOn ? "开" : "关")}";
                        return true;
                    }
                    var agL = GetAvatarAgent();
                    if (agL?.humanCharacter != null)
                    {
                        if (agL.humanCharacter.isFlashLightOn) agL.humanCharacter.TurnOffLight();
                        else agL.humanCharacter.TurnOnLight();
                        message = $"已切换 AI 角色手电：{(agL.humanCharacter.isFlashLightOn ? "开" : "关")}";
                        return true;
                    }
                    message = "AI 玩家尚未上场。";
                    return false;

                case "status":
                    message = GetAvatarSummary();
                    return true;

                default:
                    if (!string.IsNullOrEmpty(verb))
                    {
                        SetBehaviorMode(verb);
                        message = $"玩家模式姿态已设置为：{_behaviorMode}";
                        return true;
                    }
                    message = "未知的玩家模式动作：" + op;
                    return false;
            }
        }

        public static bool ExecuteRoomOp(string op, string arg, out string message)
        {
            message = "";
            if (!AiCompanion.IsPlayerMode() && !AiCompanion.IsDshMode)
            {
                message = "请在设置中开启「玩家模式」或 DSH 模式后操作联机房间。";
                return false;
            }

            var verb = (op ?? "").Trim().ToLowerInvariant();
            string codeOrAddr = !string.IsNullOrEmpty(arg) ? arg.Trim() : AiCompanion.CoopRoomCode;

            switch (verb)
            {
                case "join_code":
                case "join":
                case "enter":
                    return TryJoinRoomByCode(codeOrAddr, "", out message);

                case "join_ip":
                case "connect":
                    return TryJoinRoomDirect(codeOrAddr, out message);

                case "host":
                case "open":
                case "create":
                    int port = 7777;
                    if (int.TryParse(codeOrAddr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p > 0 && p <= 65535)
                        port = p;
                    return TryOpenHostRoom(port, true, out message);

                case "leave":
                case "disconnect":
                case "exit":
                    return TryLeaveRoom(out message);

                case "status":
                    message = GetRoomSummary();
                    return true;

                default:
                    message = "未知房间动作：" + op;
                    return false;
            }
        }

        /// <summary>
        /// 在玩家通过聊天框直接下达口语化联机/玩家指令时，给出即时响应并同步操作。
        /// 仅匹配高度明确的动作短语，普通聊天仍交给 LLM/DSH 自然回复。
        /// </summary>
        public static bool TryHandleDirectCoopChat(string text, out string reply)
        {
            reply = "";
            if ((!AiCompanion.IsPlayerMode() && !AiCompanion.IsDshMode) || string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim();

            if (t.Contains("召唤") || t.Contains("进房陪玩") || t.Contains("上线") || t.Contains("生成化身"))
            {
                ExecuteCoopOp("spawn", "", out reply);
                return true;
            }
            if (t.Contains("过来") || t.Contains("传送到我") || t.Contains("到我身边"))
            {
                ExecuteCoopOp("tp", "", out reply);
                return true;
            }
            if (t.Contains("原地待命") || t.Contains("守在这里") || t.Contains("别动") || t.Contains("驻守"))
            {
                ExecuteCoopOp("guard", "", out reply);
                return true;
            }
            if (t.Contains("跟我走") || t.Contains("紧跟我") || t.Contains("回来跟着"))
            {
                ExecuteCoopOp("follow", "", out reply);
                return true;
            }
            if (t.Contains("开启联机") || t.Contains("开个房间") || t.Contains("开房间"))
            {
                ExecuteRoomOp("host", "", out reply);
                return true;
            }
            if (t.Contains("退出房间") || t.Contains("断开联机"))
            {
                ExecuteRoomOp("leave", "", out reply);
                return true;
            }
            return false;
        }
    }
}
