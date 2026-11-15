import { mkdir, readFile, writeFile, rename, unlink } from "node:fs/promises";
import { join, resolve } from "node:path";

import { resolveGamePaths } from "./paths.js";
import { prepareAgentSetup, installZedModelSelection, ensureZedAgentSetup } from "./model-setup.js";

/** 读会话标题（session/title 事件最后一条）。 */
export function titleOf(session) {
  let title = "";
  for (const event of session.snapshotEvents()) {
    if (event.type === "session/title" && event.data && typeof event.data.title === "string") title = event.data.title;
  }
  return title;
}

function samePath(a, b) {
  return typeof a === "string" && typeof b === "string" && a.toLowerCase() === b.toLowerCase();
}

// Serialize the whole map transaction, including different saves, so atomic writes
// cannot lose each other's entries. Entries disappear when work settles; no agents
// or plugin contexts are retained by this module.
const pendingPaths = new Map();

async function readMap(file) {
  let value;
  try { value = JSON.parse((await readFile(file, "utf8")).replace(/^\uFEFF/, "")); }
  catch (error) {
    if (error.code === "ENOENT") return { version: 1, control: null, saves: {} };
    throw new Error(`ZED 会话映射无法读取，拒绝重复建立：${error.message}`);
  }
  if (!value || value.version !== 1 || !value.saves || typeof value.saves !== "object" || Array.isArray(value.saves)) {
    throw new Error("ZED 会话映射格式错误，拒绝重复建立");
  }
  if (Object.values(value.saves).some(entry => entry == null)) throw new Error("ZED 存档映射存在空条目，拒绝建立");
  const ids = new Set();
  for (const entry of [value.control, ...Object.values(value.saves)]) {
    if (entry == null) continue;
    if (!entry || typeof entry.sessionId !== "string" || !entry.sessionId.trim() ||
        !["creating", "ready"].includes(entry.status) || ids.has(entry.sessionId)) {
      throw new Error("ZED 会话映射条目损坏或重复，拒绝建立");
    }
    ids.add(entry.sessionId);
  }
  return value;
}

async function writeMap(file, value) {
  const tmp = `${file}.${crypto.randomUUID()}.tmp`;
  try {
    await writeFile(tmp, JSON.stringify(value, null, 2), { encoding: "utf8", flag: "wx" });
    await rename(tmp, file);
  } finally { await unlink(tmp).catch(error => { if (error.code !== "ENOENT") throw error; }); }
}

// Returns null when the conversation is provably gone (deleted, or a stub that
// only ever got a title): the caller must drop the stale mapping and start a new
// conversation instead of bricking the whole plugin. A real read failure still
// throws, so corrupt histories are never silently replaced by another save's.
async function inspectSession(ctx, id) {
  const live = ctx.get("sessions")?.get?.(id);
  const inspected = live ? null : await ctx.get("sessionController")?.inspect?.(id);
  const events = live ? live.snapshotEvents() : inspected?.events;
  if (events == null) return null;
  if (typeof events[Symbol.iterator] !== "function") throw new Error(`ZED 会话历史格式异常：${id}`);
  return live || { zedHistoryOnly: true, header: inspected?.meta, snapshotEvents: () => events, requestHeader: () => [...events].reverse().find(event => event.type === 'request/header')?.data?.header };
}

/** No save = homepage control conversation; saves are selected ONLY by map ID. */
export async function ensureZed(ctx, config, save) {
  const paths = await resolveGamePaths(config);
  const dataDir = paths.stateDir;
  const path = paths.workspacePath;
  const saveId = save == null ? null : save.saveId;
  if (save != null && (typeof saveId !== "string" || !saveId.trim())) throw new Error("存档 saveId 不能为空");
  const key = path.toLowerCase();
  const prior = pendingPaths.get(key) || Promise.resolve();
  const task = prior.catch(() => {}).then(() => ensureZedUnlocked(ctx, path, dataDir, saveId, save));
  pendingPaths.set(key, task);
  try { return await task; }
  finally { if (pendingPaths.get(key) === task) pendingPaths.delete(key); }
}

async function ensureZedUnlocked(ctx, path, dataDir, saveId, save) {
  const reg = ctx.get("workspaceRegistry");
  const agents = ctx.get("agents");
  const sessions = ctx.get("sessions");
  if (!reg || typeof reg.create !== "function" || typeof reg.list !== "function") throw new Error("workspaceRegistry 未就绪");
  if (!agents || typeof agents.create !== "function") throw new Error("agents.create 不可用");

  await mkdir(path, { recursive: true });
  await mkdir(dataDir, { recursive: true });
  const mapFile = join(dataDir, "dsh-sessions.json");
  const map = await readMap(mapFile);
  let entry = saveId === null ? map.control : Object.hasOwn(map.saves, saveId) ? map.saves[saveId] : null;
  // The conversation is keyed by the character card, not by a save file. A
  // conversation established under the old progress identity is adopted once,
  // for the world that is running right now — never guessed for another one.
  let adoptedFrom = null;
  if (saveId !== null && !entry && typeof save?.legacySaveId === "string" && save.legacySaveId
      && save.legacySaveId !== saveId && Object.hasOwn(map.saves, save.legacySaveId)
      && map.saves[save.legacySaveId]?.status === "ready") {
    entry = map.saves[save.legacySaveId];
    adoptedFrom = save.legacySaveId;
  }
  // A fresh world has a new progress identity, so the exact match above finds
  // nothing. When the game reports exactly ONE character card, every retired
  // progress conversation belongs to that character: take the most recent one
  // instead of starting from zero. With several cards it is ambiguous, so we
  // refuse to guess.
  if (saveId !== null && !entry && save?.singleCharacter === true) {
    const archived = Array.isArray(ctx.get("workspaceRegistry")?.archivedSessionIds) ? ctx.get("workspaceRegistry").archivedSessionIds : [];
    const candidates = Object.entries(map.saves).filter(([key, value]) => key !== saveId && value?.status === "ready");
    if (candidates.length) {
      const ranked = candidates
        .map(([key, value]) => ({ key, value, rank: archived.indexOf(value.sessionId) }))
        .filter(item => item.rank >= 0)
        .sort((a, b) => b.rank - a.rank);
      if (ranked.length) { entry = ranked[0].value; adoptedFrom = ranked[0].key; }
    }
  }
  if (entry && entry.status !== "ready") throw new Error("ZED 会话上次建立未完成，请先检查映射与历史，暂不重复建立");

  let ws = null;
  let staleId = null;
  for (const w of reg.list() || []) {
    const p = w && (w.path || (w.record && w.record.path));
    if (samePath(p, path)) { ws = w; break; }
  }
  if (!ws) ws = await reg.create(path, "ZED AI");
  if (typeof ws?.attachSession !== "function") throw new Error("工作区 attachSession 不可用");

  let found = entry ? { id: entry.sessionId } : null;
  let history = null;
  if (found) {
    // Even live agents must have readable history; never substitute another save
    // on a missing/corrupt mapped conversation.
    history = await inspectSession(ctx, found.id);
    if (history == null) {
      // The mapped conversation is gone (deleted, or a title-only stub that can
      // never resume). Drop the dead mapping and build a fresh one; keeping it
      // would fail every future start.
      staleId = found.id;
      if (saveId === null) map.control = null;
      else delete map.saves[saveId];
      found = null;
      entry = null;
      await writeMap(mapFile, map).catch(() => {});
    }
  } else if (saveId === null) {
    // Migrate only the exact legacy homepage title. Never adopt save histories.
    for (const id of ws.sessionIds || []) {
      if (typeof id !== "string" || !id.trim()) continue;
      const session = await inspectSession(ctx, id);
      if (!session) continue;
      if (titleOf(session) === "ZED") { found = { id }; history = session; break; }
    }
  }

  if (found) {
    let agent = agents.get?.(found.id);
    if (!agent && typeof agents.resume === "function") {
      try {
        const prepared = await prepareAgentSetup(ctx, history);
        const resumed = await agents.resume({ resumeSessionId: found.id, agentOptions: prepared.agentOptions, setup: prepared.setup });
        agent = resumed?.agent || resumed;
      } catch (error) {
        throw new Error(`ZED 会话暂时无法恢复：${error.message}`);
      }
    }
    if (!agent) throw new Error("ZED 会话暂时无法恢复，请稍后重试");
    await ensureZedAgentSetup(ctx, agent);
    await ws.attachSession(found.id);
    if (adoptedFrom) {
      // Same conversation, new identity: move the mapping only after the session
      // is provably usable, so a failed resume never loses the old key.
      delete map.saves[adoptedFrom];
      Object.defineProperty(map.saves, saveId, { value: entry, enumerable: true, writable: true, configurable: true });
      await writeMap(mapFile, map);
    } else if (!entry) {
      map.control = { sessionId: found.id, status: "ready" };
      await writeMap(mapFile, map);
    }
    return { id: found.id, agent, created: false, workspace: path, saveId, staleId };
  }

  const prepared = await prepareAgentSetup(ctx);
  const sessionId = `session-${crypto.randomUUID()}`;
  entry = { sessionId, status: "creating" };
  if (saveId === null) map.control = entry;
  else Object.defineProperty(map.saves, saveId, { value: entry, enumerable: true, writable: true, configurable: true });
  // Reserve before side effects. Failed create/title/attach remains explicitly
  // blocked on disk, rather than silently making another broken conversation.
  await writeMap(mapFile, map);
  const created = await agents.create({ sessionId, agentOptions: prepared.agentOptions,
    meta: { cwd: path, ...(prepared.agentPreset === undefined ? {} : { agentPreset: prepared.agentPreset }) }, setup: prepared.setup });
  let session = created?.session || created;
  if (typeof session?.append !== "function") session = sessions?.get?.(sessionId);
  if (typeof session?.append !== "function") throw new Error("找不到可写标题的 session 对象");
  const saveName = String(save?.saveName || saveId || "").replace(/[\r\n\t]/g, " ").trim();
  const title = saveId === null ? "ZED" : `ZED · ${Array.from(saveName).slice(0, 48).join("")}`;
  await session.append("session/title", { title, messageSeqs: [], source: { kind: "user" } });
  if (typeof sessions?.flush !== "function" || !await sessions.flush(session)) throw new Error("ZED 会话持久化服务不可用，暂不标记创建完成");
  await ws.attachSession(sessionId);
  const agent = created?.agent || created;
  if (!agent) throw new Error("新建 ZED 会话没有 agent");
  installZedModelSelection(ctx, agent);
  entry.status = "ready";
  await writeMap(mapFile, map);
  return { id: sessionId, agent, created: true, workspace: path, saveId, staleId };
}

/** 注册 /zed 命令：on/off/status。UI 开关与用户手打共用同一条路径。 */
export function registerZedCommand(ctx, control) {
  const commands = ctx.get("commands");
  if (!commands || typeof commands.register !== "function") return;
  try {
    commands.register({
      name: "zed",
      description: "连接或断开 ZED AI 与《隔离区》，并确保 ZED 项目与常驻会话存在",
      input: { hint: "[on|off|status]" },
      recordInput: false,
      handler: async (invocation) => {
        const arg = String((invocation && invocation.rawInput) || "").trim().toLowerCase();
        if (arg === "status") {
          const s = control.status();
          return { kind: "success", text: s.connecting ? "ZED AI 正在连接" : s.connected
            ? `ZED AI 已连接：${s.sessionId}` : `ZED AI 已断开${s.error ? `：${s.error}` : ""}` };
        }
        if (arg && arg !== "on" && arg !== "off") return { kind: "error", text: "用法：/zed on、/zed off、/zed status" };
        try {
          const s = await control.setEnabled(arg !== "off");
          return { kind: "success", text: s.connected ? `ZED AI 已连接：${s.sessionId}` : "ZED AI 已断开" };
        } catch (error) {
          return { kind: "error", text: `ZED AI 连接失败：${error.message}` };
        }
      },
    });
  } catch (error) {
    ctx.logger?.warn?.(`[zed-ai] 注册 /zed 命令失败：${error.message}`);
  }
}
