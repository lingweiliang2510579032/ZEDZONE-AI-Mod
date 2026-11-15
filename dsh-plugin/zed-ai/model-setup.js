// Profile plugins cannot resolve the host's bare packages. Mirror the public
// model-selection waterfall contract using scoped events (no private symbols).
const installed = new WeakMap();
const initialized = new WeakSet();

function route(value) {
  if (!value || typeof value.provider !== 'string' || !value.provider.trim() || typeof value.model !== 'string' || !value.model.trim()) return null;
  return { provider: value.provider, model: value.model,
    ...(typeof value.reasoningEffort === 'string' && value.reasoningEffort ? { reasoningEffort: value.reasoningEffort } : {}) };
}

export function currentModelSelection(ctx, session) {
  const projections = ctx.get('sessionProjections');
  let pending = session && !session.zedHistoryOnly ? projections?.stateOf?.(session, 'modelSelection')?.pending : undefined;
  if (session?.zedHistoryOnly) {
    for (const event of session.snapshotEvents()) {
      if (event.type === 'model/selection') pending = event.data;
      if (event.type === 'request/header' && pending) {
        const used = event.data?.header?.config;
        if (used?.provider === pending.provider && used?.model === pending.model && used?.reasoningEffort === pending.reasoningEffort) pending = undefined;
      }
    }
  }
  const selected = route(pending);
  if (selected) return selected;
  const header = session?.requestHeader?.();
  const logged = route(header?.config);
  if (logged) {
    if (header.adapterDefaults?.reasoningEffort === true) delete logged.reasoningEffort;
    return logged;
  }
  const fallback = route(ctx.get('agentDefaultModel')?.currentSelection?.());
  if (!fallback) throw new Error('ZED 会话缺少有效的默认模型选择，拒绝启动空模型会话');
  return fallback;
}

export function installZedModelSelection(ctx, agent) {
  if (!agent || typeof agent.ctx?.on !== 'function' || !agent.session) throw new Error('ZED 会话缺少 Agent scoped context，无法安装模型提示词');
  const prior = installed.get(agent);
  if (prior) return prior;
  let assembledSelection;
  const assembly = agent.ctx.on('system-prompt/assemble', async (_assembly, _context, next) => {
    const selected = currentModelSelection(ctx, agent.session);
    const result = await next();
    // Base variables also reflect constructor options; they are not proof of
    // an installed selection. Durable UI intent must override that stale route.
    assembledSelection = selected;
    return { ...result, variables: { ...result.variables, provider: assembledSelection.provider, model: assembledSelection.model } };
  });
  const request = agent.ctx.on('agent/request', async (_payload, next) => {
    const result = await next();
    if (!assembledSelection) throw new Error('ZED 请求尚未组装模型提示词，拒绝错配模型');
    const { reasoningEffort: _effort, ...rest } = result;
    return { ...rest, ...assembledSelection };
  });
  const dispose = () => { assembly?.(); request?.(); installed.delete(agent); };
  installed.set(agent, dispose);
  return dispose;
}

export async function ensureZedAgentSetup(ctx, agent) {
  if (initialized.has(agent)) return;
  const repair = async () => {
    if (initialized.has(agent)) return;
    const prepared = await prepareAgentSetup(ctx, agent.session);
    const presets = ctx.get('agentPresets');
    installZedModelSelection(ctx, agent);
    if (presets && !presets.composedPreset?.(agent.ctx)) await presets.mount(agent.ctx, prepared.agentPreset);
    initialized.add(agent);
  };
  // Official maintenance rejects busy agents instead of mutating a running turn.
  if (typeof agent.runMaintenance !== 'function') throw new Error('ZED 已有会话缺少安全维护入口，暂不修改运行中会话');
  await agent.runMaintenance(repair);
}

export async function prepareAgentSetup(ctx, session) {
  const selected = currentModelSelection(ctx, session);
  const presets = ctx.get('agentPresets');
  let preset = session?.header?.agentPreset;
  if (presets) {
    if (typeof presets.resolve !== 'function' || typeof presets.mount !== 'function') throw new Error('ZED Agent preset 服务不完整');
    // Restore the saved preset rather than silently replacing it on resume.
    if (session && !session.zedHistoryOnly) preset = ctx.get('sessionProjections')?.stateOf?.(session, 'agentPreset') ?? undefined;
    const saved = session?.snapshotEvents?.();
    if (saved && (session?.zedHistoryOnly || preset === undefined)) for (const event of saved) {
      if (event.type === 'session/header' && typeof event.data?.agentPreset === 'string') preset = event.data.agentPreset;
      if (event.type === 'agent-preset/selected' && typeof event.data?.agentPreset === 'string') preset = event.data.agentPreset;
    }
    preset = (await presets.resolve(preset)).id;
  }
  return {
    agentOptions: selected,
    ...(preset === undefined ? {} : { agentPreset: preset }),
    setup: async (agentCtx, agent) => {
      installZedModelSelection(ctx, agent);
      if (presets) await presets.mount(agentCtx, preset);
      initialized.add(agent);
    },
  };
}
