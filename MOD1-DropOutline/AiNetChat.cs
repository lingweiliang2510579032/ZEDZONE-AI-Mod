using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ZedZone.Net;

namespace Dropoutline
{
    /// <summary>
    /// 正式版（1.0）官方聊天接入层（ZedZone.Net.NetChatSync + ZedZone.Net.NetChatBar）。
    ///
    /// 游戏聊天是主机权威的：客户端 SendChat → 主机 OnHostReceive → 广播 →
    /// 各端 OnClientReceive。**所有落地的消息最终都走 NetChatSync.Append**，
    /// 所以用 Harmony 挂 Append 就能拿到聊天内容，比枚举它的 IReadOnlyList 稳
    /// （interop 里那套接口既没有 Count 也没有 GetEnumerator）。
    ///
    /// 单人模式下也会按需初始化官方 NetChatSync / NetChatBar 并开放 FeedAllowed / CanOpen / SendChat，
    /// 让单人与联机共享同一套左侧官方聊天框，不再弹出底部重复自建浮层。
    ///
    /// 发言同样是官方路径：Append(0, 名字, 文本) 以她的名义落进聊天框；
    /// 需要让别的玩家也看到时用 SendChat 走网络。
    /// </summary>
    internal static class AiNetChat
    {
        // SendChat 会以本机玩家身份回显，必须记住这些文本避免被当成新指令
        private static readonly Queue<string> _pendingEcho = new Queue<string>();
        private static float _nextEnsureAt;

        internal static bool Available
        {
            get
            {
                try { return ZedZone.Net.NetChatSync.instance != null && ZedZone.Net.NetChatBar.instance != null; }
                catch { return false; }
            }
        }

        internal static void ResetWorld()
        {
            _pendingEcho.Clear();
            _nextEnsureAt = 0f;
        }

        /// <summary>单人模式下是否启用官方左侧聊天栏与输入框。</summary>
        internal static bool ShouldEnableSingleplayerOfficialChat()
        {
            try
            {
                if (!AiSaveContext.Active) return false;
                if (NetworkManager.isOnline) return false;
                if (AiCompanion.CfgAiEnabled == null || !AiCompanion.CfgAiEnabled.Value) return false;
                if (AiCompanion.CfgChatViaOfficial != null && !AiCompanion.CfgChatViaOfficial.Value) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>单人模式下当前是否允许按回车打开官方聊天输入栏（弹窗/暂停/正在输入其他输入框时拦截）。</summary>
        internal static bool CanOpenSingleplayerChat()
        {
            try
            {
                if (!ShouldEnableSingleplayerOfficialChat()) return false;
                if (Time.timeScale <= 0.01f) return false;
                if (ModSettingsUi.IsConfigWindowOpen || UiChoicePopup.IsOpen) return false;
                if (ChatInput.IsOpen) return false;
                try { if (NetSessionPanel.isWindowOpen) return false; } catch { }
                return !HasFocusedInputField();
            }
            catch
            {
                return false;
            }
        }

        private static bool HasFocusedInputField()
        {
            try
            {
                var es = EventSystem.current;
                var go = es != null ? es.currentSelectedGameObject : null;
                if (go == null) return false;
                var field = go.GetComponent<InputField>();
                if (field != null && field.isFocused) return true;
                var comps = go.GetComponents<Component>();
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c != null && c.GetIl2CppType()?.Name == "TMP_InputField") return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>单人进档后按需初始化官方聊天同步器与左侧聊天栏 UI。</summary>
        internal static void EnsureOfficialChatReady()
        {
            if (!ShouldEnableSingleplayerOfficialChat()) return;
            float now = Time.unscaledTime;
            if (now < _nextEnsureAt) return;
            _nextEnsureAt = now + 1f;
            try
            {
                if (NetChatSync.instance != null && NetChatBar.instance != null) return;
                var nm = NetworkManager.EnsureInstance();
                if (nm == null) return;
                if (NetChatSync.instance == null) nm.gameObject.AddComponent<NetChatSync>();
                if (NetChatBar.instance == null) nm.gameObject.AddComponent<NetChatBar>();
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[单人官方聊天] 初始化失败: {e.Message}");
            }
        }

        /// <summary>单人模式下拦截 NetChatSync.SendChat，改走本地 Append 进官方栏并触发 AI 回复。</summary>
        internal static bool TryHandleOfflineSend(NetChatSync sync, string text)
        {
            try
            {
                if (NetworkManager.isOnline || !ShouldEnableSingleplayerOfficialChat()) return false;
                if (sync == null) return false;
                string clean = Clean(text);
                if (clean.Length == 0) return true;
                int playerId = 1;
                string playerName = null;
                var nm = NetworkManager.instance;
                if (nm != null)
                {
                    if (nm.localPlayerId > 0) playerId = nm.localPlayerId;
                    playerName = nm.localPlayerName;
                }
                if (string.IsNullOrWhiteSpace(playerName))
                    playerName = AiCompanion.PlayerDisplayName();
                sync.Append(playerId, playerName, clean);
                return true;
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[单人官方聊天] 发送失败: {e.Message}");
                return false;
            }
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

    /// <summary>官方聊天落地点的前后置补丁（含单人模式官方聊天栏开放）。</summary>
    [HarmonyPatch]
    internal static class NetChatPatches
    {
        [HarmonyPatch(typeof(ZedZone.Net.NetChatSync), nameof(ZedZone.Net.NetChatSync.Append), new Type[] { typeof(int), typeof(string), typeof(string) })]
        [HarmonyPostfix]
        static void AfterAppend(int senderId, string senderName, string text)
        {
            AiNetChat.OnAppend(senderId, senderName, text);
        }

        [HarmonyPatch(typeof(ZedZone.Net.NetChatBar), "FeedAllowed")]
        [HarmonyPostfix]
        static void AfterFeedAllowed(ref bool __result)
        {
            if (!__result && AiNetChat.ShouldEnableSingleplayerOfficialChat())
                __result = true;
        }

        [HarmonyPatch(typeof(ZedZone.Net.NetChatBar), "CanOpen")]
        [HarmonyPostfix]
        static void AfterCanOpen(ref bool __result)
        {
            if (!__result && AiNetChat.CanOpenSingleplayerChat())
                __result = true;
        }

        [HarmonyPatch(typeof(ZedZone.Net.NetChatSync), nameof(ZedZone.Net.NetChatSync.SendChat), new Type[] { typeof(string) })]
        [HarmonyPrefix]
        static bool BeforeSendChat(ZedZone.Net.NetChatSync __instance, string __0)
        {
            return !AiNetChat.TryHandleOfflineSend(__instance, __0);
        }
    }
}
