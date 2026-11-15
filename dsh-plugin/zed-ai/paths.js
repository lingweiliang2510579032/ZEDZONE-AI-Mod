import { readFile, stat } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';

const APP_ID = '1211600';
const clean = value => typeof value === 'string' ? value.trim() : '';
const key = value => resolve(value).replace(/[\\/]+$/, '').toLowerCase();

async function exists(file) {
  try { return (await stat(file)).isFile(); }
  catch (error) { if (error.code === 'ENOENT' || error.code === 'ENOTDIR') return false; throw error; }
}

// Valve KeyValues: support comments, escaped Windows paths and legacy libraries.
// Parse as data only; never evaluate file content or execute Steam/registry tools.
function parseVdf(text) {
  const tokens = [];
  let offset = 0;
  while (offset < text.length) {
    if (/\s/.test(text[offset])) { offset++; continue; }
    if (text.startsWith('//', offset)) {
      const end = text.indexOf('\n', offset); offset = end < 0 ? text.length : end + 1; continue;
    }
    const c = text[offset++];
    if (c === '{' || c === '}') { tokens.push(c); continue; }
    if (c !== '"') throw new Error('Steam VDF 存在非法标记');
    let value = '', closed = false;
    while (offset < text.length) {
      const next = text[offset++];
      if (next === '"') { closed = true; break; }
      if (next === '\\' && offset < text.length && ['\\', '"'].includes(text[offset])) value += text[offset++];
      else value += next;
    }
    if (!closed) throw new Error('Steam VDF 字符串未结束');
    tokens.push({ value });
  }
  let at = 0;
  function object(nested) {
    const result = Object.create(null);
    while (at < tokens.length) {
      const token = tokens[at++];
      if (token === '}') { if (!nested) throw new Error('Steam VDF 括号错误'); return result; }
      if (!token || typeof token.value !== 'string') throw new Error('Steam VDF 键错误');
      const value = tokens[at++];
      if (value === '{') result[token.value.toLowerCase()] = object(true);
      else if (value && typeof value.value === 'string') result[token.value.toLowerCase()] = value.value;
      else throw new Error('Steam VDF 值错误');
    }
    if (nested) throw new Error('Steam VDF 对象未结束');
    return result;
  }
  return object(false);
}

async function readVdf(file) {
  try { return parseVdf((await readFile(file, 'utf8')).replace(/^\uFEFF/, '')); }
  catch (error) { if (error.code === 'ENOENT' || error.code === 'ENOTDIR') return null; throw new Error(`无法读取 Steam 资料 ${file}：${error.message}`); }
}

function explicitResult(config) {
  const state = clean(config.stateDir), game = clean(config.gameDir), workspace = clean(config.workspacePath);
  if (!state && !game && !workspace) return null;
  const stateDir = resolve(state || (game ? join(game, 'ZED AI') : workspace));
  return {
    gameDir: game ? resolve(game) : basename(stateDir).toLowerCase() === 'zed ai' ? dirname(stateDir) : null,
    stateDir,
    workspacePath: resolve(workspace || stateDir),
  };
}

/** Resolve explicit directories or one verified Steam installation, without writes.
 * opts.candidateRoots replaces automatic roots for isolated tests/embedded hosts.
 */
export async function resolveGamePaths(config = {}, opts = {}) {
  const explicit = explicitResult(config);
  if (explicit) return explicit;
  const env = opts.env || process.env;
  const programDirs = [env['ProgramFiles(x86)'], env.ProgramW6432, env.ProgramFiles];
  if (env.SystemDrive) programDirs.push(join(env.SystemDrive + '\\', 'Program Files (x86)'));
  const defaultExtraRoots = opts.env ? [] : ['C:\\Steam', 'D:\\steam', 'D:\\Steam', 'E:\\steam'];
  const roots = opts.candidateRoots !== undefined ? opts.candidateRoots : [
    ...programDirs.filter(Boolean).map(dir => join(dir, 'Steam')),
    clean(env.STEAM_PATH), clean(env.SteamPath),
    ...defaultExtraRoots,
  ];
  const libraries = new Map();
  for (const root of roots) {
    if (!clean(root)) continue;
    const steam = resolve(root);
    libraries.set(key(steam), steam);
    const vdf = await readVdf(join(steam, 'steamapps', 'libraryfolders.vdf'));
    for (const [index, value] of Object.entries(vdf?.libraryfolders || {})) {
      if (!/^\d+$/.test(index)) continue;
      const path = typeof value === 'string' ? value : value?.path;
      if (clean(path) && isAbsolute(path)) libraries.set(key(path), resolve(path));
    }
  }
  const candidates = new Map();
  for (const library of libraries.values()) {
    const vdf = await readVdf(join(library, 'steamapps', `appmanifest_${APP_ID}.acf`));
    const app = vdf?.appstate;
    if (!app || app.appid !== APP_ID || !clean(app.installdir)) continue;
    // installdir is a name beneath common, never an arbitrary filesystem path.
    if (app.installdir === '.' || app.installdir === '..' || /[\\/:]/.test(app.installdir)) continue;
    const gameDir = join(library, 'steamapps', 'common', app.installdir);
    if (!(await exists(join(gameDir, 'ZEDZONE.exe'))) && !(await exists(join(gameDir, 'BepInEx', 'plugins', 'ZEDAI.dll')))) continue;
    candidates.set(key(gameDir), gameDir);
  }
  if (candidates.size === 0) throw new Error('未识别到已安装的《隔离区》。请在 ZED AI 插件配置中填写 gameDir 或 stateDir；不会创建猜测的游戏目录。');
  if (candidates.size > 1) throw new Error(`识别到多个《隔离区》安装目录，请配置 gameDir 或 stateDir 选择一个：${[...candidates.values()].join('；')}`);
  const gameDir = candidates.values().next().value;
  const stateDir = join(gameDir, 'ZED AI');
  return { gameDir, stateDir, workspacePath: stateDir };
}
