using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dropoutline
{
    // Read-only advice. Tokens are ephemeral scope identifiers, NOT executable
    // item handles. No item is classified as trash, and no drop API is called.
    internal static class AiDiscardAdvisor
    {
        private static string Q(string text) => "\"" + MiniJson.Escape(text ?? "") + "\"";
        public static string FactsJson()
        {
            bool allow = AiCompanion.CfgAllowDiscard?.Value == true;
            var output = new StringBuilder("{\"allowDiscard\":").Append(allow ? "true" : "false")
                .Append(",\"discardExecutionAvailable\":true,\"requiresOpenInventory\":false,\"noUI\":true,\"usesOfficialDropFallback\":true,\"reason\":")
                .Append(Q("仅离线单机、开关允许且当前聊天明确指定时，后台调用已核验的官方移出及落地兜底流程；不打开背包、不模拟操作。非单一原子事务，尚未实机验收。"))
                .Append(",\"discardCandidates\":[");
            int shown = 0;
            try
            {
                if (!AiSaveContext.Active) return output.Append("]}").ToString();
                var gc = GameController.instance;
                var cd = gc != null && gc.playerCharacter != null ? gc.playerCharacter.characterData : null;
                if (cd == null) return output.Append("]}").ToString();
                var inventories = cd.GetCarriedInventoryList();
                var manager = ItemManager.instance;
                var required = new HashSet<int>();
                bool missionsKnown = false;
                try
                {
                    var data = gc.gameData;
                    var missions = data?.acceptedMissionList;
                    if (missions != null)
                    {
                        for (int m = 0; m < missions.Count; m++)
                        {
                            var mission = missions[m];
                            if (mission?.missionObjectives == null) continue;
                            // All accepted objectives, including future stages,
                            // are protected. Completed-stage overprotection is safe.
                            for (int o = 0; o < mission.missionObjectives.Count; o++)
                            {
                                var objective = mission.missionObjectives[o];
                                if (objective == null) continue;
                                if (objective.collectItemID > 0) required.Add(objective.collectItemID);
                                if (objective.craftItemID > 0) required.Add(objective.craftItemID);
                            }
                        }
                        missionsKnown = true;
                    }
                }
                catch { }
                if (inventories != null) for (int n = 0; n < inventories.Count && shown < 30; n++)
                {
                    var inventory = inventories[n];
                    if (inventory?.itemList == null) continue;
                    for (int i = 0; i < inventory.itemList.Count && shown < 30; i++)
                    {
                        var item = inventory.itemList[i]; if (item == null) continue;
                        string name;
                        try { name = item.GetItemName(); } catch { name = "未知物品"; }
                        ItemAttr attr = null;
                        if (manager?.itemAttrDic != null && manager.itemAttrDic.ContainsKey(item.itemId)) attr = manager.itemAttrDic[item.itemId];
                        string reason = ProtectionReason(cd, item, attr, inventory, missionsKnown, required);
                        bool protect = reason.Length > 0;
                        if (!protect) reason = "可考虑由玩家自行丢弃；保留量按当前制造/生存计划决定，不代表垃圾。";
                        if (shown++ > 0) output.Append(',');
                        string token = TokenFor(n, i, item);
                        float quantity = item.itemNumberFloat;
                        if (float.IsNaN(quantity) || float.IsInfinity(quantity)) quantity = 0;
                        output.Append("{\"token\":").Append(Q(token)).Append(",\"name\":").Append(Q(name))
                            .Append(",\"quantity\":").Append(quantity.ToString(CultureInfo.InvariantCulture))
                            .Append(",\"protected\":").Append(protect ? "true" : "false")
                            .Append(",\"reason\":").Append(Q(reason)).Append(",\"adviceOnly\":true}");
                    }
                }
            }
            catch { /* Partial read-only evidence is preferable to invented data. */ }
            return output.Append("]}").ToString();
        }
        internal static string TokenFor(int inventoryIndex, int itemIndex, ItemData item)
        {
            // Include instance identity inside a one-way ephemeral hash so a
            // replaced same-ID stack cannot inherit an old executable target.
            string identity = AiSaveContext.SaveId + "|" + AiSaveContext.Epoch + "|" + inventoryIndex + "|" + itemIndex + "|" + item.itemId + "|" + item.Pointer + "|" + item.itemNumberFloat.ToString(CultureInfo.InvariantCulture) + "|" + item.durability.ToString(CultureInfo.InvariantCulture);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).Substring(0, 20).ToLowerInvariant();
        }
        internal static string CurrentProtection(CharacterData cd, InventoryData inv, ItemData item)
        {
            try
            {
                var required = new HashSet<int>();
                var missions = GameController.instance?.gameData?.acceptedMissionList;
                bool known = missions != null;
                if (missions != null) for (int m = 0; m < missions.Count; m++)
                {
                    var objectives = missions[m]?.missionObjectives;
                    if (objectives == null) continue;
                    for (int o = 0; o < objectives.Count; o++)
                    {
                        var objective = objectives[o]; if (objective == null) continue;
                        if (objective.collectItemID > 0) required.Add(objective.collectItemID);
                        if (objective.craftItemID > 0) required.Add(objective.craftItemID);
                    }
                }
                var manager = ItemManager.instance;
                ItemAttr attr = manager?.itemAttrDic != null && manager.itemAttrDic.ContainsKey(item.itemId) ? manager.itemAttrDic[item.itemId] : null;
                return ProtectionReason(cd, item, attr, inv, known, required);
            }
            catch { return "无法复核物品保护状态，拒绝丢弃"; }
        }
        private static string ProtectionReason(CharacterData cd, ItemData item, ItemAttr attr, InventoryData inv, bool missionsKnown, HashSet<int> required)
        {
            if (inv.isLocked || inv.sortingInProgressFlag) return "容器锁定或正在整理，保护不建议丢弃";
            if (attr == null || attr.isMissingMod) return "未知或缺失MOD物品资料，无法判断用途，保护";
            if (!missionsKnown) return "无法确认已接受任务需求，保守保护";
            if (required.Contains(item.itemId)) return "已接受任务的收集/制造目标物品，保护";
            var features = attr.itemFeatures;
            if (features != null) for (int i = 0; i < features.Count; i++)
                if (features[i] == ItemFeatureType.AccessCard) return "门禁/通行卡物品，保护";
            if (item.inventoryData?.itemList != null && item.inventoryData.itemList.Count > 0) return "内含物品的容器，保护";
            // Keep all non-material categories out of generic discard advice;
            // weapons/ammo/medicine/etc. have contextual value we cannot infer.
            if (attr.itemType != ItemType.Material) return "非普通材料，可能为装备、食物、弹药或工具，保守保护";
            bool Same(ItemData other) => other != null && other.Pointer == item.Pointer;
            var e = cd.characterEquipmentData;
            if (e != null)
            {
                if (Same(e.currentWeaponItemData) || Same(e.backpackItemData) || Same(e.hatItemData) || Same(e.faceMaskItemData) ||
                    Same(e.torseItemData) || Same(e.legItemData) || Same(e.feetItemData) || Same(e.cloakItemData) ||
                    Same(e.headDecorationItemData) || Same(e.throwableItemData) || Same(e.exoskeletonItemData) ||
                    Same(e.thrusterItemData) || Same(e.shieldItemData) || Same(e.specialEquipmentItemData) || Same(e.lightingToolItemData)) return "已装备引用，保护";
                if (e.weaponItemDataAry != null) for (int i = 0; i < e.weaponItemDataAry.Length; i++) if (Same(e.weaponItemDataAry[i])) return "装备武器引用，保护";
                if (e.handheldDeviceItemDataAry != null) for (int i = 0; i < e.handheldDeviceItemDataAry.Length; i++) if (Same(e.handheldDeviceItemDataAry[i])) return "手持装备引用，保护";
                if (e.torseItemList != null) for (int i = 0; i < e.torseItemList.Count; i++) if (Same(e.torseItemList[i])) return "装备引用，保护";
                if (e.legItemList != null) for (int i = 0; i < e.legItemList.Count; i++) if (Same(e.legItemList[i])) return "装备引用，保护";
            }
            if (Same(cd.equippedGrappleHookItemData)) return "已装备抓钩引用，保护";
            var shortcuts = cd.shortcutDataList;
            if (shortcuts != null) for (int i = 0; i < shortcuts.Count; i++) if (shortcuts[i] != null && Same(shortcuts[i].itemData)) return "快捷栏引用，保护";
            return "";
        }
    }
}
