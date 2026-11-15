using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace Dropoutline
{
    /// <summary>
    /// MOD2「AI伙伴」——独立插件。
    ///
    /// 与「持续高亮轮廓」完全分开：各自的 GUID、配置文件、DLL，
    /// 互不引用、互不干涉，可单独启用/禁用/卸载。
    ///
    /// 类名沿用 DropOutlinePlugin 只是为了少改内部引用，对外显示的是 AI伙伴。
    /// </summary>
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class DropOutlinePlugin : BasePlugin
    {
        public const string PluginGUID = "zed.ai";
        public const string PluginName = "AI伴侣（测试版v0.1）";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource L;
        internal static bool ShuttingDown;
        internal static bool _firstTickLogged;
        /// <summary>当前世界开始的时间（0 表示不在世界）。用于进世界后的保护期。</summary>
        internal static float WorldSince;
        /// <summary>
        /// 正在退出世界（存档后回主菜单/卸载场景）。这段时间里 IL2CPP 对象正处于
        /// 半销毁状态，逐帧读取它们会踩到已释放的原生内存——两次回主菜单闪退都停在
        /// 场景卸载结束那一瞬。拆除期只发布"未激活"状态，不碰任何游戏对象。
        /// </summary>
        internal static bool WorldTearingDown;
        private static float _teardownAt;

        internal static void BeginWorldTeardown(string source)
        {
            if (WorldTearingDown) return;
            WorldTearingDown = true;
            _teardownAt = Time.unscaledTime;
            try { L?.LogInfo($"[AI诊断] 进入世界拆除期（{source}），停止读取游戏对象"); } catch { }
        }

        /// <summary>卸载结束后场景已稳定，可以恢复；用时间兜底，避免卡在拆除态。</summary>
        internal static void AdvanceWorldTeardown()
        {
            if (!WorldTearingDown) return;
            if (Time.unscaledTime - _teardownAt < 3f) return;
            WorldTearingDown = false;
            try { L?.LogInfo("[AI诊断] 世界拆除期结束，恢复读取"); } catch { }
        }

        public override void Load()
        {
            L = Log;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                L.LogInfo($"[耗时] 进程启动到插件加载 {((DateTime.Now - proc.StartTime).TotalMilliseconds):F0}ms");
            }
            catch { }

            AiCompanion.Init(Config);
            try { AiHostBench.Init(Environment.GetCommandLineArgs()); } catch { }
            var harmony = new Harmony(PluginGUID);
            try { harmony.PatchAll(typeof(AiPatches)); }
            catch (Exception e) { L.LogWarning($"[Harmony] AiPatches 部分补丁挂接异常: {e.Message}"); }
            try { AiPatches.PatchGameOptionsPanel(harmony); }
            catch (Exception e) { L.LogWarning($"[MOD设置] 挂接 GameOptionsPanel.OpenPanel 失败: {e.Message}"); }
            try { harmony.PatchAll(typeof(NetChatPatches)); }
            catch (Exception e) { L.LogWarning($"[联机聊天] 挂接官方聊天失败（将退回自带聊天框）: {e.Message}"); }
            L.LogInfo("[耗时] Harmony 补丁已应用");
            L.LogInfo($"[耗时] 配置+AI初始化 {sw.ElapsedMilliseconds}ms");

            try { Application.quitting += new Action(() => { ShuttingDown = true; AiCompanion.RefreshWorld(false); }); } catch { }
            ClassInjector.RegisterTypeInIl2Cpp<ProbeBehaviour>();
            L.LogInfo($"[耗时] ClassInjector {sw.ElapsedMilliseconds}ms");
            var go = new GameObject("YuyeAIProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ProbeBehaviour>();
            L.LogInfo($"{PluginName} v{PluginVersion} 加载完成，总耗时 {sw.ElapsedMilliseconds}ms");
        }
    }

    /// <summary>设置面板每次打开都注入「MOD 设置」分区（幂等，先清后插）。</summary>
    [HarmonyPatch]
    public static class AiPatches
    {
        [HarmonyPatch(typeof(GameDataUI), nameof(GameDataUI.OnDeleteConfirm), new Type[] { typeof(Il2CppSystem.Object) })]
        [HarmonyPrefix]
        static void BeforeDeleteUi(GameDataUI __instance) { ObserveDeletedUi(__instance); }

        [HarmonyPatch(typeof(GameDataUI_IngameLoad), nameof(GameDataUI_IngameLoad.OnDeleteConfirm), new Type[] { typeof(Il2CppSystem.Object) })]
        [HarmonyPrefix]
        static void BeforeDeleteLoadUi(GameDataUI_IngameLoad __instance) { ObserveDeletedUi(__instance); }

        [HarmonyPatch(typeof(GameDataUI_IngameSave), nameof(GameDataUI_IngameSave.OnDeleteConfirm), new Type[] { typeof(Il2CppSystem.Object) })]
        [HarmonyPrefix]
        static void BeforeDeleteSaveUi(GameDataUI_IngameSave __instance) { ObserveDeletedUi(__instance); }

        static void ObserveDeletedUi(GameDataUI ui)
        {
            try
            {
                var data = ui?.gameData;
                if (data == null) return;
                AiSaveLifecycle.Observe(data.playerData != null ? data.playerData.characterId : "");
            }
            catch { }
        }

        // 存档后回主菜单：接下来就是卸载场景，从这里开始不许再碰游戏对象。
        [HarmonyPatch(typeof(InGameController), nameof(InGameController.ReturnToMainMenuWithSave), new Type[0])]
        [HarmonyPrefix]
        static void BeforeReturnToMenuWithSave() { try { DropOutlinePlugin.BeginWorldTeardown("ReturnToMainMenuWithSave"); } catch { } }

        // Deleting progress is not the end of a conversation: only deleting the
        // character card is. Nothing else here may archive a conversation.
        [HarmonyPatch(typeof(CharacterDataManager), nameof(CharacterDataManager.DeleteCharacterData), new Type[] { typeof(string) })]
        [HarmonyPrefix]
        static void BeforeDeleteCharacter(string __0, out AiSaveLifecycle.Receipt __state) { __state = AiSaveLifecycle.BeforeDeleteCharacter(__0); }

        [HarmonyPatch(typeof(CharacterDataManager), nameof(CharacterDataManager.DeleteCharacterData), new Type[] { typeof(string) })]
        [HarmonyPostfix]
        static void AfterDeleteCharacter(AiSaveLifecycle.Receipt __state) { AiSaveLifecycle.AfterDeleteCharacter(__state); }

        public static void PatchGameOptionsPanel(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(AiPatches), nameof(AfterGameOptionsOpen));
            foreach (var m in typeof(GameOptionsPanel).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.Name != nameof(GameOptionsPanel.OpenPanel) || m.IsGenericMethod) continue;
                harmony.Patch(m, postfix: postfix);
            }
        }

        static void AfterGameOptionsOpen(GameOptionsPanel __instance)
        {
            try { ModSettingsUi.Inject(__instance); }
            catch (Exception e) { DropOutlinePlugin.L.LogWarning($"[MOD设置] 注入异常: {e.Message}"); }
        }
    }

    /// <summary>主循环：设置界面 tick + AI tick + 聊天框。</summary>
    public class ProbeBehaviour : MonoBehaviour
    {
        private static bool _chatReady;

        public ProbeBehaviour(IntPtr ptr) : base(ptr) { }

        public void Update()
        {
            try
            {
                if (DropOutlinePlugin.ShuttingDown) return;

                // 首次 Update 就记录（主菜单也记），用来量「进程→主循环」要多久
                if (!DropOutlinePlugin._firstTickLogged)
                {
                    DropOutlinePlugin._firstTickLogged = true;
                    try
                    {
                        var proc = System.Diagnostics.Process.GetCurrentProcess();
                        DropOutlinePlugin.L.LogInfo($"[耗时] 进程启动到首次 Update {((DateTime.Now - proc.StartTime).TotalMilliseconds):F0}ms");
                    }
                    catch { }
                }

                // 退出世界/卸载场景期间只发布"未激活"，绝不读取游戏对象。
                DropOutlinePlugin.AdvanceWorldTeardown();
                if (DropOutlinePlugin.WorldTearingDown)
                {
                    AiActions.ClearHighlight();
                    AiCompanion.RefreshWorld(false);
                    return;
                }

                // 设置界面在主菜单也能打开，不能因为"不在世界里"就跳过
                ModSettingsUi.Tick();
                AiSaveLifecycle.Tick();
                // 开服测量必须在主菜单也跑（它要量菜单/进世界两个阶段）
                try { AiHostBench.Tick(); } catch { }

                AiActions.TickHighlight();

                if (!AiCompanion.RefreshWorld(HasActiveMapController()))
                {
                    AiCompanion.TickMenuDsh();
                    return;
                }

                if (DropOutlinePlugin.WorldSince <= 0f) DropOutlinePlugin.WorldSince = Time.unscaledTime;

                AiCompanion.Tick();
                if (!_chatReady && DropOutlinePlugin.WorldSince > 0f &&
                    Time.unscaledTime - DropOutlinePlugin.WorldSince > 12f)
                {
                    _chatReady = true;
                    ChatInput.Create();
                }
                ChatInput.Tick();
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogError($"Update 异常: {e.Message}");
            }
        }

        public void LateUpdate()
        {
            if (DropOutlinePlugin.ShuttingDown || DropOutlinePlugin.WorldTearingDown) return;
            try { AiTaskRuntime.Tick(); }
            catch (Exception error) { DropOutlinePlugin.L.LogWarning("[AI任务] " + error.Message); }
        }

        private static MapController _cachedMap;
        private static float _mapCheckAt;

        /// <summary>是否有活跃的 MapController（缓存 1 秒，避免每帧遍历场景）。</summary>
        private static bool HasActiveMapController()
        {
            try
            {
                if (_cachedMap != null && _cachedMap.gameObject != null && _cachedMap.gameObject.activeInHierarchy
                    && _cachedMap.isActiveAndEnabled
                    && Time.unscaledTime - _mapCheckAt < 1f)
                    return true;

                _mapCheckAt = Time.unscaledTime;
                foreach (var mc in Resources.FindObjectsOfTypeAll<MapController>())
                {
                    if (mc != null && mc.gameObject != null && mc.gameObject.activeInHierarchy && mc.isActiveAndEnabled)
                    {
                        _cachedMap = mc;
                        return true;
                    }
                }
                _cachedMap = null;
            }
            catch { }
            return false;
        }
    }
}





