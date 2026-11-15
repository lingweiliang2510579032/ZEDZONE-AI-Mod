using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using ZedZone.Net;

namespace Dropoutline
{
    /// <summary>
    /// 正式版（1.0）官方联机聊天接入层。
    ///
    /// 游戏聊天是主机权威的：客户端 SendChat → 主机 OnHostReceive → 广播 →
    /// 各端 OnClientReceive。**所有落地的消息最终都走 NetChatSync.Append**，
    /// 所以用 Harmony 挂 Append 就能拿到聊天内容，比枚举它的 IReadOnlyList 稳
    /// （interop 里那套接口既没有 Count 也没有 GetEnumerator）。
    ///
    /// 发言同样是官方路径：Append(0, 名字, 文本) 以她的名义落进聊天框；
    /// 需要让别的玩家也看到时用 SendChat 走网络。
    /// </summary>
    internal static class AiNetChat
    {
        // SendChat 会以本机玩家身份回显，必须记住这些文本避免被当成新指令
        private static readonly Queue<string> _pendingEcho = new Queue<string>();

        internal static bool Available
        {
            get
            {
                try { return ZedZone.Net.NetChatSync.instance != null; }
                catch { return false; }
            }
        }

        internal static void ResetWorld()
        {
            _pendingEcho.Clear();
        }

        /// <summary>收到一条官方聊天（由 Append 后置补丁调用）。</summary>
        internal static void OnAppend(int senderId, string senderName, string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                string clean = Clean(text);
                string sender = senderName ?? "";

                // 她自己说的话不再喂回给 AI
                int aiId = AiCoopPlayer.AiNetPlayerId;
                if (clean.Length == 0 || (aiId > 0 && senderId == aiId) || sender == AiCompanion.AiDisplayName()) return;

                // SendChat 的回显（以玩家身份）不能当成新指令
                if (_pendingEcho.Count > 0 && _pendingEcho.Peek() == clean) { _pendingEcho.Dequeue(); return; }

                AiCompanion.SubmitPlayerChat(clean);
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[联机聊天] 处理失败: {e.Message}");
            }
        }

        /// <summary>以她的名字在官方聊天框里留一条；玩家模式下使用 AI 玩家的独立 playerId，并在主机时同步给所有联机玩家。</summary>
        internal static bool Say(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                var chat = NetChatSync.instance;
                if (chat == null) return false;
                string clean = Clean(text);
                string aiName = AiCompanion.AiDisplayName();
                int senderId = AiCompanion.IsPlayerMode() && AiCoopPlayer.AiNetPlayerId > 0
                    ? AiCoopPlayer.AiNetPlayerId
                    : 0;
                chat.Append(senderId, aiName, clean);

                if (senderId > 0 && NetworkManager.instance != null && NetworkManager.isOnline && NetworkManager.isHost)
                {
                    try
                    {
                        NetworkManager.instance.BroadcastToJoined(
                            new MsgChat { senderId = senderId, senderName = aiName, text = clean },
                            null, DeliveryMethod.ReliableOrdered, 0);
                    }
                    catch { }
                }
                return true;
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[联机聊天] 发言失败: {e.Message}");
                return false;
            }
        }

        /// <summary>走官方网络通道发言：所有玩家都能看到（会以本机玩家身份显示）。</summary>
        internal static bool SayNetworked(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                var chat = ZedZone.Net.NetChatSync.instance;
                if (chat == null) return false;
                string clean = Clean(text);
                _pendingEcho.Enqueue(clean);
                if (_pendingEcho.Count > 16) _pendingEcho.Dequeue();
                chat.SendChat(clean);
                return true;
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[联机聊天] 网络发言失败: {e.Message}");
                return false;
            }
        }

        private static string Clean(string text)
        {
            // 官方聊天单行显示，换行会破坏排版
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }

    /// <summary>官方聊天落地点的后置补丁。</summary>
    [HarmonyPatch]
    internal static class NetChatPatches
    {
        [HarmonyPatch(typeof(ZedZone.Net.NetChatSync), nameof(ZedZone.Net.NetChatSync.Append), new Type[] { typeof(int), typeof(string), typeof(string) })]
        [HarmonyPostfix]
        static void AfterAppend(int senderId, string senderName, string text)
        {
            AiNetChat.OnAppend(senderId, senderName, text);
        }
    }
}
