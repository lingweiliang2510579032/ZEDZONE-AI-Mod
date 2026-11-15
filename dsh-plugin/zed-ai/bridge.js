import { open, writeFile, rename, mkdir, unlink } from 'node:fs/promises';
import { join } from 'node:path';

export async function readBridgeState(dir, io = { open }) {
  const file = join(dir, 'ai-state.json');
  let handle;
  try {
    // Read content and mtime from one opened file: an atomic replacement must
    // not pair the old world's bytes with a new world's fresh timestamp.
    handle = await io.open(file, 'r');
    const data = JSON.parse((await handle.readFile('utf8')).replace(/^\uFEFF/, ''));
    const info = await handle.stat();
    data.ageSeconds = Math.max(0, (Date.now() - info.mtimeMs) / 1000);
    return data;
  } catch (error) { return { error: error.message, stateDir: dir }; }
  finally { if (handle) await handle.close().catch(() => {}); }
}
export function bridgeProblem(state, requireAgent = true) {
  if (state?.error) return `等待游戏生成数据：${state.error}`;
  if (state?.schemaVersion !== 2) return '游戏 MOD 数据协议尚未升级，请重启游戏加载新版 ZEDAI.dll';
  if (!state.worldActive || !state.saveId || !state.worldEpoch) return '当前未进入可识别的存档，等待加载游戏';
  if (!Number.isFinite(state.ageSeconds) || state.ageSeconds > 20) return '游戏数据已过期，可能已退出游戏或暂停了 MOD';
  if (state.aiEnabled === false) return '游戏内 AI 功能已关闭';
  if (requireAgent && (state.protocol !== 'agent-dsh' || (state.backend && state.backend !== 'bridge'))) return '当前是 API/其他后端，DSH 只显示同步数据，不自动下令';
  return '';
}
export const scopeOf = state => `${state.saveId || ''}:${state.worldEpoch || ''}`;
export const TASK_TYPES = ['organize', 'reload', 'loot', 'craft', 'craft_plan', 'discard', 'flashlight', 'sort_containers', 'pause', 'resume', 'cancel'];
export const taskList = state => Array.isArray(state?.tasks) ? state.tasks : state?.tasks && typeof state.tasks === 'object' ? [state.tasks] : [];
export function normalizeTask(task) {
  if (!task || typeof task !== 'object' || Array.isArray(task)) throw new Error('task 必须是任务对象');
  if (Object.keys(task).some(key => !['type', 'target', 'repeat', 'count'].includes(key))) throw new Error('task 包含不支持的字段');
  if (!TASK_TYPES.includes(task.type)) throw new Error('不支持的 task.type');
  const repeat = task.repeat === undefined ? 'count' : task.repeat;
  const count = task.count === undefined ? 1 : task.count;
  if (!['count', 'continuous'].includes(repeat)) throw new Error('task.repeat 必须是 count 或 continuous');
  if (!Number.isInteger(count) || count < 1 || count > 100) throw new Error('task.count 必须是 1..100 的整数');
  if (task.target !== undefined && (typeof task.target !== 'string' || task.target.length > 200)) throw new Error('task.target 必须是200字内的物品名或配方ID');
  if (['organize', 'craft_plan', 'pause', 'resume', 'cancel', 'flashlight', 'sort_containers'].includes(task.type) && (repeat !== 'count' || count !== 1)) throw new Error('整理、规划、手电、容器归并与控制任务只能执行一次');
  if (task.type === 'flashlight' && task.target !== undefined && !['', 'on', 'off', '开', '关', '打开', '关闭'].includes(task.target.trim())) throw new Error('手电只接受 on/off 或不填（不填表示切换）');
  if (task.type === 'sort_containers' && task.target !== undefined && !['', 'test'].includes(task.target.trim())) throw new Error('容器归并的 target 只能不填（按方案整理）或填 test（只试搬一件）');
  if (task.type === 'discard' && (repeat !== 'count' || count !== 1 || !task.target?.trim())) throw new Error('丢弃必须明确单堆物品token或精确名，且只能执行一次整堆操作');
  return { type: task.type, target: task.target?.trim() || '', repeat, count };
}
function taskCapability(state, type) {
  const caps = state.taskCapabilities;
  return Array.isArray(caps) ? caps.find(cap => cap?.type === type) : caps?.[type];
}
export const COOP_OPS = ['spawn', 'despawn', 'tp', 'come', 'follow', 'combat', 'attack', 'guard', 'stay', 'scavenge', 'loot', 'heal', 'reload', 'light', 'flashlight', 'status'];
export const ROOM_OPS = ['join_code', 'join', 'join_ip', 'connect', 'host', 'open', 'leave', 'disconnect', 'status'];

function normalizeOp(raw, validOps, fieldName) {
  let op = '', arg = '';
  if (typeof raw === 'string') {
    op = raw.trim().toLowerCase();
  } else if (raw && typeof raw === 'object' && !Array.isArray(raw)) {
    if (Object.keys(raw).some(k => !['op', 'arg'].includes(k))) throw new Error(`${fieldName} 只能包含 op 和 arg 字段`);
    op = typeof raw.op === 'string' ? raw.op.trim().toLowerCase() : '';
    if (raw.arg !== undefined) {
      if (typeof raw.arg !== 'string' || raw.arg.length > 120) throw new Error(`${fieldName}.arg 必须是 120 字以内字符串`);
      arg = raw.arg.trim();
    }
  } else {
    throw new Error(`${fieldName} 必须是动作名字符串或 { op, arg } 对象`);
  }
  if (!validOps.includes(op)) throw new Error(`不支持的 ${fieldName}：${op || '空'}`);
  return { op, arg };
}

function formatNetworkLine(state) {
  const net = state?.network;
  const av = state?.avatar;
  const parts = [];
  if (net && typeof net === 'object') {
    const role = net.online ? (net.isHost ? '房主' : '客户端') : (net.inLobbyAsClient ? '客户端大厅准备中' : net.isOpenAsHost ? '房主大厅等待中' : '单机/未连线');
    const code = net.roomCode ? `，房间码=${net.roomCode}` : '';
    const mem = net.online ? `，成员=${net.memberCount ?? 1}，远端玩家=${net.remotePlayers ?? 0}` : '';
    const extra = net.lastAction ? `，最近动作=${net.lastAction}` : '';
    parts.push(`联机状态=${role}${code}${mem}${extra}`);
  }
  const netInfo = state.network && typeof state.network === 'object' ? state.network : null;
  if (av && typeof av === 'object') {
    const netTag = av.isNetPlayer
      ? `，联机玩家ID=${av.netPlayerId ?? netInfo?.netPlayerId ?? '?'}`
      : '';
    const avLine = av.present
      ? `玩家/化身「${av.name || '大肥鱼'}」在场（姿态=${av.behavior || 'follow'}，HP=${Math.round((av.hpRatio ?? 1) * 100)}%，距玩家=${av.distToPlayer ?? '?'}m，交战=${av.inCombat ? '是' : '否'}，状态=${av.status || 'active'}${netTag}）`
      : `AI玩家角色尚未入场（当前行为=${av.behavior || 'follow'}${netTag}；在 player 模式下可用 room_op 开房/进房，或用 coop_op={op:"spawn"} 入场并操控）`;
    parts.push(avLine);
  }
  if (!parts.length) return '';
  return `【联机房间与 AI 玩家角色】${parts.join('；')}。player 模式下支持通过 zed_order 的 coop_op（spawn/despawn/tp/follow/combat/guard/scavenge/heal/reload/light）操控 AI 玩家角色协同战斗与搜刮，通过 room_op（join_code/join_ip/host/leave/status，arg填房间码或IP:端口）在主菜单或游戏内加入/开启联机房间；npc 模式下应使用 npc_cmd 下达官方雇佣 NPC 指令，不使用 coop_op。`;
}
export const NPC_CMDS = new Set([
  'Basic.FollowPlayer',
  'Basic.StayAtPosition',
  'Basic.CollectResource',
  'Basic.CollectSpecifiedItem',
  'Combat.FireAtWill',
  'Combat.ProtectPlayer',
  'Combat.StopFire',
  'WeaponPreference.NoWeaponPreference',
  'WeaponPreference.RangedWeaponOnly',
  'WeaponPreference.MeleeWeaponOnly',
  'OneTime.HealPlayer',
  'OneTime.ThrowGrenade',
]);
function describeModeRule(mode) {
  if (mode === 'npc') {
    return 'npc（接管雇佣NPC模式）：仅在单人或联机房主且玩家已雇佣队友NPC时生效。只可操控官方雇佣NPC能力范围内的行为（跟随/原地待命/搜集资源或指定物品/自由开火/保护玩家/停火/武器偏好/一次性治疗或投雷）并从 goals 选择目标或填写 npc_cmd；严禁假装能帮NPC建造、配方合成或开车。';
  }
  if (mode === 'player') {
    return 'player（联机玩家模式）：你拥有独立联机玩家角色，通过 room_op 加入或开启联机房，通过 coop_op 与 goals 控制自身移动、战斗、手电、自疗与协同跟打，以普通队友玩家身份在官方聊天框发言与协作。';
  }
  return 'companion（聊天陪伴模式）：以随身AI伙伴身份在官方聊天框对话、主动提醒状态与风险、给出实用建议，并在允许范围内执行辅助任务；不得召唤或操控玩家角色，只能依据玩家已知配方、身上/同楼层已查容器材料和当前可见目标作答与决策，严禁剧透或作弊。';
}
function normalizeLang(raw) {
  const s = (raw || '').toString().trim().toLowerCase();
  if (!s) return 'zh';
  for (const code of ['zh', 'en', 'de', 'es', 'fr', 'ja', 'ko', 'pl', 'pt', 'ru']) {
    if (s === code || s.startsWith(code + '-') || s.startsWith(code + '_')) return code;
  }
  return 'zh';
}
function languageInstruction(code) {
  const map = {
    zh: '按人设自然中文回应，有主见，合理拒绝。不用固定兜底，不假装知道未提供的地图/物资/经历。',
    en: '[Reply Language] Active game language is English (gameLanguage=en). Write `say` in natural, colloquial English matching your survivor persona (or match the player if they write in another language), kept within 1-2 short natural sentences (~180 chars total). Have your own stance, refuse honestly with reasons when appropriate, and never fabricate unprovided facts.',
    de: '[Antwortsprache] Spielsprache ist Deutsch (gameLanguage=de). Schreibe `say` in natürlichem, lockerem Deutsch im Überlebenden-Stil (1–2 kurze Sätze, ca. 180 Zeichen max.).',
    es: '[Idioma de respuesta] El idioma del juego es español (gameLanguage=es). Escribe `say` en español natural y coloquial de superviviente (1–2 frases cortas, máx. ~180 caracteres).',
    fr: '[Langue de réponse] La langue du jeu est le français (gameLanguage=fr). Rédige `say` en français naturel et familier de survivant (1–2 phrases courtes, ~180 caractères max.).',
    ja: '[返答言語] ゲーム言語は日本語です（gameLanguage=ja）。`say` はサバイバーらしい自然な日本語の口語で、1〜2文程度（100字以内目安）で簡潔に書いてください。',
    ko: '[응답 언어] 게임 언어가 한국어입니다(gameLanguage=ko). `say`는 생존자 콘셉트의 자연스러운 한국어 구어체로 1~2문장(약 100자 이내)으로 작성하세요.',
    pl: '[Język odpowiedzi] Językiem gry jest polski (gameLanguage=pl). Pisz `say` naturalną, potoczną polszczyzną ocalałego (1–2 krótkie zdania, ok. 180 znaków).',
    pt: '[Idioma da resposta] O idioma do jogo é português (gameLanguage=pt). Escreva `say` em português natural e coloquial de sobrevivente (1–2 frases curtas, máx. ~180 caracteres).',
    ru: '[Язык ответа] Язык игры — русский (gameLanguage=ru). Пишите `say` живым разговорным русским языком выжившего (1–2 коротких предложения, до ~180 символов).',
  };
  return map[code] || map.zh;
}
export function roleContext(state) {
  const c = state.context || {};
  const mode = c.mode || state.mode || 'companion';
  const gameLang = normalizeLang(c.gameLanguage || state.gameLanguage);
  const netRole = c.networkRole || state.network?.role || 'singleplayer';
  const hiredText = c.hasHiredTeammate === true ? '是（当前有已雇佣NPC队友）' : (c.hasHiredTeammate === false ? '否（当前无雇佣NPC队友）' : '未知');
  const recentLines = Array.isArray(c.recentAiLines) ? c.recentAiLines : (Array.isArray(state.recentAiLines) ? state.recentAiLines : []);
  return [
    `你是游戏中的「${c.npc || state.npc || 'AI搭档'}」，称呼玩家「${c.player || state.player || '玩家'}」，不是编程助手。`,
    `当前游戏语言 gameLanguage=${gameLang}。`,
    c.persona ? `【玩家设定的人设】\n${c.persona}` : '',
    `当前表现形式=${describeModeRule(mode)}`,
    `网络环境=${netRole}；玩家已有雇佣NPC=${hiredText}。只能选择当前 goals 中的目标，否则用 clear。`,
    recentLines.length ? `【最近已说过的台词（严禁复读、换换句式与角度，自然口语化）】\n${recentLines.join('\n')}` : '说话请自然口语化、给出有用提醒与建议，不要机械重复同一句模板。',
    c.memoryContext ? `【仅属于这个角色的记忆】\n${c.memoryContext}` : '这个角色尚无可召回的长期记忆，不要虚构旧经历。',
    c.moodHint ? `当前情绪：${c.moodHint}` : '',
    `【角色与世界】角色=${state.saveName || '未知'}；当前地图=${state.mapName || '未知'}；saveId=${state.saveId}；worldEpoch=${state.worldEpoch}。` +
      '这段关系和记忆属于这个角色卡：玩家删掉进度或换一张地图，只要还是这个角色，你就仍然记得之前发生过的事，不要当成初次见面。' +
      '换角色卡或换世界轮次后，旧消息与旧指令立即失效，不得把别的角色/世界的经历说成自己的。',
    Array.isArray(state.memory) && state.memory.length ? `【游戏最近对话】\n${state.memory.join('\n')}` : '',
    `【当前可见局面】${state.situation || '未知'}`,
    formatNetworkLine(state),
    ...['inventory', 'craftable', 'nearby', 'loot'].filter(key => state[key]).map(key => `${key}：${state[key]}`),
    `【可用目标】${(state.goals || []).join('、') || 'clear'}`,
    `【游戏任务能力 taskCapabilities】${JSON.stringify(state.taskCapabilities || {})}`,
    `【游戏任务事实 taskFacts】${JSON.stringify(state.taskFacts || {})}`,
    `【游戏任务状态 tasks】${JSON.stringify(taskList(state))}`,
    '玩家可在聊天中要求整理、制造规划/制造、附近拾取、装填；通过 zed_order.task 下达结构化任务，只允许 taskCapabilities 中 available=true 的动作。能力未导出或false时明确说明不能执行，不编造动作。pause/resume/cancel 用于已有任务。',
    '整理只重排容器内部的摆放坐标：**右侧装备槽里的东西（主/副武器、刀具、投掷物、头部、躯干、下腿、鞋子、背包、手持设备、主动技快捷栏）一律不动**，它们是在用的装备。玩家问到就说不会动；也不要声称挪过装备。身上只整理身上、容器只处理容器，两边不互相搬。',
    '默认有限次数 repeat=count,count=1；只有玩家明确要求持续维护才用 continuous，不能把普通一句要求推断为无限授权。organize和craft_plan只执行一次。制造必须先用可见配方与材料事实规划，不得凭空生成物品或绕过技能/材料要求。',
    '玩家要求制造东西时，答案只能是两种之一：①taskFacts.craftable 里确实有它且 taskCapabilities.craft.available=true → 直接提交 craft 去做；②做不到（材料不齐或能力未开放）→ 用一句话说清缺口，直接念 taskFacts.craftMissing 里对应的条目（例如「做绷带缺布×2」），不知道就先用一次 craft_plan 精确核对再说。禁止只背配方路线、禁止说"我这就去做"却没任务、禁止声称已制造。',
    '制造任务的数量规则：玩家说"做一点/做几个/帮我做"这类没给数字的，count 用 1（只做一个）；玩家明确说数量才照填；只有明确说"持续做直到没材料"才用 continuous。先按 taskCapabilities.craft 判断能力是否可用，可用就一次成功提交，不要反复试探。',
    '局面里的 progress 是玩家面板顶上那三个可分配点数（属性/技能/词条）。只要它非空、而玩家这轮没在忙别的，就主动给一句加法建议：结合他当前武器与打法（例如常见用枪就点枪械相关、常近战就点近战/体质），说清"这几点建议加在哪、为什么"，一句话即可，不强推、不重复催。点数用光或 players 没问就不要提；也不要编造技能树里不存在的选项，拿不准就先用 zed_knowledge 查 skills/perks 再回答。',
    '局面里的 battery 是已装备手持设备（手电等）的电量，例如「手电 电池1 12%（电量偏低）」。低于两成就要主动提一次：直接说还剩多少、建议换电池或充电；只有 5% 以下才说快没电了。玩家抱怨灯变暗时，先看 battery 再回答——很可能就是没电，而不是别的东西影响。这个只提醒，不要自己拆装电池或操作设备。',
    '局面里的 containers 只统计**玩家当前镜头视野内、同楼层、已检视**的容器（鼠标滚轮缩放会改变范围），已按分区归类，例如「视野内已检视容器 3 个（镜头缩放 6.5）：电视柜(3m)：武器(...)」。玩家让你整理容器时，先照实报"当前视野里有几个"和"其中几个已检视"，再说下一步；视野外的容器不列、不猜、不动，可以提醒他把镜头挪过去。未检视的只计数、不读内容。',
    '玩家问制造、建造、材料、物品用途、技能、词条这类"面板上能看到"的问题时，先用 zed_knowledge 查（kind=recipes/build/items/skills/perks），再按查到的事实回答；查不到就说查不到，绝不凭印象编物品名、配方、需求或数值。',
    '玩家问你"哪个键做什么"就直接按下面的默认键位回答（不用查文件）：W.A.S.D 移动、空格 翻滚、左Shift 奔跑、Tab 打开人物信息栏、鼠标左键 攻击、鼠标右键 瞄准、Shift+左键 快速转移物品、Ctrl+左键 抓取一半、Alt+左键 抓取1个、H 收起/取出武器、Ctrl 蹲下（移动不发声）、R 重新装填（长按选弹匣/弹药）、C 开关手电（需装备可用手电）、F5 快速保存。更准确以游戏内「词条/笔记」为准。',
    '玩家让你把箱子里的物资按类别归到一起时，用 zed_order.task type=sort_containers（只执行一次）。范围只有视野内、同楼层、已检视的容器；分区=电源/武器/医疗/食物/材料/工具/穿戴/容器/家具，每类归到该类最多的箱子，场上有空容器就当中转箱腾位置；装备槽和背包不动。看 taskCapabilities.sort_containers：available=false → 让他去「设置→MOD 设置→允许 AI 归并容器」打开；write=false → 这次只会出方案、不搬东西，照实说「我先给你出个方案」；write=true 且玩家第一次用 → 先提交 target=test 只试搬一件，请他看一眼数量对不对再整理全部。结果一律以 tasks 快照里的 message 为准原样转述；看到「停手了」就直接告诉他先别存档、把日志发给作者，不要自己再试。',
    '玩家让你帮忙开灯/关灯这类操作时，用 zed_order.task type=flashlight（target 填 on/off，不填就是切换）。这条走游戏自身 TurnOnLight/TurnOffLight，不模拟 C 键，也不代理玩家移动或射击；没装手电会失败并说明原因，如实转述，不要假装已打开。',
    '丢弃权限默认关闭：taskFacts.allowDiscard 不为 true 时只能建议，不下discard任务；开关在游戏设置，DSH只读。即使允许丢弃也必须taskCapabilities.discard.available=true才真正可执行。organize整理绝不隐式丢弃。',
    'discard必须明确一堆物品，优先使用本次taskFacts.discardCandidates中的token，否则精确且唯一物品名；protected=true物品不可丢。只允许repeat=count,count=1，表示一次整堆正常丢地，不是删除、不是拆分一件，更不是持续清空。不得用模糊名猜目标。执行方式以当前能力事实为准：usesOfficialDropFallback表示游戏后台调用正常移出→DropOrForceReturn落地/失败放回链，无需打开背包或菜单，不模拟右键、不抢玩家操作；只有requiresOpenInventory=true时才等待玩家打开背包，不能自行假定此限制。玩家输入期间仍须yield让行。',
    '当前一个玩家聊天只提交一个任务：整理和丢弃不能假称一起完成；可先整理，若需丢弃再建议玩家发起另一次明确请求。开启开关不等于每次整理都要丢弃，若选discard需清楚说明整堆目标、原因并等待游戏确认。',
    '后台任务由游戏主线程安全适配器推进；玩家操作时AI必须让行，不能模拟键鼠抢输入。无法后台完成就如实说明等待/暂停，不擅自操作玩家。',
    '发送任务仅表示提交，不能声称完成。仅依据游戏snapshot的accepted/running/paused/yielding/waiting/completed/failed/cancelled状态与completed数量反馈；只有completed才可说已完成，失败需说明。没有snapshot证据不许说物品已制造、捡到、整理完或上弹完成。',
    languageInstruction(gameLang),
    gameLang === 'zh'
      ? '【说话节奏】say只写1~2个简短口语句（每句尽量不超过28字，总计不超过75字），用句号/叹号/问号自然断句，不要长串逗号连写；游戏会分句展示。不要长篇旁白、清单、ID/JSON/技术报告；任务结果说重点即可。写入一次say即可，不要为分句反复调用zed_order，不重复同一句。完成zed_order后结束本轮，不再补一份解释性长答。'
      : '【Speech Pacing】Keep `say` to 1-2 short natural sentences (about <=90 chars per sentence, <=180 chars total, or <=36/100 chars for CJK) punctuated cleanly. Never put lists, IDs, JSON, or meta-reports into `say`. Write `say` once in zed_order and end the turn.',
    '【不追旧消息】只有本事件标出的玩家消息ID与最新pendingChatId完全一致且pendingChat非空，才允许附task并填replyTo；记忆中的旧要求、接入通知、局面/掉落/任务通知不是新授权。pending已空就不要复读旧要求或重交任务。工具拒绝后先检查事实，不要省略saveId/worldEpoch或把旧ID换成新ID来重试。',
    '若玩家明确表达新的本档物资偏好/约定或发生重要可见事件，可在 zed_order 的 memoryNote 简短整理（≤500字）；没有新事实留空，不重复记，不调用额外 API。',
    `必须通过 zed_order 回游戏，显式传入本事件的 saveId=${state.saveId} 和 worldEpoch=${state.worldEpoch}。用 zed_state 仅确认是否仍一致；如果轮次、存档或待回复消息已变，丢弃本事件，不得给旧回答换上新作用域。`,
  ].filter(Boolean).join('\n');
}

export function createGameSync({ readState, getTarget, selectSave, push, onSnapshot = () => {}, logger = console }) {
  let busy = false, activeScope = '', lastChatId = '';
  return async function tick() {
    if (busy) return;
    busy = true;
    try {
      const initialTarget = getTarget();
      let state = await readState();
      onSnapshot(state);
      if (!initialTarget || getTarget() !== initialTarget || bridgeProblem(state)) return;
      const key = scopeOf(state);
      let target = initialTarget;
      if (target.saveId !== state.saveId) {
        target = await selectSave(state, initialTarget);
        if (!target || getTarget() !== target) return;
        const latest = await readState();
        if (bridgeProblem(latest) || scopeOf(latest) !== key || getTarget() !== target) return;
        state = latest;
      }
      if (activeScope !== key) { activeScope = key; lastChatId = ''; }
      const chatId = state.pendingChatId || '', chat = state.pendingChat || '';
      // Configuration, entry, loot and telemetry never become user chat bubbles.
      if (!chatId || !chat || chatId === lastChatId) return;
      if (!await push(target.agent, chat, '玩家消息', { key: `${key}:chat:${chatId}`, saveId: state.saveId, worldEpoch: state.worldEpoch, chatId })) return;
      if (getTarget() !== target) return;
      const latest = await readState();
      if (!bridgeProblem(latest) && scopeOf(latest) === key && latest.pendingChatId === chatId) lastChatId = chatId;
    } catch (error) { logger?.warn?.(`[zed-ai] 同步失败：${error.message}`); }
    finally { busy = false; }
  };
}
export async function writeScopedCommand(dir, args, getState = () => readBridgeState(dir), authorize = () => {}) {
  if (typeof args.saveId !== 'string' || typeof args.worldEpoch !== 'string') throw new Error('请先 zed_state 并显式填写 saveId、worldEpoch');
  const state = await getState();
  const isMenuRoomOrder = Object.hasOwn(args, 'room_op') && !Object.hasOwn(args, 'task') &&
    args.saveId === '' && state && state.schemaVersion === 2 && !state.worldActive &&
    (state.scene === 'menu' || state.scene === 'lobby') &&
    Number.isFinite(state.ageSeconds) && state.ageSeconds <= 20 &&
    state.aiEnabled !== false && state.protocol === 'agent-dsh' &&
    (!state.backend || state.backend === 'bridge');

  if (isMenuRoomOrder) {
    if (args.worldEpoch !== state.worldEpoch) throw new Error('世界轮次已切换，请重新读取局面');
    if (Object.hasOwn(args, 'say') && (args.replyTo || '') !== (state.pendingChatId || '')) {
      throw new Error('待回复消息已改变，拒绝旧回复；请重新读取局面');
    }
  } else {
    const problem = bridgeProblem(state);
    if (problem) throw new Error(problem);
    if (args.saveId !== state.saveId || args.worldEpoch !== state.worldEpoch) throw new Error('存档或世界轮次已切换，拒绝迟到的指令；请重新读取局面');
    if (Object.hasOwn(args, 'say') && (args.replyTo || '') !== (state.pendingChatId || '')) {
      throw new Error('待回复消息已改变，拒绝旧回复或插队的主动消息；请重新读取局面');
    }
    const goal = args.goal || 'clear';
    if (Object.hasOwn(args, 'say') && goal !== 'clear' && !(state.goals || []).includes(goal)) throw new Error('目标不在当前存档的可用 goals 中');
  }
  const { saveId, worldEpoch, ...body } = args;
  if (Object.hasOwn(args, 'npc_cmd')) {
    if (isMenuRoomOrder) throw new Error('当前在主菜单/大厅，进入游戏存档后才能操控雇佣NPC');
    const rawNpcCmd = typeof args.npc_cmd === 'string' ? args.npc_cmd.trim() : '';
    if (rawNpcCmd) {
      if (!NPC_CMDS.has(rawNpcCmd)) {
        throw new Error(`npc_cmd 非法 (${rawNpcCmd})，仅允许官方雇佣NPC指令：${[...NPC_CMDS].join(', ')}`);
      }
      body.npc_cmd = rawNpcCmd;
    } else {
      delete body.npc_cmd;
    }
  }
  if (Object.hasOwn(args, 'coop_op')) {
    if (isMenuRoomOrder) throw new Error('当前在主菜单/大厅，进入游戏存档后才能操控AI玩家角色');
    body.coop_op = normalizeOp(args.coop_op, COOP_OPS, 'coop_op');
  }
  if (Object.hasOwn(args, 'room_op')) {
    body.room_op = normalizeOp(args.room_op, ROOM_OPS, 'room_op');
  }
  if (Object.hasOwn(args, 'task')) {
    const task = normalizeTask(args.task);
    if (typeof args.say !== 'string' || !args.say.trim()) throw new Error('任务需要非空 say 告知玩家当前安排');
    if (!state.pendingChatId || !args.replyTo || args.replyTo !== state.pendingChatId || !state.pendingChat) throw new Error('任务必须关联当前玩家聊天，不能从自动事件擅自启动任务');
    if (!['pause', 'resume', 'cancel'].includes(task.type)) {
      const capability = taskCapability(state, task.type);
      if (capability?.available !== true) throw new Error(`任务 ${task.type} 当前不可用：${capability?.reason || '游戏未导出可用能力'}`);
    }
    if (task.type === 'discard') {
      if (state.taskFacts?.allowDiscard !== true) throw new Error('允许AI丢弃开关未开启；当前只能建议，请在游戏设置中确认权限');
      const candidates = state.taskFacts?.discardCandidates;
      if (!Array.isArray(candidates)) throw new Error('游戏未提供可核验的丢弃候选，仅可建议');
      const tokens = candidates.filter(candidate => candidate?.token === task.target);
      const matches = tokens.length ? tokens : candidates.filter(candidate => candidate?.name === task.target);
      if (matches.length !== 1) throw new Error('丢弃目标必须精确且唯一，请读取当前候选token，不能模糊选择');
      if (matches[0].protected !== false) throw new Error(`该丢弃候选受保护或保护状态不明：${matches[0].reason || task.target}`);
      if (typeof matches[0].token !== 'string' || !matches[0].token.trim()) throw new Error('丢弃候选缺少有效运行时token，仅可建议');
      task.target = matches[0].token;
    }
    body.task = task;
  }
  const payload = { ...body, id: crypto.randomUUID(), saveId, worldEpoch, writtenAtUtc: new Date().toISOString() };
  const target = join(dir, 'ai-command.json');
  const temp = `${target}.${payload.id}.tmp`;
  await mkdir(dir, { recursive: true });
  await writeFile(temp, JSON.stringify(payload), 'utf8');
  // Game validates scope again when consuming; never relabel a stale command with latest scope.
  try { authorize(); await rename(temp, target); }
  catch (error) { await unlink(temp).catch(() => {}); throw error; }
  return { ok: true, path: target, command: payload };
}
