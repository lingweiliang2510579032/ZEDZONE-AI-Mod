import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createDurableDelivery, deliveryId } from '../delivery.js';

const metadata = (chatId = 'm1', saveId = 'A', worldEpoch = 'e1') => ({ key: `${saveId}:${worldEpoch}:chat:${chatId}`, saveId, worldEpoch, chatId });
async function fixture(run) {
  const dir = await mkdtemp(join(tmpdir(), 'zed-delivery-'));
  try { await run(dir); } finally { await rm(dir, { recursive: true, force: true }); }
}
function agentOf(events = []) {
  const received = [];
  return { session: { id: 'session-A', snapshotEvents: () => events }, received, followup(message) { received.push(message); } };
}
const ledgerAt = async dir => JSON.parse(await readFile(join(dir, 'dsh-deliveries.json'), 'utf8'));

test('durable delivery sends only exact player text with user source and deterministic identity', async () => fixture(async dir => {
  const a = agentOf(), text = '帮我整理背包\n别丢木材 {{model}}';
  assert.equal(await createDurableDelivery(async () => dir)(a, text, '玩家消息', metadata()), true);
  assert.deepEqual(a.received, [{ role: 'user', id: deliveryId(metadata().key), content: [{ type: 'text', text }], source: { kind: 'user' } }]);
  const record = (await ledgerAt(dir)).entries[deliveryId(metadata().key)];
  assert.equal(record.sessionId, 'session-A'); assert.equal(record.status, 'sent');
}));

test('same ID is delivered once across new delivery instances and concurrent same-instance calls', async () => fixture(async dir => {
  const a = agentOf(), first = createDurableDelivery(async () => dir);
  await Promise.all([first(a, '在吗', '玩家消息', metadata()), first(a, '在吗', '玩家消息', metadata())]);
  await createDurableDelivery(async () => dir)(a, '在吗', '玩家消息', metadata());
  assert.equal(a.received.length, 1);
}));

test('identical text with different chat IDs and epochs remains separate real player messages', async () => fixture(async dir => {
  const a = agentOf(), deliver = createDurableDelivery(async () => dir);
  await deliver(a, '在吗', '玩家消息', metadata());
  await deliver(a, '在吗', '玩家消息', metadata('m2'));
  await deliver(a, '在吗', '玩家消息', metadata('m2', 'A', 'e2'));
  assert.equal(a.received.length, 3); assert.equal(new Set(a.received.map(m => m.id)).size, 3);
}));

test('reserve is persisted before enqueue, and a reserved crash window fails closed across restart', async () => fixture(async dir => {
  const a = agentOf();
  a.followup = async message => {
    const entry = (await ledgerAt(dir)).entries[message.id];
    assert.equal(entry.status, 'reserved'); a.received.push(message);
  };
  await createDurableDelivery(async () => dir)(a, '在吗', '玩家消息', metadata());
  const id = deliveryId(metadata('m2').key);
  await writeFile(join(dir, 'dsh-deliveries.json'), JSON.stringify({ version: 1, entries: { [id]: { status: 'reserved', sessionId: a.session.id } } }));
  await createDurableDelivery(async () => dir)(a, '后来这条是否入队未知', '玩家消息', metadata('m2'));
  assert.equal(a.received.length, 1);
}));

test('explicit enqueue rejection clears reservation so a later attempt may be accepted once', async () => fixture(async dir => {
  const a = agentOf(); let attempts = 0;
  a.followup = async message => { if (++attempts === 1) throw new Error('not accepted'); a.received.push(message); };
  const deliver = createDurableDelivery(async () => dir);
  await assert.rejects(deliver(a, '在吗', '玩家消息', metadata()), /not accepted/);
  assert.equal((await ledgerAt(dir)).entries[deliveryId(metadata().key)], undefined);
  await deliver(a, '在吗', '玩家消息', metadata());
  assert.equal(attempts, 2); assert.equal(a.received.length, 1);
}));

test('an explicit false enqueue result is not committed as a successful delivery', async () => fixture(async dir => {
  const a = agentOf(); let calls = 0;
  a.followup = message => { if (++calls === 1) return false; a.received.push(message); };
  const deliver = createDurableDelivery(async () => dir);
  let accepted;
  try { accepted = await deliver(a, '在吗', '玩家消息', metadata()); } catch { accepted = false; }
  assert.equal(accepted, false);
  await deliver(a, '在吗', '玩家消息', metadata());
  assert.equal(a.received.length, 1); assert.equal(calls, 2);
}));

test('malformed ledger and identity/session mismatch stop before enqueue', async () => fixture(async dir => {
  const a = agentOf(), deliver = createDurableDelivery(async () => dir);
  await writeFile(join(dir, 'dsh-deliveries.json'), '{invalid');
  await assert.rejects(deliver(a, '在吗', '玩家消息', metadata()), /投递记录/);
  await writeFile(join(dir, 'dsh-deliveries.json'), JSON.stringify({ version: 1, entries: { [deliveryId(metadata().key)]: { status: 'sent', sessionId: 'other-session' } } }));
  await assert.rejects(deliver(a, '在吗', '玩家消息', metadata()), /身份与存档会话不一致/);
  assert.deepEqual(a.received, []);
}));

test('pre-upgrade real inbox-spliced history recovers matching old-body scope and chat ID without resend', async () => fixture(async dir => {
  const a = agentOf([{ type: 'agent/inbox/spliced', data: { inserted: [{ id: 'old-random', content: [{ type: 'text', text: '【存档边界】saveId=A；worldEpoch=e1。【玩家刚说｜消息ID m1】在吗' }] }] } }]);
  await createDurableDelivery(async () => dir)(a, '在吗', '玩家消息', metadata());
  assert.deepEqual(a.received, []);
  assert.equal((await ledgerAt(dir)).entries[deliveryId(metadata().key)].recovered, true);
}));

test('same text or chat ID in a different historical scope does not suppress current player input', async () => fixture(async dir => {
  const a = agentOf([{ type: 'agent/inbox/spliced', data: { inserted: [{ content: [{ type: 'text', text: '【存档边界】saveId=B；worldEpoch=e0。【玩家刚说｜消息ID m1】在吗' }] }] } }]);
  await createDurableDelivery(async () => dir)(a, '在吗', '玩家消息', metadata());
  assert.equal(a.received.length, 1);
}));

test('historical scope and chat identity prefix collisions cannot suppress a different current input', async () => fixture(async dir => {
  const a = agentOf([{ type: 'agent/inbox/spliced', data: { inserted: [{ content: [{ type: 'text', text: '【存档边界】saveId=AA；worldEpoch=e11。【玩家刚说｜消息ID m10】在吗' }] }] } }]);
  await createDurableDelivery(async () => dir)(a, '在吗', '玩家消息', metadata());
  assert.equal(a.received.length, 1);
}));

test('deterministic inbox message ID is recovered even when content is pure text', async () => fixture(async dir => {
  const a = agentOf([{ type: 'agent/inbox/spliced', data: { inserted: [{ id: deliveryId(metadata().key), content: [{ type: 'text', text: '在吗' }] }] } }]);
  await createDurableDelivery(async () => dir)(a, '在吗', '玩家消息', metadata());
  assert.deepEqual(a.received, []);
}));

test('missing delivery metadata or Agent identity never enqueues', async () => fixture(async dir => {
  const a = agentOf(), deliver = createDurableDelivery(async () => dir);
  await assert.rejects(deliver(a, '在吗', '玩家消息'), /投递身份/);
  await assert.rejects(deliver({ followup: assert.fail }, '在吗', '玩家消息', metadata()), /投递身份/);
  assert.deepEqual(a.received, []);
}));
