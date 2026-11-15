import test from 'node:test';
import assert from 'node:assert/strict';
import { apply } from '../index.js';

test('host exposes API controls before session services start, without requiring webServer', async () => {
  const effects = [], tools = [], injections = [], routes = new Map();
  const ctx = {
    tools: { register: tool => tools.push(tool) },
    effect(fn) { effects.push(fn()); },
    inject(names, fn) { injections.push({ names, fn }); },
  };
  try {
    apply(ctx, { autoConnect: false, stateDir: new URL('./fixtures-no-game', import.meta.url).pathname });
    assert.equal(tools.length, 4);
    const rpc = injections.find(i => i.names.includes('connection'));
    assert.deepEqual(rpc.names, ['connection']);
    rpc.fn({ connection: { fetch: { register(route) { routes.set(route.path, route); } } } });
    const invoke = async (action, payload) => (await (await routes.get('/api/zed-ai/' + action).fetch(new Request('http://localhost/api/zed-ai/' + action, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ type: 'client-request', rpcId: 'test', method: 'zed-ai/' + action, payload }),
    }))).json()).result;
    const off = await invoke('setEnabled', { enabled: false });
    assert.equal(off.ok, true); assert.equal(off.value.enabled, false);
    const on = await invoke('setEnabled', { enabled: true });
    assert.equal(on.ok, false); assert.match(on.error.message, /尚未就绪/);
    assert.equal((await invoke('status', {})).value.enabled, false);
  } finally { for (const dispose of effects.reverse()) dispose?.(); }
});
