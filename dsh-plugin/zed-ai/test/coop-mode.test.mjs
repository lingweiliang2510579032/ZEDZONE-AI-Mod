import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { roleContext, writeScopedCommand, COOP_OPS, ROOM_OPS, NPC_CMDS } from '../bridge.js';
import { apply } from '../index.js';

function menuState(overrides = {}) {
  return {
    schemaVersion: 2,
    worldActive: false,
    runMode: 'dsh',
    scene: 'menu',
    saveId: '',
    worldEpoch: 'menu-epoch-1',
    protocol: 'agent-dsh',
    backend: 'bridge',
    aiEnabled: true,
    ageSeconds: 1,
    pendingChatId: '',
    network: { online: false, isHost: false, isClient: false, roomCode: '', memberCount: 0, remotePlayers: 0 },
    avatar: { present: false, name: '大肥鱼', behavior: 'follow' },
    ...overrides,
  };
}

function worldState(overrides = {}) {
  return {
    schemaVersion: 2,
    worldActive: true,
    runMode: 'dsh',
    scene: 'world',
    saveId: 'save-A',
    saveName: '大肥鱼',
    worldEpoch: 'epoch-1',
    protocol: 'agent-dsh',
    backend: 'bridge',
    aiEnabled: true,
    ageSeconds: 1,
    pendingChatId: 'chat-1',
    pendingChat: '进房间陪我打僵尸',
    goals: ['FollowLeader', 'EliminateThreat'],
    network: { online: true, isHost: true, isClient: false, roomCode: 'ZED777', memberCount: 2, remotePlayers: 1 },
    avatar: { present: true, name: '大肥鱼', behavior: 'follow', hpRatio: 0.95, distToPlayer: 2.3, inCombat: false, status: 'following' },
    ...overrides,
  };
}

test('roleContext surfaces network room and avatar coop telemetry', () => {
  const ctx = roleContext(worldState());
  assert.match(ctx, /联机状态=房主，房间码=ZED777，成员=2，远端玩家=1/);
  assert.match(ctx, /化身「大肥鱼」在场（姿态=follow，HP=95%，距玩家=2.3m/);
  assert.match(ctx, /coop_op.*room_op/);
});

test('writeScopedCommand accepts coop_op and room_op inside active world', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-coop-'));
  try {
    const res = await writeScopedCommand(dir, {
      saveId: 'save-A',
      worldEpoch: 'epoch-1',
      replyTo: 'chat-1',
      goal: 'FollowLeader',
      say: '来啦，马上召出来跟你走！',
      coop_op: { op: 'spawn', arg: '' },
      room_op: { op: 'host', arg: '7777' },
    }, async () => worldState());
    assert.equal(res.ok, true);
    const written = JSON.parse(await readFile(res.path, 'utf8'));
    assert.deepEqual(written.coop_op, { op: 'spawn', arg: '' });
    assert.deepEqual(written.room_op, { op: 'host', arg: '7777' });
    assert.ok(COOP_OPS.includes('tp'));
    assert.ok(ROOM_OPS.includes('join_code'));
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test('writeScopedCommand rejects invalid coop_op / room_op verbs', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-coop-bad-'));
  try {
    await assert.rejects(
      writeScopedCommand(dir, {
        saveId: 'save-A',
        worldEpoch: 'epoch-1',
        replyTo: 'chat-1',
        say: '试一下',
        coop_op: { op: 'fly_to_moon' },
      }, async () => worldState()),
      /不支持的 coop_op/,
    );
    await assert.rejects(
      writeScopedCommand(dir, {
        saveId: 'save-A',
        worldEpoch: 'epoch-1',
        replyTo: 'chat-1',
        say: '试一下',
        room_op: { op: 'hack_server' },
      }, async () => worldState()),
      /不支持的 room_op/,
    );
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test('writeScopedCommand permits menu/lobby room_op with empty saveId while keeping coop_op/in-world guards strict', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-menu-room-'));
  try {
    const res = await writeScopedCommand(dir, {
      saveId: '',
      worldEpoch: 'menu-epoch-1',
      replyTo: '',
      say: '这就按房间码 ABCD12 连进去！',
      room_op: { op: 'join_code', arg: 'ABCD12' },
    }, async () => menuState());
    assert.equal(res.ok, true);
    const written = JSON.parse(await readFile(res.path, 'utf8'));
    assert.deepEqual(written.room_op, { op: 'join_code', arg: 'ABCD12' });
    assert.equal(written.saveId, '');

    // coop_op in menu must still be rejected
    await assert.rejects(
      writeScopedCommand(dir, {
        saveId: '',
        worldEpoch: 'menu-epoch-1',
        replyTo: '',
        say: '尝试召化身',
        coop_op: { op: 'spawn' },
        room_op: { op: 'status' },
      }, async () => menuState()),
      /进入游戏存档后才能操控(AI玩家角色|陪玩化身)/,
    );

    // npc_cmd in menu (both with and without room_op) must be rejected
    await assert.rejects(
      writeScopedCommand(dir, {
        saveId: '',
        worldEpoch: 'menu-epoch-1',
        replyTo: '',
        say: '让雇佣队友跟我走',
        npc_cmd: 'Basic.FollowPlayer',
        room_op: { op: 'status' },
      }, async () => menuState()),
      /进入游戏存档后才能操控雇佣NPC/,
    );
    await assert.rejects(
      writeScopedCommand(dir, {
        saveId: '',
        worldEpoch: 'menu-epoch-1',
        replyTo: '',
        say: '让雇佣队友跟我走',
        npc_cmd: 'Basic.FollowPlayer',
      }, async () => menuState()),
      /当前未进入可识别的存档/,
    );

    // wrong worldEpoch at menu must still be rejected
    await assert.rejects(
      writeScopedCommand(dir, {
        saveId: '',
        worldEpoch: 'stale-epoch',
        replyTo: '',
        say: '加入房间',
        room_op: { op: 'join_code', arg: 'ABCD12' },
      }, async () => menuState()),
      /世界轮次已切换/,
    );
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test('zed_order keeps exact 4-tool surface while exposing coop_op, room_op and npc_cmd schema', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'zed-schema-'));
  const tools = [];
  const cleanups = [];
  try {
    apply({
      logger: { info() {}, warn() {} },
      tools: { register(t) { tools.push(t); return () => {}; } },
      effect(fn) { cleanups.push(fn()); },
      get() { return null; },
      on() { return () => {}; },
      inject() {},
    }, { dataDir: dir });
    assert.equal(tools.length, 4);
    const order = tools.find(t => t.name === 'zed_order');
    assert.ok(order.parameters.properties.npc_cmd);
    assert.ok(order.parameters.properties.coop_op);
    assert.ok(order.parameters.properties.room_op);
    assert.equal(order.parameters.properties.coop_op.additionalProperties, false);
    assert.equal(order.parameters.properties.room_op.additionalProperties, false);
  } finally {
    for (const cleanup of cleanups) {
      if (typeof cleanup === 'function') cleanup();
    }
    await rm(dir, { recursive: true, force: true });
  }
});

test('writeScopedCommand validates npc_cmd against official hired-NPC allowlist', async () => {
  assert.ok(NPC_CMDS.has('Basic.FollowPlayer'));
  assert.ok(NPC_CMDS.has('OneTime.HealPlayer'));
  const dir = await mkdtemp(join(tmpdir(), 'zed-npc-'));
  try {
    const res = await writeScopedCommand(dir, {
      saveId: 'save-A',
      worldEpoch: 'epoch-1',
      replyTo: 'chat-1',
      goal: 'FollowLeader',
      say: '收到，切换雇佣队友护卫模式！',
      npc_cmd: 'Combat.ProtectPlayer',
    }, async () => worldState({ mode: 'npc' }));
    assert.equal(res.ok, true);
    const written = JSON.parse(await readFile(res.path, 'utf8'));
    assert.equal(written.npc_cmd, 'Combat.ProtectPlayer');

    await assert.rejects(
      writeScopedCommand(dir, {
        saveId: 'save-A',
        worldEpoch: 'epoch-1',
        replyTo: 'chat-1',
        say: '作弊一下',
        npc_cmd: 'Hack.InfiniteAmmo',
      }, async () => worldState({ mode: 'npc' })),
      /npc_cmd 非法/,
    );
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});
