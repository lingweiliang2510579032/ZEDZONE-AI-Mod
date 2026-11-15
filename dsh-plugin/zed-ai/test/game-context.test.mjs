import test from 'node:test';
import assert from 'node:assert/strict';
import { installGameContext } from '../game-context.js';
import { deliveryId } from '../delivery.js';

const stateOf = change => ({ schemaVersion: 2, worldActive: true, aiEnabled: true, ageSeconds: 0,
  protocol: 'agent-dsh', backend: 'bridge', saveId: 'A', worldEpoch: 'e1',
  pendingChat: '帮我整理背包', pendingChatId: 'm1',
  context: { npc: '大肥鱼', player: '用户', persona: '软软的 {{model}} 与 {{unregistered}}', memoryContext: '只属于A的真实记忆' },
  tasks: [{ id: 't', status: 'waiting', completed: 0 }], taskFacts: { inventory: '一堆木材' }, taskCapabilities: { organize: { available: true } },
  goals: ['follow'], situation: '安全', ...change });
function fixture(initial = stateOf()) {
  let state = initial;
  const hooks = [], chat = [];
  const agent = { ctx: { on(name, listener) { hooks.push({ name, listener }); return () => {}; } }, followup: value => chat.push(value) };
  const read = async () => state;
  installGameContext(agent, read);
  const assemble = async () => hooks.find(h => h.name === 'system-prompt/assemble').listener({}, {}, async () => ({ sections: [{ name: 'base', text: 'keep' }], variables: { model: 'selected-model' }, extra: 7 }));
  const claim = id => hooks.find(h => h.name === 'agent/inbox/claimed').listener({ message: { id } });
  return { agent, hooks, chat, read, assemble, claim, set: value => { state = value; } };
}

test('game configuration is a scoped noninterpolated system section, never a synthetic user message', async () => {
  const f = fixture();
  const result = await f.assemble();
  assert.deepEqual(f.hooks.map(h => h.name), ['agent/inbox/claimed', 'system-prompt/assemble']);
  assert.deepEqual(f.chat, []);
  assert.equal(result.extra, 7); assert.deepEqual(result.variables, { model: 'selected-model' });
  assert.deepEqual(result.sections[0], { name: 'base', text: 'keep' });
  const section = result.sections[1];
  assert.equal(section.name, 'zed:game-context'); assert.equal(section.interpolate, false);
  assert.match(section.text, /大肥鱼/); assert.match(section.text, /只属于A的真实记忆/);
  assert.match(section.text, /\{\{model\}\} 与 \{\{unregistered\}\}/);
  assert.match(section.text, /saveId=A.*worldEpoch=e1/);
  assert.match(section.text, /taskFacts.*一堆木材/); assert.match(section.text, /tasks.*waiting/);
  assert.match(section.text, /当前真实玩家消息.*帮我整理背包/); assert.match(section.text, /replyTo/);
  assert.match(section.text, /最终回复只写实际say/); assert.match(section.text, /最多一次成功zed_order/);
});

test('context installation is once per Agent and reads fresh state per assembly', async () => {
  const f = fixture(); installGameContext(f.agent, f.read); assert.equal(f.hooks.length, 2);
  await f.assemble(); f.set(stateOf({ pendingChat: '在吗', pendingChatId: 'm2', context: { npc: '新名字' } }));
  const text = (await f.assemble()).sections[1].text;
  assert.match(text, /新名字/); assert.match(text, /当前可回复消息ID：m2/); assert.match(text, /当前真实玩家消息.*在吗/);
  assert.doesNotMatch(text, /只属于A的真实记忆/);
});

test('no pending message explicitly forbids restarting remembered tasks', async () => {
  const f = fixture(stateOf({ pendingChat: '', pendingChatId: '' }));
  assert.match((await f.assemble()).sections[1].text, /无新玩家消息，不从记忆重启旧任务/);
  assert.deepEqual(f.chat, []);
});

test('stale, API, disabled, inactive and old schema context becomes read-only without leaking old facts', async () => {
  for (const change of [{ ageSeconds: 21 }, { protocol: 'chat', backend: 'api' }, { aiEnabled: false }, { worldActive: false }, { schemaVersion: 1 }, { error: '该会话不是当前游戏存档' }]) {
    const f = fixture(stateOf(change)); const section = (await f.assemble()).sections[1];
    assert.equal(section.interpolate, false); assert.match(section.text, /当前游戏联动不可写/);
    assert.match(section.text, /不要向游戏提交指令，不引用旧局面/);
    assert.doesNotMatch(section.text, /只属于A的真实记忆|帮我整理背包|一堆木材/);
    assert.deepEqual(f.chat, []);
  }
});

test('separate Agents never inherit other save context', async () => {
  const a = fixture(), b = fixture(stateOf({ saveId: 'B', context: { memoryContext: '仅B记忆' } }));
  assert.match((await a.assemble()).sections[1].text, /只属于A的真实记忆/);
  const text = (await b.assemble()).sections[1].text;
  assert.match(text, /仅B记忆/); assert.doesNotMatch(text, /只属于A的真实记忆/);
});

test('an old queued turn cannot inherit new pending player text or task authorization', async () => {
  const f = fixture(); f.claim(deliveryId('A:e1:chat:m1'));
  f.set(stateOf({ pendingChat: '新要求丢弃材料', pendingChatId: 'm2' }));
  const text = (await f.assemble()).sections[1].text;
  assert.match(text, /当前处理的聊天已过期/); assert.match(text, /当前游戏联动不可写/);
  assert.doesNotMatch(text, /新要求丢弃材料|当前可回复消息ID：m2/);
});

test('current claimed deterministic message receives only its matching fresh context', async () => {
  const f = fixture(); f.claim(deliveryId('A:e1:chat:m1'));
  const text = (await f.assemble()).sections[1].text;
  assert.match(text, /当前真实玩家消息.*帮我整理背包/); assert.match(text, /当前可回复消息ID：m1/);
  assert.doesNotMatch(text, /当前游戏联动不可写/);
});

test('missing scoped context rejects instead of silently losing persona configuration', () => {
  assert.throws(() => installGameContext({}, async () => stateOf()), /上下文作用域/);
});

test('a message typed directly in DSH is never mistaken for a stale game turn', async () => {
  const f = fixture(); f.claim('f9c1f0f0-0000-4000-8000-000000000000');
  const text = (await f.assemble()).sections[1].text;
  assert.doesNotMatch(text, /当前处理的聊天已过期|当前游戏联动不可写/);
  assert.match(text, /当前真实玩家消息.*帮我整理背包/);
});

test('an already-consumed game turn stays writable once the game has no newer pending chat', async () => {
  const f = fixture(); f.claim(deliveryId('A:e1:chat:m1'));
  f.set(stateOf({ pendingChat: '', pendingChatId: '' }));
  const text = (await f.assemble()).sections[1].text;
  assert.doesNotMatch(text, /当前处理的聊天已过期/);
  assert.match(text, /无新玩家消息，不从记忆重启旧任务/);
});

test('a session bound to another save reports the mismatch as read-only, not as missing data', async () => {
  const f = fixture(stateOf({ scopeMismatch: true, error: '该会话不是当前游戏存档，禁止引用其他存档或向游戏写入' }));
  const section = (await f.assemble()).sections[1];
  assert.equal(section.interpolate, false);
  assert.match(section.text, /该会话不是当前游戏存档/);
  assert.doesNotMatch(section.text, /等待游戏生成数据/);
  assert.doesNotMatch(section.text, /只属于A的真实记忆|帮我整理背包/);
});
