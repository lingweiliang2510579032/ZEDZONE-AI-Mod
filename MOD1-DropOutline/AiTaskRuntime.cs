using System;
using System.Text;
using UnityEngine;

namespace Dropoutline
{
    // Unity/native work runs only on the game main thread. No synthetic keys,
    // cursor movement, UI opening, sleeps, save editing, or off-thread native calls.
    internal static class AiTaskRuntime
    {
        private static readonly AiTaskScheduler Scheduler = new AiTaskScheduler();
        private static float _lastPlayerInput = -100f;
        private static Vector3 _lastMouse;
        private static bool _mouseKnown;
        private static string _chatId = "", _chatText = "", _chatScope = "", _usedChatId = "", _usedControl = "", _lastRecorded = "";
        public static string Scope => AiSaveContext.SaveId + ":" + AiSaveContext.Epoch;

        public static AiTaskRequest Parse(object obj)
        {
            if (obj == null) return null;
            var type = MiniJson.Get(obj, "type") as string;
            if (string.IsNullOrEmpty(type)) throw new ArgumentException("任务需要 type");
            var countValue = MiniJson.Get(obj, "count");
            double count = countValue == null ? 1 : countValue is double d ? d : double.NaN;
            if (double.IsNaN(count) || double.IsInfinity(count) || count != Math.Truncate(count) || count < 1 || count > 100) throw new ArgumentException("任务次数必须为1到100的整数");
            string repeat = MiniJson.Get(obj, "repeat") as string ?? "count";
            string target = MiniJson.Get(obj, "target") as string ?? "";
            if (target.Length > 200) throw new ArgumentException("任务目标过长");
            return new AiTaskRequest { Type = type, Target = target.Trim(), Repeat = repeat, Count = (int)count };
        }
        public static void OnPlayerChat(string id, string text)
        {
            _chatId = id ?? ""; _chatText = text ?? ""; _chatScope = Scope;
            var control = _chatText.Trim().TrimEnd('。', '！', '!', ' ');
            string kind = control == "停止任务" || control == "取消任务" || control == "停止AI任务" || control == "停止所有任务" ? "cancel" :
                control == "暂停任务" || control == "暂停AI任务" ? "pause" : control == "继续任务" || control == "恢复任务" ? "resume" : "";
            if (kind.Length > 0) { Control(kind); _usedChatId = _chatId; _usedControl = kind; }
        }
        private static void Control(string type)
        {
            if (type == "cancel") { AiNativeActions.Interrupt(); AiDiscardActions.Reset(); Scheduler.Cancel(); }
            else if (type == "pause") { AiNativeActions.Interrupt(); Scheduler.Pause(); }
            else if (type == "resume") Scheduler.Resume();
        }
        public static string Accept(AiTaskRequest request, string commandId, string replyTo)
        {
            if (request == null) return "";
            if (!AiSaveContext.Active || AiCompanion.CfgAiEnabled?.Value != true) throw new InvalidOperationException("没有可执行任务的活动世界");
            if (string.IsNullOrEmpty(replyTo) || replyTo != _chatId || _chatScope != Scope || string.IsNullOrWhiteSpace(_chatText)) throw new InvalidOperationException("新任务或任务控制必须关联当前玩家的聊天，不接受自主编造任务");
            if (_usedChatId == replyTo)
            {
                if (!string.IsNullOrEmpty(_usedControl) && request.Type == _usedControl) return "本消息的任务控制已处理：" + Scheduler.Status;
                throw new InvalidOperationException("这条玩家消息已经下达过任务，不能重复或改成其他控制");
            }
            if (request.Type == "cancel" || request.Type == "pause" || request.Type == "resume")
            { Control(request.Type); _usedChatId = replyTo; _usedControl = request.Type; return "任务控制已处理：" + Scheduler.Status; }
            if (request.Type == "discard" && AiCompanion.CfgAllowDiscard?.Value != true) throw new InvalidOperationException("允许AI丢弃开关已关闭，只能建议玩家自行丢弃");
            if (request.Type != "craft_plan" && !OfflineWritesAllowed()) throw new InvalidOperationException("多人/联网或未知网络状态尚未验证物品事务，仅支持只读规划，不执行写操作");
            if (request.Repeat == "continuous" && !AllowsContinuous(_chatText)) throw new InvalidOperationException("持续任务需要玩家明确说持续、一直、自动维护或保持；本次没有这样的授权");
            if (request.Type == "craft" && AiCompanion.CfgAllowDirectCraft?.Value != true)
                throw new InvalidOperationException("允许AI直接合成开关已关闭，只能做制造规划；材料不够时会直接说缺什么");
            if (Scheduler.Active) throw new InvalidOperationException("已有任务执行中，请先取消或等它结束，不能悄悄替换");
            if (!Scheduler.Start(commandId, Scope, request, Time.unscaledTime)) throw new InvalidOperationException("任务未启动");
            AiNativeActions.Reset(); AiInventoryTasks.Reset(); AiDiscardActions.Reset();
            _usedChatId = replyTo; _usedControl = ""; _lastRecorded = "";
            ChatInput.Log("任务状态", "已接收：" + request.Type + "，等待玩家停止操作后执行；不是完成通知。");
            return "任务已接收，尚未完成；可从tasks读取实际进度";
        }
        private static bool AllowsContinuous(string text)
        {
            text = text ?? "";
            if (text.Contains("不要") || text.Contains("别") || text.Contains("停止") || text.Contains("取消") || text.Contains("不再") || text.Contains("不用")) return false;
            return text.Contains("持续") || text.Contains("一直") || text.Contains("自动维护") || text.Contains("保持") || text.Contains("自动帮") || text.Contains("自动捡") || text.Contains("自动上弹");
        }
        private static bool OfflineWritesAllowed()
        {
            if (!AiSaveContext.Active) return false;
            try { return !ZedZone.Net.NetworkManager.isOnline && !ZedZone.Net.NetworkManager.isHost && !ZedZone.Net.NetworkManager.isClient &&
                !ZedZone.Net.NetworkManager.hasPendingClientEscrow && !ZedZone.Net.NetworkManager.resettingWorld; }
            catch { return false; }
        }
        public static void WorldChanged()
        {
            AiNativeActions.Interrupt(); Scheduler.Cancel("世界或后端已改变，任务已取消");
            AiNativeActions.Reset(); AiInventoryTasks.Reset(); AiDiscardActions.Reset();
            _chatId = _chatText = _chatScope = _usedChatId = _usedControl = "";
            _mouseKnown = false; _lastPlayerInput = Time.unscaledTime;
        }
        public static void Tick()
        {
            if (!AiSaveContext.Active || AiCompanion.CfgAiEnabled?.Value != true) return;
            if (Scheduler.Active && Scheduler.Request.Type != "craft_plan" && !OfflineWritesAllowed())
            { AiNativeActions.Interrupt(); Scheduler.Cancel("网络状态改变或不明，已停止未验证的物品写操作"); }
            bool busy = PlayerBusyNow();
            Scheduler.Tick(Scope, Time.unscaledTime, busy, request => request.Type == "organize" ? AiInventoryTasks.Step(request) : request.Type == "discard" ? AiDiscardActions.Step(request) : AiNativeActions.Step(request), AiNativeActions.Interrupt);
            if (!Scheduler.Active && !string.IsNullOrEmpty(Scheduler.Id))
            {
                var key = Scheduler.Id + ":" + Scheduler.Status;
                if (key != _lastRecorded && Scheduler.Scope == Scope)
                {
                    _lastRecorded = key;
                    ChatInput.Log("任务状态", Scheduler.Status + "：" + Scheduler.Message);
                    AiMemory.AddEvent("任务事实：" + Scheduler.Request.Type + " " + Scheduler.Status + "，已执行" + Scheduler.Completed + "次；" + Scheduler.Message, AiMemory.TierImportant);
                    AiMemory.Save();
                }
            }
        }
        public static bool PlayerBusyNow()
        {
            try
            {
                if (!Application.isFocused || ChatInput.IsOpen || Time.timeScale <= 0f || !GameInputManager.inputAllowed) return true;
                var gc = GameController.instance;
                if (gc == null || gc.playerCharacter == null || gc.isSoftPaused || gc.playerCharacter.characterData == null || gc.playerCharacter.characterData.deadFlag) return true;
                bool input = Input.anyKey || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2) || Input.mouseScrollDelta.sqrMagnitude > 0;
                var mouse = Input.mousePosition;
                if (_mouseKnown && (mouse - _lastMouse).sqrMagnitude > 0.1f) input = true;
                _lastMouse = mouse; _mouseKnown = true;
                if (Math.Abs(GameInputManager.GetAxis(GameInputManager.AxisType.LeftStickX)) > 0.15f || Math.Abs(GameInputManager.GetAxis(GameInputManager.AxisType.LeftStickY)) > 0.15f ||
                    Math.Abs(GameInputManager.GetAxis(GameInputManager.AxisType.RightStickX)) > 0.15f || Math.Abs(GameInputManager.GetAxis(GameInputManager.AxisType.RightStickY)) > 0.15f ||
                    Math.Abs(GameInputManager.GetAxis(GameInputManager.AxisType.TriggerL1)) > 0.15f || Math.Abs(GameInputManager.GetAxis(GameInputManager.AxisType.TriggerR1)) > 0.15f) input = true;
                if (MouseController.instance != null && MouseController.instance.isDraggingItemUI) input = true;
                if (gc.playerCharacter.isSleeping || gc.playerCharacter.isDriving) input = true;
                if (input) _lastPlayerInput = Time.unscaledTime;
                return input || Time.unscaledTime - _lastPlayerInput < 1f;
            }
            catch { return true; } // Unknown input state never grants control to AI.
        }
        public static string TasksJson()
        {
            if (string.IsNullOrEmpty(Scheduler.Id) || Scheduler.Scope != Scope) return "[]";
            return "[{\"id\":\"" + MiniJson.Escape(Scheduler.Id) + "\",\"type\":\"" + Scheduler.Request.Type +
                "\",\"target\":\"" + MiniJson.Escape(Scheduler.Request.Target) + "\",\"status\":\"" + Scheduler.Status +
                "\",\"repeat\":\"" + Scheduler.Request.Repeat + "\",\"count\":" + Scheduler.Request.Count + ",\"completed\":" + Scheduler.Completed +
                ",\"message\":\"" + MiniJson.Escape(Scheduler.Message) + "\",\"saveId\":\"" + AiSaveContext.SaveId + "\",\"worldEpoch\":\"" + AiSaveContext.Epoch + "\"}]";
        }
        public static string CapabilitiesJson()
        {
            string writable = OfflineWritesAllowed() ? "true" : "false";
            string discard = OfflineWritesAllowed() && AiCompanion.CfgAllowDiscard?.Value == true ? "true" : "false";
            string craft = OfflineWritesAllowed() && AiCompanion.CfgAllowDirectCraft?.Value == true ? "true" : "false";
            string sortContainers = OfflineWritesAllowed() && AiCompanion.CfgAllowContainerSort?.Value == true ? "true" : "false";
            string sortWrite = OfflineWritesAllowed() && AiCompanion.CfgAllowContainerSortWrite?.Value == true ? "true" : "false";
            string sortNote = sortContainers == "true"
                ? "按分区把视野内、已检视、同楼层容器里的同类物品归到同一个箱子，只走官方TryAddItem/RemoveItem，逐件核对目标有源没有"
                : "开关未开启或非离线：只能读容器内容给分区建议，不能搬动";
            string craftNote = craft == "true"
                ? "先核对配方与随身材料：缺口会直接说明，材料够才短暂打开官方合成面板并加入制造队列，未指定数量默认1个"
                : "开关未开启或非离线，只能做制造规划（craft_plan）";
            return "{\"organize\":{\"available\":" + writable + ",\"mode\":\"background-main-thread\",\"note\":\"仅离线随身同容器排布，不增删不合并数量；保留装备/快捷/锁定，预算优化不保证数学全局最优\"},\"reload\":{\"available\":" + writable + ",\"mode\":\"player-action-yield\",\"note\":\"仅离线当前武器官方Reload，特殊R枪机/排障未验证\"},\"loot\":{\"available\":" + writable + ",\"mode\":\"nearby-visible-only\",\"note\":\"仅离线可见近距且官方视线校验通过的地面物品，不寻路不穿墙\"},\"craft_plan\":{\"available\":true,\"mode\":\"read-only\"},\"craft\":{\"available\":" + craft + ",\"mode\":\"official-craft-panel\",\"note\":\"" + craftNote + "\"},\"flashlight\":{\"available\":" + writable + ",\"mode\":\"native-toggle\",\"note\":\"调用游戏自身TurnOnLight/TurnOffLight并回读状态校验；不模拟C键；未装备手电会如实说明\"},\"sort_containers\":{\"available\":" + sortContainers + ",\"mode\":\"official-inventory-move\",\"scope\":\"view-inspected-same-floor\",\"write\":" + sortWrite + ",\"note\":\"" + sortNote + "\"},\"discard\":{\"available\":" + discard + ",\"requiresOpenInventory\":false,\"note\":\"默认关闭只建议；开启后明确未保护单堆，复用已核验的官方库存移出与DropOrForceReturn落地/失败恢复流程，不打开背包不抢操作；原生同实例落地回执确认\"},\"pause\":{\"available\":true},\"resume\":{\"available\":true},\"cancel\":{\"available\":true}}";
        }
        public static string FactsJson()
        {
            string advice = AiDiscardAdvisor.FactsJson();
            string craftable = MiniJson.Escape(GameFacts.CraftableSummary() ?? "");
            string craftMissing = MiniJson.Escape(GameFacts.CraftDeficitSummary() ?? "");
            return advice.Substring(0, advice.Length - 1) + ",\"native\":" + AiNativeActions.FactsJson() + ",\"inventory\":" + AiInventoryTasks.FactsJson()
                + ",\"craftable\":\"" + craftable + "\",\"craftMissing\":\"" + craftMissing + "\"}";
        }
        public static string Context() => "\n【可执行任务能力】" + CapabilitiesJson() + "\n【任务事实】" + FactsJson() + "\n【真实任务进度】" + TasksJson() +
            "\n只在玩家当前聊天确实要求时输出task。默认一次；明确持续维护才continuous，不得擅自无限制造/消耗。已接收不等于完成，不得编造成功。type=organize/reload/loot/craft_plan/craft/discard/sort_containers/flashlight/pause/resume/cancel，target是明确物品名或配方ID，repeat=count或continuous,count=1..100。organize/craft_plan/discard/sort_containers/flashlight仅一次。discard仅候选protected=false且权限打开才可，token对应明确单堆，不自动拆分；整理不隐式丢弃，关闭只给建议。玩家输入AI必须让行。";
    }
}
