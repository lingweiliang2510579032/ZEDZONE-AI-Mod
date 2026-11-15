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

    function GameDetails({ game }) {
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
      const discardExecution = discardCapability?.requiresOpenInventory === true
        ? '当前能力要求打开背包后执行。'
        : '无需打开背包或菜单，走游戏后台正常落地/失败放回流程，不模拟键鼠。';
      const tasks = Array.isArray(game.tasks) ? game.tasks : game.tasks && typeof game.tasks === 'object' ? [game.tasks] : [];
      const taskNames = { organize: '整理背包', reload: '装填', loot: '附近拾取', craft: '制造', craft_plan: '制造规划', discard: '丢到地上（单堆）' };
      const statuses = { accepted: '已接收', running: '执行中', waiting: '等待条件', paused: '已暂停', yielding: '玩家操作优先，正在让行', completed: '已完成', failed: '失败', cancelled: '已取消', idle: '空闲' };
      const taskText = tasks.length ? tasks.map(task => {
        const status = task.status || task.state || 'unknown';
        const count = task.repeat === 'continuous' ? '持续' : Number.isInteger(task.count) ? task.count : '?';
        return `${taskNames[task.type] || task.type || '任务'}${task.target ? ' · ' + task.target : ''}｜${statuses[status] || status}｜完成 ${Number.isInteger(task.completed) ? task.completed : 0}/${count}\n${task.message || ''}`;
      }).join('\n\n') : '暂无任务。聊天下达任务后在此显示真实进度；玩家操作时 AI 让行。';
      const net = game.network;
      const av = game.avatar;
      const presenceMode = c.mode || game.mode || 'companion';
      const modeDescMap = {
        companion: '聊天陪伴模式（官方聊天框对话、去复读提醒、基于已知配方与现有材料的建议与辅助任务）',
        npc: '接管招募（雇佣）NPC 模式（仅单人/房主且已有雇佣队友时生效；按官方雇佣 NPC 范围操控跟随/驻守/搜集/保护/开火/投雷/治疗）',
        player: '联机玩家模式（创建独立 MpCharacters 联机角色卡加入房间，以正常玩家身份协同战斗、搜刮与聊天）',
      };
      const presenceLabel = modeDescMap[presenceMode] || c.modeLabel || presenceMode;
      const roleTag = c.networkRole ? `[${c.networkRole}] ` : '';
      const hiredTag = c.hasHiredTeammate === true ? ' · 已雇佣NPC队友' : (c.hasHiredTeammate === false && presenceMode === 'npc' ? ' · 暂无雇佣NPC' : '');
      const netText = net && typeof net === 'object'
        ? `${roleTag}${net.online ? (net.isHost ? '联机房主' : '联机客户端') : (net.inLobbyAsClient ? '大厅准备中' : net.isOpenAsHost ? '大厅等待玩家' : '单机/未连线')}${net.roomCode ? ' · 房间码 ' + net.roomCode : ''}${net.online ? ` · 成员 ${net.memberCount ?? 1}` : ''}${hiredTag}${net.lastAction ? '\n' + net.lastAction : ''}`
        : `${roleTag || '未知'}${hiredTag}`;
      const avText = av && typeof av === 'object'
        ? (av.present
            ? `${av.name || c.npc || '大肥鱼'} · ${av.behavior || 'follow'}${av.isNetPlayer ? ` · 联机玩家ID ${av.netPlayerId ?? '?'}` : ''} · HP ${Math.round((av.hpRatio ?? 1) * 100)}% · 距玩家 ${av.distToPlayer ?? '?'}m${av.inCombat ? ' · 交战中' : ''}`
            : (presenceMode === 'player'
                ? `AI 玩家角色尚未入场（行为 ${av.behavior || 'follow'}，可开房/进房或在游戏内触发 spawn 入场）`
                : presenceMode === 'npc'
                  ? '当前由接管雇佣 NPC 模式驱动（需先在游戏中招募雇佣队友）'
                  : '当前为聊天陪伴模式（不生成独立玩家角色）'))
        : '未启用';
      const runModeLabel = game.runMode === 'api' || (game.protocol && game.protocol !== 'agent-dsh')
        ? `API 模式（${game.protocol || 'api'}：模组直连大模型，DSH 只读同步）`
        : 'DSH 模式（agent-dsh：本地 DSH Agent 实时思考与下发决策）';
      const statusText = !game.worldActive
        ? `游戏已启动 · ${game.scene === 'lobby' ? '多人大厅' : '主菜单'}（未载入存档）`
        : (game.syncProblem || '已进入存档，数据持续同步');
      const section = (title, text) => h('div', { style: { marginTop: 12 } },
        h('strong', null, title), h('div', { style: { whiteSpace: 'pre-wrap', marginTop: 5, overflowWrap: 'anywhere' } }, text || '暂无'));
      return h('section', { 'aria-label': '游戏同步与角色信息', style: { marginTop: 20, paddingTop: 16, borderTop: '1px solid var(--dsw-alias-border, #ddd)', fontSize: 13, lineHeight: 1.7 } },
        h('h3', { style: { margin: 0, fontSize: 15 } }, '游戏同步'),
        section('状态', statusText),
        section('接入模式', runModeLabel),
        section('表现形式', presenceLabel),
        section('联机房间', netText),
        section('AI 玩家角色 / 实体状态', avText),
        section('当前角色 / 地图', game.saveName ? `${game.saveName}（${game.saveId}）` : game.saveId),
        section('角色 / 玩家称呼', `${c.npc || game.npc || '未设置'} / ${c.player || game.player || '未设置'}`),
        section('共享人设（游戏设置为准）', c.persona),
        section('角色记忆（跟随角色卡）', c.memoryContext || '这个角色还没有可召回的长期记忆；记忆跟着角色卡走，换地图或删掉进度都不会丢。'),
        section('最近游戏对话', Array.isArray(game.memory) ? game.memory.join('\n') : ''),
        section('当前局面', game.situation),
        section('游戏任务', taskText),
        section('允许 AI 丢弃（游戏设置，只读）', game.taskFacts?.allowDiscard === true
          ? `已开启权限；仍需当前丢弃能力可用，并明确单堆目标。${discardExecution}玩家操作时 AI 让行；整理不会默认丢弃。${discardCapability?.available === false && discardCapability.reason ? '当前不可用：' + discardCapability.reason : ''}`
          : '已关闭或未提供权限：AI 只会给出取舍建议，不会丢弃物品。'),
        h('div', { style: { marginTop: 10, opacity: 0.6, fontSize: 12 } },
          `后端：${game.protocol || '未知'}；数据年龄：${Number.isFinite(game.ageSeconds) ? Math.round(game.ageSeconds) + ' 秒' : '未知'}。`,
          h('br'), 'API 模式只在此页显示同步内容，不重复调用 DSH 模型；切回 agent 后会带入角色记忆。对话与记忆跟着角色卡走：删掉进度、换一张地图都不影响；新角色卡是另一段对话。人设跨角色共享。'));
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

      const run = async (next) => {
        if (actionBusy.current) return;
        actionBusy.current = true; sequence.current++;
        setBusy(true); setMsg('');
        try {
          const value = await request(ctx, 'setEnabled', { enabled: next });
          if (!mounted.current) return;
          setState(value); setReadError('');
          setMsg(value.connected ? '已连接游戏联动' : value.connecting ? '正在连接…' : '已断开游戏联动；保留工作区、会话和游戏数据。');
        } catch (error) {
          if (mounted.current) setMsg('执行失败：' + (error?.message || String(error)));
        } finally {
          actionBusy.current = false;
          if (mounted.current) setBusy(false);
        }
      };

      const on = state?.enabled === true;
      const label = !state ? '状态未就绪' : state.connecting ? '正在连接…' : state.connected ? '已开启' : '已关闭';
      return h('div', { style: { padding: '26px 30px', maxWidth: 560 } },
        h('h2', { style: { margin: '0 0 8px', fontSize: 20 } }, 'ZED AI'),
        h('p', { style: { margin: '0 0 18px', opacity: 0.72, lineHeight: 1.7, fontSize: 13 } },
          '连接《隔离区》：支持 API / DSH 两种接入模式，以及「聊天陪伴 / 接管雇佣NPC / 联机玩家」三种表现形式与单联机自适应。'),
        h('div', { style: { display: 'flex', alignItems: 'center', gap: 10 } },
          h(Toggle, { on, disabled: busy || !state || !!readError, onClick: () => run(!on) }),
          h('span', { style: { fontSize: 14, opacity: 0.9 } }, busy ? '正在执行…' : label)),
        (readError || msg || state?.error) ? h('div', { role: 'status', style: { marginTop: 10, fontSize: 12, opacity: 0.7 } },
          readError ? '状态读取失败：' + readError : msg || state.error) : null,
        h(GameDetails, { game: state?.game }),
        h('div', { style: { marginTop: 22, fontSize: 12, opacity: 0.5, lineHeight: 1.7 } },
          '工作区 / 数据目录：<游戏目录>\\ZED AI（ai-state.json、ai-command.json）。',
          h('br'), '命令：/zed on、/zed off、/zed status；模式与参数在游戏「设置 → AI 伙伴 → 打开 AI 伙伴设置」中切换。',
          h('br'), '关闭仅停止自动联动，不会删除数据或取消已开始的 AI 回复；重启 DSH 后按 autoConnect 配置连接。'));
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
