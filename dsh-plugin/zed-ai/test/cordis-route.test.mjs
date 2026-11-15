import test from 'node:test';
import assert from 'node:assert/strict';
import { bundledText, loadCordis } from './runtime-probe.mjs';
import { registerZedControlRpc, createZedControl } from '../control.js';

const { Context, Service } = await loadCordis();
const source = bundledText('node_modules/@deepseek-ai/dsh-client-connection/lib/index.js');
const between = (a, b) => source.slice(source.indexOf(a), source.indexOf(b, source.indexOf(a)));
// Actual getter, maps, registration and owner-only dispatch, under real Cordis tracing.
const Connection = new Function('Service', 'assertFetchRoute', `return class extends Service {
  constructor(ctx) { super(ctx, 'connection'); }
  fetchRoutes = new Map(); interceptors = new Map();
  ${between('\tget fetch() {', '\t/** Apply the Host/Origin fence')}
  ${between('\tcreateSharedFetchHandler(channel) {', '\tregister(owner, channel, handler) {')}
}`)(Service, route => assert.ok(route.path.startsWith('/api/')));
// Actual browser call + envelope validation. Binary responses are not used by ZED.
const browser = bundledText('node_modules/@deepseek-ai/dsh-client-connection/lib/client.browser.js');
const browserStart = browser.indexOf('const CHANNEL_PATTERN');
const browserEnd = browser.indexOf('function isLoopbackHostname(', browserStart);
const browserFactory = new Function('RpcId', 'randomUuid', browser.slice(browserStart, browserEnd) + '\nreturn createWebConnectionRpc;')(x => x, () => 'runtime-test-rpc');

async function setup() {
  const root = new Context();
  const carrier = root.plugin({ apply(ctx) { ctx.provide('webServer', { register() { throw new Error('must use existing /api carrier'); } }); } });
  await carrier;
  const connection = root.plugin({ apply(ctx) { new Connection(ctx); } });
  await connection;
  const control = createZedControl(async () => ({ id: 'zed-runtime-test', agent: {} }), () => {});
  let registration;
  const plugin = root.plugin({ apply(ctx) {
    registration = ctx.inject(['connection'], scoped => { registerZedControlRpc(scoped, control); });
  } });
  await plugin; await new Promise(resolve => setImmediate(resolve)); await registration;
  const service = root.get('connection');
  const dispatch = service.createSharedFetchHandler('/api');
  return { root, plugin, connection, carrier, control, service, dispatch };
}

test('real Cordis and browser RPC work across separate service fibers: status/on/off and owner fence', async () => {
  const f = await setup();
  try {
    assert.equal(f.service.fetchRoutes.size, 2);
    const rpc = browserFactory((url, init) => f.dispatch.fetch(new Request('http://localhost/' + url, init), { principal: { kind: 'owner' } }));
    assert.equal((await rpc.call('/api', 'zed-ai/status', {})).value.enabled, false);
    assert.equal((await rpc.call('/api', 'zed-ai/setEnabled', { enabled: true })).value.connected, true);
    assert.equal((await rpc.call('/api', 'zed-ai/setEnabled', { enabled: false })).value.connected, false);
    const denied = await f.dispatch.fetch(new Request('http://localhost/api/zed-ai/setEnabled', {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}',
    }), { principal: { kind: 'remote' } });
    assert.equal(denied.status, 403);
    const failure = await rpc.call('/api', 'zed-ai/setEnabled', { enabled: 'false' });
    assert.equal(failure.ok, false); assert.deepEqual(failure.error.details, {});
    await f.plugin.dispose();
    assert.equal(f.service.fetchRoutes.size, 0);
  } finally { await f.plugin.dispose(); await f.connection.dispose(); await f.carrier.dispose(); }
});
