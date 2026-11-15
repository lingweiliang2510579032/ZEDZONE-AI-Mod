import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, readFile, rm, mkdir, readdir } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { reconcileDeletedSaves, restoreRestoredSaves } from '../save-archive.js';

const A = 'a'.repeat(64), B = 'b'.repeat(64);
const event = (saveId = A, eventId = 'deleted-a') => ({ type: 'deleted', saveId, eventId, deletedAtUtc: '2026-11-02T07:49:00.1234567Z' });
async function fixture(fn) {
  const dir = await mkdtemp(join(tmpdir(), 'zed-archive-'));
  const write = (name, value) => writeFile(join(dir, name), typeof value === 'string' ? value : JSON.stringify(value));
  const map = { version: 1, control: { sessionId: 'control', status: 'ready' }, saves: { [A]: { sessionId: 'session-a', status: 'ready' }, [B]: { sessionId: 'session-b', status: 'ready' } } };
  const calls = [], ws = { path: dir, sessionIds: ['control', 'session-a', 'session-b'] };
  const reg = { archivedSessionIds: [], list: () => [ws], async archiveSession(id, options) { calls.push({ id, options }); this.archivedSessionIds.push(id); },
    async unarchiveSession(id) { this.archivedSessionIds = this.archivedSessionIds.filter(value => value !== id); calls.push({ id, unarchive: true }); } };
  const ctx = { get: name => name === 'workspaceRegistry' ? reg : undefined };
  const config = { stateDir: dir, workspacePath: dir };
  const lifecycle = { schemaVersion: 1, knownSaves: [], events: [event()] };
  await write('ai-save-lifecycle.json', lifecycle); await write('dsh-sessions.json', map); await write('ai-state.json', { schemaVersion: 2, worldActive: false });
  try {
    await fn({ dir, write, map, calls, ws, reg, ctx, config, lifecycle,
      run: state => reconcileDeletedSaves(ctx, config, state),
      restore: state => restoreRestoredSaves(ctx, config, state) });
  }
  finally { await rm(dir, { recursive: true, force: true }); }
}

test('validated explicit deletion archives only mapped save using official stopActivity and keeps map/history', () => fixture(async f => {
  const original = await readFile(join(f.dir, 'dsh-sessions.json'), 'utf8');
  assert.deepEqual(await f.run(), { archived: ['session-a'] });
  assert.deepEqual(f.calls, [{ id: 'session-a', options: { stopActivity: true } }]);
  assert.equal(await readFile(join(f.dir, 'dsh-sessions.json'), 'utf8'), original);
  const receipt = JSON.parse(await readFile(join(f.dir, 'dsh-save-archives.json'), 'utf8'));
  assert.equal(receipt.receipts[0].eventId, 'deleted-a');
  assert.equal(receipt.receipts[0].saveId, A);
}));

test('missing lifecycle file silently noops even without services', () => fixture(async f => {
  await rm(join(f.dir, 'ai-save-lifecycle.json'));
  assert.deepEqual(await reconcileDeletedSaves({ get: () => undefined }, f.config), { archived: [] });
  assert.equal(f.calls.length, 0);
}));

test('bad JSON, null, bad schema, unknown event and invalid id/date never archive', () => fixture(async f => {
  for (const raw of ['{broken', 'null', { schemaVersion: 2, events: [], knownSaves: [] }, { schemaVersion: 1, events: [null], knownSaves: [] }, { schemaVersion: 1, events: [{ ...event(), type: 'inactive' }], knownSaves: [] }, { schemaVersion: 1, events: [{ ...event(), saveId: '../save' }], knownSaves: [] }, { schemaVersion: 1, events: [{ ...event(), deletedAtUtc: 'yesterday' }], knownSaves: [] }]) {
    await f.write('ai-save-lifecycle.json', raw);
    await assert.rejects(f.run());
  }
  assert.equal(f.calls.length, 0);
}));

test('lifecycle read failure does not infer a deletion', () => fixture(async f => {
  await rm(join(f.dir, 'ai-save-lifecycle.json')); await mkdir(join(f.dir, 'ai-save-lifecycle.json'));
  await assert.rejects(f.run(), /无法读取/); assert.equal(f.calls.length, 0);
}));

test('unmapped deleted saves and creating mappings are ignored', () => fixture(async f => {
  f.lifecycle.events = [event('c'.repeat(64)), event(B, 'b')];
  f.map.saves[B].status = 'creating'; await f.write('dsh-sessions.json', f.map); await f.write('ai-save-lifecycle.json', f.lifecycle);
  assert.deepEqual(await f.run(), { archived: [] }); assert.equal(f.calls.length, 0);
}));

test('control identity is never archived even through a malicious duplicate save mapping', () => fixture(async f => {
  f.map.saves[A].sessionId = 'control'; await f.write('dsh-sessions.json', f.map);
  await assert.rejects(f.run(), /映射/); assert.equal(f.calls.length, 0);
}));

test('wrong or ambiguous workspace ownership never archives', () => fixture(async f => {
  f.ws.path = join(f.dir, 'other'); assert.deepEqual(await f.run(), { archived: [] });
  f.ws.path = f.dir; f.reg.list = () => [f.ws, { path: join(f.dir, 'foreign'), sessionIds: ['session-a'] }];
  assert.deepEqual(await f.run(), { archived: [] }); assert.equal(f.calls.length, 0);
}));

test('current fresh active same save conflicts with old deleted event', () => fixture(async f => {
  assert.deepEqual(await f.run({ worldActive: true, saveId: A, ageSeconds: 0 }), { archived: [] });
  assert.equal(f.calls.length, 0);
}));

test('latest on-disk active same save prevents archive regardless of caller snapshot', () => fixture(async f => {
  await f.write('ai-state.json', { schemaVersion: 2, worldActive: true, saveId: A });
  assert.deepEqual(await f.run({ worldActive: false }), { archived: [] }); assert.equal(f.calls.length, 0);
}));

test('missing or unreadable current state fails closed, not treated as evidence of deletion', () => fixture(async f => {
  await f.write('ai-state.json', '{broken'); assert.deepEqual(await f.run(), { archived: [] });
  await rm(join(f.dir, 'ai-state.json')); assert.deepEqual(await f.run(), { archived: [] }); assert.equal(f.calls.length, 0);
}));

test('concurrent reconciles serialize archive and receipts without duplicate calls', () => fixture(async f => {
  const results = await Promise.all([f.run(), f.run(), f.run()]);
  assert.equal(f.calls.length, 1); assert.ok(results.every(r => r.archived.includes('session-a')));
  const receipt = JSON.parse(await readFile(join(f.dir, 'dsh-save-archives.json'), 'utf8'));
  assert.equal(receipt.receipts.length, 1);
  assert.ok(!(await readdir(f.dir)).some(name => name.endsWith('.tmp')));
}));

test('official archive rejection creates no receipt and next reconciliation retries', () => fixture(async f => {
  f.reg.archiveSession = async () => { throw new Error('busy archive rejected'); };
  await assert.rejects(f.run(), /busy archive/);
  await assert.rejects(readFile(join(f.dir, 'dsh-save-archives.json')), { code: 'ENOENT' });
  f.reg.archiveSession = async id => { f.calls.push(id); f.reg.archivedSessionIds.push(id); };
  assert.deepEqual(await f.run(), { archived: ['session-a'] }); assert.equal(f.calls.length, 1);
}));

test('authoritative archived ids repair a lost receipt without another archive call', () => fixture(async f => {
  f.reg.archivedSessionIds.push('session-a');
  assert.deepEqual(await f.run(), { archived: ['session-a'] }); assert.equal(f.calls.length, 0);
  const receipt = JSON.parse(await readFile(join(f.dir, 'dsh-save-archives.json'), 'utf8'));
  assert.equal(receipt.receipts.length, 1);
}));

test('corrupt receipt journal blocks action instead of overwriting evidence', () => fixture(async f => {
  await f.write('dsh-save-archives.json', { schemaVersion: 1, receipts: [null] });
  await assert.rejects(f.run(), /收据/); assert.equal(f.calls.length, 0);
}));

test('multiple events retain every receipt and never include control or unrelated session', () => fixture(async f => {
  f.lifecycle.events = [event(), event(B, 'deleted-b')]; await f.write('ai-save-lifecycle.json', f.lifecycle);
  assert.deepEqual(await f.run(), { archived: ['session-a', 'session-b'] });
  const receipt = JSON.parse(await readFile(join(f.dir, 'dsh-save-archives.json'), 'utf8'));
  assert.equal(receipt.receipts.length, 2);
  assert.deepEqual(JSON.parse(await readFile(join(f.dir, 'ai-save-lifecycle.json'), 'utf8')), f.lifecycle);
}));

// A restored save keeps its identity, so it maps back to the archived
// conversation; DSH blocks waking an archived session, so it must be unlocked.
const active = extra => ({ schemaVersion: 2, worldActive: true, saveId: A, ageSeconds: 0, ...extra });

test('a save restored after our own deletion unlocks its conversation again', () => fixture(async f => {
  assert.deepEqual(await f.run(), { archived: ['session-a'] });
  f.calls.length = 0;
  await f.write('ai-state.json', active());
  f.reg.unarchiveSession = async function (id) { this.archivedSessionIds = this.archivedSessionIds.filter(v => v !== id); f.calls.push(id); };
  assert.deepEqual(await f.restore(active()), { restored: ['session-a'] });
  assert.deepEqual(f.calls, ['session-a']);
  assert.deepEqual(f.reg.archivedSessionIds, []);
  // Consuming the same tombstone again must not re-archive what we just unlocked.
  assert.deepEqual(await f.run(active()), { archived: [] });
  assert.deepEqual(f.reg.archivedSessionIds, []);
}));

test('a conversation the user archived by hand is never resurrected', () => fixture(async f => {
  f.reg.archivedSessionIds.push('session-a');   // no receipt exists: not our archive
  await f.write('ai-state.json', active());
  assert.deepEqual(await f.restore(active()), { restored: [] });
  assert.deepEqual(f.reg.archivedSessionIds, ['session-a']);
}));

test('a spent receipt cannot resurrect a conversation archived again later', () => fixture(async f => {
  await f.run(); f.calls.length = 0;
  await f.write('ai-state.json', active());
  assert.deepEqual(await f.restore(active()), { restored: ['session-a'] });
  assert.deepEqual(await f.restore(active()), { restored: [] }, 'the same deletion unlocks only once');
  f.reg.archivedSessionIds.push('session-a');   // user archives it by hand afterwards
  assert.deepEqual(await f.restore(active()), { restored: [] });
  assert.deepEqual(f.reg.archivedSessionIds, ['session-a']);
  const receipt = JSON.parse(await readFile(join(f.dir, 'dsh-save-archives.json'), 'utf8'));
  assert.ok(receipt.receipts.every(r => typeof r.restoredAtUtc === 'string'), 'restore is recorded in the receipt');
}));

test('restore needs a live save, a matching mapping and unambiguous ownership', () => fixture(async f => {
  await f.run(); f.calls.length = 0;
  await f.write('ai-state.json', active());
  for (const state of [active({ worldActive: false }), active({ saveId: B }), active({ ageSeconds: 60 })]) {
    assert.deepEqual(await f.restore(state), { restored: [] });
  }
  f.reg.list = () => [f.ws, { path: join(f.dir, 'foreign'), sessionIds: ['session-a'] }];
  assert.deepEqual(await f.restore(active()), { restored: [] });
  f.reg.list = () => [f.ws];
  f.map.saves[A].sessionId = 'control'; await f.write('dsh-sessions.json', f.map);
  await assert.rejects(f.restore(active()), /映射/);
  assert.deepEqual(f.calls, []);
}));

test('stale on-disk state blocks restore even when the caller claims the save is alive', () => fixture(async f => {
  await f.run(); f.calls.length = 0;
  await f.write('ai-state.json', { schemaVersion: 2, worldActive: false });
  assert.deepEqual(await f.restore(active()), { restored: [] });
  await f.write('ai-state.json', '{broken');
  assert.deepEqual(await f.restore(active()), { restored: [] });
  assert.deepEqual(f.calls, []);
}));
