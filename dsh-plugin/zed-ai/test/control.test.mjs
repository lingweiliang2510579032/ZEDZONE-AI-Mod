import test from 'node:test';
import assert from 'node:assert/strict';
import { createZedControl, registerZedControlRpc } from '../control.js';
import { registerZedCommand } from '../connect.js';

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function fixture(connect = async () => ({ id: 'zed-session', agent: {} })) {
  const targets = [];
  return { targets, control: createZedControl(connect, t => targets.push(t)) };
}

test('initial status is disconnected, closing needs no session and never connects', async () => {
  let calls = 0;
  const { control, targets } = fixture(() => { calls++; throw new Error('must not connect'); });
  assert.equal(control.status().enabled, false);
  assert.equal((await control.setEnabled(false)).connected, false);
  assert.deepEqual(targets, [null]);
  assert.equal(calls, 0);
});
test('open / close / reopen reports the real target', async () => {
  const { control, targets } = fixture();
  assert.equal((await control.setEnabled(true)).sessionId, 'zed-session');
  assert.equal((await control.setEnabled(false)).sessionId, null);
  assert.equal((await control.setEnabled(true)).connected, true);
  assert.equal(targets.length, 3);
});
test('off wins over an in-flight automatic connection', async () => {
  const d = deferred();
  const { control, targets } = fixture(() => d.promise);
  const opening = control.setEnabled(true);
  await control.setEnabled(false);
  d.resolve({ id: 'late-session', agent: {} });
  await opening;
  assert.equal(control.status().enabled, false);
  assert.deepEqual(targets, [null]);
});
test('rapid off / on shares an in-flight connect and does not duplicate sessions', async () => {
  const d = deferred(); let calls = 0;
  const { control, targets } = fixture(() => { calls++; return d.promise; });
  const first = control.setEnabled(true);
  await control.setEnabled(false);
  const second = control.setEnabled(true);
  d.resolve({ id: 'one-session', agent: {} });
  await Promise.all([first, second]);
  assert.equal(calls, 1);
  assert.equal(control.status().connected, true);
  assert.deepEqual(targets.map(t => t?.id || null), [null, 'one-session']);
});
test('connect errors are visible and can be retried', async () => {
  let calls = 0;
  const { control } = fixture(async () => {
    if (++calls === 1) throw new Error('resume failed');
    return { id: 'recovered', agent: {} };
  });
  await assert.rejects(control.setEnabled(true), /resume failed/);
  assert.equal(control.status().enabled, false);
  assert.equal(control.status().error, 'resume failed');
  assert.equal((await control.setEnabled(true)).connected, true);
  assert.equal(control.status().error, '');
});
test('dispose prevents late connections and future actions', async () => {
  const d = deferred(); const { control, targets } = fixture(() => d.promise);
  const opening = control.setEnabled(true);
  control.dispose(); d.resolve({ id: 'late' }); await opening;
  assert.deepEqual(targets, [null]);
  await assert.rejects(control.setEnabled(true), /已卸载/);
});
test('exact API handlers validate payloads and preserve browser RPC envelopes', async () => {
  const routes = new Map();
  const { control } = fixture();
  registerZedControlRpc({ connection: { fetch: { register(route) { routes.set(route.path, route); } } } }, control);
  const invoke = async (action, payload) => {
    const method = 'zed-ai/' + action;
    const response = await routes.get('/api/' + method).fetch(new Request('http://localhost/api/' + method, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ type: 'client-request', rpcId: 'test-id', method, payload }),
    }));
    const message = await response.json();
    assert.equal(message.type, 'server-response'); assert.equal(message.rpcId, 'test-id');
    return message.result;
  };
  for (const [action, payload] of [['setEnabled', { enabled: 'false' }], ['setEnabled', { enabled: null }], ['status', { extra: true }]]) {
    const failure = await invoke(action, payload);
    assert.equal(failure.ok, false); assert.deepEqual(failure.error.details, {});
  }
  const result = await invoke('setEnabled', { enabled: false });
  assert.equal(result.ok, true); assert.equal(result.value.enabled, false);
  assert.equal((await invoke('status', {})).value.connected, false);
});
test('/zed status and off share controller state; invalid commands do not enable', async () => {
  let command;
  const { control } = fixture();
  registerZedCommand({ get: () => ({ register: c => { command = c; } }) }, control);
  assert.match((await command.handler({ rawInput: 'status' })).text, /已断开/);
  assert.equal((await command.handler({ rawInput: 'oops' })).kind, 'error');
  await command.handler({ rawInput: 'on' });
  assert.match((await command.handler({ rawInput: 'status' })).text, /zed-session/);
  await command.handler({ rawInput: 'off' });
  assert.equal(control.status().enabled, false);
});
