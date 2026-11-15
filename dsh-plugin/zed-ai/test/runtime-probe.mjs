// Read-only ASAR inspection helper used to exercise the installed Cordis runtime.
import { openSync, readSync, closeSync } from 'node:fs';
export function bundledText(path) {
  const fd = openSync('D:/DSH/DSH NEXT/resources/app.asar', 'r');
  const prefix = Buffer.alloc(16); readSync(fd, prefix, 0, 16, 0);
  const header = Buffer.alloc(prefix.readUInt32LE(12)); readSync(fd, header, 0, header.length, 16);
  let entry = JSON.parse(header);
  for (const key of path.split('/')) entry = entry.files[key];
  const data = Buffer.alloc(entry.size);
  readSync(fd, data, 0, data.length, 8 + prefix.readUInt32LE(4) + Number(entry.offset));
  closeSync(fd);
  return data.toString('utf8');
}
const urlOf = source => 'data:text/javascript;base64,' + Buffer.from(source).toString('base64');
export async function loadCordis() {
  const cosmokit = urlOf(bundledText('node_modules/@deepseek-ai/cosmokit/lib/index.js'));
  const source = bundledText('node_modules/@deepseek-ai/cordis/lib/index.js').replace('"@deepseek-ai/cosmokit"', JSON.stringify(cosmokit));
  return import(urlOf(source));
}
