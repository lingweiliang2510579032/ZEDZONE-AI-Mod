import { readFile, writeFile, rename, unlink } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { randomUUID } from 'node:crypto';
import { resolveGamePaths } from './paths.js';
import { readBridgeState } from './bridge.js';

// One transaction per data directory, including different saves; never truncate
// lifecycle events or touch conversation history / the authoritative session map.
const pending = new Map();
const MISSING = Symbol('missing-file');
const object = v => v !== null && typeof v === 'object' && !Array.isArray(v);
const id = v => typeof v === 'string' && v.length > 0 && v.length <= 256 && v.trim() === v;
const save = v => typeof v === 'string' && /^[a-fA-F0-9]{64}$/.test(v);
const pathKey = p => resolve(p).replace(/[\\/]+$/, '').toLowerCase();
const iso = v => typeof v === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|[+-]\d\d:\d\d)$/.test(v) && Number.isFinite(Date.parse(v));

async function json(file, missing = false) {
  try { return JSON.parse((await readFile(file, 'utf8')).replace(/^\uFEFF/, '')); }
  catch (e) { if (missing && e.code === 'ENOENT') return MISSING; throw new Error(`ZED 归档资料无法读取：${file}：${e.message}`); }
}
function lifecycle(value) {
  if (!object(value) || value.schemaVersion !== 1 || !Array.isArray(value.events) || !Array.isArray(value.knownSaves)) throw new Error('ZED 存档生命周期协议无效');
  const events = new Map();
  for (const e of value.events) {
    if (!object(e) || e.type !== 'deleted' || !save(e.saveId) || !id(e.eventId) || !iso(e.deletedAtUtc)) throw new Error('ZED 存档删除事件无效，拒绝归档');
    if (events.has(e.eventId) && JSON.stringify(events.get(e.eventId)) !== JSON.stringify(e)) throw new Error('ZED 存档删除事件ID冲突');
    events.set(e.eventId, e);
  }
  return [...events.values()];
}
function sessionMap(value) {
  if (!object(value) || value.version !== 1 || !object(value.saves) || (value.control != null && (!object(value.control) || !id(value.control.sessionId)))) throw new Error('ZED 会话映射无效，拒绝归档');
  const seen = new Set(value.control ? [value.control.sessionId] : []);
  for (const [key, entry] of Object.entries(value.saves)) {
    if (!save(key) || !object(entry) || !id(entry.sessionId) || !['ready', 'creating'].includes(entry.status) || seen.has(entry.sessionId)) throw new Error('ZED 会话映射重复或损坏，拒绝归档');
    seen.add(entry.sessionId);
  }
  return value;
}
function journal(value) {
  if (value === MISSING) return { schemaVersion: 1, receipts: [] };
  if (!object(value) || value.schemaVersion !== 1 || !Array.isArray(value.receipts)) throw new Error('ZED 归档收据无效，拒绝归档');
  const keys = new Set();
  for (const r of value.receipts) {
    if (!object(r) || !id(r.eventId) || !save(r.saveId) || !id(r.sessionId) || !iso(r.deletedAtUtc) || !iso(r.archivedAtUtc)) throw new Error('ZED 归档收据条目无效');
    if (Object.hasOwn(r, 'restoredAtUtc') && !iso(r.restoredAtUtc)) throw new Error('ZED 归档收据恢复时间无效');
    const key = `${r.eventId}:${r.sessionId}`;
    if (keys.has(key)) throw new Error('ZED 归档收据重复');
    keys.add(key);
  }
  return value;
}
async function writeJournal(file, value) {
  const tmp = `${file}.${randomUUID()}.tmp`;
  try { await writeFile(tmp, JSON.stringify(value, null, 2), { encoding: 'utf8', flag: 'wx' }); await rename(tmp, file); }
  finally { await unlink(tmp).catch(e => { if (e.code !== 'ENOENT') throw e; }); }
}
function activeSame(state, saveId) {
  return state?.worldActive === true && state.saveId === saveId && Number.isFinite(state.ageSeconds) && state.ageSeconds <= 20;
}
function owned(reg, workspacePath, sessionId) {
  const workspaces = reg.list();
  if (!Array.isArray(workspaces)) return false;
  const owners = workspaces.filter(w => Array.isArray(w?.sessionIds) && w.sessionIds.includes(sessionId));
  return owners.length === 1 && typeof (owners[0].path || owners[0].record?.path) === 'string' && pathKey(owners[0].path || owners[0].record.path) === pathKey(workspacePath);
}
async function reconcile(ctx, paths, state) {
  const eventFile = join(paths.stateDir, 'ai-save-lifecycle.json');
  const raw = await json(eventFile, true);
  if (raw === MISSING) return { archived: [] };
  const events = lifecycle(raw);
  if (!events.length) return { archived: [] };
  const reg = ctx.get('workspaceRegistry');
  if (!reg || typeof reg.list !== 'function' || typeof reg.archiveSession !== 'function' || !Array.isArray(reg.archivedSessionIds)) throw new Error('ZED 官方会话归档服务未就绪');
  const mapped = sessionMap(await json(join(paths.stateDir, 'dsh-sessions.json')));
  const receiptFile = join(paths.stateDir, 'dsh-save-archives.json');
  const receipts = journal(await json(receiptFile, true));
  const archived = new Set();
  for (const event of events) {
    const entry = Object.hasOwn(mapped.saves, event.saveId) ? mapped.saves[event.saveId] : null;
    if (!entry || entry.status !== 'ready' || entry.sessionId === mapped.control?.sessionId) continue;
    const sessionId = entry.sessionId;
    const prior = receipts.receipts.find(r => r.eventId === event.eventId && r.sessionId === sessionId);
    if (prior && (prior.saveId !== event.saveId || prior.deletedAtUtc !== event.deletedAtUtc)) throw new Error('ZED 归档收据与事件冲突');
    if (!owned(reg, paths.workspacePath, sessionId) || activeSame(state, event.saveId)) continue;
    // Re-read immediately before the official call: never archive a restored,
    // currently active save based only on an old tombstone or caller snapshot.
    // Map/lifecycle are rechecked after asynchronous IO to avoid acting on an
    // event removed/replaced or a session remapped by another bridge operation.
    const freshEvents = lifecycle(await json(eventFile));
    if (!freshEvents.some(e => e.eventId === event.eventId && e.saveId === event.saveId && e.deletedAtUtc === event.deletedAtUtc)) continue;
    const freshMap = sessionMap(await json(join(paths.stateDir, 'dsh-sessions.json')));
    if (freshMap.saves[event.saveId]?.sessionId !== sessionId || freshMap.saves[event.saveId]?.status !== 'ready' || freshMap.control?.sessionId === sessionId || !owned(reg, paths.workspacePath, sessionId)) continue;
    const latest = await readBridgeState(paths.stateDir);
    if (latest.error || latest.schemaVersion !== 2 || typeof latest.worldActive !== 'boolean' || activeSame(latest, event.saveId)) continue;
    if (prior) {
      // A deliberate unarchive is not undone by an old, already-consumed event.
      if (reg.archivedSessionIds.includes(sessionId)) archived.add(sessionId);
      continue;
    }
    if (!reg.archivedSessionIds.includes(sessionId)) await reg.archiveSession(sessionId, { stopActivity: true });
    // Authoritative already-archived state repairs a lost receipt after a crash.
    receipts.receipts.push({ eventId: event.eventId, saveId: event.saveId, sessionId, deletedAtUtc: event.deletedAtUtc, archivedAtUtc: new Date().toISOString() });
    await writeJournal(receiptFile, receipts);
    archived.add(sessionId);
  }
  return { archived: [...archived] };
}

// Archiving is durable and DSH blocks every wake of an archived session, so a
// save restored from a backup (same gameId + createdDateTime => same saveId)
// would map to the archived conversation and never answer again. The running
// game is the only proof that a specific save is alive, and our own receipt is
// the only proof that *we* archived it: a conversation the user archived by
// hand is never resurrected.
async function restore(ctx, paths, state) {
  const reg = ctx.get('workspaceRegistry');
  if (!reg || typeof reg.unarchiveSession !== 'function' || !Array.isArray(reg.archivedSessionIds)) return { restored: [] };
  if (state?.worldActive !== true || !save(state.saveId) || !activeSame(state, state.saveId)) return { restored: [] };
  if (!reg.archivedSessionIds.length) return { restored: [] };
  const mapped = sessionMap(await json(join(paths.stateDir, 'dsh-sessions.json')));
  const entry = Object.hasOwn(mapped.saves, state.saveId) ? mapped.saves[state.saveId] : null;
  if (!entry || entry.status !== 'ready' || entry.sessionId === mapped.control?.sessionId) return { restored: [] };
  const sessionId = entry.sessionId;
  if (!reg.archivedSessionIds.includes(sessionId)) return { restored: [] };
  const receipts = journal(await json(join(paths.stateDir, 'dsh-save-archives.json'), true));
  // The receipt proves *we* archived this conversation. Its recorded saveId may
  // belong to the retired progress era while the conversation now follows the
  // character, so the match is by session — a conversation we never archived is
  // still never resurrected. A receipt already spent cannot resurrect a session
  // the user archived by hand afterwards.
  const spent = receipts.receipts.filter(r => r.sessionId === sessionId && !r.restoredAtUtc);
  if (!spent.length) return { restored: [] };
  if (!owned(reg, paths.workspacePath, sessionId)) return { restored: [] };
  // Re-read right before writing: the save may have been deleted again, or the
  // mapping remapped, while this ran.
  const freshMap = sessionMap(await json(join(paths.stateDir, 'dsh-sessions.json')));
  if (freshMap.saves[state.saveId]?.sessionId !== sessionId || freshMap.control?.sessionId === sessionId) return { restored: [] };
  const latest = await readBridgeState(paths.stateDir);
  if (latest.error || !activeSame(latest, state.saveId)) return { restored: [] };
  await reg.unarchiveSession(sessionId);
  const at = new Date().toISOString();
  for (const receipt of spent) receipt.restoredAtUtc = at;
  await writeJournal(join(paths.stateDir, 'dsh-save-archives.json'), receipts);
  return { restored: [sessionId] };
}

export async function restoreRestoredSaves(ctx, config, state = {}) {
  const paths = await resolveGamePaths(config);
  const key = pathKey(paths.stateDir);
  const before = pending.get(key) || Promise.resolve();
  const task = before.catch(() => {}).then(() => restore(ctx, paths, state));
  pending.set(key, task);
  try { return await task; }
  finally { if (pending.get(key) === task) pending.delete(key); }
}

export async function reconcileDeletedSaves(ctx, config, state = {}) {
  const paths = await resolveGamePaths(config);
  const key = pathKey(paths.stateDir);
  const before = pending.get(key) || Promise.resolve();
  const task = before.catch(() => {}).then(() => reconcile(ctx, paths, state));
  pending.set(key, task);
  try { return await task; }
  finally { if (pending.get(key) === task) pending.delete(key); }
}
