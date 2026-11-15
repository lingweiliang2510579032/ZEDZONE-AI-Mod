using System;
using UnityEngine;

namespace Dropoutline
{
    // Native sequence verified in InventoryPanel.CollectAllItemsToPlayer:
    // remove a source item, then use DropOrForceReturn on the detached original.
    // Not a single atomic transaction. No UI, synthetic input, direct list edits,
    // item creation, quantity mutation, or managed spawn/restore implementation.
    internal static class AiDiscardActions
    {
        private static bool _invoked, _helperCalled;
        private static string _scope = "", _failure = "", _metadata = "";
        private static ItemData _item;
        private static InventoryData _source;
        private static int _itemId;
        private static IntPtr _playerPointer, _characterPointer;
        private static bool CurrentWorld() => AiSaveContext.Active && Scope == _scope &&
            GameController.instance?.playerCharacter != null && GameController.instance.playerCharacter.Pointer == _playerPointer &&
            GameController.instance.playerCharacter.characterData != null && GameController.instance.playerCharacter.characterData.Pointer == _characterPointer;
        private static string Metadata(ItemData item)
        {
            var b = new System.Text.StringBuilder();
            b.Append(item.inventoryData?.Pointer.ToString() ?? "null").Append('|').Append(item.itemSellingPrice);
            var pairs = item.itemPropertyPairs;
            if (pairs != null && pairs.Count > 128) throw new InvalidOperationException("物品元资料过多，拒绝自动丢弃");
            b.Append('|').Append(pairs?.Count ?? -1);
            for (int i = 0; pairs != null && i < pairs.Count; i++)
            {
                var pair = pairs[i];
                if (pair == null) { b.Append("|null"); continue; }
                string key = Convert.ToString(pair.key), value = Convert.ToString(pair.value);
                b.Append('|').Append(key?.Length ?? -1).Append(':').Append(key).Append('|').Append(value?.Length ?? -1).Append(':').Append(value);
            }
            return b.ToString();
        }
        private static float _quantity, _durability, _invokedAt;
        public static void Reset() { _invoked = _helperCalled = false; _scope = _failure = _metadata = ""; _item = null; _source = null; _itemId = 0; _quantity = _durability = _invokedAt = 0; }
        public static string FactsJson() => "{\"discard\":{\"available\":true,\"requiresOpenInventory\":false,\"requiresAllowDiscard\":true,\"requiresExplicitTarget\":true,\"countLimit\":1,\"background\":true,\"noUI\":true,\"usesOfficialDropFallback\":true,\"atomicTransaction\":false,\"autoConfirm\":false}}";
        // Compatibility only. Runtime must use its normal input/pause gates;
        // there is no longer any inventory-menu exception for discard.
        public static bool IsInventoryReady() => false;
        private static bool Offline()
        {
            try { return !ZedZone.Net.NetworkManager.isOnline && !ZedZone.Net.NetworkManager.isHost && !ZedZone.Net.NetworkManager.isClient &&
                !ZedZone.Net.NetworkManager.hasPendingClientEscrow && !ZedZone.Net.NetworkManager.resettingWorld; }
            catch { return false; }
        }
        private static string Scope => AiSaveContext.SaveId + ":" + AiSaveContext.Epoch;
        private static bool Busy() => CharacterActionManager.instance?.HasPlayerAction() == true || MouseController.instance?.isDraggingItemUI == true;
        private static bool Carried(CharacterData cd, ItemData item)
        {
            var inventories = cd.GetCarriedInventoryList();
            if (inventories == null) throw new InvalidOperationException("无法复核随身容器");
            for (int n = 0; n < inventories.Count; n++)
            {
                var list = inventories[n]?.itemList;
                for (int i = 0; list != null && i < list.Count; i++) if (list[i] != null && list[i].Pointer == item.Pointer) return true;
            }
            return false;
        }
        private static bool Ground(ItemData item, bool exact)
        {
            var player = GameController.instance.playerWorldPosition;
            foreach (var map in Resources.FindObjectsOfTypeAll<MapController>())
            {
                if (map == null || map.gameObject == null || !map.gameObject.activeInHierarchy) continue;
                var ground = map.GetAllGroundItems();
                for (int i = 0; ground != null && i < ground.Count; i++)
                {
                    var obj = ground[i];
                    if (obj == null || obj.itemData == null || obj.itemData.Pointer != item.Pointer) continue;
                    if (!exact) return true;
                    if (obj.itemData.itemId != _itemId || obj.itemData.itemNumberFloat != _quantity || obj.itemData.durability != _durability) continue;
                    var pos = obj.transform.position;
                    if ((pos.x - player.x) * (pos.x - player.x) + (pos.y - player.y) * (pos.y - player.y) <= 36f) return true;
                }
            }
            return false;
        }
        private static void HelperOnce()
        {
            if (_helperCalled) return;
            _helperCalled = true; // Before native call; never repeat after throw.
            InventoryPanel.DropOrForceReturn(_item, _source);
        }
        private static AiTaskStep PreserveAfterCallback(CharacterData cd, string reason)
        {
            _failure = reason;
            if (!CurrentWorld()) return new AiTaskStep("failed", reason + "；作用域/角色已变，不再操作旧物品，请检查存档");
            try { if (Carried(cd, _item) || Ground(_item, false)) return new AiTaskStep("failed", reason + "；物品已有容器或地面归属，未再丢弃"); }
            catch { return new AiTaskStep("failed", reason + "；无法确认物品归属，停止且不盲目恢复/落地"); }
            // Official restoration API; never overwrite callback changes.
            try { if (_source.TryAddItemWithoutChangeItem(_item)) return new AiTaskStep("failed", reason + "；已通过官方接口放回源容器，未继续丢弃"); }
            catch { }
            if (!CurrentWorld()) return new AiTaskStep("failed", reason + "；恢复回调更换世界/玩家，停止访问旧物品");
            try { if (Carried(cd, _item) || Ground(_item, false)) return new AiTaskStep("failed", reason + "；回调已保全物品，未再丢弃"); }
            catch { return new AiTaskStep("failed", reason + "；恢复后无法确认归属，不再操作"); }
            if (!CurrentWorld() || !Offline() || _helperCalled) return new AiTaskStep("failed", reason + "；官方恢复未确认，禁止跨世界/联网或重复落地，请检查物品");
            // Last-resort *native* fallback preserves the original instance;
            // this can land it, so report failure/possible safety landing, not success.
            try { HelperOnce(); }
            catch (Exception e) { return new AiTaskStep("failed", reason + "；官方保全接口异常，停止不重试：" + e.Message); }
            return new AiTaskStep("failed", reason + "；已调用一次官方落地/塞回兜底保全，请检查背包与地面，不计任务成功");
        }
        public static AiTaskStep Step(AiTaskRequest request)
        {
            if (request == null || request.Type != "discard" || request.Repeat != "count" || request.Count != 1)
                return new AiTaskStep("failed", "丢弃只允许明确指定一组物品且仅执行一次，不接受持续或批量任务");
            if (AiCompanion.CfgAllowDiscard?.Value != true) return new AiTaskStep("failed", "允许AI丢弃开关已关闭，未执行新的丢弃");
            if (!Offline()) return new AiTaskStep("failed", "仅离线单机可执行丢弃；联网、主机、客户端、托管或世界重置均拒绝");
            if (!AiSaveContext.Active || (_scope.Length > 0 && _scope != Scope)) return new AiTaskStep("failed", "世界已变化，旧丢弃任务失效");
            var cd = GameController.instance?.playerCharacter?.characterData;
            if (cd == null || cd.deadFlag) return new AiTaskStep("failed", "玩家库存不可用或角色死亡");
            if (_failure.Length > 0) return new AiTaskStep("failed", _failure + "；已停止，不自动重试");
            if (_invoked) return Receipt(cd);
            if (Busy()) return new AiTaskStep("waiting", "玩家正在拖拽或已有制造、治疗等读条动作，AI让行");
            string target = (request.Target ?? "").Trim();
            if (target.Length == 0) return new AiTaskStep("failed", "丢弃必须指定当前候选token或唯一完整物品名");
            var carried = cd.GetCarriedInventoryList();
            if (carried == null) return new AiTaskStep("failed", "随身容器不可用");
            ItemData selected = null; InventoryData source = null; int matches = 0;
            for (int n = 0; n < carried.Count; n++)
            {
                var inventory = carried[n]; if (inventory?.itemList == null) continue;
                for (int i = 0; i < inventory.itemList.Count; i++)
                {
                    var item = inventory.itemList[i]; if (item == null) continue;
                    if (target != AiDiscardAdvisor.TokenFor(n, i, item) && target != item.GetItemName()) continue;
                    matches++; selected = item; source = inventory;
                }
            }
            if (matches != 1 || selected == null || source == null) return new AiTaskStep("failed", "丢弃目标缺失或不唯一，请重新查看当前候选并明确指定");
            string protection = AiDiscardAdvisor.CurrentProtection(cd, source, selected);
            if (protection.Length > 0) return new AiTaskStep("failed", "已保护，未丢弃：" + protection);
            string scope = Scope;
            if (AiCompanion.CfgAllowDiscard?.Value != true || !Offline() || !AiSaveContext.Active || scope != Scope || Busy() ||
                !Carried(cd, selected) || AiDiscardAdvisor.CurrentProtection(cd, source, selected).Length > 0)
                return new AiTaskStep("failed", "执行前权限、作用域或保护条件改变，未丢弃");
            _scope = scope; _playerPointer = GameController.instance.playerCharacter.Pointer; _characterPointer = cd.Pointer; _metadata = Metadata(selected); _item = selected; _source = source; _itemId = selected.itemId; _quantity = selected.itemNumberFloat; _durability = selected.durability; _invokedAt = Time.unscaledTime;
            if (!float.IsFinite(_quantity) || _quantity <= 0 || !float.IsFinite(_durability)) return new AiTaskStep("failed", "物品数量或耐久无效，未丢弃");
            _invoked = true;
            bool removed;
            try { removed = source.RemoveItem(selected, true); }
            catch (Exception e) { return PreserveAfterCallback(cd, "官方移出接口异常：" + e.Message); }
            if (!CurrentWorld()) { _failure = "移出回调更换了世界或玩家，不再访问旧容器，请检查存档"; return new AiTaskStep("failed", _failure); }
            try
            {
            // The callbacks may legally restore/move/change the item. Never
            // duplicate their results or overwrite their quantity/durability.
            if (Carried(cd, selected)) { _failure = "原物品仍在随身容器（移出失败或回调已移回），未调用落地接口"; return new AiTaskStep("failed", _failure); }
            if (!removed) return PreserveAfterCallback(cd, "官方移出返回失败且物品已离开源容器");
            if (!AiSaveContext.Active || Scope != scope || !Offline() || AiCompanion.CfgAllowDiscard?.Value != true || Busy() ||
                selected.itemId != _itemId || selected.itemNumberFloat != _quantity || selected.durability != _durability || Metadata(selected) != _metadata ||
                AiDiscardAdvisor.CurrentProtection(cd, source, selected).Length > 0)
                return PreserveAfterCallback(cd, "移出回调改变了权限、物品或保护状态，已中止原定丢弃");
            if (Ground(selected, false)) { _failure = "移出回调已将物品放到地面，未重复落地"; return new AiTaskStep("failed", _failure); }
            try { HelperOnce(); }
            catch (Exception e) { return PreserveAfterCallback(cd, "官方落地兜底异常，停止且不重试：" + e.Message); }
            return Receipt(cd);
            }
            catch (Exception e) { return PreserveAfterCallback(cd, "移出后无法复核物品状态：" + e.Message); }
        }
        private static AiTaskStep Receipt(CharacterData cd)
        {
            if (!CurrentWorld()) return new AiTaskStep("failed", "玩家或存档已变，不读取旧任务回执");
            bool carried = Carried(cd, _item), ground = Ground(_item, false);
            if (carried && ground) { _failure = "异常：同一物品同时出现在随身容器和地面，已停止，不自动重复或修复，请检查"; return new AiTaskStep("failed", _failure); }
            if (carried) { _failure = "落地未完成；原物品已由官方兜底放回随身容器，不重试"; return new AiTaskStep("failed", _failure); }
            if (Ground(_item, true)) return new AiTaskStep("done", "已通过官方移出及落地兜底流程处理，并确认同一物品及数量出现在玩家附近地面", true);
            if (Time.unscaledTime - _invokedAt > 30f) { _failure = "已调用一次官方落地兜底，无法确认完整地面回执；停止且不重试，请检查背包/地面"; return new AiTaskStep("failed", _failure); }
            return new AiTaskStep("waiting", "原物品已移出，等待附近地面同一物品的数量回执；不会重复调用");
        }
    }
}
