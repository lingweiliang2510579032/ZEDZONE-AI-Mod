window.__ModuleLoader__.load({
  id: '@local/zed-ai',
  factory(require) {
    const React = require('react');
    const h = React.createElement;

    function ZedIcon() {
      return h('svg', { viewBox: '0 0 24 24', width: 18, height: 18, 'aria-hidden': true, style: { display: 'block' } },
        h('rect', { x: 2, y: 2, width: 20, height: 20, rx: 5, fill: '#1f7a4d' }),
        h('path', { d: 'M7 7h10l-6.2 10H17', fill: 'none', stroke: '#eafff3', strokeWidth: 2.2, strokeLinecap: 'round', strokeLinejoin: 'round' }));
    }

    async function request(ctx, endpoint, payload, signal) {
      const result = await ctx.connection.rpc.call('/api', 'zed-ai/' + endpoint, payload, signal);
      if (!result || result.ok !== true) throw new Error(result?.error?.message || 'ZED AI 控制服务不可用');
      const value = result.value;
      if (!value || typeof value.enabled !== 'boolean' || typeof value.connected !== 'boolean') {
        throw new Error('ZED AI 返回了无效状态');
      }
      return value;
    }

    function Toggle(props) {
      const on = !!props.on;
      return h('button', {
        type: 'button', role: 'switch', 'aria-label': 'ZED AI 联动', 'aria-checked': on, disabled: props.disabled,
        onClick: props.onClick, title: on ? '关闭' : '开启',
        style: {
          width: 46, height: 26, borderRadius: 13, border: 'none', padding: 0, position: 'relative',
          cursor: props.disabled ? 'wait' : 'pointer', flex: 'none', opacity: props.disabled ? 0.6 : 1,
          background: on ? '#1f7a4d' : 'var(--dsw-alias-border, #666)', transition: 'background .15s',
        },
      }, h('span', { style: {
        position: 'absolute', top: 3, left: on ? 23 : 3, width: 20, height: 20, borderRadius: '50%',
        background: '#fff', transition: 'left .15s', boxShadow: '0 1px 2px rgba(0,0,0,.35)',
      } }));
    }

    // The bridge rewrites ai-state.json every two seconds while the game process
    // runs, so a stale file is the only reliable "the game is not running" signal.
    // An unknown age (no reading yet) is shown rather than hidden.
    const LIVE_WINDOW_SECONDS = 15;
    const gameStale = game => Number.isFinite(game?.ageSeconds) && game.ageSeconds > LIVE_WINDOW_SECONDS;

    function normalizeLang(raw) {
      const s = (raw || '').toString().trim().toLowerCase();
      if (!s) return 'zh';
      for (const code of ['zh', 'en', 'de', 'es', 'fr', 'ja', 'ko', 'pl', 'pt', 'ru']) {
        if (s === code || s.startsWith(code + '-') || s.startsWith(code + '_')) return code;
      }
      return 'zh';
    }

    const EN_STRINGS = {
      ariaSync: 'Game Sync & Character Info',
      titleSync: 'Game Sync',
      waitRead: 'Waiting for game data…',
      notRunningTitle: 'Game not running',
      notRunningDesc: 'ZED ZONE is not running. Launch the game and load a save to inspect character, persona, memory, and task sync info.',
      requiresOpenInv: 'Current capability requires opening your backpack first.',
      bgDrop: 'Runs in background cleanly without needing an open menu.',
      tasks: { organize: 'Sort Backpack', reload: 'Reload', loot: 'Loot Nearby', craft: 'Craft', craft_plan: 'Craft Plan', discard: 'Drop Stack' },
      statuses: { accepted: 'Accepted', running: 'Running', waiting: 'Waiting', paused: 'Paused', yielding: 'Yielding to Player', completed: 'Completed', failed: 'Failed', cancelled: 'Cancelled', idle: 'Idle' },
      continuous: 'Continuous',
      completedPrefix: 'Progress',
      noTasks: 'No active tasks. Tasks requested in chat appear here with live progress; AI yields whenever you act.',
      modeDesc: {
        companion: 'Chat Companion Mode (official chat dialog, anti-repetition alerts, advice & helper tasks within known recipes & materials)',
        npc: 'Hired NPC Takeover Mode (singleplayer/host with recruited teammate; controls follow/hold/loot/protect/fire/grenade/heal only)',
        player: 'Co-op Player Mode (joins the room as a dedicated co-op player character for combat, looting, and chat)',
      },
      hiredYes: ' · Hired NPC ready',
      hiredNo: ' · No hired NPC yet',
      roleHost: 'Co-op Host',
      roleClient: 'Co-op Client',
      roleLobbyClient: 'Client Lobby Ready',
      roleLobbyHost: 'Host Lobby Waiting',
      roleSolo: 'Singleplayer / Offline',
      roomCodeLabel: ' · Code ',
      membersLabel: ' · Members ',
      unknown: 'Unknown',
      notEnabled: 'Inactive',
      netPlayerIdLabel: ' · Net Player ID ',
      distLabel: ' · Dist ',
      inCombat: ' · In Combat',
      avatarNotSpawned: b => `AI player not spawned yet (posture: ${b || 'follow'}; host/join a room or spawn in-game)`,
      avatarNpcHint: 'Driven by Hired NPC Mode (recruit a teammate in-game first)',
      avatarCompanionHint: 'Driven by Chat Companion Mode (no separate player avatar)',
      runModeApi: p => `API Mode (${p || 'api'}: direct LLM calls in-mod; DSH shows read-only sync)`,
      runModeDsh: 'DSH Mode (agent-dsh: local DSH Agent thinking & orders)',
      worldMenu: s => `Game running · ${s === 'lobby' ? 'Co-op Lobby' : 'Main Menu'} (no save loaded)`,
      syncedOk: 'Save loaded, live sync active',
      none: 'None',
      lblStatus: 'Status',
      lblRunMode: 'Connection Mode',
      lblPresence: 'Companion Form',
      lblNetwork: 'Co-op Room',
      lblAvatar: 'AI Player / Entity',
      lblSave: 'Character / Map',
      lblNames: 'AI / Player Names',
      lblPersona: 'Shared Persona (from in-game settings)',
      lblMemory: 'Character Memory (bound to character)',
      emptyMemory: 'No long-term memories recalled yet for this character; memory persists across progress resets or map changes.',
      lblDialogue: 'Recent In-Game Chat',
      lblSituation: 'Current Situation',
      lblTasks: 'Mod Tasks',
      lblDiscard: 'Allow AI Discard (in-game setting, read-only)',
      discardOn: (exec, err) => `Allowed; requires active discard capability and a single exact stack. ${exec} AI yields when you act.${err ? ' Currently unavailable: ' + err : ''}`,
      discardOff: 'Disabled or unavailable: AI only offers suggestions and never drops items.',
      footerProtocol: (p, age) => `Backend: ${p || 'Unknown'} · Data age: ${Number.isFinite(age) ? Math.round(age) + 's' : 'Unknown'}.`,
      footerNote: 'API Mode displays read-only sync here without duplicate DSH calls; switching back to DSH keeps character memory. Chat memory follows the character profile, while persona is shared.',
      panelDesc: 'Connects ZED ZONE: supports API & DSH modes, 10 game languages, and Chat Companion / Hired NPC / Co-op Player forms.',
      connectedMsg: 'Connected to ZED ZONE bridge',
      connectingMsg: 'Connecting…',
      disconnectedMsg: 'Disconnected bridge; workspace, session, and save data preserved.',
      failPrefix: 'Action failed: ',
      statusNotReady: 'Not ready',
      statusOn: 'Enabled',
      statusOff: 'Disabled',
      busyText: 'Working…',
      readErrPrefix: 'Status read failed: ',
      dirHint: 'Workspace / Data Dir: <Game Folder>\\ZED AI (ai-state.json, ai-command.json).',
      cmdHint: 'Commands: /zed on, /zed off, /zed status · Change modes in-game via Options -> AI Companion -> Open AI Companion Settings.',
      offHint: 'Disabling only stops automatic bridge sync without deleting data or cancelling active replies.',
      toggleAria: 'ZED AI Bridge',
      toggleOn: 'Disable',
      toggleOff: 'Enable',
    };

    const EXTRA_HEADERS = {
      de: { titleSync: 'Spiel-Sync', lblStatus: 'Status', lblRunMode: 'Verbindungsmodus', lblPresence: 'KI-Rolle', lblNetwork: 'Koop-Raum', lblAvatar: 'KI-Spieler / Einheit', lblSave: 'Charakter / Karte', lblNames: 'KI- / Spielername', lblPersona: 'Persönlichkeit', lblMemory: 'Charakter-Erinnerung', lblDialogue: 'Letzter Spielchat', lblSituation: 'Aktuelle Lage', lblTasks: 'Mod-Aufgaben', lblDiscard: 'Abwurf-Berechtigung', statusOn: 'Aktiviert', statusOff: 'Deaktiviert', connectingMsg: 'Verbinde…', panelDesc: 'Verbindet ZED ZONE (API & DSH Modus, 10 Sprachen, Begleiter / Söldner-NPC / Koop-Spieler).' },
      es: { titleSync: 'Sincronización', lblStatus: 'Estado', lblRunMode: 'Modo de conexión', lblPresence: 'Forma de IA', lblNetwork: 'Sala Coop', lblAvatar: 'Jugador IA / Entidad', lblSave: 'Personaje / Mapa', lblNames: 'Nombre IA / Jugador', lblPersona: 'Personalidad', lblMemory: 'Memoria del personaje', lblDialogue: 'Chat reciente', lblSituation: 'Situación actual', lblTasks: 'Tareas', lblDiscard: 'Permiso de descarte', statusOn: 'Activado', statusOff: 'Desactivado', connectingMsg: 'Conectando…', panelDesc: 'Conecta ZED ZONE (modos API y DSH, 10 idiomas, Compañero / PNJ / Jugador Coop).' },
      fr: { titleSync: 'Synchro jeu', lblStatus: 'Statut', lblRunMode: 'Mode de connexion', lblPresence: 'Forme IA', lblNetwork: 'Salle Coop', lblAvatar: 'Joueur IA / Entité', lblSave: 'Personnage / Carte', lblNames: 'Nom IA / Joueur', lblPersona: 'Personnalité', lblMemory: 'Mémoire du personnage', lblDialogue: 'Chat récent', lblSituation: 'Situation', lblTasks: 'Tâches', lblDiscard: 'Dépôt d\'objets', statusOn: 'Activé', statusOff: 'Désactivé', connectingMsg: 'Connexion…', panelDesc: 'Connecte ZED ZONE (modes API et DSH, 10 langues, Compagnon / PNJ / Joueur Coop).' },
      ja: { titleSync: 'ゲーム同期', lblStatus: '状態', lblRunMode: '接続方式', lblPresence: '参加形態', lblNetwork: '協力部屋', lblAvatar: 'AIプレイヤー / 状態', lblSave: 'キャラ / マップ', lblNames: 'AI / プレイヤー名', lblPersona: '共有性格設定', lblMemory: 'キャラ記憶', lblDialogue: '直近の会話', lblSituation: '現在の状況', lblTasks: 'タスク進捗', lblDiscard: '廃棄権限（読み取り専用）', statusOn: '有効', statusOff: '無効', connectingMsg: '接続中…', panelDesc: '《ZED ZONE》と連携：API / DSH 接続、ゲーム公式10言語、相棒 / 雇用NPC / 協力プレイヤーに対応。' },
      ko: { titleSync: '게임 동기화', lblStatus: '상태', lblRunMode: '연결 방식', lblPresence: '참여 형태', lblNetwork: '협동 방', lblAvatar: 'AI 플레이어 / 개체', lblSave: '캐릭터 / 맵', lblNames: 'AI / 플레이어 호칭', lblPersona: '공유 성격 설정', lblMemory: '캐릭터 기억', lblDialogue: '최근 게임 대화', lblSituation: '현재 상황', lblTasks: '게임 작업', lblDiscard: '아이템 버리기 권한', statusOn: '켜짐', statusOff: '꺼짐', connectingMsg: '연결 중…', panelDesc: 'ZED ZONE 연동: API / DSH 접속, 10개 공식 언어 및 채팅 동반자 / 고용 NPC / 협동 플레이어 지원.' },
      pl: { titleSync: 'Synchronizacja gry', lblStatus: 'Status', lblRunMode: 'Tryb połączenia', lblPresence: 'Forma AI', lblNetwork: 'Pokój Co-op', lblAvatar: 'Gracz AI / Jednostka', lblSave: 'Postać / Mapa', lblNames: 'Nazwa AI / Gracza', lblPersona: 'Osobowość', lblMemory: 'Pamięć postaci', lblDialogue: 'Ostatni czat', lblSituation: 'Obecna sytuacja', lblTasks: 'Zadania moda', lblDiscard: 'Zgoda na wyrzucanie', statusOn: 'Włączone', statusOff: 'Wyłączone', connectingMsg: 'Łączenie…', panelDesc: 'Łączy ZED ZONE (tryby API i DSH, 10 języków gry, Towarzysz / Najęty NPC / Gracz Co-op).' },
      pt: { titleSync: 'Sincronização', lblStatus: 'Estado', lblRunMode: 'Modo de conexão', lblPresence: 'Forma da IA', lblNetwork: 'Sala Coop', lblAvatar: 'Jogador IA / Entidade', lblSave: 'Personagem / Mapa', lblNames: 'Nome IA / Jogador', lblPersona: 'Personalidade', lblMemory: 'Memória do personagem', lblDialogue: 'Chat recente', lblSituation: 'Situação atual', lblTasks: 'Tarefas', lblDiscard: 'Permissão de descarte', statusOn: 'Ativado', statusOff: 'Desativado', connectingMsg: 'Conectando…', panelDesc: 'Conecta ZED ZONE (modos API e DSH, 10 idiomas, Companheiro / NPC / Jogador Coop).' },
      ru: { titleSync: 'Синхронизация', lblStatus: 'Статус', lblRunMode: 'Режим подключения', lblPresence: 'Роль ИИ', lblNetwork: 'Комната коопа', lblAvatar: 'ИИ-игрок / сущность', lblSave: 'Персонаж / Карта', lblNames: 'Имя ИИ / Игрока', lblPersona: 'Характер', lblMemory: 'Память персонажа', lblDialogue: 'Недавний чат', lblSituation: 'Текущая обстановка', lblTasks: 'Задачи мода', lblDiscard: 'Право выброса вещей', statusOn: 'Включено', statusOff: 'Выключено', connectingMsg: 'Подключение…', panelDesc: 'Связывает ZED ZONE (API и DSH, 10 языков игры, Компаньон / Наёмный NPC / Игрок коопа).' },
    };

    function getUi(lang) {
      if (!lang || lang === 'zh') return null;
      const patch = EXTRA_HEADERS[lang];
      return patch ? { ...EN_STRINGS, ...patch } : EN_STRINGS;
    }

    function GameDetails({ game }) {
      const ui = game && !game.error && !gameStale(game)
        ? getUi(normalizeLang(game?.context?.gameLanguage || game?.gameLanguage))
        : null;
      if (!game) return h('p', { role: 'status', style: { opacity: 0.7 } }, '等待读取游戏数据…');
      if (game.error || gameStale(game)) {
        return h('section', { 'aria-label': '游戏未启动', style: { marginTop: 20, paddingTop: 16, borderTop: '1px solid var(--dsw-alias-border, #ddd)', fontSize: 13, lineHeight: 1.7 } },
          h('h3', { style: { margin: 0, fontSize: 15 } }, '游戏同步'),
          h('p', { role: 'status', style: { margin: '8px 0 0', opacity: 0.75 } },
            '《隔离区》未启动。启动游戏并进入存档后，这里才显示角色、人设、记忆与任务信息。'));
      }
      const c = game.context || {};
      const discardCapability = Array.isArray(game.taskCapabilities)
        ? game.taskCapabilities.find(capability => capability?.type === 'discard')
        : game.taskCapabilities?.discard;
      const discardExecution = ui
        ? (discardCapability?.requiresOpenInventory === true ? ui.requiresOpenInv : ui.bgDrop)
        : (discardCapability?.requiresOpenInventory === true
            ? '当前能力要求打开背包后执行。'
            : '无需打开背包或菜单，走游戏后台正常落地/失败放回流程，不模拟键鼠。');
      const tasks = Array.isArray(game.tasks) ? game.tasks : game.tasks && typeof game.tasks === 'object' ? [game.tasks] : [];
      const taskNames = ui ? ui.tasks : { organize: '整理背包', reload: '装填', loot: '附近拾取', craft: '制造', craft_plan: '制造规划', discard: '丢到地上（单堆）' };
      const statuses = ui ? ui.statuses : { accepted: '已接收', running: '执行中', waiting: '等待条件', paused: '已暂停', yielding: '玩家操作优先，正在让行', completed: '已完成', failed: '失败', cancelled: '已取消', idle: '空闲' };
      const taskText = tasks.length ? tasks.map(task => {
        const status = task.status || task.state || 'unknown';
        const count = task.repeat === 'continuous' ? (ui ? ui.continuous : '持续') : Number.isInteger(task.count) ? task.count : '?';
        return ui
          ? `${taskNames[task.type] || task.type || 'Task'}${task.target ? ' · ' + task.target : ''} | ${statuses[status] || status} | ${ui.completedPrefix} ${Number.isInteger(task.completed) ? task.completed : 0}/${count}\n${task.message || ''}`
          : `${taskNames[task.type] || task.type || '任务'}${task.target ? ' · ' + task.target : ''}｜${statuses[status] || status}｜完成 ${Number.isInteger(task.completed) ? task.completed : 0}/${count}\n${task.message || ''}`;
      }).join('\n\n') : (ui ? ui.noTasks : '暂无任务。聊天下达任务后在此显示真实进度；玩家操作时 AI 让行。');
      const net = game.network;
      const av = game.avatar;
      const presenceMode = c.mode || game.mode || 'companion';
      const modeDescMap = ui ? ui.modeDesc : {
        companion: '聊天陪伴模式（官方聊天框对话、去复读提醒、基于已知配方与现有材料的建议与辅助任务）',
        npc: '接管招募（雇佣）NPC 模式（仅单人/房主且已有雇佣队友时生效；按官方雇佣 NPC 范围操控跟随/驻守/搜集/保护/开火/投雷/治疗）',
        player: '联机玩家模式（创建独立联机角色档案加入房间，以正常玩家身份协同战斗、搜刮与聊天）',
      };
      const presenceLabel = modeDescMap[presenceMode] || c.modeLabel || presenceMode;
      const roleTag = c.networkRole ? `[${c.networkRole}] ` : '';
      const hiredTag = c.hasHiredTeammate === true ? (ui ? ui.hiredYes : ' · 已雇佣NPC队友') : (c.hasHiredTeammate === false && presenceMode === 'npc' ? (ui ? ui.hiredNo : ' · 暂无雇佣NPC') : '');
      const netText = net && typeof net === 'object'
        ? `${roleTag}${net.online ? (net.isHost ? (ui ? ui.roleHost : '联机房主') : (ui ? ui.roleClient : '联机客户端')) : (net.inLobbyAsClient ? (ui ? ui.roleLobbyClient : '大厅准备中') : net.isOpenAsHost ? (ui ? ui.roleLobbyHost : '大厅等待玩家') : (ui ? ui.roleSolo : '单机/未连线'))}${net.roomCode ? (ui ? ui.roomCodeLabel : ' · 房间码 ') + net.roomCode : ''}${net.online ? `${ui ? ui.membersLabel : ' · 成员 '}${net.memberCount ?? 1}` : ''}${hiredTag}${net.lastAction ? '\n' + net.lastAction : ''}`
        : `${roleTag || (ui ? ui.unknown : '未知')}${hiredTag}`;
      const avText = av && typeof av === 'object'
        ? (av.present
            ? `${av.name || c.npc || '大肥鱼'} · ${av.behavior || 'follow'}${av.isNetPlayer ? `${ui ? ui.netPlayerIdLabel : ' · 联机玩家ID '}${av.netPlayerId ?? '?'}` : ''} · HP ${Math.round((av.hpRatio ?? 1) * 100)}%${ui ? ui.distLabel : ' · 距玩家 '}${av.distToPlayer ?? '?'}m${av.inCombat ? (ui ? ui.inCombat : ' · 交战中') : ''}`
            : (presenceMode === 'player'
                ? (ui ? ui.avatarNotSpawned(av.behavior) : `AI 玩家角色尚未入场（行为 ${av.behavior || 'follow'}，可开房/进房或在游戏内触发 spawn 入场）`)
                : presenceMode === 'npc'
                  ? (ui ? ui.avatarNpcHint : '当前由接管雇佣 NPC 模式驱动（需先在游戏中招募雇佣队友）')
                  : (ui ? ui.avatarCompanionHint : '当前为聊天陪伴模式（不生成独立玩家角色）')))
        : (ui ? ui.notEnabled : '未启用');
      const runModeLabel = game.runMode === 'api' || (game.protocol && game.protocol !== 'agent-dsh')
        ? (ui ? ui.runModeApi(game.protocol) : `API 模式（${game.protocol || 'api'}：模组直连大模型，DSH 只读同步）`)
        : (ui ? ui.runModeDsh : 'DSH 模式（agent-dsh：本地 DSH Agent 实时思考与下发决策）');
      const statusText = !game.worldActive
        ? (ui ? ui.worldMenu(game.scene) : `游戏已启动 · ${game.scene === 'lobby' ? '多人大厅' : '主菜单'}（未载入存档）`)
        : (game.syncProblem || (ui ? ui.syncedOk : '已进入存档，数据持续同步'));
      const section = (title, text) => h('div', { style: { marginTop: 12 } },
        h('strong', null, title), h('div', { style: { whiteSpace: 'pre-wrap', marginTop: 5, overflowWrap: 'anywhere' } }, text || (ui ? ui.none : '暂无')));
      return h('section', { 'aria-label': ui ? ui.ariaSync : '游戏同步与角色信息', style: { marginTop: 20, paddingTop: 16, borderTop: '1px solid var(--dsw-alias-border, #ddd)', fontSize: 13, lineHeight: 1.7 } },
        h('h3', { style: { margin: 0, fontSize: 15 } }, ui ? ui.titleSync : '游戏同步'),
        section(ui ? ui.lblStatus : '状态', statusText),
        section(ui ? ui.lblRunMode : '接入模式', runModeLabel),
        section(ui ? ui.lblPresence : '表现形式', presenceLabel),
        section(ui ? ui.lblNetwork : '联机房间', netText),
        section(ui ? ui.lblAvatar : 'AI 玩家角色 / 实体状态', avText),
        section(ui ? ui.lblSave : '当前角色 / 地图', game.saveName ? `${game.saveName}（${game.saveId}）` : game.saveId),
        section(ui ? ui.lblNames : '角色 / 玩家称呼', `${c.npc || game.npc || (ui ? ui.unknown : '未设置')} / ${c.player || game.player || (ui ? ui.unknown : '未设置')}`),
        section(ui ? ui.lblPersona : '共享人设（游戏设置为准）', c.persona),
        section(ui ? ui.lblMemory : '角色记忆（跟随角色卡）', c.memoryContext || (ui ? ui.emptyMemory : '这个角色还没有可召回的长期记忆；记忆跟着角色卡走，换地图或删掉进度都不会丢。')),
        section(ui ? ui.lblDialogue : '最近游戏对话', Array.isArray(game.memory) ? game.memory.join('\n') : ''),
        section(ui ? ui.lblSituation : '当前局面', game.situation),
        section(ui ? ui.lblTasks : '游戏任务', taskText),
        section(ui ? ui.lblDiscard : '允许 AI 丢弃（游戏设置，只读）', game.taskFacts?.allowDiscard === true
          ? (ui ? ui.discardOn(discardExecution, discardCapability?.available === false ? discardCapability.reason : '') : `已开启权限；仍需当前丢弃能力可用，并明确单堆目标。${discardExecution}玩家操作时 AI 让行；整理不会默认丢弃。${discardCapability?.available === false && discardCapability.reason ? '当前不可用：' + discardCapability.reason : ''}`)
          : (ui ? ui.discardOff : '已关闭或未提供权限：AI 只会给出取舍建议，不会丢弃物品。')),
        h('div', { style: { marginTop: 10, opacity: 0.6, fontSize: 12 } },
          ui ? ui.footerProtocol(game.protocol, game.ageSeconds) : `后端：${game.protocol || '未知'}；数据年龄：${Number.isFinite(game.ageSeconds) ? Math.round(game.ageSeconds) + ' 秒' : '未知'}。`,
          h('br'), ui ? ui.footerNote : 'API 模式只在此页显示同步内容，不重复调用 DSH 模型；切回 agent 后会带入角色记忆。对话与记忆跟着角色卡走：删掉进度、换一张地图都不影响；新角色卡是另一段对话。人设跨角色共享。'));
    }

    function Panel(props) {
      const ctx = props.ctx;
      const [state, setState] = React.useState(null);
      const [busy, setBusy] = React.useState(false);
      const [msg, setMsg] = React.useState('');
      const [readError, setReadError] = React.useState('');
      const mounted = React.useRef(false);
      const sequence = React.useRef(0);
      const actionBusy = React.useRef(false);

      React.useEffect(() => {
        mounted.current = true;
        const abort = new AbortController();
        let reading = false;
        const refresh = async () => {
          if (reading || actionBusy.current) return;
          reading = true;
          const version = sequence.current;
          try {
            const value = await request(ctx, 'status', {}, abort.signal);
            if (mounted.current && version === sequence.current) { setState(value); setReadError(''); }
          } catch (error) {
            if (mounted.current && version === sequence.current) setReadError(error?.message || String(error));
          } finally { reading = false; }
        };
        void refresh();
        const timer = setInterval(() => { void refresh(); }, 2500);
        return () => { mounted.current = false; abort.abort(); clearInterval(timer); };
      }, [ctx]);

      const ui = state?.game && !state.game.error && !gameStale(state.game)
        ? getUi(normalizeLang(state?.game?.context?.gameLanguage || state?.game?.gameLanguage))
        : null;

      const run = async (next) => {
        if (actionBusy.current) return;
        actionBusy.current = true; sequence.current++;
        setBusy(true); setMsg('');
        try {
          const value = await request(ctx, 'setEnabled', { enabled: next });
          if (!mounted.current) return;
          setState(value); setReadError('');
          const curUi = value?.game && !value.game.error && !gameStale(value.game)
            ? getUi(normalizeLang(value?.game?.context?.gameLanguage || value?.game?.gameLanguage))
            : ui;
          setMsg(value.connected ? (curUi ? curUi.connectedMsg : '已连接游戏联动') : value.connecting ? (curUi ? curUi.connectingMsg : '正在连接…') : (curUi ? curUi.disconnectedMsg : '已断开游戏联动；保留工作区、会话和游戏数据。'));
        } catch (error) {
          if (mounted.current) setMsg((ui ? ui.failPrefix : '执行失败：') + (error?.message || String(error)));
        } finally {
          actionBusy.current = false;
          if (mounted.current) setBusy(false);
        }
      };

      const on = state?.enabled === true;
      const label = !state ? (ui ? ui.statusNotReady : '状态未就绪') : state.connecting ? (ui ? ui.connectingMsg : '正在连接…') : state.connected ? (ui ? ui.statusOn : '已开启') : (ui ? ui.statusOff : '已关闭');
      return h('div', { style: { padding: '26px 30px', maxWidth: 560 } },
        h('h2', { style: { margin: '0 0 8px', fontSize: 20 } }, 'ZED AI'),
        h('p', { style: { margin: '0 0 18px', opacity: 0.72, lineHeight: 1.7, fontSize: 13 } },
          ui ? ui.panelDesc : '连接《隔离区》：支持 API / DSH 两种接入模式，以及「聊天陪伴 / 接管雇佣NPC / 联机玩家」三种表现形式与单联机自适应。'),
        h('div', { style: { display: 'flex', alignItems: 'center', gap: 10 } },
          h(Toggle, { on, disabled: busy || !state || !!readError, onClick: () => run(!on) }),
          h('span', { style: { fontSize: 14, opacity: 0.9 } }, busy ? (ui ? ui.busyText : '正在执行…') : label)),
        (readError || msg || state?.error) ? h('div', { role: 'status', style: { marginTop: 10, fontSize: 12, opacity: 0.7 } },
          readError ? (ui ? ui.readErrPrefix : '状态读取失败：') + readError : msg || state.error) : null,
        h(GameDetails, { game: state?.game }),
        h('div', { style: { marginTop: 22, fontSize: 12, opacity: 0.5, lineHeight: 1.7 } },
          ui ? ui.dirHint : '工作区 / 数据目录：<游戏目录>\\ZED AI（ai-state.json、ai-command.json）。',
          h('br'), ui ? ui.cmdHint : '命令：/zed on、/zed off、/zed status；模式与参数在游戏「设置 → AI 伙伴 → 打开 AI 伙伴设置」中切换。',
          h('br'), ui ? ui.offHint : '关闭仅停止自动联动，不会删除数据或取消已开始的 AI 回复；重启 DSH 后按 autoConnect 配置连接。'));
    }

    return {
      inject: ['slots', 'connection'],
      apply(ctx) {
        ctx.slots.inject('sidebar.panellist', () => ctx.slots.register({
          name: 'sidebar.panellist', id: 'zed-ai', order: 2, label: () => 'ZED AI',
        }, ZedIcon));
        ctx.slots.inject('main', () => ctx.slots.register({
          name: 'main', key: 'zed-ai',
        }, () => h(Panel, { ctx })));
      },
    };
  },
});
