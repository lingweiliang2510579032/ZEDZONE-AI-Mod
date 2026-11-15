import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// Minimal hook runner: exercise the actual shipped panel with no sessions/commands service.
async function panelHarness(call) {
  const source = await readFile(new URL('../client.js', import.meta.url), 'utf8');
  const values = [], effects = [], timers = [];
  let cursor = 0, declaration, panel;
  const React = {
    createElement: (type, props, ...children) => ({ type, props: props || {}, children }),
    useState(initial) {
      const i = cursor++;
      if (!(i in values)) values[i] = initial;
      return [values[i], value => { values[i] = value; }];
    },
    useRef(initial) {
      const i = cursor++;
      if (!(i in values)) values[i] = { current: initial };
      return values[i];
    },
    useEffect(fn) { cursor++; if (!effects.length) effects.push(fn); },
  };
  const ctx = {
    connection: { rpc: { call } },
    slots: {
      inject(_name, fn) { fn(); },
      register(options, component) { if (options.key === 'zed-ai') panel = component; },
    },
  };
  vm.runInNewContext(source, {
    window: { __ModuleLoader__: { load(module) { declaration = module.factory(() => React); } } },
    AbortController,
    setInterval(fn) { timers.push(fn); return timers.length; }, clearInterval() {},
  });
  assert.deepEqual([...declaration.inject], ['slots', 'connection']);
  declaration.apply(ctx);
  function render() {
    cursor = 0;
    const node = panel();
    return node.type(node.props);
  }
  const initial = render();
  const cleanup = effects[0]();
  const flush = () => new Promise(resolve => setImmediate(resolve));
  const find = (node, predicate) => {
    if (!node || typeof node !== 'object') return null;
    if (predicate(node)) return node;
    for (const child of node.children || []) { const hit = find(child, predicate); if (hit) return hit; }
    return null;
  };
  const toggle = () => find(render(), node => typeof node.type === 'function');
  // Render only this leaf component: recursively invoking Panel would allocate
  // hooks again and produce a false test harness state.
  const gameDetails = () => {
    const node = find(render(), node => typeof node.type === 'function' && node.type.name === 'GameDetails');
    assert.ok(node, 'shipped panel must include GameDetails');
    return node.type(node.props);
  };
  const textOf = node => {
    if (node == null || typeof node === 'boolean') return '';
    if (typeof node !== 'object') return String(node);
    if (Array.isArray(node)) return node.map(textOf).join('');
    return (node.children || []).map(textOf).join('');
  };
  return { initial, render, toggle, flush, cleanup, timers, find, gameDetails, textOf };
}

test('panel closes with zero current sessions via dedicated RPC, then reload reads off', async () => {
  let enabled = true; const calls = [];
  const rpc = async (channel, endpoint, payload) => {
    calls.push({ channel, endpoint, payload });
    if (endpoint === 'zed-ai/setEnabled') enabled = payload.enabled;
    return { ok: true, value: { enabled, connected: enabled, connecting: false, error: '' } };
  };
  const p = await panelHarness(rpc);
  await p.flush();
  assert.equal(p.toggle().props.on, true);
  await p.toggle().props.onClick();
  assert.equal(p.toggle().props.on, false);
  assert.equal(calls[1].channel, '/api');
  assert.equal(calls[1].endpoint, 'zed-ai/setEnabled');
  assert.equal(calls[1].payload.enabled, false);
  p.cleanup();
  const reloaded = await panelHarness(rpc); await reloaded.flush();
  assert.equal(reloaded.toggle().props.on, false);
  reloaded.cleanup();
});
test('failed toggle does not falsely change the switch', async () => {
  const p = await panelHarness(async (_channel, endpoint) => endpoint === 'zed-ai/status'
    ? { ok: true, value: { enabled: true, connected: true } }
    : { ok: false, error: { message: 'operation failed' } });
  await p.flush(); await p.toggle().props.onClick();
  assert.equal(p.toggle().props.on, true);
  assert.ok(JSON.stringify(p.render()).includes('operation failed'));
  p.cleanup();
});
test('panel starts unknown/disabled and disables stale state on transport failure', async () => {
  const p = await panelHarness(async () => { throw new Error('offline'); });
  await p.flush();
  assert.equal(p.toggle().props.disabled, true);
  assert.equal(p.toggle().props.on, false);
  assert.ok(JSON.stringify(p.render()).includes('offline'));
  p.cleanup();
});

test('actual GameDetails renders authoritative persona, address, save memory and game dialogue', async () => {
  let game = {
    worldActive: true, saveId: 'save-A', saveName: '基地档', protocol: 'agent-dsh', ageSeconds: 3.2,
    npc: '旧NPC', player: '旧称呼', situation: '血量正常，附近安全',
    context: { npc: '大肥鱼', player: '用户', persona: '话多、可爱，会合理拒绝危险建议。', memoryContext: '本档：用户说要去加油站。' },
    memory: ['玩家：去加油站吧', '大肥鱼：记住啦，我们先准备物资。'],
  };
  const p = await panelHarness(async () => ({ ok: true, value: { enabled: true, connected: true, game } }));
  await p.flush();
  const tree = p.gameDetails(), text = p.textOf(tree);
  assert.equal(tree.type, 'section'); assert.equal(tree.props['aria-label'], '游戏同步与角色信息');
  for (const expected of ['游戏同步', '基地档（save-A）', '大肥鱼 / 用户', game.context.persona,
    '本档：用户说要去加油站。', '玩家：去加油站吧', '大肥鱼：记住啦，我们先准备物资。', '血量正常，附近安全', '3 秒']) {
    assert.ok(text.includes(expected), `visible UI missing ${expected}`);
  }
  assert.ok(!text.includes('旧NPC')); assert.ok(!text.includes('旧称呼'));
  // Same shared persona, independent new save memory: visible UI must update,
  // not retain the previous save's diary or dialogue.
  game = { ...game, saveId: 'save-B', saveName: '新档', context: { ...game.context, memoryContext: '' }, memory: [] };
  p.timers[0](); await p.flush();
  const next = p.textOf(p.gameDetails());
  assert.ok(next.includes('新档（save-B）')); assert.ok(next.includes(game.context.persona));
  assert.ok(next.includes('记忆跟着角色卡走')); assert.ok(!next.includes('用户说要去加油站'));
  assert.ok(!next.includes('玩家：去加油站吧'));
  p.cleanup();
});

// The panel must not present leftover data from the previous session as current.
test('a stale or unreadable game snapshot shows only the not-running notice', async () => {
  const stale = { worldActive: true, saveId: 'old', saveName: '旧档', ageSeconds: 120,
    context: { npc: '大肥鱼', player: '用户', persona: '过期的人设', memoryContext: '过期的记忆' },
    memory: ['玩家：旧消息'], situation: '旧局面' };
  const p = await panelHarness(async () => ({ ok: true, value: { enabled: true, connected: false, game: { ...stale } } }));
  await p.flush();
  const text = p.textOf(p.gameDetails());
  assert.ok(text.includes('未启动'));
  for (const hidden of ['过期的人设', '过期的记忆', '旧档', '旧消息', '旧局面', '大肥鱼']) {
    assert.ok(!text.includes(hidden), `stale panel must not show ${hidden}`);
  }
  p.cleanup();

  const broken = await panelHarness(async () => ({ ok: true, value: { enabled: true, connected: false, game: { error: 'ENOENT', stateDir: 'x' } } }));
  await broken.flush();
  const brokenText = broken.textOf(broken.gameDetails());
  assert.ok(brokenText.includes('未启动'));
  assert.ok(!brokenText.includes('ENOENT'));
  broken.cleanup();
});

test('GameDetails renders API read-only syncProblem rather than hiding game context', async () => {
  const syncProblem = '当前是 API/其他后端，DSH 只显示同步数据，不自动下令';
  const p = await panelHarness(async () => ({ ok: true, value: { enabled: true, connected: true, game: {
    worldActive: true, saveId: 'api-save', protocol: 'chat-completions', syncProblem,
    context: { npc: '大肥鱼', player: '用户', persona: 'API和agent共享的人设', memoryContext: 'API模式的本档事实' },
    memory: ['玩家：API消息', '大肥鱼：API回复'],
  } } }));
  await p.flush(); const text = p.textOf(p.gameDetails());
  for (const expected of [syncProblem, 'API 模式只在此页显示同步内容', 'chat-completions', 'API和agent共享的人设', 'API模式的本档事实', 'API回复']) {
    assert.ok(text.includes(expected), `visible API UI missing ${expected}`);
  }
  p.cleanup();
});

test('GameDetails shows unloaded-world status and handles missing game snapshot', async () => {
  let game;
  const p = await panelHarness(async () => ({ ok: true, value: { enabled: true, connected: true, game } }));
  await p.flush(); assert.ok(p.textOf(p.gameDetails()).includes('等待读取游戏数据'));
  game = { worldActive: false, context: { npc: '大肥鱼', player: '用户', persona: '共享人设仍可查看' } };
  p.timers[0](); await p.flush();
  const text = p.textOf(p.gameDetails());
  assert.ok(text.includes('游戏已启动 · 主菜单（未载入存档）')); assert.ok(text.includes('共享人设仍可查看'));
  assert.ok(text.includes('数据年龄：未知'));
  p.cleanup();
});

test('GameDetails displays 3 presence modes and netPlayer identity', async () => {
  const playerPanel = await panelHarness(async () => ({ ok: true, value: { enabled: true, connected: true, game: {
    worldActive: true, runMode: 'dsh', protocol: 'agent-dsh', saveId: 's1', ageSeconds: 1,
    context: { npc: '大肥鱼', player: '用户', mode: 'player', networkRole: '联机房主' },
    network: { online: true, isHost: true, roomCode: 'ZED888', memberCount: 2 },
    avatar: { present: true, isNetPlayer: true, netPlayerId: 2, name: '大肥鱼', behavior: 'combat', hpRatio: 0.9, distToPlayer: 3.1, inCombat: true },
  } } }));
  await playerPanel.flush();
  const pText = playerPanel.textOf(playerPanel.gameDetails());
  assert.ok(pText.includes('联机玩家模式'));
  assert.ok(pText.includes('联机玩家ID 2'));
  assert.ok(pText.includes('[联机房主]'));
  playerPanel.cleanup();

  const npcPanel = await panelHarness(async () => ({ ok: true, value: { enabled: true, connected: true, game: {
    worldActive: true, runMode: 'api', protocol: 'chat-completions', saveId: 's2', ageSeconds: 1,
    context: { npc: '大肥鱼', player: '用户', mode: 'npc', networkRole: '单人离线', hasHiredTeammate: true },
    avatar: { present: false, behavior: 'follow' },
  } } }));
  await npcPanel.flush();
  const nText = npcPanel.textOf(npcPanel.gameDetails());
  assert.ok(nText.includes('接管招募（雇佣）NPC 模式'));
  assert.ok(nText.includes('已雇佣NPC队友'));
  assert.ok(nText.includes('API 模式'));
  npcPanel.cleanup();
});
