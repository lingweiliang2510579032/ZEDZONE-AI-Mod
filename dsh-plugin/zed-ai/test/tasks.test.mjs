import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readdir, readFile, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { apply } from '../index.js';
import { normalizeTask, writeScopedCommand, createGameSync, roleContext } from '../bridge.js';
const stateOf = (change = {}) => ({ schemaVersion: 2, worldActive: true, aiEnabled: true, ageSeconds: 0,
  saveId: 'A', worldEpoch: 'w1', protocol: 'agent-dsh', backend: 'bridge', pendingChat: '帮我整理背包', pendingChatId: 'p1',
  taskCapabilities: { organize: { available: true }, reload: { available: true }, loot: { available: true }, craft: { available: false, reason: '未安全接入制造' }, craft_plan: { available: true } },
  taskFacts: { weapon: { loadedAmmo: 4, reloading: false } }, tasks: [], goals: [], situation: '', loot: '', ...change });
const command = task => ({ saveId: 'A', worldEpoch: 'w1', replyTo: 'p1', say: '收到，我先安排任务，进度以游戏回报为准。', task });

test('zed_order task schema exposes finite defaults without adding tools or weakening required scope', () => {
  const tools = [], effects = [];
  const ctx = { tools: { register: t => tools.push(t) }, effect(fn) { effects.push(fn()); }, inject() {} };
  try {
    apply(ctx, { stateDir: join(tmpdir(), 'zed-no-game-task-schema'), autoConnect: false });
    assert.equal(tools.length, 4);
    const order = tools.find(t => t.name === 'zed_order'), schema = order.parameters.properties.task;
    assert.deepEqual(order.parameters.required, ['say', 'saveId', 'worldEpoch']);
    assert.equal(schema.additionalProperties, false);
    assert.deepEqual(schema.properties.type.enum, ['organize', 'reload', 'loot', 'craft', 'craft_plan', 'discard', 'flashlight', 'sort_containers', 'pause', 'resume', 'cancel']);
    assert.equal(schema.properties.count.type, 'integer'); assert.equal(schema.properties.count.minimum, 1); assert.equal(schema.properties.count.maximum, 100);
    assert.equal(schema.properties.count.default, 1); assert.equal(schema.properties.repeat.default, 'count');
    assert.match(tools.find(t => t.name === 'zed_state').description, /tasks.*taskCapabilities.*taskFacts/);
  } finally { for (const cleanup of effects.reverse()) cleanup?.(); }
});

test('task normalizer enforces enums, integer counts, no unknown fields and one-shot organizing/planning/control', () => {
  assert.deepEqual(normalizeTask({ type: 'reload' }), { type: 'reload', target: '', repeat: 'count', count: 1 });
  assert.equal(normalizeTask({ type: 'loot', repeat: 'continuous', count: 1 }).repeat, 'continuous');
  for (const invalid of [null, [], { type: 'execute-js' }, { type: 'reload', count: 0 }, { type: 'reload', count: 101 }, { type: 'reload', count: 1.5 }, { type: 'reload', count: '2' }, { type: 'loot', repeat: 'forever' }, { type: 'organize', count: 2 }, { type: 'craft_plan', repeat: 'continuous' }, { type: 'cancel', count: 2 }, { type: 'reload', target: {} }, { type: 'reload', evil: true }, { type: 'discard' }, { type: 'discard', target: ' ' }, { type: 'discard', target: 'item:1', count: 2 }, { type: 'discard', target: 'item:1', repeat: 'continuous' }]) assert.throws(() => normalizeTask(invalid));
});

test('sort_containers is one-shot and only accepts an empty target or test', () => {
  assert.deepEqual(normalizeTask({ type: 'sort_containers' }), { type: 'sort_containers', target: '', repeat: 'count', count: 1 });
  assert.equal(normalizeTask({ type: 'sort_containers', target: ' test ' }).target, 'test');
  for (const invalid of [{ type: 'sort_containers', count: 2 }, { type: 'sort_containers', repeat: 'continuous' }, { type: 'sort_containers', target: '电视柜' }, { type: 'sort_containers', target: 'all' }])
    assert.throws(() => normalizeTask(invalid));
  const text = String(roleContext({ saveId: 's', worldEpoch: 'e', taskCapabilities: { sort_containers: { available: true, write: false } } }));
  assert.match(text, /write=false/);
  assert.match(text, /target=test/);
  assert.match(text, /停手了/);
});

test('task command retains normalized task but requires capability and current player message', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-task-write-'));
  try {
    await writeScopedCommand(dir, command({ type: 'organize' }), async () => stateOf());
    const saved = JSON.parse(await readFile(join(dir, 'ai-command.json'), 'utf8'));
    assert.deepEqual(saved.task, { type: 'organize', target: '', repeat: 'count', count: 1 });
    assert.equal(saved.saveId, 'A'); assert.equal(saved.worldEpoch, 'w1'); assert.equal(saved.replyTo, 'p1');
    assert.equal(saved.say, command().say);
    await writeScopedCommand(dir, command({ type: 'reload', count: 3 }), async () => stateOf({ taskCapabilities: [{ type: 'reload', available: true }] }));
    const repeated = JSON.parse(await readFile(join(dir, 'ai-command.json'), 'utf8')); assert.equal(repeated.task.count, 3);
    await writeScopedCommand(dir, command({ type: 'cancel' }), async () => stateOf({ taskCapabilities: {} }));
    const control = JSON.parse(await readFile(join(dir, 'ai-command.json'), 'utf8')); assert.equal(control.task.type, 'cancel');
    await assert.rejects(writeScopedCommand(dir, command({ type: 'craft', target: 'recipe-7' }), async () => stateOf()), /不可用/);
    await assert.rejects(writeScopedCommand(dir, command({ type: 'reload' }), async () => stateOf({ taskCapabilities: {} })), /不可用/);
    await assert.rejects(writeScopedCommand(dir, { ...command({ type: 'reload' }), replyTo: '' }, async () => stateOf()), /待回复/);
    await assert.rejects(writeScopedCommand(dir, { ...command({ type: 'reload' }), replyTo: '' }, async () => stateOf({ pendingChat: '', pendingChatId: '' })), /玩家聊天/);
    await assert.rejects(writeScopedCommand(dir, { ...command({ type: 'reload' }), say: '   ' }, async () => stateOf()), /非空/);
  } finally { await rm(dir, { recursive: true, force: true }); }
});

test('tasks cannot bypass old-save/epoch, backend, freshness or publication authorization gates', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-task-scope-'));
  try {
    for (const change of [{ saveId: 'B' }, { worldEpoch: 'old' }, { protocol: 'chat', backend: 'api' }, { ageSeconds: 50 }, { worldActive: false }])
      await assert.rejects(writeScopedCommand(dir, command({ type: 'reload' }), async () => stateOf(change)));
    await assert.rejects(writeScopedCommand(dir, command({ type: 'reload' }), async () => stateOf(), () => { throw new Error('revoked'); }), /revoked/);
    assert.deepEqual(await readdir(dir), []);
  } finally { await rm(dir, { recursive: true, force: true }); }
});

test('role context includes native task facts and explicitly forbids invented completion or unlimited authorization', () => {
  const text = roleContext(stateOf({ tasks: [{ id: 't1', type: 'organize', status: 'accepted', completed: 0, count: 1 }] }));
  assert.match(text, /taskCapabilities.*organize.*available/); assert.match(text, /taskFacts.*loadedAmmo/); assert.match(text, /tasks.*accepted/);
  assert.match(text, /只有玩家明确要求持续维护/); assert.match(text, /玩家操作时AI必须让行/);
  assert.match(text, /没有snapshot证据不许说/); assert.match(text, /只有completed才可说已完成/);
  assert.match(text, /丢弃权限默认关闭/); assert.match(text, /organize整理绝不隐式丢弃/);
  assert.match(text, /一次整堆正常丢地/); assert.match(text, /整理和丢弃不能假称一起完成/);
});

function syncFixture() {
  let state = stateOf({ pendingChat: '', pendingChatId: '' }), clock = 100000;
  const target = { saveId: 'A', agent: {} }, events = [];
  const tick = createGameSync({ readState: async () => state, getTarget: () => target, selectSave: assert.fail,
    push: async (_agent, text, label) => { events.push({ text, label }); return true; }, now: () => clock, logger: { warn: assert.fail } });
  return { tick, events, set(change) { state = { ...state, ...change }; }, advance(n) { clock += n; } };
}
test('all task status changes remain telemetry and never create user dialogue or wakeups', async () => {
  const f = syncFixture(); await f.tick();
  for (const status of ['accepted', 'running', 'yielding', 'waiting', 'paused', 'completed', 'failed', 'cancelled']) {
    f.set({ tasks: [{ id: 't1', type: 'reload', status, completed: status === 'completed' ? 3 : 0, count: 3, message: '游戏事实' }] });
    await f.tick(); f.advance(120000); await f.tick();
  }
  assert.deepEqual(f.events, []);
});
test('real pending player text is sent alone while task telemetry changes do not repeat it', async () => {
  const f = syncFixture(); await f.tick();
  f.set({ pendingChat: '先暂停', pendingChatId: 'p2', tasks: [{ id: 't2', type: 'loot', status: 'failed', message: '空间不足' }] });
  await f.tick(); await f.tick();
  assert.deepEqual(f.events, [{ text: '先暂停', label: '玩家消息' }]);
  f.set({ pendingChat: '', pendingChatId: '' }); f.advance(6000); await f.tick();
  f.set({ tasks: [{ id: 't3', type: 'loot', status: 'cancelled', message: '玩家取消' }] }); await f.tick();
  assert.deepEqual(f.events, [{ text: '先暂停', label: '玩家消息' }]);
});

test('GameDetails task UI renders snapshot status/message/count without claiming acceptance is completion', () => {
  let declaration, panel;
  const React = { createElement: (type, props, ...children) => ({ type, props: props || {}, children }),
    useState: () => [null, () => {}], useRef: value => ({ current: value }), useEffect() {} };
  vm.runInNewContext(readFileSync(new URL('../client.js', import.meta.url), 'utf8'), { window: { __ModuleLoader__: { load(value) { declaration = value.factory(() => React); } } }, AbortController, setInterval, clearInterval });
  declaration.apply({ slots: { inject(_name, fn) { fn(); }, register(spec, render) { if (spec.key === 'zed-ai') panel = render; } } });
  const tree = panel().type(panel().props);
  const find = node => node && typeof node === 'object' ? node.type?.name === 'GameDetails' ? node : node.children.map(find).find(Boolean) : null;
  const details = find(tree);
  const rendered = details.type({ game: stateOf({ tasks: [{ id: 't1', type: 'organize', status: 'accepted', completed: 0, count: 1, message: '等待安全时机' }] }) });
  const text = JSON.stringify(rendered);
  assert.match(text, /已接收/); assert.match(text, /完成 0\/1/); assert.match(text, /等待安全时机/); assert.doesNotMatch(text, /整理背包.*已完成/);
});


test('registered zed_order preserves task through real current-save authorization while no caller is rejected', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-task-tool-'));
  const effects = [], tools = new Map(), injections = [], routes = new Map(), live = new Map();
  const state = stateOf();
  const workspace = { path: dir, sessionIds: [], async attachSession(id) { if (!this.sessionIds.includes(id)) this.sessionIds.push(id); } };
  const services = {
    agentDefaultModel: { currentSelection: () => ({ provider: 'test', model: 'test-model' }) },
    sessionProjections: { stateOf: (_session, name) => name === 'modelSelection' ? { pending: null } : undefined },
    workspaceRegistry: { list: () => [workspace], create: assert.fail },
    sessions: { get: id => live.get(id)?.session, async flush() { return true; } },
    agents: { get: id => live.get(id)?.agent, async create({ sessionId, setup }) {
      const events = [], session = { id: sessionId, requestHeader: () => undefined, append(type, data) { events.push({ type, data }); }, snapshotEvents: () => events };
      const hooks = [], received = [];
      const agent = { id: sessionId, session, testHooks: hooks, received,
        ctx: { on(name, listener) { const hook = { name, listener }; hooks.push(hook); return () => { const i = hooks.indexOf(hook); if (i >= 0) hooks.splice(i, 1); }; } },
        followup(message) { received.push(message); } };
      assert.equal(typeof setup, 'function'); await setup(agent.ctx, agent);
      assert.deepEqual(hooks.map(h => h.name), ['system-prompt/assemble', 'agent/request'], 'prepublication setup installs both model selection waterfalls');
      live.set(sessionId, { session, agent }); return { agent, dispose() {} };
    } },
  };
  const ctx = { tools: { register(t) { tools.set(t.name, t); } }, effect(fn) { effects.push(fn()); }, inject(names, fn) { injections.push({ names, fn }); }, logger: { info() {}, warn() {} } };
  try {
    await (await import('node:fs/promises')).writeFile(join(dir, 'ai-state.json'), JSON.stringify(state));
    apply(ctx, { stateDir: dir, workspacePath: dir, autoConnect: false });
    injections.find(i => i.names.includes('connection')).fn({ connection: { fetch: { register(route) { routes.set(route.path, route); } } } });
    injections.find(i => i.names.includes('workspaceRegistry')).fn({ get: name => services[name], effect: ctx.effect });
    const response = await routes.get('/api/zed-ai/setEnabled').fetch(new Request('http://localhost/api/zed-ai/setEnabled', {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ type: 'client-request', rpcId: 'test', method: 'zed-ai/setEnabled', payload: { enabled: true } }),
    }));
    assert.equal((await response.json()).result.ok, true);
    await new Promise(resolve => setTimeout(resolve, 1700));
    const map = JSON.parse(await readFile(join(dir, 'dsh-sessions.json'), 'utf8'));
    const execution = { agent: live.get(map.saves.A.sessionId).agent }, order = tools.get('zed_order');
    assert.deepEqual(execution.agent.testHooks.map(h => h.name), ['system-prompt/assemble', 'agent/request', 'agent/inbox/claimed', 'system-prompt/assemble'], 'two model hooks plus claimed-message guard and scoped game context hook');
    assert.equal(execution.agent.received.length, 1);
    assert.deepEqual(execution.agent.received[0].content, [{ type: 'text', text: state.pendingChat }]);
    assert.deepEqual(execution.agent.received[0].source, { kind: 'user' });
    await assert.rejects(order.execute(command({ type: 'organize' })), /对应会话/);
    await order.execute(command({ type: 'organize' }), execution);
    const saved = JSON.parse(await readFile(join(dir, 'ai-command.json'), 'utf8'));
    assert.deepEqual(saved.task, { type: 'organize', target: '', repeat: 'count', count: 1 });
    assert.equal(saved.replyTo, 'p1');
    await assert.rejects(order.execute({ ...command({ type: 'organize' }), worldEpoch: 'stale' }, execution), /切换/);
  } finally { for (const dispose of effects.reverse()) dispose?.(); await rm(dir, { recursive: true, force: true }); }
});

test('discard requires explicit permission, native availability and one unprotected current token', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-discard-safe-'));
  const candidate = { token: 'runtime:one', name: '废料', quantity: 7, protected: false };
  const enabled = stateOf({ taskCapabilities: { discard: { available: true } }, taskFacts: { allowDiscard: true, discardCandidates: [candidate] } });
  try {
    await writeScopedCommand(dir, command({ type: 'discard', target: '废料' }), async () => enabled);
    const payload = JSON.parse(await readFile(join(dir, 'ai-command.json'), 'utf8'));
    assert.equal(payload.task.target, 'runtime:one'); assert.equal(payload.task.count, 1);
    for (const change of [
      { taskFacts: { ...enabled.taskFacts, allowDiscard: false } },
      { taskCapabilities: { discard: { available: false } } },
      { taskFacts: { ...enabled.taskFacts, discardCandidates: [{ ...candidate, protected: true }] } },
      { taskFacts: { ...enabled.taskFacts, discardCandidates: [candidate, { ...candidate, token: 'runtime:two' }] } },
      { taskFacts: { allowDiscard: true } },
    ]) await assert.rejects(writeScopedCommand(dir, command({ type: 'discard', target: '废料' }), async () => ({ ...enabled, ...change })));
    await assert.rejects(writeScopedCommand(dir, command({ type: 'discard', target: '废料', count: 2 }), async () => enabled), /一次整堆/);
    await assert.rejects(writeScopedCommand(dir, command({ type: 'discard', target: '废料', repeat: 'continuous' }), async () => enabled), /一次整堆/);
    await assert.rejects(writeScopedCommand(dir, { ...command({ type: 'discard', target: candidate.token }), worldEpoch: 'old' }, async () => enabled), /切换/);
  } finally { await rm(dir, { recursive: true, force: true }); }
});
