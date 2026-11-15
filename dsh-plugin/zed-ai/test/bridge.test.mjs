import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createGameSync, bridgeProblem, writeScopedCommand, readBridgeState } from '../bridge.js';
const stateOf = (saveId = 'A', worldEpoch = 'entry1', pendingChat = '', pendingChatId = '') => ({
  schemaVersion: 2, worldActive: true, aiEnabled: true, ageSeconds: 0, protocol: 'agent-dsh',
  saveId, saveName: saveId, worldEpoch, pendingChat, pendingChatId,
  context: { npc: '大肥鱼', player: '用户', persona: '话多，可爱', memoryContext: saveId === 'A' ? '玩家说不需要植物纤维' : '' },
  memory: [], goals: [], situation: '安全',
});
function fixture(initial = stateOf()) {
  let state = initial, target = { id: 'lobby', agent: {} };
  const events = [], selected = [], sessions = new Map();
  const tick = createGameSync({ readState: async () => state, getTarget: () => target,
    async selectSave(s) { selected.push(s.saveId); if (!sessions.has(s.saveId)) sessions.set(s.saveId, { id: 'session-' + s.saveId, saveId: s.saveId, agent: {} }); target = sessions.get(s.saveId); return target; },
    push(agent, text, label, metadata) { events.push({ agent, text, label, metadata }); return true; }, logger: { warn() {} },
  });
  return { tick, events, selected, state: next => { state = next; }, target: () => target };
}
test('entry and A→B→A select separate save targets without injecting configuration or automatic chat', async () => {
  const f = fixture(); await f.tick();
  const a = f.target();
  f.state(stateOf('B')); await f.tick(); assert.notEqual(f.target(), a);
  f.state(stateOf('A', 'entry2')); await f.tick(); assert.equal(f.target(), a);
  assert.deepEqual(f.selected, ['A', 'B', 'A']);
  assert.deepEqual(f.events, []);
});
test('identical text with distinct message IDs delivers twice, same ID only once', async () => {
  const f = fixture(stateOf('A', 'entry1', '在吗', 'm1'));
  await f.tick(); await f.tick();
  f.state(stateOf('A', 'entry1', '在吗', 'm2')); await f.tick();
  assert.deepEqual(f.events.map(e => e.text), ['在吗', '在吗']);
  assert.deepEqual(f.events.map(e => e.label), ['玩家消息', '玩家消息']);
  assert.deepEqual(f.events.map(e => e.metadata), [
    { key: 'A:entry1:chat:m1', saveId: 'A', worldEpoch: 'entry1', chatId: 'm1' },
    { key: 'A:entry1:chat:m2', saveId: 'A', worldEpoch: 'entry1', chatId: 'm2' },
  ]);
});
test('API mode, stale, inactive and old-schema states never drive agent', async () => {
  for (const change of [{ protocol: 'chat' }, { ageSeconds: 50 }, { worldActive: false }, { schemaVersion: 1 }]) {
    const f = fixture({ ...stateOf(), ...change }); await f.tick();
    assert.equal(f.events.length, 0); assert.equal(f.selected.length, 0);
  }
});
test('failed push is retryable and not pre-acknowledged', async () => {
  const s = stateOf('A', 'entry1', '在吗', 'm1'), target = { saveId: 'A', agent: {} };
  let calls = 0;
  const tick = createGameSync({ readState: async () => s, getTarget: () => target, selectSave: assert.fail,
    push() { return ++calls > 1; }, logger: { warn() {} } });
  await tick(); await tick(); await tick(); assert.equal(calls, 2);
});
test('a switch during selectSave does not deliver stale pending chat', async () => {
  let state = stateOf('A', 'entry1', 'old A', 'm1'), target = { id: 'lobby' }; const sent = [];
  const tick = createGameSync({ readState: async () => state, getTarget: () => target,
    async selectSave(s) { state = stateOf('B'); target = { saveId: s.saveId, agent: {} }; return target; },
    push(_agent, text) { sent.push(text); return true; }, logger: { warn() {} } });
  await tick(); assert.equal(sent.length, 0);
});
test('telemetry, tasks, loot and entry cannot wake the Agent without a real pending player message', async () => {
  const f = fixture(); await f.tick();
  for (const change of [
    { worldIsNew: true }, { situation: '附近有僵尸' }, { loot: '绷带' },
    { tasks: [{ id: 'x', status: 'completed', completed: 1 }] },
    { pendingChat: '只有文本没有ID' }, { pendingChatId: '只有ID没有文本' },
  ]) { f.state({ ...stateOf(), ...change }); await f.tick(); }
  assert.deepEqual(f.events, []);
});

test('scoped write rejects old save, old epoch, API, stale and missing scopes', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-scoped-'));
  try {
    const current = stateOf();
    await assert.rejects(writeScopedCommand(dir, { saveId: 'B', worldEpoch: 'entry1' }, async () => current), /切换/);
    await assert.rejects(writeScopedCommand(dir, { saveId: 'A', worldEpoch: 'old' }, async () => current), /切换/);
    await assert.rejects(writeScopedCommand(dir, {}, async () => current), /显式/);
    for (const change of [{ protocol: 'chat' }, { ageSeconds: 50 }, { worldActive: false }]) {
      await assert.rejects(writeScopedCommand(dir, { saveId: 'A', worldEpoch: 'entry1' }, async () => ({ ...current, ...change })));
    }
    await writeScopedCommand(dir, { saveId: 'A', worldEpoch: 'entry1', say: '用户，我在。', replyTo: '' }, async () => current);
    const saved = JSON.parse(await readFile(join(dir, 'ai-command.json'), 'utf8'));
    assert.equal(saved.saveId, 'A'); assert.equal(saved.worldEpoch, 'entry1'); assert.equal(saved.replyTo, '');
  } finally { await rm(dir, { recursive: true, force: true }); }
});

test('state content and age use same open handle even if path was replaced', async () => {
  const operations = [], mtimeMs = Date.now() - 60000;
  const state = await readBridgeState('virtual-dir', {
    async open(file, mode) {
      operations.push(['open', file, mode]);
      return {
        async readFile(encoding) { operations.push(['read', encoding]); return '\uFEFF{"saveId":"old-world"}'; },
        async stat() { operations.push(['handle-stat']); return { mtimeMs }; },
        async close() { operations.push(['close']); },
      };
    },
  });
  assert.equal(state.saveId, 'old-world'); assert.ok(state.ageSeconds >= 60);
  assert.deepEqual(operations.map(op => op[0]), ['open', 'read', 'handle-stat', 'close']);
});

test('state parse/stat failures close open handle and return explicit read errors', async () => {
  for (const fail of ['parse', 'stat']) {
    let closed = 0;
    const state = await readBridgeState('virtual-dir', { async open() { return {
      async readFile() { return fail === 'parse' ? '{bad' : '{}'; },
      async stat() { throw new Error('file stat unavailable'); },
      async close() { closed++; },
    }; } });
    assert.ok(state.error); assert.equal(state.stateDir, 'virtual-dir'); assert.equal(closed, 1);
  }
});

test('async rejected push remains retryable rather than marking pending message delivered', async () => {
  const state = stateOf('A', 'entry1', '在吗', 'm1'), target = { saveId: 'A', agent: {} };
  let calls = 0;
  const tick = createGameSync({ readState: async () => state, getTarget: () => target, selectSave: assert.fail,
    async push() { if (++calls === 1) throw new Error('delivery rejected'); return true; }, logger: { warn() {} } });
  await tick(); await tick(); await tick(); assert.equal(calls, 2);
});

test('async push finishing after off/on does not mark replacement target delivered', async () => {
  const state = stateOf('A', 'entry1', '在吗', 'm1');
  let target = { saveId: 'A', agent: {} }, release, calls = 0;
  const wait = new Promise(resolve => { release = resolve; });
  const tick = createGameSync({ readState: async () => state, getTarget: () => target, selectSave: assert.fail,
    async push() { if (++calls === 1) await wait; return true; }, logger: { warn() {} } });
  const first = tick(); await new Promise(resolve => setImmediate(resolve));
  target = { saveId: 'A', agent: {} }; release(); await first;
  await tick(); assert.equal(calls, 2);
});

test('async save selection uses latest pending message rather than stale snapshot', async () => {
  let state = stateOf('A', 'entry1', '旧消息', 'm1'), target = { id: 'lobby' }; const events = [];
  const tick = createGameSync({ readState: async () => state, getTarget: () => target,
    async selectSave() { state = stateOf('A', 'entry1', '新消息', 'm2'); target = { saveId: 'A', agent: {} }; return target; },
    push(_agent, text) { events.push(text); return true; }, logger: { warn() {} } });
  await tick(); assert.deepEqual(events, ['新消息']);
});

test('async push world switch is revalidated before dedup acknowledgement', async () => {
  let state = stateOf('A', 'entry1', '在吗', 'm1'), target = { saveId: 'A', agent: {} }, calls = 0;
  const tick = createGameSync({ readState: async () => state, getTarget: () => target, selectSave: assert.fail,
    async push() { if (++calls === 1) state = stateOf('A', 'entry2', '在吗', 'm1'); return true; }, logger: { warn() {} } });
  await tick(); await tick(); assert.equal(calls, 2);
});
