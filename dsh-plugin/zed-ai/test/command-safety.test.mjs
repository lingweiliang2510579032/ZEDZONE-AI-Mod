import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readdir, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { writeScopedCommand } from '../bridge.js';
const state = { schemaVersion: 2, worldActive: true, aiEnabled: true, saveId: 'A', worldEpoch: 'w1',
  protocol: 'agent-dsh', ageSeconds: 0, pendingChatId: 'p1', goals: ['FollowLeader'] };

test('old reply, spontaneous insertion and unknown goal cannot overwrite a pending player message', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-reply-safety-'));
  try {
    for (const args of [{ replyTo: 'old' }, { replyTo: '' }, { replyTo: 'p1', goal: 'not-a-goal' }]) {
      await assert.rejects(writeScopedCommand(dir, { saveId: 'A', worldEpoch: 'w1', say: 'bad', ...args }, async () => state));
    }
    assert.deepEqual(await readdir(dir), []);
  } finally { await rm(dir, { recursive: true, force: true }); }
});

test('a revoked connection before atomic publication leaves no command or temp file', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-revoke-'));
  try {
    await assert.rejects(writeScopedCommand(dir, { saveId: 'A', worldEpoch: 'w1', replyTo: 'p1', say: 'not delivered' },
      async () => state, () => { throw new Error('connection revoked'); }), /revoked/);
    assert.deepEqual(await readdir(dir), []);
  } finally { await rm(dir, { recursive: true, force: true }); }
});
