import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm, stat } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { resolveGamePaths } from '../paths.js';

async function fixture(run) {
  const root = await mkdtemp(join(tmpdir(), 'zed-paths-'));
  try { await run(root); } finally { await rm(root, { recursive: true, force: true }); }
}
const quoted = value => '"' + value.replaceAll('\\', '\\\\').replaceAll('"', '\\"') + '"';
async function install(library, name = 'ZED ZONE', marker = 'ZEDZONE.exe') {
  const game = join(library, 'steamapps', 'common', name);
  await mkdir(join(game, 'BepInEx', 'plugins'), { recursive: true });
  await writeFile(join(library, 'steamapps', 'appmanifest_1211600.acf'), `"AppState" { "appid" "1211600" "installdir" ${quoted(name)} }`);
  await writeFile(join(game, marker), '');
  return game;
}

test('explicit custom state directory needs no installation or mkdir', () => fixture(async root => {
  const stateDir = join(root, 'custom-data');
  assert.deepEqual(await resolveGamePaths({ stateDir }, { candidateRoots: [] }), {
    gameDir: null, stateDir, workspacePath: stateDir,
  });
  await assert.rejects(stat(stateDir), { code: 'ENOENT' });
}));

test('explicit state/game/workspace take precedence without reading malformed Steam files', () => fixture(async root => {
  const stateDir = join(root, 'other-data'), gameDir = join(root, 'game'), workspacePath = join(root, 'workspace');
  assert.deepEqual(await resolveGamePaths({ stateDir, gameDir, workspacePath }, { candidateRoots: [root] }), {
    gameDir, stateDir, workspacePath,
  });
}));

test('gameDir default data directory and workspace-only custom directory', () => fixture(async root => {
  const gameDir = join(root, 'game');
  const stateDir = join(gameDir, 'ZED AI');
  assert.deepEqual(await resolveGamePaths({ gameDir }), { gameDir, stateDir, workspacePath: stateDir });
  assert.deepEqual(await resolveGamePaths({ stateDir }), { gameDir, stateDir, workspacePath: stateDir });
  const workspacePath = join(root, 'custom');
  assert.deepEqual(await resolveGamePaths({ workspacePath }), { gameDir: null, stateDir: workspacePath, workspacePath });
}));

test('detect installation through modern libraryfolders VDF', () => fixture(async root => {
  const steam = join(root, 'Steam'), library = join(root, 'library');
  await mkdir(join(steam, 'steamapps'), { recursive: true });
  await writeFile(join(steam, 'steamapps', 'libraryfolders.vdf'), `// libraries\n"libraryfolders" { "0" { "path" ${quoted(library)} "apps" { "1211600" "1" } } }`);
  const gameDir = await install(library);
  const result = await resolveGamePaths({}, { candidateRoots: [steam, steam] });
  assert.deepEqual(result, { gameDir, stateDir: join(gameDir, 'ZED AI'), workspacePath: join(gameDir, 'ZED AI') });
  await assert.rejects(stat(result.stateDir), { code: 'ENOENT' });
}));

test('detect legacy libraries and DLL marker without EXE', () => fixture(async root => {
  const steam = join(root, 'Steam'), library = join(root, 'library');
  await mkdir(join(steam, 'steamapps'), { recursive: true });
  await writeFile(join(steam, 'steamapps', 'libraryfolders.vdf'), `"LibraryFolders" { "1" ${quoted(library)} }`);
  const gameDir = await install(library, 'ZED ZONE', join('BepInEx', 'plugins', 'ZEDAI.dll'));
  assert.equal((await resolveGamePaths({}, { candidateRoots: [steam] })).gameDir, gameDir);
}));

test('program-files environment root finds Steam without hard-coded user drive', () => fixture(async root => {
  const gameDir = await install(join(root, 'Steam'));
  const result = await resolveGamePaths({}, { env: { 'ProgramFiles(x86)': root } });
  assert.equal(result.gameDir, gameDir);
}));

test('multiple verified installations fail explicitly', () => fixture(async root => {
  const a = join(root, 'a'), b = join(root, 'b');
  await install(a); await install(b);
  await assert.rejects(resolveGamePaths({}, { candidateRoots: [a, b] }), /多个.*安装目录/);
}));

test('missing manifest or executable never fabricates an installation', () => fixture(async root => {
  await assert.rejects(resolveGamePaths({}, { candidateRoots: [root] }), /未识别到/);
  const game = await install(root);
  await rm(join(game, 'ZEDZONE.exe'));
  await assert.rejects(resolveGamePaths({}, { candidateRoots: [root] }), /未识别到/);
  await assert.rejects(stat(join(game, 'ZED AI')), { code: 'ENOENT' });
}));

test('malformed Steam VDF reports configuration problem without choosing silently', () => fixture(async root => {
  await mkdir(join(root, 'steamapps'), { recursive: true });
  await writeFile(join(root, 'steamapps', 'libraryfolders.vdf'), '"libraryfolders" { "0" ');
  await assert.rejects(resolveGamePaths({}, { candidateRoots: [root] }), /无法读取 Steam 资料/);
}));

test('manifest path traversal is not accepted', () => fixture(async root => {
  await mkdir(join(root, 'steamapps'), { recursive: true });
  await writeFile(join(root, 'steamapps', 'appmanifest_1211600.acf'), '"AppState" { "appid" "1211600" "installdir" "../escape" }');
  await assert.rejects(resolveGamePaths({}, { candidateRoots: [root] }), /未识别到/);
}));
