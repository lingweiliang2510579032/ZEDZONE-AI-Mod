import test from 'node:test';
import assert from 'node:assert/strict';
import { currentModelSelection, installZedModelSelection, prepareAgentSetup } from '../model-setup.js';

// Exercise the public scoped waterfall contract without starting Agent turns,
// loading providers, invoking a model, or doing network/game IO.
function fixture(options = {}) {
  const state = { fallback: { provider: 'default-provider', model: 'default-model' }, pending: null, ...options };
  const hooks = new Map(), subscriptions = [], disposed = [];
  const session = { requestHeader: () => state.header, snapshotEvents: () => state.events || [] };
  const agentCtx = { on(name, listener) {
    subscriptions.push(name); hooks.set(name, listener);
    return () => { disposed.push(name); hooks.delete(name); };
  } };
  const agent = { session, ctx: agentCtx };
  const services = {
    agentDefaultModel: { currentSelection: () => state.fallback },
    sessionProjections: { stateOf(s, name) {
      assert.equal(s, session);
      return name === 'modelSelection' ? { pending: state.pending } : name === 'agentPreset' ? state.preset : undefined;
    } },
    ...(state.presets ? { agentPresets: state.presets } : {}),
  };
  const ctx = { get: name => services[name] };
  const assemble = (variables = {}) => hooks.get('system-prompt/assemble')({}, {}, async () => ({ text: 'system {{model}}', variables, marker: 1 }));
  const request = (config = {}) => hooks.get('agent/request')({}, async () => ({ marker: 2, ...config }));
  return { ctx, agent, session, state, hooks, subscriptions, disposed, assemble, request, services };
}

test('new session takes configured default provider/model rather than an empty setup', () => {
  const f = fixture();
  assert.deepEqual(currentModelSelection(f.ctx), { provider: 'default-provider', model: 'default-model' });
});

test('pending durable model choice wins over request header and current default', () => {
  const f = fixture({ pending: { provider: 'picked', model: 'next', reasoningEffort: 'high' }, header: { config: { provider: 'old', model: 'old-model' } } });
  assert.deepEqual(currentModelSelection(f.ctx, f.session), { provider: 'picked', model: 'next', reasoningEffort: 'high' });
});

test('resume restores logged route and an explicitly selected reasoning effort', () => {
  const f = fixture({ header: { config: { provider: 'logged', model: 'logged-model', reasoningEffort: 'medium' } } });
  assert.deepEqual(currentModelSelection(f.ctx, f.session), { provider: 'logged', model: 'logged-model', reasoningEffort: 'medium' });
});

test('adapter-default reasoning effort is not promoted into a persisted user choice', () => {
  const f = fixture({ header: { config: { provider: 'logged', model: 'logged-model', reasoningEffort: 'high' }, adapterDefaults: { reasoningEffort: true } } });
  assert.deepEqual(currentModelSelection(f.ctx, f.session), { provider: 'logged', model: 'logged-model' });
});

test('invalid pending/header route cannot produce missing model and valid default is used', () => {
  const f = fixture({ pending: { provider: 'picked', model: ' ' }, header: { config: { model: 'orphan-model' } } });
  assert.equal(currentModelSelection(f.ctx, f.session).model, 'default-model');
});

test('no valid model default fails closed before create/resume setup is returned', async () => {
  const f = fixture({ fallback: { provider: 'provider-only' } });
  await assert.rejects(prepareAgentSetup(f.ctx), /模型/);
  assert.equal(f.subscriptions.length, 0);
});

test('prepared setup installs model variables before first prompt and matching request routing', async () => {
  const f = fixture();
  const prepared = await prepareAgentSetup(f.ctx);
  assert.deepEqual(prepared.agentOptions, { provider: 'default-provider', model: 'default-model' });
  await prepared.setup(f.agent.ctx, f.agent);
  const assembled = await f.assemble({ unrelated: 'kept' });
  assert.deepEqual(assembled.variables, { unrelated: 'kept', provider: 'default-provider', model: 'default-model' });
  assert.equal(assembled.marker, 1);
  assert.deepEqual(await f.request({ provider: 'wrong', model: 'wrong', reasoningEffort: 'inherited' }), { marker: 2, provider: 'default-provider', model: 'default-model' });
});

test('request uses the assembled model snapshot even if selection changes before request', async () => {
  const f = fixture({ pending: { provider: 'one', model: 'model-one', reasoningEffort: 'low' } });
  installZedModelSelection(f.ctx, f.agent);
  await f.assemble();
  f.state.pending = { provider: 'two', model: 'model-two', reasoningEffort: 'high' };
  assert.deepEqual(await f.request(), { marker: 2, provider: 'one', model: 'model-one', reasoningEffort: 'low' });
  await f.assemble();
  assert.deepEqual(await f.request(), { marker: 2, provider: 'two', model: 'model-two', reasoningEffort: 'high' });
});

test('durable pending selection overrides stale constructor provider/model variables', async () => {
  const f = fixture({ pending: { provider: 'picked', model: 'picked-model', reasoningEffort: 'high' } });
  installZedModelSelection(f.ctx, f.agent);
  assert.deepEqual((await f.assemble({ provider: 'constructor', model: 'stale-model' })).variables, { provider: 'picked', model: 'picked-model' });
  assert.deepEqual(await f.request(), { marker: 2, provider: 'picked', model: 'picked-model', reasoningEffort: 'high' });
});

test('request before prompt assembly is rejected rather than routing a mismatched model', async () => {
  const f = fixture(); installZedModelSelection(f.ctx, f.agent);
  await assert.rejects(f.request(), /组装/);
});

test('live agent model setup is idempotent and disposal permits a clean reinstall', () => {
  const f = fixture();
  const dispose = installZedModelSelection(f.ctx, f.agent);
  assert.equal(installZedModelSelection(f.ctx, f.agent), dispose);
  assert.equal(f.subscriptions.length, 2);
  dispose(); assert.equal(f.disposed.length, 2); assert.equal(f.hooks.size, 0);
  assert.notEqual(installZedModelSelection(f.ctx, f.agent), dispose);
  assert.equal(f.subscriptions.length, 4);
});

test('model selection is per agent/session, never shared across saved games', async () => {
  const a = fixture({ pending: { provider: 'A', model: 'A-model' } });
  const b = fixture({ pending: { provider: 'B', model: 'B-model' } });
  installZedModelSelection(a.ctx, a.agent); installZedModelSelection(b.ctx, b.agent);
  await a.assemble(); await b.assemble();
  assert.equal((await a.request()).model, 'A-model'); assert.equal((await b.request()).model, 'B-model');
});

test('preset preparation resolves and mounts only inside prepublication setup after model hooks', async () => {
  const calls = [];
  const f = fixture({ presets: { async resolve(id) { calls.push(['resolve', id]); return { id: 'default-preset' }; }, async mount(ctx, id) { calls.push(['mount', id]); assert.equal(ctx, f.agent.ctx); assert.equal(f.hooks.size, 2); } } });
  const prepared = await prepareAgentSetup(f.ctx);
  assert.equal(prepared.agentPreset, 'default-preset');
  assert.deepEqual(calls, [['resolve', undefined]]);
  await prepared.setup(f.agent.ctx, f.agent);
  assert.deepEqual(calls, [['resolve', undefined], ['mount', 'default-preset']]);
});

test('resume preserves the real durable agentPreset event field rather than a default replacement', async () => {
  const f = fixture({ events: [{ type: 'agent-preset/selected', data: { agentPreset: 'saved-preset' } }], preset: 'saved-preset', presets: {
    async resolve(id) { assert.equal(id, 'saved-preset'); return { id }; }, async mount() {},
  } });
  assert.equal((await prepareAgentSetup(f.ctx, f.session)).agentPreset, 'saved-preset');
});

test('preset resolve/mount errors propagate and incomplete preset services are rejected', async () => {
  const broken = fixture({ presets: { async resolve() { throw new Error('preset unavailable'); }, async mount() {} } });
  await assert.rejects(prepareAgentSetup(broken.ctx), /preset unavailable/);
  const incomplete = fixture({ presets: { resolve() {} } });
  await assert.rejects(prepareAgentSetup(incomplete.ctx), /preset/);
  const failedMount = fixture({ presets: { async resolve() { return { id: 'p' }; }, async mount() { throw new Error('mount failed'); } } });
  const prepared = await prepareAgentSetup(failedMount.ctx);
  await assert.rejects(prepared.setup(failedMount.agent.ctx, failedMount.agent), /mount failed/);
});

test('invalid agent scoped context fails rather than pretending an agent is model-ready', () => {
  const f = fixture();
  assert.throws(() => installZedModelSelection(f.ctx, { session: f.session }), /scoped context/);
});
