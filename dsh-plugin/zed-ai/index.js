/**
 * DSH 插件：ZED ZONE（隔离区）AI 搭档桥
 *
 * 游戏里的 MOD 会持续写 ai-state.json / ai-prompt.md；本插件把
 * 「读局面 / 下指令 / 替玩家说话」注册成 DSH 工具，
 * 任何 DSH 会话都能直接驱动游戏里的 AI 搭档或队友。
 *
 * 说明：不 import @deepseek-ai/dsh-tools —— 插件是从 profile 目录按相对路径加载的，
 * 裸包名解析不到。这里直接构造 tools.register() 需要的规范形状
 * （name / description / parameters(JSON Schema) / output{schema,render} / execute）。
 */
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { ensureZed, registerZedCommand } from "./connect.js";
import { createZedControl, registerZedControlRpc } from "./control.js";
import { readBridgeState, bridgeProblem, createGameSync, writeScopedCommand, TASK_TYPES } from "./bridge.js";
import { resolveGamePaths } from "./paths.js";
import { createDurableDelivery } from "./delivery.js";
import { reconcileDeletedSaves, restoreRestoredSaves } from "./save-archive.js";
import { installGameContext } from "./game-context.js";

export const name = "zed-companion";

export const inject = ["tools"];   // sessions / sessionController 用 ctx.get 可选获取，避免因服务名变化导致插件不加载

/** 统一的输出形状：ok + text（text 里放 JSON 或人话），避免动态 schema。 */
const OUTPUT = {
  schema: {
    type: "object",
    additionalProperties: false,
    properties: {
      ok: { type: "boolean" },
      text: { type: "string" },
    },
    required: ["ok", "text"],
  },
  render(_args, value) {
    return [{ type: "text", text: value && value.text ? value.text : "" }];
  },
};

function tool(def) {
  return {
    name: def.name,
    description: def.description,
    parameters: def.parameters,
    output: OUTPUT,
    async execute(args, execution) {
      const value = await def.run(args ?? {}, execution);
      return { ok: true, text: typeof value === "string" ? value : JSON.stringify(value, null, 2) };
    },
  };
}

const readState = readBridgeState;

export function apply(ctx, config) {
  let dir = null;
  let resolvedConfig = config || {};
  let pathError = null;
  const pathsReady = resolveGamePaths(config).then(paths => {
    dir = paths.stateDir; resolvedConfig = { ...config, ...paths }; return paths;
  }).catch(error => { pathError = error; return null; });
  const currentState = async () => {
    await pathsReady;
    return pathError ? { error: pathError.message } : readState(dir);
  };
  let zedTarget = null;
  let connectionContext = null;
  let gameSnapshot = null;
  const control = createZedControl(async () => {
    await pathsReady;
    if (pathError) throw pathError;
    if (!connectionContext) throw new Error("ZED AI 连接服务尚未就绪，请稍后重试");
    const ensured = await ensureZed(connectionContext, resolvedConfig);
    if (ensured?.staleId) ctx.logger?.warn?.(`旧的 ZED 会话已失效，已改用新会话：${ensured.staleId}`);
    return bindContext(ensured);
  }, (target) => { zedTarget = target; }, () => gameSnapshot);
  ctx.effect(() => () => control.dispose());
  const deliver = createDurableDelivery(async () => { await pathsReady; if (pathError) throw pathError; return dir; }, ctx.logger);
  // DSH rejects the model step of an archived Session, so a player line sent
  // there is consumed as `blocked` and silently lost. Leave it pending instead:
  // the game still holds it, and unarchiving in the sidebar delivers it.
  const deliveredArchived = new Set();
  const pushIfLive = async (agent, text, label, metadata) => {
    const sessionId = agent?.session?.id;
    const registry = connectionContext?.get?.("workspaceRegistry");
    const archived = Array.isArray(registry?.archivedSessionIds) && sessionId && registry.archivedSessionIds.includes(sessionId);
    if (!archived) return deliver(agent, text, label, metadata);
    if (!deliveredArchived.has(sessionId)) {
      deliveredArchived.add(sessionId);
      ctx.logger?.warn?.("[zed-ai] 该存档会话已在 DSH 里被归档，暂不投递局内消息；取消归档后会自动补上");
    }
    return false;
  };
  const bindContext = target => { installGameContext(target.agent, async () => {
    const state = await currentState();
    // Only the session bound to the save the game is actually running may speak
    // or command; the homepage control session never owns a save.
    return target.saveId && target.saveId === state.saveId ? state
      : { scopeMismatch: true, error: '该会话不是当前游戏存档，禁止引用其他存档或向游戏写入' };
  }); return target; };
  const sync = createGameSync({
    readState: currentState,
    getTarget: () => zedTarget,
    onSnapshot(state) {
      gameSnapshot = { ...state, syncProblem: bridgeProblem(state),
        sessionId: zedTarget && zedTarget.saveId === state.saveId ? zedTarget.id : null };
    },
    async selectSave(state, previous) {
      if (!connectionContext) return null;
      const next = bindContext(await ensureZed(connectionContext, resolvedConfig, state));
      if (next?.staleId) ctx.logger?.warn?.(`旧的 ZED 会话已失效，已改用新会话：${next.staleId}`);
      return control.replaceTarget(next, previous) ? next : null;
    },
    push: pushIfLive,
    logger: ctx.logger,
  });
  let lifecycleBusy = false;
  const tick = async () => {
    if (lifecycleBusy) return;
    lifecycleBusy = true;
    try {
      await pathsReady;
      if (!pathError && connectionContext) {
        const state = await currentState();
        // A save restored from a backup keeps its identity; unlock our own
        // archive before anything binds that conversation again.
        if (state?.worldActive === true) await restoreRestoredSaves(connectionContext, resolvedConfig, state);
        const result = await reconcileDeletedSaves(connectionContext, resolvedConfig, state);
        // The bound conversation was just archived because the game deleted its
        // save. Drop it and reattach: the game now shows either the homepage
        // control conversation or another surviving save, never the dead one.
        if (result.archived?.includes(zedTarget?.id)) {
          await control.setEnabled(false);
          await control.setEnabled(true).catch(error => ctx.logger?.warn?.(`[zed-ai] 存档已删除，重新连接失败：${error.message}`));
        }
      }
      await sync();
    } catch (error) { ctx.logger?.warn?.(`[zed-ai] 存档同步失败：${error.message}`); }
    finally { lifecycleBusy = false; }
  };
  const timer = setInterval(() => { void tick(); }, 1500);
  ctx.effect(() => () => clearInterval(timer));
  void tick();
  const captureAuthorization = (execution, saveId, allowMenuControl = false) => {
    const target = zedTarget, revision = control.status().revision;
    const callerId = execution?.agent?.session?.id || execution?.agent?.id;
    const check = () => {
      if (!control.status().enabled || control.status().revision !== revision || zedTarget !== target) throw new Error("连接已关闭或目标会话已切换");
      if (allowMenuControl && saveId === "" && callerId && target && target.saveId === null && target.id === callerId) return;
      if (!callerId || !target?.saveId || target.saveId !== saveId || target.id !== callerId) throw new Error("仅当前存档的 ZED 会话可写入游戏；请在对应会话中操作");
    };
    check(); return check;
  };

  ctx.tools.register(tool({
    name: "zed_state",
    description:
      "读取《隔离区（ZED ZONE）》当前局面：玩家/队友的血量与饥渴疲劳、附近威胁、可用目标 goals、最近对话、待回应的玩家消息 pendingChat，以及 tasks 当前执行进度、taskCapabilities 可用动作、taskFacts 制造/背包/武器事实。决定让 AI 搭档做什么、说什么之前先读这个；不得把提交任务当完成。",
    parameters: { type: "object", properties: {}, additionalProperties: false },
    run: async () => ({ ...(await currentState()), stateDir: dir }),
  }));

  ctx.tools.register(tool({
    name: "zed_order",
    description:
      "给《隔离区》里的 AI 下达当前表现形式对应决策。goal 必须取自 zed_state 返回的 goals（或 clear 表示维持现状），say 是要对玩家说的话（注意口语化勿复读）。companion 模式可选 task 下达整理/制造/拾取/装填/手电/容器归并/丢弃任务；npc 模式可选 npc_cmd 下达官方雇佣NPC指令（仅限跟随/驻留/搜集/保护/开火停火/武器偏好/一次性治疗或投雷）；player 模式可用 coop_op（spawn/despawn/tp/follow/combat/guard/scavenge/heal/reload/light）操控 AI 独立玩家角色，或用 room_op（join_code/join_ip/host/leave/status）在菜单或游戏内加入/开启联机房间。写入仅为提交，必须看 tasks/network/avatar 快照确认。",
    parameters: {
      type: "object",
      additionalProperties: false,
      properties: {
        saveId: { type: "string", description: "本次看到的存档 ID，取自 zed_state（主菜单发起 room_op 进房时填空字符串）" },
        worldEpoch: { type: "string", description: "本次世界轮次，取自 zed_state；切档或重进后旧值无效" },
        replyTo: { type: "string", description: "回复玩家时填 pendingChatId；主动提醒留空" },
        goal: { type: "string", description: "目标名（取自 goals），或 clear" },
        say: { type: "string", description: "对玩家说的一句话" },
        reason: { type: "string", description: "可选：为什么这么决定，或拒绝的理由" },
        memoryNote: { type: "string", description: "可选：≤500字，仅把本档玩家明确表达的喜好/约定或可见重要事件记入本档记忆；无新事实留空，不编造" },
        npc_cmd: {
          type: "string",
          enum: [
            "Basic.FollowPlayer",
            "Basic.StayAtPosition",
            "Basic.CollectResource",
            "Basic.CollectSpecifiedItem",
            "Combat.FireAtWill",
            "Combat.ProtectPlayer",
            "Combat.StopFire",
            "WeaponPreference.NoWeaponPreference",
            "WeaponPreference.RangedWeaponOnly",
            "WeaponPreference.MeleeWeaponOnly",
            "OneTime.HealPlayer",
            "OneTime.ThrowGrenade",
          ],
          description: "可选（仅 npc 接管雇佣NPC模式）：官方雇佣NPC指挥指令",
        },
        coop_op: {
          type: "object", additionalProperties: false,
          description: "可选（player 联机玩家模式，API/DSH 均可用）：操控游戏内的 AI 玩家角色",
          properties: {
            op: { type: "string", enum: ["spawn", "despawn", "tp", "come", "follow", "combat", "attack", "guard", "stay", "scavenge", "loot", "heal", "reload", "light", "flashlight", "status"], description: "玩家角色动作" },
            arg: { type: "string", maxLength: 120, description: "可选附加参数" },
          },
          required: ["op"],
        },
        room_op: {
          type: "object", additionalProperties: false,
          description: "可选（player 联机玩家模式，API/DSH 均可用）：进入、创建或退出多人联机房间（支持主菜单或世界内）",
          properties: {
            op: { type: "string", enum: ["join_code", "join", "join_ip", "connect", "host", "open", "leave", "disconnect", "status"], description: "联机房间操作" },
            arg: { type: "string", maxLength: 120, description: "房间码（如 ABCD12）或 IP:端口（如 127.0.0.1:7777）" },
          },
          required: ["op"],
        },
        task: {
          type: "object", additionalProperties: false,
          properties: {
            type: { type: "string", enum: TASK_TYPES, description: "仅用当前available能力；控制项作用于已有任务；flashlight=开关手电（走游戏原生接口，不模拟C键）；sort_containers=视野内已检视容器按分区归并（write=false时只出方案）" },
            target: { type: "string", maxLength: 200, description: "物品名或明确配方ID；discard用本次discardCandidates的单堆token（优先）或唯一精确名，不能猜；sort_containers只能不填或填test（只试搬一件）" },
            repeat: { type: "string", enum: ["count", "continuous"], default: "count", description: "默认限次；仅明确持续授权才continuous" },
            count: { type: "integer", minimum: 1, maximum: 100, default: 1, description: "限次执行数量，默认1；整理/规划/控制/discard只能1次，discard为一次整堆正常丢地" },
          },
          required: ["type"],
        },
      },
      required: ["say", "saveId", "worldEpoch"],
    },
    run: async (args, execution) => {
      if (typeof args.say !== "string" || !args.say.trim()) throw new Error("zed_order 需要非空的 say");
      const allowMenuRoom = Object.hasOwn(args, "room_op") && !Object.hasOwn(args, "task") && args.saveId === "";
      const authorize = captureAuthorization(execution, args.saveId, allowMenuRoom);
      return writeScopedCommand(dir, {
        saveId: args.saveId, worldEpoch: args.worldEpoch, replyTo: args.replyTo || "",
        goal: typeof args.goal === "string" && args.goal.length > 0 ? args.goal : "clear",
        say: args.say,
        reason: typeof args.reason === "string" ? args.reason : "",
        memoryNote: typeof args.memoryNote === "string" ? args.memoryNote.slice(0, 500) : "",
        ...(Object.hasOwn(args, "npc_cmd") ? { npc_cmd: args.npc_cmd } : {}),
        ...(Object.hasOwn(args, "coop_op") ? { coop_op: args.coop_op } : {}),
        ...(Object.hasOwn(args, "room_op") ? { room_op: args.room_op } : {}),
        ...(Object.hasOwn(args, "task") ? { task: args.task } : {}),
      }, currentState, authorize);
    },
  }));

  ctx.tools.register(tool({
    name: "zed_knowledge",
    description:
      "查询《隔离区》游戏知识：制造配方、建造结构、物品资料（用途/价格/重量）、技能、词条(perk)。kind 选 recipes / build / items / skills / perks / all；query 可选关键词过滤。",
    parameters: {
      type: "object",
      additionalProperties: false,
      properties: {
        kind: { type: "string", enum: ["recipes", "build", "items", "skills", "perks", "all"], description: "要查哪一类" },
        query: { type: "string", description: "关键词，例如「绷带」「药」" },
        limit: { type: "number", description: "最多返回多少条，默认 30" },
      },
      required: ["kind"],
    },
    run: async (args) => {
      await pathsReady;
      if (pathError) throw pathError;
      const state = await currentState();
      const stateProblem = bridgeProblem(state, false);
      if (stateProblem) throw new Error(stateProblem);
      const file = join(dir, "ai-knowledge.json");
      let data;
      try {
        data = JSON.parse((await readFile(file, "utf8")).replace(/^\uFEFF/, ""));
      } catch (error) {
        return { error: `知识文件读取失败：${error.message}（进游戏后会自动生成）` };
      }
      if (data?.schemaVersion !== 2 || data.saveId !== state.saveId || data.worldEpoch !== state.worldEpoch) throw new Error("等待游戏生成当前存档及轮次的已解锁知识；拒绝读取旧档或旧版知识");
      const kind = args.kind || "all";
      const q = typeof args.query === "string" ? args.query.trim() : "";
      const limit = Math.max(1, Math.min(80, Number(args.limit) || 30));
      const match = (s) => !q || String(s || "").includes(q);
      const out = {};
      if ((kind === "recipes" || kind === "all") && Array.isArray(data.recipes)) {
        out.recipes = data.recipes.filter((r) => match(r.name) || match(r.need)).slice(0, limit);
        out.recipesTotal = data.recipes.length;
      }
      if ((kind === "items" || kind === "all") && Array.isArray(data.items)) {
        out.items = data.items.filter((it) => match(it.name) || match(it.desc)).slice(0, limit);
        out.itemsTotal = data.items.length;
      }
      if ((kind === "build" || kind === "all") && Array.isArray(data.build)) {
        out.build = data.build.filter((b) => match(b.name) || match(b.need)).slice(0, limit);
        out.buildTotal = data.build.length;
      }
      if ((kind === "perks" || kind === "all") && data.perks) out.perks = data.perks;
      if ((kind === "skills" || kind === "all") && data.skills) out.skills = data.skills;
      out.ts = data.ts;
      return out;
    },
  }));
  ctx.tools.register(tool({
    name: "zed_chat",
    description:
      "把一句话当成玩家在游戏里说的，送进 AI 搭档的对话通道（会触发它回应）。适合替玩家传话，或测试对话链路是否打通。",
    parameters: {
      type: "object",
      additionalProperties: false,
      properties: {
        text: { type: "string", description: "玩家说的话" },
        saveId: { type: "string", description: "取自 zed_state 的存档 ID" },
        worldEpoch: { type: "string", description: "取自 zed_state 的世界轮次" },
      },
      required: ["text", "saveId", "worldEpoch"],
    },
    run: async (args, execution) => {
      if (typeof args.text !== "string" || args.text.length === 0) throw new Error("zed_chat 需要非空的 text");
      const authorize = captureAuthorization(execution, args.saveId);
      return writeScopedCommand(dir, { chat: args.text, saveId: args.saveId, worldEpoch: args.worldEpoch }, currentState, authorize);
    },
  }));

  // UI 使用无会话 RPC；用户手打 /zed 命令复用同一个真实连接状态。
  ctx.inject(["commands"], (scoped) => { registerZedCommand(scoped, control); });
  ctx.inject(["connection"], (scoped) => {
    registerZedControlRpc(scoped, control);
    scoped.logger?.info?.("[zed-ai] 控制接口已注册：/api/zed-ai/status、/api/zed-ai/setEnabled");
  });
  ctx.inject(["workspaceRegistry", "agents", "sessions"], (scoped) => {
    connectionContext = scoped;
    scoped.effect(() => () => {
      if (connectionContext === scoped) connectionContext = null;
      void control.setEnabled(false).catch(() => {});
    });
    if (!config || config.autoConnect !== false) {
      control.setEnabled(true)
        .then((s) => { if (s.connected) ctx.logger?.info?.(`[zed-ai] 自动连接成功：${s.sessionId}`); })
        .catch((e) => ctx.logger?.warn?.(`[zed-ai] 自动连接失败：${e.message}`));
    }
  });
}
