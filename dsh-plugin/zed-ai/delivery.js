import { readFile, writeFile, rename, unlink, mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { createHash } from 'node:crypto';

export const deliveryId = key => `zed-${createHash('sha256').update(key).digest('hex')}`;

// Persist acceptance across reconnects/restarts. Reserve before enqueue: after a
// crash in the enqueue/commit window, do not blindly enqueue a second copy.
export function createDurableDelivery(getDir, logger = console) {
  let chain = Promise.resolve();
  return (agent, text, label, metadata = {}) => {
    const operation = chain.catch(() => {}).then(async () => {
      if (!metadata.key || !agent?.session?.id) throw new Error('ZED 消息缺少可核验的投递身份');
      const dir = await getDir();
      await mkdir(dir, { recursive: true });
      const path = join(dir, 'dsh-deliveries.json');
      let ledger;
      try { ledger = JSON.parse((await readFile(path, 'utf8')).replace(/^\uFEFF/, '')); }
      catch (e) { if (e.code !== 'ENOENT') throw new Error(`ZED 投递记录读取失败，停止重复发送：${e.message}`); ledger = { version: 1, entries: {} }; }
      if (ledger?.version !== 1 || !ledger.entries || typeof ledger.entries !== 'object' || Array.isArray(ledger.entries)) throw new Error('ZED 投递记录格式错误，停止重复发送');
      const id = deliveryId(metadata.key), sessionId = agent.session.id;
      const previous = ledger.entries[id];
      if (previous) {
        if (previous.sessionId !== sessionId) throw new Error('ZED 投递身份与存档会话不一致');
        return true;
      }
      const save = async () => {
        const tmp = `${path}.${crypto.randomUUID()}.tmp`;
        try { await writeFile(tmp, JSON.stringify(ledger), { encoding: 'utf8', flag: 'wx' }); await rename(tmp, path); }
        finally { await unlink(tmp).catch(e => { if (e.code !== 'ENOENT') throw e; }); }
      };
      // Existing pre-upgrade history can already contain this pending chat: a
      // restart must adopt it instead of enqueuing the same player line twice.
      if (metadata.chatId && agent.session.snapshotEvents) {
        for (const event of agent.session.snapshotEvents()) {
          if (event.type !== 'agent/inbox/spliced') continue;
          const inserted = event.data?.inserted;
          if (!Array.isArray(inserted)) continue;
          const found = inserted.some(message => {
            const body = (message.content || []).filter(part => part.type === 'text').map(part => part.text).join('\n');
            const scope = body.match(/【存档边界】saveId=([^；\r\n]+)；worldEpoch=([^。\r\n]+)。/);
            const chat = body.match(/【玩家刚说｜消息ID ([^】\r\n]+)】/);
            return message.id === id || scope?.[1] === metadata.saveId && scope?.[2] === metadata.worldEpoch && chat?.[1] === metadata.chatId;
          });
          if (found) {
            ledger.entries[id] = { sessionId, status: 'sent', at: Date.now(), recovered: true }; await save(); return true;
          }
        }
      }
      ledger.entries[id] = { sessionId, status: 'reserved', at: Date.now() };
      await save();
      const message = { role: 'user', id, content: [{ type: 'text', text }], source: { kind: 'user' } };
      try {
        let accepted;
        if (typeof agent.followup === 'function') accepted = await agent.followup(message);
        else if (typeof agent.send === 'function') accepted = await agent.send(message, 'next-turn', true);
        else throw new Error('Agent 不支持接收游戏消息');
        if (accepted === false) { delete ledger.entries[id]; await save(); return false; }
      } catch (error) {
        delete ledger.entries[id]; await save(); throw error;
      }
      ledger.entries[id].status = 'sent';
      try { await save(); } catch (error) { logger?.warn?.(`[zed-ai] ${label}已入队，但投递回执写入失败；保留预留记录防止重发：${error.message}`); }
      return true;
    });
    chain = operation;
    return operation;
  };
}
