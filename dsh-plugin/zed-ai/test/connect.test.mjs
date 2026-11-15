import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { ensureZed } from '../connect.js';

function scopedAgent(session) {
  const hooks = new Map(), maintenance = [];
  return { id: session.id, session, hooks, maintenance, runMaintenance: async fn => { maintenance.push(fn); return fn(); }, ctx: { on(name, listener) { hooks.set(name, listener); return () => hooks.delete(name); } }, followup() {} };
}
function modelServices() {
  return { agentDefaultModel: { currentSelection: () => ({ provider: 'test-provider', model: 'test-model' }) },
    sessionProjections: { stateOf: (_session, name) => name === 'modelSelection' ? { pending: null } : undefined } };
}
async function withFixture(run) {
  const path = await mkdtemp(join(tmpdir(), 'zed-connection-test-'));
  try { await run(path); } finally { await rm(path, { recursive: true, force: true }); }
}

test('cold history is inspected and resumed with resumeSessionId, not duplicated', () => withFixture(async path => {
  let inspected = 0, resumed = 0;
  const agent = scopedAgent({ id: 'historic-zed', requestHeader: () => undefined });
  const services = {
    ...modelServices(),
    workspaceRegistry: { list: () => [{ path, sessionIds: ['historic-zed'], async attachSession() {} }], create() { assert.fail('duplicate workspace'); } },
    sessions: { get: () => null },
    sessionController: { async inspect(id) {
      inspected++; assert.equal(id, 'historic-zed');
      return { events: [{ type: 'session/title', data: { title: 'ZED' } }] };
    } },
    agents: {
      get: () => null,
      create() { assert.fail('duplicate session'); },
      async resume(options) {
        resumed++;
        assert.equal(options.resumeSessionId, 'historic-zed');
        assert.equal(options.sessionId, undefined);
        assert.deepEqual(options.agentOptions, { provider: 'test-provider', model: 'test-model' });
        assert.equal(typeof options.setup, 'function');
        await options.setup(agent.ctx, agent);
        assert.equal(agent.hooks.size, 2, 'resume must install both model waterfalls before publication');
        return { agent };
      },
    },
  };
  const result = await ensureZed({ get: name => services[name] }, { stateDir: path });
  assert.equal(result.id, 'historic-zed'); assert.equal(result.agent, agent);
  assert.equal(result.created, false); assert.equal(inspected, 1); assert.equal(resumed, 1);
}));

test('failed resume is an error, not a falsely connected null agent', () => withFixture(async path => {
  const services = {
    ...modelServices(),
    workspaceRegistry: { list: () => [{ path, sessionIds: ['zed'], async attachSession() {} }], create() {} },
    sessions: { get: () => ({ snapshotEvents: () => [{ type: 'session/title', data: { title: 'ZED' } }] }) },
    agents: { get: () => null, create() { assert.fail('must not create'); }, async resume() { throw new Error('broken history'); } },
  };
  await assert.rejects(ensureZed({ get: name => services[name] }, { stateDir: path }), /暂时无法恢复/);
}));

// The conversation now belongs to the character card. A conversation created
// under the old progress identity is adopted exactly once by the running world.
test('an existing progress-keyed conversation is adopted by the character identity', () => withFixture(async path => {
  const f = fixture(path), config = { stateDir: path };
  const first = await ensureZed(f.ctx, config, { saveId: 'legacy-progress', saveName: '雨夜' });
  const adopted = await ensureZed(f.ctx, config, { saveId: 'character-card', saveName: '雨夜 · yuye', legacySaveId: 'legacy-progress' });
  assert.equal(adopted.id, first.id, 'same conversation, not a new one');
  assert.equal(adopted.created, false);
  assert.equal(f.counts.create, 1, 'adoption must never create a session');
  const map = JSON.parse(await readFile(join(path, 'dsh-sessions.json'), 'utf8'));
  assert.deepEqual(Object.keys(map.saves), ['character-card']);
  assert.equal(map.saves['character-card'].sessionId, first.id);
  // Later worlds of the same character reuse the mapping directly.
  const again = await ensureZed(f.ctx, config, { saveId: 'character-card', saveName: '雨夜 · 新地图', legacySaveId: 'other-progress' });
  assert.equal(again.id, first.id); assert.equal(f.counts.create, 1);
}));

test('adoption only accepts a ready conversation and never guesses across identities', () => withFixture(async path => {
  const f = fixture(path), config = { stateDir: path };
  await writeFile(join(path, 'dsh-sessions.json'), JSON.stringify({ version: 1, control: null,
    saves: { 'legacy-progress': { sessionId: 'session-half', status: 'creating' }, 'other-progress': { sessionId: 'session-other', status: 'ready' } } }));
  const fresh = await ensureZed(f.ctx, config, { saveId: 'character-card', legacySaveId: 'legacy-progress' });
  assert.notEqual(fresh.id, 'session-half', 'a half-created conversation is never adopted');
  assert.equal(f.counts.create, 1);
  const map = JSON.parse(await readFile(join(path, 'dsh-sessions.json'), 'utf8'));
  assert.deepEqual(Object.keys(map.saves).sort(), ['character-card', 'legacy-progress', 'other-progress']);
  assert.equal(map.saves['legacy-progress'].status, 'creating', 'the blocked reservation stays for the user to inspect');
}));

// With exactly one character card, every retired progress conversation belongs
// to that character, so the newest one is adopted instead of starting over.
test('a single character card adopts its most recently archived conversation', () => withFixture(async path => {
  const f = fixture(path), config = { stateDir: path };
  f.services.workspaceRegistry.archivedSessionIds = ['session-old', 'session-new'];
  f.services.workspaceRegistry.unarchiveSession = async () => {};
  await writeFile(join(path, 'dsh-sessions.json'), JSON.stringify({ version: 1, control: null, saves: {
    'progress-old': { sessionId: 'session-old', status: 'ready' },
    'progress-new': { sessionId: 'session-new', status: 'ready' } } }));
  f.records.set('session-old', { id: 'session-old', requestHeader: () => undefined, snapshotEvents: () => [] });
  f.records.set('session-new', { id: 'session-new', requestHeader: () => undefined, snapshotEvents: () => [] });
  const adopted = await ensureZed(f.ctx, config, { saveId: 'character-card', legacySaveId: 'unrelated-progress', singleCharacter: true });
  assert.equal(adopted.id, 'session-new', 'the newest archived conversation wins');
  assert.equal(f.counts.create, 0);
  const map = JSON.parse(await readFile(join(path, 'dsh-sessions.json'), 'utf8'));
  assert.deepEqual(Object.keys(map.saves).sort(), ['character-card', 'progress-old']);
}));

test('without the single-character proof, retired conversations are never guessed', () => withFixture(async path => {
  const f = fixture(path), config = { stateDir: path };
  f.services.workspaceRegistry.archivedSessionIds = ['session-old', 'session-new'];
  await writeFile(join(path, 'dsh-sessions.json'), JSON.stringify({ version: 1, control: null, saves: {
    'progress-old': { sessionId: 'session-old', status: 'ready' },
    'progress-new': { sessionId: 'session-new', status: 'ready' } } }));
  for (const save of [{ saveId: 'character-card', singleCharacter: false }, { saveId: 'character-card' }]) {
    const created = await ensureZed(f.ctx, config, save);
    assert.ok(!['session-old', 'session-new'].includes(created.id), 'never adopts without proof');
    const map = JSON.parse(await readFile(join(path, 'dsh-sessions.json'), 'utf8'));
    delete map.saves['character-card'];
    await writeFile(join(path, 'dsh-sessions.json'), JSON.stringify(map));
    f.agents.clear(); f.records.delete(created.id);
  }
  assert.equal(f.counts.create, 2);
}));

function fixture(path, options = {}) {
  const records = new Map(), agents = new Map(), workspaces = [];
  const counts = { create: 0, resume: 0, inspect: 0, flush: 0 };
  const ws = { path, sessionIds: [], async attachSession(id) {
    if (options.failAttach) throw new Error('attach failed');
    if (!this.sessionIds.includes(id)) this.sessionIds.push(id);
  } };
  const services = {
    ...modelServices(),
    workspaceRegistry: {
      list: () => workspaces,
      async create(p, name) { assert.equal(p, path); assert.equal(name, 'ZED AI'); workspaces.push(ws); return ws; },
    },
    sessions: {
      get: id => records.get(id),
      async flush(session) {
        counts.flush++;
        assert.ok([...records.values()].includes(session), 'flush must receive the actual registered Session');
        assert.equal(session.snapshotEvents().at(-1).type, 'session/title');
        if (options.failFlush) throw new Error('flush failed');
        if (options.beforeFlush) await options.beforeFlush(session);
        return options.flushAbsent ? false : true;
      },
    },
    sessionController: { async inspect(id) {
      counts.inspect++;
      if (options.goneInspect) return null;
      if (options.failInspect || !records.has(id)) throw new Error('history unreadable');
      return { events: records.get(id).snapshotEvents() };
    } },
    agents: {
      get: id => agents.get(id),
      async resume({ resumeSessionId, setup, agentOptions }) {
        assert.deepEqual(agentOptions, { provider: 'test-provider', model: 'test-model' });
        counts.resume++; const agent = scopedAgent(records.get(resumeSessionId));
        assert.equal(typeof setup, 'function'); await setup(agent.ctx, agent);
        assert.equal(agent.hooks.size, 2, 'cold resume setup must install both model waterfalls');
        agents.set(resumeSessionId, agent); return { agent };
      },
      async create({ sessionId, setup, agentOptions }) {
        assert.deepEqual(agentOptions, { provider: 'test-provider', model: 'test-model' });
        counts.create++;
        const events = [];
        const session = { id: sessionId, requestHeader: () => undefined, snapshotEvents: () => events, append(type, data) {
          if (options.failAppend) throw new Error('append failed');
          assert.deepEqual(data.messageSeqs, []); assert.equal(data.source.kind, 'user');
          events.push({ type, data });
        } };
        const agent = scopedAgent(session);
        assert.equal(typeof setup, 'function'); await setup(agent.ctx, agent);
        assert.equal(agent.hooks.size, 2, 'create setup must install both model waterfalls before publishing');
        records.set(sessionId, session); agents.set(sessionId, agent);
        // Real AgentRegistry returns an owned handle, not { session, agent }.
        // ensureZed must recover the published Session from sessions.get(id).
        return { agent, dispose: async () => {} };
      },
    },
  };
  return { ctx: { get: name => services[name] }, services, records, agents, ws, counts, options };
}

test('save A → B → A uses precise durable IDs even with identical titles', () => withFixture(async path => {
  const f = fixture(path), config = { stateDir: path };
  const a = await ensureZed(f.ctx, config, { saveId: 'A', saveName: '同名存档' });
  const b = await ensureZed(f.ctx, config, { saveId: 'B', saveName: '同名存档' });
  const again = await ensureZed(f.ctx, config, { saveId: 'A', saveName: '改了名' });
  assert.notEqual(a.id, b.id); assert.equal(again.id, a.id); assert.equal(again.saveId, 'A');
  assert.equal(f.counts.create, 2); assert.equal(f.ws.sessionIds.length, 2);
  const map = JSON.parse(await readFile(join(path, 'dsh-sessions.json'), 'utf8'));
  assert.equal(map.saves.A.sessionId, a.id); assert.equal(map.saves.B.sessionId, b.id);
  assert.equal(f.records.get(a.id).snapshotEvents()[0].data.title, 'ZED · 同名存档');
}));

test('legacy homepage ZED is never adopted by a save; homepage stays separate', () => withFixture(async path => {
  const f = fixture(path), config = { stateDir: path };
  const home = await ensureZed(f.ctx, config);
  const save = await ensureZed(f.ctx, config, { saveId: 'new-save', saveName: 'ZED' });
  assert.notEqual(home.id, save.id); assert.equal(home.saveId, null);
  assert.equal((await ensureZed(f.ctx, config)).id, home.id);
}));

test('mapped cold save resumes by ID without relying on title or memory agent', () => withFixture(async path => {
  const f = fixture(path), config = { stateDir: path };
  const first = await ensureZed(f.ctx, config, { saveId: 'A', saveName: '世界' });
  const stored = f.records.get(first.id);
  f.services.sessions.get = () => null;
  f.agents.clear();
  assert.ok(stored);
  const next = await ensureZed(f.ctx, config, { saveId: 'A', saveName: '世界' });
  assert.equal(next.id, first.id); assert.equal(f.counts.inspect, 1); assert.equal(f.counts.resume, 1);
  assert.equal(f.counts.create, 1);
}));

test('corrupt map fails closed without creating a session', () => withFixture(async path => {
  const f = fixture(path);
  await writeFile(join(path, 'dsh-sessions.json'), '{bad', 'utf8');
  await assert.rejects(ensureZed(f.ctx, { stateDir: path }, { saveId: 'A' }), /映射无法读取/);
  assert.equal(f.counts.create, 0);
}));

test('mapped unreadable history fails closed without replacement', () => withFixture(async path => {
  const f = fixture(path), config = { stateDir: path };
  await ensureZed(f.ctx, config, { saveId: 'A' });
  f.services.sessions.get = () => null; f.options.failInspect = true;
  await assert.rejects(ensureZed(f.ctx, config, { saveId: 'A' }), /history unreadable/);
  assert.equal(f.counts.create, 1);
}));

test('a mapped conversation that no longer exists is replaced instead of bricking', () => withFixture(async path => {
  const f = fixture(path), config = { stateDir: path };
  const first = await ensureZed(f.ctx, config, { saveId: 'A' });
  // Simulate the real bug: the mapped Session was deleted / only ever got a title,
  // so neither the live registry nor the controller can hand back any history.
  const realGet = f.services.sessions.get;
  f.services.sessions.get = id => (id === first.id ? undefined : realGet(id));
  f.options.goneInspect = true;
  const again = await ensureZed(f.ctx, config, { saveId: 'A' });
  assert.notEqual(again.id, first.id);
  assert.equal(again.staleId, first.id);
  assert.equal(f.counts.create, 2);
  const map = JSON.parse(await readFile(join(path, 'dsh-sessions.json'), 'utf8'));
  assert.equal(map.saves.A.sessionId, again.id, 'dead mapping must be rewritten to the new session');
}));

test('a dead homepage control conversation is replaced instead of bricking the plugin', () => withFixture(async path => {
  const f = fixture(path), config = { stateDir: path };
  const first = await ensureZed(f.ctx, config);
  const realGet = f.services.sessions.get;
  f.services.sessions.get = id => (id === first.id ? undefined : realGet(id));
  f.options.goneInspect = true;
  const again = await ensureZed(f.ctx, config);
  assert.notEqual(again.id, first.id);
  assert.equal(again.staleId, first.id);
  const map = JSON.parse(await readFile(join(path, 'dsh-sessions.json'), 'utf8'));
  assert.equal(map.control.sessionId, again.id);
}));

test('concurrent same and different saves serialize map transactions without lost entries', () => withFixture(async path => {
  const f = fixture(path), config = { stateDir: path };
  const [a, b, again] = await Promise.all(['A', 'B', 'A'].map(saveId => ensureZed(f.ctx, config, { saveId })));
  assert.equal(a.id, again.id); assert.notEqual(a.id, b.id); assert.equal(f.counts.create, 2);
  const map = JSON.parse(await readFile(join(path, 'dsh-sessions.json'), 'utf8'));
  assert.deepEqual(Object.keys(map.saves).sort(), ['A', 'B']);
}));

for (const failure of ['failAppend', 'failAttach', 'failFlush']) {
  test(`${failure} propagates and durable reservation prevents another broken session`, () => withFixture(async path => {
    const f = fixture(path, { [failure]: true }), config = { stateDir: path };
    await assert.rejects(ensureZed(f.ctx, config, { saveId: 'A' }), /failed/);
    f.options[failure] = false;
    await assert.rejects(ensureZed(f.ctx, config, { saveId: 'A' }), /上次建立未完成/);
    assert.equal(f.counts.create, 1);
    const map = JSON.parse(await readFile(join(path, 'dsh-sessions.json'), 'utf8'));
    assert.equal(map.saves.A.status, 'creating');
  }));
}

test('real-shaped {agent,dispose} handle recovers Session and flushes before map ready', () => withFixture(async path => {
  const f = fixture(path, { async beforeFlush(session) {
    const map = JSON.parse(await readFile(join(path, 'dsh-sessions.json'), 'utf8'));
    assert.equal(map.saves.A.status, 'creating');
    assert.equal(session.snapshotEvents().at(-1).data.title, 'ZED · A');
  } });
  const result = await ensureZed(f.ctx, { stateDir: path }, { saveId: 'A' });
  assert.equal(result.agent, f.agents.get(result.id));
  assert.equal(f.counts.flush, 1);
  const map = JSON.parse(await readFile(join(path, 'dsh-sessions.json'), 'utf8'));
  assert.equal(map.saves.A.status, 'ready');
}));

test('existing live Agent with an empty historical setup is repaired without create/resume', () => withFixture(async path => {
  const f = fixture(path), config = { stateDir: path };
  const first = await ensureZed(f.ctx, config, { saveId: 'A' });
  const emptyAgent = scopedAgent(f.records.get(first.id));
  f.agents.set(first.id, emptyAgent);
  assert.equal(emptyAgent.hooks.size, 0);
  const repaired = await ensureZed(f.ctx, config, { saveId: 'A' });
  assert.equal(repaired.agent, emptyAgent); assert.equal(f.counts.create, 1); assert.equal(f.counts.resume, 0);
  assert.equal(emptyAgent.hooks.size, 2);
  const assembled = await emptyAgent.hooks.get('system-prompt/assemble')({}, {}, async () => ({ variables: {} }));
  assert.equal(assembled.variables.model, 'test-model');
  const request = await emptyAgent.hooks.get('agent/request')({}, async () => ({ model: 'wrong' }));
  assert.equal(request.model, assembled.variables.model); assert.equal(request.provider, 'test-provider');
}));

test('missing valid default model rejects before reserving or creating a new save session', () => withFixture(async path => {
  const f = fixture(path); delete f.services.agentDefaultModel;
  await assert.rejects(ensureZed(f.ctx, { stateDir: path }, { saveId: 'A' }), /模型/);
  assert.equal(f.counts.create, 0); assert.equal(f.counts.resume, 0);
  await assert.rejects(readFile(join(path, 'dsh-sessions.json')), { code: 'ENOENT' });
}));

for (const kind of ['absent', 'missing-method']) {
  test(`missing durability listener (${kind}) refuses readiness and repeated create`, () => withFixture(async path => {
    const f = fixture(path, { flushAbsent: kind === 'absent' });
    if (kind === 'missing-method') delete f.services.sessions.flush;
    const config = { stateDir: path };
    await assert.rejects(ensureZed(f.ctx, config, { saveId: 'A' }), /持久化/);
    await assert.rejects(ensureZed(f.ctx, config, { saveId: 'A' }), /上次建立未完成/);
    const map = JSON.parse(await readFile(join(path, 'dsh-sessions.json'), 'utf8'));
    assert.equal(map.saves.A.status, 'creating'); assert.equal(f.counts.create, 1);
  }));
}
