import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, readFile, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { apply } from '../index.js';

// Integration through registered tools, control API and the real polling runtime.
test('tools require the active save session and explicit current scope, off/API/old epoch cannot write', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-tools-'));
  const effects = [], tools = new Map(), injections = [], routes = new Map(), live = new Map();
  let state = { schemaVersion: 2, worldActive: true, aiEnabled: true, saveId: 'A', saveName: '测试档', worldEpoch: 'entry1',
    protocol: 'agent-dsh', pendingChat: '在吗', pendingChatId: 'm1', situation: '安全', memory: [], goals: [], context: {} };
  const save = () => writeFile(join(dir, 'ai-state.json'), JSON.stringify(state));
  const workspace = { path: dir, sessionIds: [], async attachSession(id) { if (!this.sessionIds.includes(id)) this.sessionIds.push(id); } };
  const services = {
    agentDefaultModel: { currentSelection: () => ({ provider: 'test', model: 'test-model' }) },
    sessionProjections: { stateOf: (_session, name) => name === 'modelSelection' ? { pending: null } : undefined },
    workspaceRegistry: { list: () => [workspace], create() { assert.fail('duplicate workspace'); } },
    sessions: { get: id => live.get(id)?.session, async flush() { return true; } },
    agents: { get: id => live.get(id)?.agent, async create({ sessionId, setup }) {
      const events = [], session = { id: sessionId, requestHeader: () => undefined, append(type, data) { events.push({ type, data }); }, snapshotEvents: () => events };
      const hooks = [], received = [];
      const agent = { id: sessionId, session, testHooks: hooks, received,
        ctx: { on(name, listener) { const hook = { name, listener }; hooks.push(hook); return () => { const i = hooks.indexOf(hook); if (i >= 0) hooks.splice(i, 1); }; } },
        followup(message) { received.push(message); } };
      assert.equal(typeof setup, 'function'); await setup(agent.ctx, agent);
      assert.deepEqual(hooks.map(h => h.name), ['system-prompt/assemble', 'agent/request'], 'actual create setup installs both model selection waterfalls');
      const entry = { agent, session }; live.set(sessionId, entry); return entry;
    } },
  };
  const ctx = { tools: { register(tool) { tools.set(tool.name, tool); } },
    effect(fn) { effects.push(fn()); }, inject(names, fn) { injections.push({ names, fn }); },
    logger: { info() {}, warn() {} },
  };
  try {
    await save();
    apply(ctx, { stateDir: dir, workspacePath: dir, autoConnect: false });
    injections.find(i => i.names.includes('connection')).fn({ connection: { fetch: { register(route) { routes.set(route.path, route); } } }, logger: ctx.logger });
    injections.find(i => i.names.includes('workspaceRegistry')).fn({ get: name => services[name], effect: ctx.effect });
    const toggle = async enabled => (await (await routes.get('/api/zed-ai/setEnabled').fetch(new Request('http://localhost/api/zed-ai/setEnabled', {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ type: 'client-request', rpcId: 'id', method: 'zed-ai/setEnabled', payload: { enabled } }),
    }))).json()).result;
    assert.equal((await toggle(true)).ok, true);
    // One polling interval selects this save. No model is called by the stub agent.
    await new Promise(resolve => setTimeout(resolve, 1700));
    const map = JSON.parse(await readFile(join(dir, 'dsh-sessions.json'), 'utf8'));
    const id = map.saves.A.sessionId, execution = { agent: live.get(id).agent };
    assert.deepEqual(execution.agent.testHooks.map(h => h.name), ['system-prompt/assemble', 'agent/request', 'agent/inbox/claimed', 'system-prompt/assemble'], 'two model hooks plus claimed-message guard and game context hook');
    assert.equal(execution.agent.received.length, 1);
    assert.deepEqual(execution.agent.received[0].content, [{ type: 'text', text: '在吗' }]);
    assert.deepEqual(execution.agent.received[0].source, { kind: 'user' });
    const assembleGame = execution.agent.testHooks.filter(h => h.name === 'system-prompt/assemble').at(-1).listener;
    const currentContext = await assembleGame({}, {}, async () => ({ sections: [] }));
    assert.equal(currentContext.sections[0].name, 'zed:game-context');
    assert.equal(currentContext.sections[0].interpolate, false);
    assert.match(currentContext.sections[0].text, /saveId=A.*worldEpoch=entry1/);
    const lobby = live.get(map.control.sessionId).agent;
    assert.deepEqual(lobby.testHooks.map(h => h.name), ['system-prompt/assemble', 'agent/request', 'agent/inbox/claimed', 'system-prompt/assemble'], 'homepage also receives scoped game context and claimed-message guards');
    const lobbyContext = await lobby.testHooks.filter(h => h.name === 'system-prompt/assemble').at(-1).listener({}, {}, async () => ({ sections: [] }));
    assert.match(lobbyContext.sections[0].text, /当前游戏联动不可写/);
    assert.doesNotMatch(lobbyContext.sections[0].text, /当前真实玩家消息.*在吗/);
    const order = tools.get('zed_order');
    const args = { saveId: 'A', worldEpoch: 'entry1', replyTo: 'm1', say: '用户，我在。' };
    await assert.rejects(order.execute(args), /对应会话/);
    await assert.rejects(order.execute(args, { agent: { id: 'another-session' } }), /对应会话/);
    await assert.rejects(order.execute({ ...args, worldEpoch: 'old' }, execution), /切换/);
    await assert.rejects(order.execute({ ...args, replyTo: 'old-message' }, execution), /待回复/);
    await order.execute(args, execution);
    const command = JSON.parse(await readFile(join(dir, 'ai-command.json'), 'utf8'));
    assert.equal(command.saveId, 'A'); assert.equal(command.replyTo, 'm1');
    state = { ...state, protocol: 'chat' }; await save();
    await assert.rejects(order.execute(args, execution), /API/);
    state = { ...state, protocol: 'agent-dsh' }; await save();
    for (const change of [{ schemaVersion: 1 }, { worldActive: false }]) {
      state = { ...state, ...change }; await save();
      await assert.rejects(order.execute(args, execution));
      state = { ...state, schemaVersion: 2, worldActive: true };
    }
    state = { ...state, saveId: 'B', worldEpoch: 'entry2' }; await save();
    const oldContext = await assembleGame({}, {}, async () => ({ sections: [] }));
    assert.match(oldContext.sections[0].text, /当前游戏联动不可写/);
    assert.doesNotMatch(oldContext.sections[0].text, /saveId=B|当前真实玩家消息.*在吗/);
    await toggle(false);
    await assert.rejects(order.execute(args, execution), /关闭|切换/);
  } finally { for (const dispose of effects.reverse()) dispose?.(); await rm(dir, { recursive: true, force: true }); }
});

// DSH rejects the model step of an archived Session, so a player line sent
// there would be consumed as blocked and lost. Hold it instead of dropping it.
test('a player line waits while its session is archived and arrives after unarchiving', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-archived-'));
  const effects = [], injections = [], routes = new Map(), live = new Map(), archivedSessionIds = [];
  const waits = ms => new Promise(resolve => setTimeout(resolve, ms));
  let state = { schemaVersion: 2, worldActive: true, aiEnabled: true, saveId: 'A', saveName: '测试档', worldEpoch: 'entry1',
    protocol: 'agent-dsh', pendingChat: '在吗', pendingChatId: 'm1', situation: '安全', memory: [], goals: [], context: {} };
  const save = () => writeFile(join(dir, 'ai-state.json'), JSON.stringify(state));
  const workspace = { path: dir, sessionIds: [], async attachSession(id) { if (!this.sessionIds.includes(id)) this.sessionIds.push(id); } };
  const services = {
    agentDefaultModel: { currentSelection: () => ({ provider: 'test', model: 'test-model' }) },
    sessionProjections: { stateOf: () => undefined },
    workspaceRegistry: { archivedSessionIds, list: () => [workspace], create() { assert.fail('duplicate workspace'); } },
    sessions: { get: id => live.get(id)?.session, async flush() { return true; } },
    agents: { get: id => live.get(id)?.agent, async create({ sessionId, setup }) {
      const events = [], session = { id: sessionId, requestHeader: () => undefined, append(type, data) { events.push({ type, data }); }, snapshotEvents: () => events };
      const hooks = [], received = [];
      const agent = { id: sessionId, session, testHooks: hooks, received,
        ctx: { on(name, listener) { hooks.push({ name, listener }); return () => {}; } },
        followup(message) { received.push(message); } };
      await setup(agent.ctx, agent);
      const entry = { agent, session }; live.set(sessionId, entry); return entry;
    } },
  };
  const ctx = { tools: { register() {} }, effect(fn) { effects.push(fn()); }, inject(names, fn) { injections.push({ names, fn }); }, logger: { info() {}, warn() {} } };
  try {
    await save();
    apply(ctx, { stateDir: dir, workspacePath: dir, autoConnect: false });
    injections.find(i => i.names.includes('connection')).fn({ connection: { fetch: { register(route) { routes.set(route.path, route); } } }, logger: ctx.logger });
    injections.find(i => i.names.includes('workspaceRegistry')).fn({ get: name => services[name], effect: ctx.effect });
    await (await routes.get('/api/zed-ai/setEnabled').fetch(new Request('http://localhost/api/zed-ai/setEnabled', {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ type: 'client-request', rpcId: 'id', method: 'zed-ai/setEnabled', payload: { enabled: true } }),
    }))).json();
    await waits(1700);
    const sessionId = JSON.parse(await readFile(join(dir, 'dsh-sessions.json'), 'utf8')).saves.A.sessionId;
    const agent = live.get(sessionId).agent;
    assert.equal(agent.received.length, 1);
    archivedSessionIds.push(sessionId);
    state = { ...state, pendingChat: '第二句', pendingChatId: 'm2' }; await save();
    await waits(2200);
    assert.equal(agent.received.length, 1, 'never consumed while archived');
    archivedSessionIds.length = 0;   // the user clicks 取消归档
    await waits(2200);
    assert.equal(agent.received.length, 2);
    assert.equal(agent.received[1].content[0].text, '第二句');
  } finally { for (const dispose of effects.reverse()) dispose?.(); await rm(dir, { recursive: true, force: true }); }
});
