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
        public static string[] BehaviorLabels => ModLocalization.BehaviorLabels();

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
                    if (NetLobby.inLobbyAsClient)
                        return string.Format(ModLocalization.Pick(
                            "大厅准备中 房间码:{0}", "Lobby ready, code: {0}", "Lobby bereit, Code: {0}", "Sala lista, código: {0}",
                            "Salon prêt, code : {0}", "ロビー待機中 コード:{0}", "로비 준비 중 코드:{0}", "Lobby gotowe, kod: {0}", "Lobby pronto, código: {0}", "Лобби готово, код: {0}"), code);
                    if (NetLobby.isOpenAsHost)
                        return string.Format(ModLocalization.Pick(
                            "大厅等待玩家 房间码:{0}", "Lobby waiting, code: {0}", "Warte in Lobby, Code: {0}", "Esperando en sala, código: {0}",
                            "Attente salon, code : {0}", "ロビー待機中（ホスト）コード:{0}", "호스트 대기 중 코드:{0}", "Lobby (host), kod: {0}", "Lobby aberto, código: {0}", "Лобби (хост), код: {0}"), code);
                    if (ncc != null && ncc.joinPhase != NetCoordClient.JoinPhase.Idle)
                        return string.Format(ModLocalization.Pick(
                            "连线阶段:{0} {1}", "Connecting: {0} {1}", "Verbindung: {0} {1}", "Conectando: {0} {1}",
                            "Connexion : {0} {1}", "接続中:{0} {1}", "연결 중:{0} {1}", "Łączenie: {0} {1}", "Conectando: {0} {1}", "Подключение: {0} {1}"),
                            ncc.joinPhase, ncc.joinStatusLocalized ?? ncc.joinStatus);
                    return string.IsNullOrEmpty(_lastJoinResult) ? ModLocalization.NetworkRoleLabel("offline") : _lastJoinResult;
                }

                string role = ModLocalization.NetworkRoleLabel(NetworkManager.isHost ? "host" : "client");
                int mem = nm.members != null ? nm.members.Count : 1;
                return string.Format(ModLocalization.Pick(
                    "{0} ({1}) 成员:{2} 房间:{3}", "{0} ({1}) Members:{2} Room:{3}", "{0} ({1}) Mitgl.:{2} Raum:{3}",
                    "{0} ({1}) Miembros:{2} Sala:{3}", "{0} ({1}) Membres:{2} Salon:{3}", "{0} ({1}) メンバー:{2} 部屋:{3}",
                    "{0} ({1}) 인원:{2} 방:{3}", "{0} ({1}) Gracze:{2} Pokój:{3}", "{0} ({1}) Membros:{2} Sala:{3}", "{0} ({1}) Игроки:{2} Комн.:{3}"),
                    role, nm.clientState, mem, string.IsNullOrEmpty(code) ? "-" : code);
            }
            catch
            {
                return ModLocalization.NetworkRoleLabel("unknown");
            }
        }

        public static bool TryJoinRoomByCode(string code, string password, out string message)
        {
            message = "";
            if (DropOutlinePlugin.WorldTearingDown)
            {
                message = ModLocalization.Pick(
                    "场景切换中，请稍后再试。", "Scene is loading, please try again shortly.", "Szene lädt noch, bitte kurz warten.",
                    "Cambiando de escena, inténtalo en un momento.", "Changement de scène, réessayez.", "シーン切替中です。少しお待ちください。",
                    "장면 전환 중입니다. 잠시 후 다시 시도하세요.", "Trwa ładowanie sceny, spróbuj za chwilę.", "Trocando de cena, aguarde.", "Сцена загружается, подождите немного.");
                return false;
            }
            var clean = (code ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(clean))
            {
                message = ModLocalization.Pick(
                    "请先填写房间码，再执行加入房间。", "Enter a room code before joining.", "Bitte zuerst einen Raumcode eingeben.",
                    "Introduce un código de sala primero.", "Saisissez un code de salon d'abord.", "先に部屋コードを入力してください。",
                    "먼저 방 코드를 입력하세요.", "Wpisz kod pokoju przed dołączeniem.", "Informe o código da sala primeiro.", "Сначала введите код комнаты.");
                _lastJoinResult = message;
                return false;
            }

            if (Time.unscaledTime - _lastJoinAttemptAt < 1.5f)
            {
                message = ModLocalization.Pick(
                    "正在发起连线，请稍候。", "Connection already in progress, please wait.", "Verbindung wird bereits hergestellt.",
                    "Conexión en curso, espera un momento.", "Connexion déjà en cours, patientez.", "接続処理中です。お待ちください。",
                    "연결 중입니다. 잠시만 기다리세요.", "Łączenie w toku, proszę czekać.", "Conexão em andamento, aguarde.", "Подключение уже выполняется, подождите.");
                return false;
            }
            _lastJoinAttemptAt = Time.unscaledTime;

            try
            {
                var ncc = NetCoordClient.instance;
                if (ncc == null)
                {
                    message = ModLocalization.Pick(
                        "联机协调器未就绪（NetCoordClient.instance 为空）。", "Room coordinator is not ready yet.", "Raum-Koordinator noch nicht bereit.",
                        "Coordinador de red no listo.", "Coordinateur réseau non prêt.", "通信コーディネーターが未初期化です。",
                        "네트워크 코디네이터가 준비되지 않았습니다.", "Koordynator sieci nie jest gotowy.", "Coordenador de rede não está pronto.", "Координатор сети не готов.");
                    _lastJoinResult = message;
                    return false;
                }

                _lobbyProfileSent = false;
                bool ok = ncc.JoinByCode(clean, password ?? "", false);
                message = ok
                    ? string.Format(ModLocalization.Pick(
                        "已发起房间码连线：{0}（{1}）", "Connecting with code: {0} ({1})", "Verbinde mit Code: {0} ({1})",
                        "Conectando con código: {0} ({1})", "Connexion via code : {0} ({1})", "コード {0} で接続中（{1}）",
                        "코드 {0} 연결 중 ({1})", "Łączenie kodem: {0} ({1})", "Conectando ao código: {0} ({1})", "Подключение по коду: {0} ({1})"), clean, ncc.joinPhase)
                    : string.Format(ModLocalization.Pick(
                        "加入房间失败：{0}", "Failed to join room: {0}", "Beitritt fehlgeschlagen: {0}",
                        "Error al unirse: {0}", "Échec de connexion : {0}", "参加失敗：{0}",
                        "방 참가 실패: {0}", "Nie udało się dołączyć: {0}", "Falha ao entrar: {0}", "Ошибка входа: {0}"),
                        ncc.joinStatusLocalized ?? ncc.joinStatus ?? ModLocalization.Pick(
                            "协调服务拒绝或格式无效", "Rejected or invalid format", "Abgelehnt oder ungültiges Format",
                            "Rechazado o formato inválido", "Refusé ou format invalide", "拒否または形式が無効です",
                            "거절됨 또는 잘못된 형식", "Odrzucono lub zły format", "Recusado ou formato inválido", "Отклонено или неверный формат"));
                _lastJoinResult = message;
                DropOutlinePlugin.L.LogInfo($"[AI联机] JoinByCode({clean}) -> {ok} / {message}");
                return ok;
            }
            catch (Exception e)
            {
                message = ModLocalization.Pick("加入房间异常：", "Join error: ", "Beitrittsfehler: ", "Error al entrar: ", "Erreur salon : ",
                    "参加エラー：", "방 참가 오류: ", "Błąd dołączania: ", "Erro ao entrar: ", "Ошибка входа: ") + e.Message;
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
                message = ModLocalization.Pick(
                    "场景切换中，请稍后再试。", "Scene is loading, please try again shortly.", "Szene lädt noch, bitte kurz warten.",
                    "Cambiando de escena, inténtalo en un momento.", "Changement de scène, réessayez.", "シーン切替中です。少しお待ちください。",
                    "장면 전환 중입니다. 잠시 후 다시 시도하세요.", "Trwa ładowanie sceny, spróbuj za chwilę.", "Trocando de cena, aguarde.", "Сцена загружается, подождите немного.");
                return false;
            }
            var raw = (hostAndPort ?? "").Trim();
            if (string.IsNullOrEmpty(raw))
            {
                message = ModLocalization.Pick(
                    "请提供 IP 或 IP:端口。", "Enter an IP or IP:port.", "Bitte IP oder IP:Port angeben.", "Introduce IP o IP:puerto.",
                    "Indiquez une IP ou IP:port.", "IP または IP:ポートを入力してください。", "IP 또는 IP:포트를 입력하세요.",
                    "Podaj IP lub IP:port.", "Informe o IP ou IP:porta.", "Укажите IP или IP:порт.");
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
                    message = ModLocalization.Pick(
                        "NetworkManager 未就绪。", "NetworkManager is not ready.", "NetworkManager nicht bereit.", "NetworkManager no está listo.",
                        "NetworkManager non prêt.", "NetworkManager が未準備です。", "NetworkManager가 준비되지 않았습니다.",
                        "NetworkManager nie jest gotowy.", "NetworkManager não está pronto.", "NetworkManager не готов.");
                    _lastJoinResult = message;
                    return false;
                }
                _lobbyProfileSent = false;
                bool ok = nm.StartClient(host, port);
                message = ok
                    ? string.Format(ModLocalization.Pick(
                        "正在直连服务器 {0}:{1}", "Connecting to {0}:{1}...", "Verbinde mit {0}:{1}...",
                        "Conectando a {0}:{1}...", "Connexion à {0}:{1}...", "{0}:{1} に接続中...",
                        "{0}:{1}에 연결 중...", "Łączenie z {0}:{1}...", "Conectando a {0}:{1}...", "Подключение к {0}:{1}..."), host, port)
                    : string.Format(ModLocalization.Pick(
                        "直连 {0}:{1} 失败", "Direct connect to {0}:{1} failed", "Direktverbindung zu {0}:{1} fehlgeschlagen",
                        "Falló conexión directa a {0}:{1}", "Échec connexion à {0}:{1}", "{0}:{1} への接続に失敗しました",
                        "{0}:{1} 직접 연결 실패", "Nie udało się połączyć z {0}:{1}", "Falha ao conectar a {0}:{1}", "Не удалось подключиться к {0}:{1}"), host, port);
                _lastJoinResult = message;
                DropOutlinePlugin.L.LogInfo($"[AI联机] StartClient({host}:{port}) -> {ok}");
                return ok;
            }
            catch (Exception e)
            {
                message = ModLocalization.Pick("直连异常：", "Direct connect error: ", "Verbindungsfehler: ", "Error de conexión: ",
                    "Erreur de connexion : ", "直接接続エラー：", "연결 오류: ", "Błąd połączenia: ", "Erro de conexão: ", "Ошибка подключения: ") + e.Message;
                _lastJoinResult = message;
                return false;
            }
        }

        public static bool TryOpenHostRoom(int port, bool registerRoomCode, out string message)
        {
            message = "";
            if (DropOutlinePlugin.WorldTearingDown)
            {
                message = ModLocalization.Pick(
                    "场景切换中，请稍后再试。", "Scene is loading, please try again shortly.", "Szene lädt noch, bitte kurz warten.",
                    "Cambiando de escena, inténtalo en un momento.", "Changement de scène, réessayez.", "シーン切替中です。少しお待ちください。",
                    "장면 전환 중입니다. 잠시 후 다시 시도하세요.", "Trwa ładowanie sceny, spróbuj za chwilę.", "Trocando de cena, aguarde.", "Сцена загружается, подождите немного.");
                return false;
            }
            if (port <= 0 || port > 65535) port = 7777;

            try
            {
                var nm = NetworkManager.instance;
                if (nm == null)
                {
                    message = ModLocalization.Pick(
                        "NetworkManager 未就绪，无法开房。", "NetworkManager not ready to host.", "NetworkManager für Hosting nicht bereit.",
                        "NetworkManager no listo para alojar.", "NetworkManager non prêt à héberger.", "NetworkManager 未準備のため作成できません。",
                        "NetworkManager가 준비되지 않아 방을 열 수 없습니다.", "NetworkManager nie jest gotowy do hostowania.",
                        "NetworkManager não está pronto para hospedar.", "NetworkManager не готов к созданию комнаты.");
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
                        ? string.Format(ModLocalization.Pick(
                            "当前已是房主（端口 {0}）。", "Already hosting (port {0}).", "Bereits Host (Port {0}).",
                            "Ya eres anfitrión (puerto {0}).", "Déjà hôte (port {0}).", "すでにホスト中です（ポート {0}）。",
                            "이미 호스트입니다 (포트 {0}).", "Już hostujesz (port {0}).", "Já é anfitrião (porta {0}).", "Вы уже хост (порт {0})."), nm.hostPort)
                        : string.Format(ModLocalization.Pick(
                            "当前已开启联机房间，房间码：{0}", "Room already open, code: {0}", "Raum bereits offen, Code: {0}",
                            "Sala ya abierta, código: {0}", "Salon déjà ouvert, code : {0}", "部屋はすでに開放中・コード：{0}",
                            "방이 이미 열려 있습니다. 코드: {0}", "Pokój już otwarty, kod: {0}", "Sala já aberta, código: {0}", "Комната уже открыта, код: {0}"), existingCode);
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
                        ? (string.IsNullOrEmpty(code)
                            ? string.Format(ModLocalization.Pick(
                                "已开启联机房主模式（端口 {0}），等待分配房间码", "Host started (port {0}), waiting for room code",
                                "Host gestartet (Port {0}), warte auf Code", "Anfitrión iniciado (puerto {0}), esperando código",
                                "Hôte démarré (port {0}), code en attente", "ホスト開始（ポート {0}）、コード取得待ち",
                                "호스트 시작 (포트 {0}), 방 코드 대기 중", "Host uruchomiony (port {0}), czekanie na kod",
                                "Hospedagem iniciada (porta {0}), aguardando código", "Хост запущен (порт {0}), ждём код"), port)
                            : string.Format(ModLocalization.Pick(
                                "已开启联机房间，房间码：{0}", "Coop room open, code: {0}", "Koop-Raum offen, Code: {0}",
                                "Sala abierta, código: {0}", "Salon ouvert, code : {0}", "部屋を開放しました・コード：{0}",
                                "협동 방이 열렸습니다. 코드: {0}", "Pokój otwarty, kod: {0}", "Sala aberta, código: {0}", "Комната открыта, код: {0}"), code))
                        : ModLocalization.Pick(
                            "开启房主模式失败（请确认端口未被占用）", "Failed to start host (check port availability)",
                            "Host-Start fehlgeschlagen (Port prüfen)", "No se pudo iniciar anfitrión (revisa el puerto)",
                            "Échec démarrage hôte (vérifiez le port)", "ホスト起動失敗（ポート使用状況を確認）",
                            "호스트 시작 실패 (포트 사용 여부 확인)", "Nie udało się uruchomić hosta (sprawdź port)",
                            "Falha ao hospedar (verifique a porta)", "Не удалось запустить хост (проверьте порт)");
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
                    ? string.Format(ModLocalization.Pick(
                        "已在大厅创建联机房间{0}", "Lobby room created{0}", "Lobby-Raum erstellt{0}", "Sala creada en lobby{0}",
                        "Salon créé dans le lobby{0}", "ロビーで部屋を作成しました{0}", "로비 방 생성 완료{0}", "Utworzono pokój w lobby{0}",
                        "Sala criada no lobby{0}", "Комната в лобби создана{0}"),
                        string.IsNullOrEmpty(lobbyCode) ? "" : " " + ModLocalization.Pick("房间码:", "Code:", "Code:", "Código:", "Code:", "コード:", "코드:", "Kod:", "Código:", "Код:") + lobbyCode)
                    : ModLocalization.Pick(
                        "大厅开房失败", "Failed to create lobby room", "Lobby-Erstellung fehlgeschlagen", "Error al crear sala",
                        "Échec création salon", "ロビー作成失敗", "로비 방 생성 실패", "Nie udało się utworzyć pokoju", "Falha ao criar sala", "Ошибка создания лобби");
                _lastJoinResult = message;
                DropOutlinePlugin.L.LogInfo($"[AI联机] Lobby HostOpen -> {opened}, code={lobbyCode}");
                return opened;
            }
            catch (Exception e)
            {
                message = ModLocalization.Pick("开房异常：", "Host error: ", "Host-Fehler: ", "Error al alojar: ", "Erreur d'hébergement : ",
                    "ホスト作成エラー：", "호스트 오류: ", "Błąd hostowania: ", "Erro ao hospedar: ", "Ошибка создания комнаты: ") + e.Message;
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
                    message = ModLocalization.Pick("联机模块未启动。", "Coop module not running.", "Koop-Modul inaktiv.",
                        "Módulo multijugador inactivo.", "Module réseau inactif.", "通信モジュール未起動。",
                        "멀티 모듈이 실행 중이 아닙니다.", "Moduł sieciowy nieaktywny.", "Módulo coop inativo.", "Сетевой модуль не запущен.");
                    return false;
                }
                nm.Shutdown();
                _lobbyProfileSent = false;
                message = ModLocalization.Pick("已退出当前联机房间。", "Left the coop room.", "Koop-Raum verlassen.",
                    "Saliste de la sala.", "Salon quitté.", "部屋から退出しました。", "협동 방에서 나갔습니다.", "Opuszczono pokój.", "Saiu da sala.", "Вы вышли из комнаты.");
                _lastJoinResult = message;
                DropOutlinePlugin.L.LogInfo("[AI联机] Shutdown()");
                return true;
            }
            catch (Exception e)
            {
                message = ModLocalization.Pick("退出房间失败：", "Failed to leave room: ", "Verlassen fehlgeschlagen: ",
                    "Error al salir: ", "Échec sortie : ", "退出失敗：", "방 나가기 실패: ", "Błąd opuszczania: ", "Falha ao sair: ", "Ошибка выхода: ") + e.Message;
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
                _lastJoinResult = string.Format(ModLocalization.Pick(
                    "大厅角色已就绪：{0}", "Lobby character ready: {0}", "Lobby-Charakter bereit: {0}", "Personaje listo: {0}",
                    "Personnage prêt : {0}", "ロビーキャラ準備完了：{0}", "로비 캐릭터 준비 완료: {0}", "Postać gotowa: {0}", "Personagem pronto: {0}", "Персонаж в лобби готов: {0}"), chr.characterName);
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
            string cleanName = string.IsNullOrWhiteSpace(aiName) ? ModLocalization.DefaultAiName() : aiName.Trim();
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
                message = ModLocalization.Pick("场景切换中，暂无法生成 AI 玩家。", "Scene is loading, cannot spawn AI player yet.",
                    "Szene lädt noch, KI-Spieler noch nicht möglich.", "Cambiando de escena, aún no se puede crear el jugador IA.",
                    "Changement de scène, impossible de générer le joueur IA.", "シーン切替中のため AI プレイヤーを生成できません。",
                    "장면 전환 중이므로 AI 플레이어를 소환할 수 없습니다.", "Ładowanie sceny, nie można teraz przyzwać gracza AI.",
                    "Trocando de cena, ainda não é possível gerar o jogador IA.", "Сцена загружается, ИИ-игрок пока недоступен.");
                return false;
            }

            if (IsNetPlayerActive)
            {
                message = string.Format(ModLocalization.Pick(
                    "AI 玩家 {0} 已在房间中（playerId={1}）。", "AI player {0} is already in the room (playerId={1}).",
                    "KI-Spieler {0} ist bereits im Raum (ID={1}).", "El jugador IA {0} ya está en la sala (ID={1}).",
                    "Le joueur IA {0} est déjà dans le salon (ID={1}).", "AI プレイヤー {0} はすでに部屋にいます（ID={1}）。",
                    "AI 플레이어 {0}은(는) 이미 방에 있습니다 (ID={1}).", "Gracz AI {0} jest już w pokoju (ID={1}).",
                    "O jogador IA {0} já está na sala (ID={1}).", "ИИ-игрок {0} уже в комнате (ID={1})."), AiCompanion.GetNpcName(), _aiNetPlayerId);
                return true;
            }

            var existing = GetAvatarAgent();
            if (existing != null && !AiCompanion.IsPlayerMode())
            {
                SyncAvatarSquadRegistration(existing, GameController.instance?.playerCharacter);
                message = string.Format(ModLocalization.Pick(
                    "实体化身 {0} 已在队伍中。", "AI companion {0} is already in the squad.", "KI-Begleiter {0} ist bereits im Trupp.",
                    "El compañero {0} ya está en el escuadrón.", "Le compagnon {0} est déjà dans l'escouade.", "仲間化身 {0} はすでに隊列にいます。",
                    "AI 동료 {0}은(는) 이미 분대에 있습니다.", "Towarzysz {0} jest już w drużynie.", "O companheiro {0} já está no grupo.", "Напарник {0} уже в отряде."), AiCompanion.GetNpcName());
                return true;
            }

            try
            {
                if (NetworkManager.instance != null && NetworkManager.isOnline && NetworkManager.isClient && !NetworkManager.isHost)
                {
                    message = ModLocalization.Pick(
                        "当前是联机客户端，请由房主生成远端 AI 玩家，或开启「客户端自动跟房主」。",
                        "You are a multiplayer client; ask the host to spawn the AI player or enable local client follow.",
                        "Als Client bitte den Host den KI-Spieler spawnen lassen oder lokale Client-Begleitung aktivieren.",
                        "Eres cliente multijugador; pide al anfitrión generar al jugador IA o activa el seguimiento local.",
                        "Vous êtes client ; demandez à l'hôte de créer le joueur IA ou activez le suivi local.",
                        "マルチクライアント側です。ホストに生成してもらうかクライアント追従を有効にしてください。",
                        "클라이언트 상태입니다. 호스트에게 소환을 요청하거나 클라이언트 자율을 켜세요.",
                        "Jesteś klientem; poproś hosta o przyzwanie gracza AI lub włącz tryb lokalny.",
                        "Você é cliente; peça ao anfitrião para gerar o jogador IA ou ative o modo local.",
                        "Вы клиент; попросите хоста создать ИИ-игрока или включите локальное следование.");
                    return false;
                }
            }
            catch { }

            if (!forceSpawn && !AiCompanion.CoopAutoAvatarEnabled)
            {
                message = ModLocalization.Pick("自动生成 AI 玩家已关闭。", "Auto-spawn AI player is disabled.", "Automatisches Spawnen deaktiviert.",
                    "Generación automática desactivada.", "Apparition auto désactivée.", "AI プレイヤー自動生成はオフです。",
                    "AI 플레이어 자동 소환이 꺼져 있습니다.", "Automatyczne przyzywanie AI jest wyłączone.", "Geração automática desativada.", "Автоспавн ИИ-игрока выключен.");
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
                message = ModLocalization.Pick("尚未进入可操控存档，无法生成 AI 玩家。", "Enter a playable save before spawning an AI player.",
                    "Erst ins Spiel laden, um den KI-Spieler zu spawnen.", "Entra en una partida antes de crear al jugador IA.",
                    "Chargez une partie avant d'invoquer le joueur IA.", "操作可能なセーブに入ってから生成してください。",
                    "플레이 가능한 세이브에 진입한 후 소환할 수 있습니다.", "Wejdź do gry przed przyzwaniem gracza AI.",
                    "Entre em um save jogável antes de gerar o jogador IA.", "Сначала загрузите сохранение, чтобы призвать ИИ-игрока.");
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
                    message = string.Format(ModLocalization.Pick(
                        "AI 伙伴 {0} 已在本地小队中（未开联机房）。", "AI companion {0} is in the local squad (offline).",
                        "KI-Begleiter {0} ist im lokalen Trupp (offline).", "El compañero {0} está en el escuadrón local.",
                        "Le compagnon {0} est dans l'escouade locale.", "仲間 {0} はローカル小隊にいます。",
                        "AI 동료 {0}은(는) 로컬 분대에 있습니다.", "Towarzysz {0} jest w lokalnej drużynie.",
                        "O companheiro {0} está no grupo local.", "Напарник {0} уже в локальном отряде."), AiCompanion.GetNpcName());
                    return true;
                }
            }

            if (existing != null)
            {
                SyncAvatarSquadRegistration(existing, player);
                message = string.Format(ModLocalization.Pick(
                    "AI 化身 {0} 已在队伍中。", "AI avatar {0} is already in the squad.", "KI-Avatar {0} ist bereits im Trupp.",
                    "El avatar {0} ya está en el escuadrón.", "L'avatar {0} est déjà dans l'escouade.", "AI 分身 {0} はすでに隊列にいます。",
                    "AI 분신 {0}은(는) 이미 분대에 있습니다.", "Awatar {0} jest już w drużynie.", "O avatar {0} já está no grupo.", "Аватар {0} уже в отряде."), AiCompanion.GetNpcName());
                return true;
            }

            var igc = InGameController.instance;
            var mgr = GOAPNPCManager.instance;
            if (igc == null || mgr == null)
            {
                message = ModLocalization.Pick("游戏 NPC 管理器尚未就绪，无法生成实体。", "NPC manager is not ready yet.",
                    "NPC-Manager noch nicht bereit.", "Gestor de NPC no listo.", "Gestionnaire PNJ non prêt.", "NPC マネージャー未準備です。",
                    "NPC 관리자가 준비되지 않았습니다.", "Menedżer NPC nie jest gotowy.", "Gerenciador de NPC não está pronto.", "Менеджер NPC не готов.");
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
                    message = ModLocalization.Pick("游戏生成实体实例失败。", "Failed to spawn entity instance.", "Spawn der Instanz fehlgeschlagen.",
                        "No se pudo crear la entidad.", "Échec création entité.", "エンティティ生成に失敗しました。",
                        "엔티티 인스턴스 생성 실패.", "Nie udało się utworzyć postaci.", "Falha ao gerar instância.", "Не удалось создать сущность.");
                    return false;
                }

                var ag = human.GetComponent<GOAPAgent>();
                if (ag == null || ag.npcData == null)
                {
                    message = ModLocalization.Pick("生成的实体缺少 GOAPAgent/npcData 组件。", "Spawned character lacks AI/NPC components.",
                        "Gespawneter Figur fehlen NPC-Daten.", "Faltan componentes NPC en la entidad.", "Composants PNJ manquants.", "NPC コンポーネントが不足しています。",
                        "생성된 캐릭터에 NPC 구성이 없습니다.", "Brak komponentów NPC.", "Componentes de NPC ausentes.", "У сущности отсутствуют компоненты NPC.");
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
                message = string.Format(ModLocalization.Pick(
                    "已召唤 {0}（单机本地实体）并加入小队！", "Summoned {0} into your local squad!", "{0} zum lokalen Trupp gerufen!",
                    "¡{0} se unió a tu escuadrón local!", "{0} a rejoint votre escouade locale !", "{0} を小隊に呼び出しました！",
                    "{0}을(를) 로컬 분대로 소환했습니다!", "Przyzwano {0} do lokalnej drużyny!", "{0} entrou no grupo local!", "{0} призван в локальный отряд!"), wantName);
                ShowAvatarBubble(string.Format(ModLocalization.Pick(
                    "{0}，{1}来陪你玩啦！", "{0}, {1} is here to join you!", "{0}, {1} ist jetzt dabei!",
                    "¡{0}, {1} está aquí contigo!", "{0}, {1} arrive à vos côtés !", "{0}、{1}が合流したよ！",
                    "{0}, {1} 합류했어!", "{0}, {1} melduje się na miejscu!", "{0}, {1} chegou para jogar!", "{0}, {1} уже здесь с тобой!"), player.characterData.characterName, wantName), 4.5f);
                DropOutlinePlugin.L.LogInfo($"[AI陪玩] 召唤本地实体成功 name={wantName} guid={_avatarGuid}");
                return true;
            }
            catch (Exception e)
            {
                message = ModLocalization.Pick("召唤 AI 实体失败：", "Failed to summon AI: ", "Fehler beim Rufen: ", "Error al invocar IA: ",
                    "Échec invocation IA : ", "AI 呼び出し失敗：", "AI 소환 실패: ", "Błąd przyzywania AI: ", "Falha ao invocar IA: ", "Ошибка призыва ИИ: ") + e.Message;
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
                    message = ModLocalization.Pick("联机房主模块未就绪。", "Coop host sync is not ready.", "Host-Sync noch nicht bereit.",
                        "Sincronización de anfitrión no lista.", "Synchro hôte non prête.", "ホスト同期が未準備です。",
                        "호스트 동기화 모듈이 준비되지 않았습니다.", "Synchronizacja hosta niegotowa.", "Sincronização do host não pronta.", "Модуль хоста не готов.");
                    return false;
                }

                string wantName = AiCompanion.GetNpcName();
                int pid = _aiNetPlayerId > 0 ? _aiNetPlayerId : AllocateAiPlayerId(nm);
                int camp = hostPlayer.characterData != null ? hostPlayer.characterData.camp : 0;
                var charData = EnsureAiMpCharacterProfile(wantName, camp);
                if (charData == null)
                {
                    message = ModLocalization.Pick("初始化 AI 联机角色卡失败。", "Failed to create AI character profile.", "KI-Profil konnte nicht erstellt werden.",
                        "Error al crear perfil IA.", "Échec création profil IA.", "AI キャラデータ初期化失敗。",
                        "AI 캐릭터 프로필 생성 실패.", "Nie udało się utworzyć profilu AI.", "Falha ao criar perfil IA.", "Ошибка создания профиля ИИ.");
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
                    message = ModLocalization.Pick("NetPlayerSync 生成联机玩家失败。", "Failed to spawn network AI player.", "Netzwerk-KI-Spieler konnte nicht gespawnt werden.",
                        "No se pudo crear el jugador en red.", "Échec création joueur réseau.", "通信プレイヤーの生成に失敗しました。",
                        "멀티플레이어 AI 스폰 실패.", "Nie udało się utworzyć gracza sieciowego.", "Falha ao gerar jogador em rede.", "Не удалось создать сетевого ИИ-игрока.");
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

                message = string.Format(ModLocalization.Pick(
                    "已创建 AI 联机角色「{0}」(玩家ID:{1}) 并以正常联机玩家身份加入房间！",
                    "Created coop AI player \"{0}\" (ID:{1}) and joined the room!",
                    "KI-Koop-Spieler „{0}“ (ID:{1}) erstellt und beigetreten!",
                    "¡Jugador IA \"{0}\" (ID:{1}) creado y unido a la sala!",
                    "Joueur IA « {0} » (ID:{1}) créé et connecté au salon !",
                    "協力 AI「{0}」(ID:{1}) が部屋に参加しました！",
                    "협동 AI \"{0}\" (ID:{1})이(가) 방에 참가했습니다!",
                    "Gracz AI „{0}” (ID:{1}) dołączył do pokoju!",
                    "Jogador IA \"{0}\" (ID:{1}) criado e entrou na sala!",
                    "ИИ-игрок «{0}» (ID:{1}) создан и вошёл в комнату!"), wantName, pid);
                ShowAvatarBubble(string.Format(ModLocalization.Pick(
                    "{0}，{1}已上线进房！", "{0}, {1} has joined the room!", "{0}, {1} ist im Raum!",
                    "¡{0}, {1} entró en la sala!", "{0}, {1} est connecté !", "{0}、{1}が参加したよ！",
                    "{0}, {1} 접속했어!", "{0}, {1} dołączył!", "{0}, {1} entrou na sala!", "{0}, {1} уже в комнате!"),
                    hostPlayer.characterData?.characterName ?? ModLocalization.DefaultPlayerTitle(), wantName), 4.5f);
                DropOutlinePlugin.L.LogInfo($"[玩家模式] {message}");
                return true;
            }
            catch (Exception e)
            {
                message = ModLocalization.Pick("生成联机 AI 玩家失败：", "Failed to spawn coop AI player: ", "Fehler beim Spawnen: ",
                    "Error al crear jugador IA: ", "Échec apparition joueur IA : ", "協力プレイヤー生成失敗：",
                    "협동 AI 소환 실패: ", "Błąd przyzywania gracza AI: ", "Falha ao gerar jogador IA: ", "Ошибка создания ИИ-игрока: ") + e.Message;
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
                message = removedAny
                    ? ModLocalization.Pick("已让 AI 玩家/化身离队退房。", "Removed AI player/companion from the squad.", "KI-Spieler/Begleiter entfernt.",
                        "Se retiró al jugador/compañero IA.", "Joueur/compagnon IA retiré.", "AI プレイヤー/分身を解散しました。",
                        "AI 플레이어/분신이 방에서 퇴장했습니다.", "Usunięto gracza/towarzysza AI.", "Jogador/companheiro IA removido.", "ИИ-игрок/напарник покинул отряд.")
                    : ModLocalization.Pick("当前没有在场的 AI 玩家或化身。", "No active AI player or companion right now.", "Kein aktiver KI-Spieler vorhanden.",
                        "No hay ningún jugador IA activo.", "Aucun joueur IA actif.", "現在参加中の AI はいません。",
                        "현재 참여 중인 AI가 없습니다.", "Brak aktywnego gracza AI.", "Nenhum jogador IA ativo no momento.", "Сейчас нет активного ИИ-игрока.");
                return true;
            }
            catch (Exception e)
            {
                message = ModLocalization.Pick("收回 AI 角色失败：", "Failed to dismiss AI character: ", "Entfernen fehlgeschlagen: ",
                    "Error al retirar IA: ", "Échec du retrait : ", "撤収失敗：", "소환 해제 실패: ", "Błąd odwołania AI: ", "Falha ao dispensar IA: ", "Ошибка отзыва ИИ: ") + e.Message;
                return false;
            }
        }

        public static bool ForceTeleportToPlayer(out string message)
        {
            message = "";
            var player = GameController.instance?.playerCharacter;
            if (player == null)
            {
                message = ModLocalization.Pick("玩家角色不在场景中。", "Player character is not in the scene.", "Spieler nicht in der Szene.",
                    "El jugador no está en la escena.", "Joueur absent de la scène.", "プレイヤーがシーンにいません。",
                    "플레이어 캐릭터가 장면에 없습니다.", "Brak postaci gracza na scenie.", "O jogador não está na cena.", "Персонаж игрока не на сцене.");
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
                    ShowAvatarBubble(ModLocalization.Pick("我过来了！", "Coming over!", "Ich komme!", "¡Allá voy!", "J'arrive !", "今向かいます！", "지금 갈게요!", "Idę do ciebie!", "Estou indo!", "Уже бегу!"), 3f);
                    message = string.Format(ModLocalization.Pick(
                        "已将 AI 玩家 {0} 传送到你身边。", "Teleported AI player {0} to you.", "KI-Spieler {0} zu dir teleportiert.",
                        "Jugador IA {0} teletransportado contigo.", "Joueur IA {0} téléporté vers vous.", "AI プレイヤー {0} をあなたの近くに移動しました。",
                        "AI 플레이어 {0}을(를) 곁으로 이동했습니다.", "Przeteleportowano {0} do ciebie.", "Jogador IA {0} teleportado até você.", "ИИ-игрок {0} перемещён к вам."), AiCompanion.GetNpcName());
                    return true;
                }
                catch (Exception e)
                {
                    message = ModLocalization.Pick("传送 AI 玩家失败：", "Failed to teleport AI player: ", "Teleport fehlgeschlagen: ",
                        "Error al teletransportar: ", "Échec téléportation : ", "移動失敗：", "이동 실패: ", "Błąd teleportacji: ", "Falha ao teleportar: ", "Ошибка телепортации: ") + e.Message;
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
                    message = ModLocalization.Pick("无法定位 AI 玩家位置。", "Cannot locate AI player.", "KI-Spieler nicht gefunden.",
                        "No se encuentra al jugador IA.", "Joueur IA introuvable.", "AI の位置が見つかりません。",
                        "AI 위치를 찾을 수 없습니다.", "Nie można namierzyć gracza AI.", "Não é possível localizar o jogador IA.", "Не удалось найти ИИ-игрока.");
                    return false;
                }
            }

            try
            {
                Vector2 target = new Vector2(player.transform.position.x + 1.6f, player.transform.position.y - 0.6f);
                ag.character.TeleportTo(target, false);
                SyncAvatarToNetworkClients(ag.humanCharacter);
                ShowAvatarBubble(ModLocalization.Pick("到你身边啦！", "Right by your side!", "Bin da!", "¡Ya estoy contigo!", "Me voilà !", "すぐそばに着きました！", "곁에 도착했어요!", "Jestem obok!", "Cheguei!", "Я уже рядом!"), 3f);
                message = string.Format(ModLocalization.Pick(
                    "已将 {0} 传送到你身边。", "Teleported {0} to you.", "{0} zu dir teleportiert.", "{0} teletransportado contigo.",
                    "{0} téléporté près de vous.", "{0} をあなたの近くに移動しました。", "{0}을(를) 곁으로 이동했습니다.", "Przeteleportowano {0} do ciebie.", "{0} teleportado até você.", "{0} перемещён к вам."), AiCompanion.GetNpcName());
                return true;
            }
            catch (Exception e)
            {
                message = ModLocalization.Pick("传送失败：", "Teleport failed: ", "Teleport fehlgeschlagen: ", "Error al teletransportar: ",
                    "Échec téléportation : ", "移動失敗：", "이동 실패: ", "Błąd teleportacji: ", "Falha ao teleportar: ", "Ошибка телепортации: ") + e.Message;
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
                    string distTxt = dist >= 0f ? (ModLocalization.IsZh ? $" 距{dist:F0}m" : $" {dist:F0}m") : "";
                    return $"{AiCompanion.GetNpcName()}[{ModLocalization.Pick("联机玩家ID:", "Coop ID:", "Koop-ID:", "ID Koop:", "ID Coop :", "通信ID:", "멀티 ID:", "ID:", "ID Coop:", "ID: ")}{_aiNetPlayerId}] · {_behaviorMode} · HP {pct}%{distTxt}";
                }
                catch
                {
                    return $"{AiCompanion.GetNpcName()}[{ModLocalization.Pick("联机玩家ID:", "Coop ID:", "Koop-ID:", "ID Koop:", "ID Coop :", "通信ID:", "멀티 ID:", "ID:", "ID Coop:", "ID: ")}{_aiNetPlayerId}] · {_behaviorMode}";
                }
            }

            var ag = GetAvatarAgent();
            if (ag == null || ag.npcData == null || ag.character == null)
            {
                if (NetworkManager.instance != null && NetworkManager.isOnline && NetworkManager.isClient && !NetworkManager.isHost)
                    return AiCompanion.CoopDriveLocalWhenClient
                        ? $"{ModLocalization.Pick("客户端自主协同", "Client local assist", "Client-Lokale Begleitung", "Asistencia local", "Suivi local client", "クライアント自律協力", "클라이언트 자율 협동", "Lokalna asysta klienta", "Assistência local", "Локальная помощь")} ({_behaviorMode})"
                        : ModLocalization.Pick("联机客户端等待中", "Client waiting", "Client wartet", "Cliente en espera", "Client en attente", "クライアント待機中", "클라이언트 대기 중", "Klient oczekuje", "Cliente aguardando", "Клиент в ожидании");
                return ModLocalization.Pick("AI 玩家未上线", "AI player offline", "KI-Spieler offline", "Jugador IA desconectado",
                    "Joueur IA hors ligne", "AI プレイヤー未参加", "AI 플레이어 오프라인", "Gracz AI offline", "Jogador IA offline", "ИИ-игрок не в сети");
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
                message = ModLocalization.Pick(
                    "请先在设置中将「表现形式」切换为玩家模式（或使用 DSH 模式）。",
                    "Switch Form to Coop Player Mode (or use DSH Mode) in settings first.",
                    "Wechsle in den Einstellungen zu „Koop-Spieler“ (oder DSH-Modus).",
                    "Cambia el Modo de presentación a Jugador cooperativo (o usa DSH).",
                    "Passez en mode Joueur coopératif (ou mode DSH) dans les paramètres.",
                    "先に設定で「動作形態」をプレイヤーモード（またはDSH）に切り替えてください。",
                    "먼저 설정에서 '활동 형태'를 플레이어 모드(또는 DSH)로 전환하세요.",
                    "Najpierw przełącz tryb na Gracz kooperacyjny (lub tryb DSH) w ustawieniach.",
                    "Altere para o modo Jogador Cooperativo (ou modo DSH) nas configurações.",
                    "Сначала переключите форму на «Игрок (кооператив)» (или режим DSH).");
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
                    ShowAvatarBubble(ModLocalization.Pick("好嘞，我紧跟着你！", "Got it, staying right behind you!", "Alles klar, bleibe dicht dran!",
                        "¡Entendido, te sigo de cerca!", "Compris, je vous suis de près !", "了解、すぐ後ろにつきます！",
                        "알겠어, 바짝 따라갈게!", "Jasne, trzymam się blisko!", "Beleza, vou bem atrás de você!", "Понял, держусь рядом!"), 3f);
                    message = ModLocalization.Pick("AI 玩家已切换为紧跟模式。", "AI player switched to Follow mode.", "KI-Spieler auf Folgen gestellt.",
                        "Jugador IA en modo Seguir.", "Joueur IA en mode Suivre.", "追従モードに切り替えました。",
                        "따라가기 모드로 전환되었습니다.", "Gracz AI w trybie Podążania.", "Jogador IA no modo Seguir.", "ИИ переключён в режим следования.");
                    return true;

                case "combat":
                case "attack":
                case "fight":
                case "cover":
                    SetBehaviorMode("combat");
                    EnsureOrFindAvatarNpc(true, out _);
                    ShowAvatarBubble(ModLocalization.Pick("交给我来掩护开火！", "Covering fire! Leave them to me!", "Ich gebe Feuerschutz!",
                        "¡Yo te cubro!", "Je couvre vos arrières !", "援護射撃は任せてください！",
                        "엄호 사격할게, 맡겨줘!", "Osłaniam cię!", "Deixa comigo, dando cobertura!", "Прикрываю огнём!"), 3f);
                    message = ModLocalization.Pick("AI 玩家已切换为积极迎战模式。", "AI player switched to Combat mode.", "KI-Spieler auf Kampfmodus gestellt.",
                        "Jugador IA en modo Combate.", "Joueur IA en mode Combat.", "積極交戦モードに切り替えました。",
                        "적극 교전 모드로 전환되었습니다.", "Gracz AI w trybie Walki.", "Jogador IA no modo Combate.", "ИИ переключён в боевой режим.");
                    return true;

                case "guard":
                case "stay":
                case "hold":
                    SetBehaviorMode("guard");
                    ShowAvatarBubble(ModLocalization.Pick("收到，我守在这里！", "Copy that, holding this spot!", "Verstanden, halte diese Position!",
                        "¡Recibido, mantengo la posición!", "Reçu, je garde la position !", "了解、ここを守ります！",
                        "알겠어, 이 자리를 지킬게!", "Przyjąłem, bronię pozycji!", "Entendido, fico guardando aqui!", "Принял, держу позицию!"), 3f);
                    message = ModLocalization.Pick("AI 玩家已切换为原地驻守模式。", "AI player switched to Guard mode.", "KI-Spieler auf Wache gestellt.",
                        "Jugador IA en modo Guardia.", "Joueur IA en mode Garde.", "その場待機モードに切り替えました。",
                        "제자리 수비 모드로 전환되었습니다.", "Gracz AI w trybie Warty.", "Jogador IA no modo Guarda.", "ИИ переключён в режим охраны.");
                    return true;

                case "scavenge":
                case "loot":
                    SetBehaviorMode("scavenge");
                    EnsureOrFindAvatarNpc(true, out _);
                    ShowAvatarBubble(ModLocalization.Pick("边走边帮你盯着物资！", "Keeping an eye on loot along the way!", "Halte Ausschau nach Beute!",
                        "¡Vigilando el botín por el camino!", "Je repère le butin en chemin !", "移動しながら物資を見ておきます！",
                        "따라가면서 주변 물자도 살필게!", "Wypatruję łupów po drodze!", "De olho nos suprimentos no caminho!", "Буду подмечать добычу по пути!"), 3f);
                    message = ModLocalization.Pick("AI 玩家已切换为协同拾荒模式。", "AI player switched to Scavenge mode.", "KI-Spieler auf Plündern gestellt.",
                        "Jugador IA en modo Saqueo.", "Joueur IA en mode Pillage.", "探索協力モードに切り替えました。",
                        "협동 수색 모드로 전환되었습니다.", "Gracz AI w trybie Szabrownictwa.", "Jogador IA no modo Coleta.", "ИИ переключён в режим поиска добычи.");
                    return true;

                case "heal":
                    if (IsNetPlayerActive)
                    {
                        _netPlayerCtrl.HealAllBodyPartInjuries();
                        var st = _netPlayerCtrl.characterData?.characterStatusData;
                        if (st != null) st.hp = st.maxhp;
                        ShowAvatarBubble(ModLocalization.Pick("状态拉满了！", "Back in top shape!", "Wieder voll fit!", "¡Listo y al máximo!",
                            "En pleine forme !", "体調万全です！", "컨디션 최고!", "W pełni sił!", "Tudo pronto de novo!", "Снова в полной форме!"), 2.5f);
                        message = ModLocalization.Pick("已为 AI 玩家恢复伤势与生命。", "Restored AI player health and injuries.",
                            "KI-Spieler geheilt.", "Salud de jugador IA restaurada.", "Santé du joueur IA restaurée.",
                            "AI プレイヤーの負傷と体力を回復しました。", "AI 플레이어의 부상과 체력을 회복했습니다.",
                            "Uleczono gracza AI.", "Vida e ferimentos do jogador IA restaurados.", "Здоровье и раны ИИ-игрока восстановлены.");
                        return true;
                    }
                    var agH = GetAvatarAgent();
                    if (agH?.humanCharacter != null)
                    {
                        agH.humanCharacter.FullyHealInjuries();
                        ShowAvatarBubble(ModLocalization.Pick("状态拉满了！", "Back in top shape!", "Wieder voll fit!", "¡Listo y al máximo!",
                            "En pleine forme !", "体調万全です！", "컨디션 최고!", "W pełni sił!", "Tudo pronto de novo!", "Снова в полной форме!"), 2.5f);
                        message = ModLocalization.Pick("已为 AI 角色恢复伤势。", "Healed AI companion injuries.",
                            "KI-Begleiter geheilt.", "Heridas del compañero IA curadas.", "Blessures du compagnon soignées.",
                            "AI キャラの負傷を回復しました。", "AI 캐릭터의 부상을 회복했습니다.",
                            "Wyleczono towarzysza AI.", "Ferimentos do companheiro IA curados.", "Раны ИИ-персонажа вылечены.");
                        return true;
                    }
                    message = ModLocalization.Pick("AI 玩家尚未上场。", "AI player has not spawned yet.",
                        "KI-Spieler noch nicht im Spiel.", "El jugador IA aún no ha aparecido.", "Le joueur IA n'est pas encore apparu.",
                        "AI プレイヤーはまだ参加していません。", "AI 플레이어가 아직 스폰되지 않았습니다.", "Gracz AI jeszcze nie dołączył.",
                        "Jogador IA ainda não entrou.", "ИИ-игрок ещё не появился.");
                    return false;

                case "reload":
                    if (IsNetPlayerActive)
                    {
                        _netPlayerCtrl.ReloadButtonDown();
                        message = ModLocalization.Pick("已让 AI 玩家换弹。", "Instructed AI player to reload.", "KI-Spieler lädt nach.",
                            "Jugador IA recargando.", "Le joueur IA recharge.", "AI プレイヤーをリロードさせました。",
                            "AI 플레이어에게 재장전을 지시했습니다.", "Gracz AI przeładowuje.", "Jogador IA recarregando.", "ИИ-игрок перезаряжается.");
                        return true;
                    }
                    var agR = GetAvatarAgent();
                    if (agR != null)
                    {
                        agR.CheckAndReloadMagazine();
                        agR.humanCharacter?.ReloadButtonDown();
                        message = ModLocalization.Pick("已让 AI 角色检查并换弹。", "Instructed AI character to check and reload.", "KI-Charakter prüft und lädt nach.",
                            "El personaje IA revisa y recarga.", "Le compagnon vérifie et recharge.", "AI キャラに残弾確認とリロードを指示しました。",
                            "AI 캐릭터에게 장탄 확인 및 재장전을 지시했습니다.", "Towarzysz AI sprawdza i przeładowuje.",
                            "Companheiro IA verificando e recarregando.", "ИИ проверяет оружие и перезаряжается.");
                        return true;
                    }
                    message = ModLocalization.Pick("AI 玩家尚未上场。", "AI player has not spawned yet.",
                        "KI-Spieler noch nicht im Spiel.", "El jugador IA aún no ha aparecido.", "Le joueur IA n'est pas encore apparu.",
                        "AI プレイヤーはまだ参加していません。", "AI 플레이어가 아직 스폰되지 않았습니다.", "Gracz AI jeszcze nie dołączył.",
                        "Jogador IA ainda não entrou.", "ИИ-игрок ещё не появился.");
                    return false;

                case "light":
                case "flashlight":
                    if (IsNetPlayerActive)
                    {
                        if (_netPlayerCtrl.isFlashLightOn) _netPlayerCtrl.TurnOffLight();
                        else _netPlayerCtrl.TurnOnLight();
                        message = (_netPlayerCtrl.isFlashLightOn
                            ? ModLocalization.Pick("已切换 AI 玩家手电：开", "AI player flashlight: ON", "KI-Taschenlampe: AN", "Linterna IA: ON",
                                "Lampe IA : ON", "AI プレイヤーのライト：オン", "AI 플레이어 손전등: 켜짐", "Latarka gracza AI: WŁ.", "Lanterna IA: LIGADA", "Фонарик ИИ: ВКЛ")
                            : ModLocalization.Pick("已切换 AI 玩家手电：关", "AI player flashlight: OFF", "KI-Taschenlampe: AUS", "Linterna IA: OFF",
                                "Lampe IA : OFF", "AI プレイヤーのライト：オフ", "AI 플레이어 손전등: 꺼짐", "Latarka gracza AI: WYŁ.", "Lanterna IA: DESLIGADA", "Фонарик ИИ: ВЫКЛ"));
                        return true;
                    }
                    var agL = GetAvatarAgent();
                    if (agL?.humanCharacter != null)
                    {
                        if (agL.humanCharacter.isFlashLightOn) agL.humanCharacter.TurnOffLight();
                        else agL.humanCharacter.TurnOnLight();
                        message = (agL.humanCharacter.isFlashLightOn
                            ? ModLocalization.Pick("已切换 AI 角色手电：开", "AI character flashlight: ON", "KI-Begleiter-Lampe: AN", "Linterna compañero IA: ON",
                                "Lampe compagnon : ON", "AI キャラのライト：オン", "AI 캐릭터 손전등: 켜짐", "Latarka AI: WŁ.", "Lanterna companheiro IA: LIGADA", "Фонарик напарника: ВКЛ")
                            : ModLocalization.Pick("已切换 AI 角色手电：关", "AI character flashlight: OFF", "KI-Begleiter-Lampe: AUS", "Linterna compañero IA: OFF",
                                "Lampe compagnon : OFF", "AI キャラのライト：オフ", "AI 캐릭터 손전등: 꺼짐", "Latarka AI: WYŁ.", "Lanterna companheiro IA: DESLIGADA", "Фонарик напарника: ВЫКЛ"));
                        return true;
                    }
                    message = ModLocalization.Pick("AI 玩家尚未上场。", "AI player has not spawned yet.",
                        "KI-Spieler noch nicht im Spiel.", "El jugador IA aún no ha aparecido.", "Le joueur IA n'est pas encore apparu.",
                        "AI プレイヤーはまだ参加していません。", "AI 플레이어가 아직 스폰되지 않았습니다.", "Gracz AI jeszcze nie dołączył.",
                        "Jogador IA ainda não entrou.", "ИИ-игрок ещё не появился.");
                    return false;

                case "status":
                    message = GetAvatarSummary();
                    return true;

                default:
                    if (!string.IsNullOrEmpty(verb))
                    {
                        SetBehaviorMode(verb);
                        message = ModLocalization.Pick("玩家模式姿态已设置为：", "AI player stance set to: ", "KI-Spielerhaltung gesetzt auf: ",
                            "Postura de jugador IA: ", "Posture joueur IA : ", "プレイヤーモードの行動方針：",
                            "플레이어 모드 행동 설정: ", "Tryb zachowania gracza AI: ", "Postura do jogador IA: ", "Режим поведения ИИ: ") + _behaviorMode;
                        return true;
                    }
                    message = ModLocalization.Pick("未知的玩家模式动作：", "Unknown coop player action: ", "Unbekannte KI-Spieleraktion: ",
                        "Acción desconocida de jugador IA: ", "Action joueur IA inconnue : ", "不明なプレイヤー操作：",
                        "알 수 없는 플레이어 동작: ", "Nieznana akcja gracza AI: ", "Ação desconhecida do jogador IA: ", "Неизвестное действие ИИ: ") + op;
                    return false;
            }
        }

        public static bool ExecuteRoomOp(string op, string arg, out string message)
        {
            message = "";
            if (!AiCompanion.IsPlayerMode() && !AiCompanion.IsDshMode)
            {
                message = ModLocalization.Pick("请在设置中开启「玩家模式」或 DSH 模式后操作联机房间。",
                    "Enable Coop Player Mode or DSH Mode in settings before managing rooms.",
                    "Aktiviere „Koop-Spieler“ oder DSH-Modus für Raumfunktionen.",
                    "Activa el modo Jugador cooperativo o DSH para usar funciones de sala.",
                    "Activez le mode Joueur coopératif ou DSH pour gérer les salons.",
                    "ルーム操作には設定で「プレイヤーモード」または DSH を有効にしてください。",
                    "방 관리 전 설정에서 '플레이어 모드' 또는 DSH 모드를 켜세요.",
                    "Włącz tryb Gracz kooperacyjny lub DSH w ustawieniach, aby zarządzać pokojami.",
                    "Ative o modo Jogador Cooperativo ou DSH para gerenciar salas.",
                    "Включите режим «Игрок (кооператив)» или DSH для управления комнатами.");
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
                    message = ModLocalization.Pick("未知房间动作：", "Unknown room action: ", "Unbekannte Raumaktion: ",
                        "Acción de sala desconocida: ", "Action salon inconnue : ", "不明なルーム操作：",
                        "알 수 없는 방 동작: ", "Nieznana akcja pokoju: ", "Ação de sala desconhecida: ", "Неизвестное действие с комнатой: ") + op;
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
            var low = t.ToLowerInvariant();

            if (t.Contains("召唤") || t.Contains("进房陪玩") || t.Contains("上线") || t.Contains("生成化身") ||
                low.Contains("spawn ai") || low.Contains("summon ai") || low == "spawn" || low == "summon" || low.Contains("join room"))
            {
                ExecuteCoopOp("spawn", "", out reply);
                return true;
            }
            if (t.Contains("过来") || t.Contains("传送到我") || t.Contains("到我身边") ||
                low.Contains("come here") || low.Contains("teleport to me") || low == "tp")
            {
                ExecuteCoopOp("tp", "", out reply);
                return true;
            }
            if (t.Contains("原地待命") || t.Contains("守在这里") || t.Contains("别动") || t.Contains("驻守") ||
                low.Contains("hold position") || low.Contains("stay here") || low.Contains("guard here"))
            {
                ExecuteCoopOp("guard", "", out reply);
                return true;
            }
            if (t.Contains("跟我走") || t.Contains("紧跟我") || t.Contains("回来跟着") ||
                low.Contains("follow me") || low.Contains("stay close"))
            {
                ExecuteCoopOp("follow", "", out reply);
                return true;
            }
            if (t.Contains("开启联机") || t.Contains("开个房间") || t.Contains("开房间") ||
                low.Contains("host room") || low.Contains("open room"))
            {
                ExecuteRoomOp("host", "", out reply);
                return true;
            }
            if (t.Contains("退出房间") || t.Contains("断开联机") ||
                low.Contains("leave room") || low.Contains("disconnect"))
            {
                ExecuteRoomOp("leave", "", out reply);
                return true;
            }
            return false;
        }
    }
}
