import { roleContext, bridgeProblem, scopeOf } from './bridge.js';
import { deliveryId } from './delivery.js';
const attached = new WeakSet();

// Keep configuration/snapshots out of user chat bubbles. This is an Agent-scoped
// prompt section; never synthesize assistant transcript events.
export function installGameContext(agent, readState) {
  if (attached.has(agent)) return;
  if (typeof agent?.ctx?.on !== 'function') throw new Error('缺少游戏上下文作用域');
  let claimedId = '';
  // Only ZED-delivered messages carry the deterministic `zed-` prefix. A message
  // the user typed directly in DSH is never treated as a stale game turn.
  agent.ctx.on('agent/inbox/claimed', payload => { claimedId = payload.message?.id || ''; });
  agent.ctx.on('system-prompt/assemble', async (_assembly, _context, next) => {
    const state = await readState();
    const assembled = await next();
    let problem = state.scopeMismatch ? state.error : bridgeProblem(state);
    const expectedId = state.pendingChatId ? deliveryId(`${scopeOf(state)}:chat:${state.pendingChatId}`) : '';
    // An older queued game message must not inherit a *newer* player's task
    // authorization: gate only while a newer pending chat actually exists.
    // Messages typed directly in DSH carry no `zed-` id and are never gated.
    if (claimedId.startsWith('zed-') && state.pendingChatId && claimedId !== expectedId) {
      problem = '当前处理的聊天已过期；不要回应新消息或执行其任务，结束本轮等待新的玩家消息';
    }
    const text = problem ? `当前游戏联动不可写：${problem}。不要向游戏提交指令，不引用旧局面。` :
      `${roleContext(state)}\n【当前真实玩家消息】${state.pendingChat || '无新玩家消息，不从记忆重启旧任务'}\n` +
      `当前可回复消息ID：${state.pendingChatId || '空'}。调用zed_order若回复当前消息，replyTo必须精确为此值，saveId/worldEpoch必须齐全。\n` +
      'DSH此会话仅记录局内玩家与AI对话。设定、快照、调试分析不写入最终回复。先确认当前消息仍有效，最多一次成功zed_order。最终回复只写实际say的同一组简短口语句，不加完成报告/工具说明/ID。工具失败则只用一句自然中文说明没有执行，不伪造游戏已说话。';
    return { ...assembled, sections: [...(assembled.sections || []), { name: 'zed:game-context', text, interpolate: false }] };
  });
  attached.add(agent);
}
