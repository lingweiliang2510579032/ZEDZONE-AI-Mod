import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, utimes, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { apply } from '../index.js';

const stateOf = change => ({ schemaVersion: 2, worldActive: true, aiEnabled: true, saveId: 'save-A', worldEpoch: 'entry-A', protocol: 'agent-dsh', backend: 'bridge', ...change });
const knowledgeOf = change => ({ schemaVersion: 2, saveId: 'save-A', worldEpoch: 'entry-A',
  recipes: [{ name: '绷带', need: '布料×2', id: 'known-recipe' }],
  items: [{ name: '布料', desc: '已识别的物品' }], build: [], skills: ['本档技能'], perks: ['本档词条'], ts: 'test', ...change });

async function fixture(run) {
  const dir = await mkdtemp(join(tmpdir(), 'zed-knowledge-scope-'));
  const effects = [], tools = new Map();
  const ctx = { tools: { register(tool) { tools.set(tool.name, tool); } },
    effect(fn) { effects.push(fn()); }, inject() {}, logger: { warn() {} } };
  try {
    await writeFile(join(dir, 'ai-state.json'), JSON.stringify(stateOf()));
    await writeFile(join(dir, 'ai-knowledge.json'), JSON.stringify(knowledgeOf()));
    apply(ctx, { stateDir: dir, autoConnect: false });
    const update = async ({ state = stateOf(), knowledge = knowledgeOf(), stale = false } = {}) => {
      await writeFile(join(dir, 'ai-state.json'), JSON.stringify(state));
      await writeFile(join(dir, 'ai-knowledge.json'), JSON.stringify(knowledge));
      if (stale) { const old = new Date(Date.now() - 60000); await utimes(join(dir, 'ai-state.json'), old, old); }
    };
    await run({ tool: tools.get('zed_knowledge'), update });
  } finally { for (const dispose of effects.reverse()) dispose?.(); await rm(dir, { recursive: true, force: true }); }
}

test('registered knowledge tool reads active fresh current-save current-epoch schema2 snapshot', () => fixture(async ({ tool }) => {
  const response = await tool.execute({ kind: 'recipes', query: '绷带' });
  assert.equal(response.ok, true);
  const data = JSON.parse(response.text);
  assert.deepEqual(data.recipes, [{ name: '绷带', need: '布料×2', id: 'known-recipe' }]);
  assert.equal(data.recipesTotal, 1);
}));

for (const [name, change] of [['old save', { saveId: 'save-B' }], ['old epoch', { worldEpoch: 'entry-old' }], ['old schema', { schemaVersion: 1 }], ['missing schema', { schemaVersion: undefined }]]) {
  test(`registered knowledge tool rejects ${name} metadata instead of exposing old unlocked recipes`, () => fixture(async ({ tool, update }) => {
    await update({ knowledge: knowledgeOf(change) });
    await assert.rejects(tool.execute({ kind: 'all' }), /当前存档及轮次.*拒绝读取旧档或旧版知识/);
  }));
}

for (const [name, change, message] of [['inactive', { worldActive: false }, /未进入可识别/], ['unidentified save', { saveId: '' }, /未进入可识别/], ['unidentified epoch', { worldEpoch: '' }, /未进入可识别/], ['old state schema', { schemaVersion: 1 }, /数据协议尚未升级/]]) {
  test(`registered knowledge tool rejects ${name} world state`, () => fixture(async ({ tool, update }) => {
    await update({ state: stateOf(change) });
    await assert.rejects(tool.execute({ kind: 'recipes' }), message);
  }));
}

test('registered knowledge tool rejects stale state even while the knowledge file is freshly written', () => fixture(async ({ tool, update }) => {
  await update({ stale: true });
  await assert.rejects(tool.execute({ kind: 'recipes' }), /数据已过期/);
}));

test('API backend may read only correctly scoped knowledge without attaching or creating any session', () => fixture(async ({ tool, update }) => {
  await update({ state: stateOf({ protocol: 'chat-completions', backend: 'api' }) });
  const data = JSON.parse((await tool.execute({ kind: 'all' }, { agent: { id: 'read-only-caller' } })).text);
  assert.equal(data.recipes[0].name, '绷带'); assert.deepEqual(data.skills, ['本档技能']);
  await update({ state: stateOf({ protocol: 'chat-completions', backend: 'api' }), knowledge: knowledgeOf({ saveId: 'other-save' }) });
  await assert.rejects(tool.execute({ kind: 'all' }), /拒绝读取旧档/);
}));
