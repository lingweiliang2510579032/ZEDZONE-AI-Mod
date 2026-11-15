using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Dropoutline
{
    /// <summary>Main-thread native adapter. Never simulates input, walks the player,
    /// creates items or writes ammunition counters. Uses normal game entry points only.</summary>
    internal static class AiNativeActions
    {
        private static BasicRangedWeapon _ownedReload;
        private static string _ownedScope = "";
        private static float _reloadStarted;
        private static int _reloadAmmo;
        private static float _reloadToken;
        private static BasicItem _pendingLoot;
        private static int _pendingLootId;
        private static float _pendingLootHave, _pendingLootStarted, _pendingLootQuantity;
        private static string _pendingLootName = "", _pendingLootScope = "";

        public static string CapabilitiesJson() =>
            "{\"reload\":{\"available\":true,\"mode\":\"idle-character-action\",\"note\":\"仅当前手持武器的官方Reload入口；不支持模拟按键或强改弹药\"}," +
            "\"loot\":{\"available\":true,\"mode\":\"background-nearby\",\"range\":2.5,\"note\":\"只拾取当前可见且已进入游戏拾取范围的地面物品；不自动走路\"}," +
            "\"craft_plan\":{\"available\":true,\"mode\":\"read-only\"}," +
            "\"sort_containers\":{\"available\":" + (AiCompanion.CfgAllowContainerSort != null && AiCompanion.CfgAllowContainerSort.Value ? "true" : "false") +
                ",\"mode\":\"official-inventory-move\",\"scope\":\"view-inspected-same-floor\",\"note\":\"按分区分区把容器内物品归并到同一个箱子，逐件核对，不一致即撤回\"}," +
            "\"craft\":{\"available\":false,\"reason\":\"尚无验证过的无UI创建及取消退款接口，禁止绕过材料、技能或制造时间\"}}";

        public static string FactsJson()
        {
            if (!AiSaveContext.Active) return "{}";
            try
            {
                var player = GameController.instance?.playerCharacter;
                var weapon = player?.rangedWeapon;
                return "{\"weapon\":\"" + MiniJson.Escape(weapon?.weaponData?.GetItemName() ?? "") +
                    "\",\"loadedAmmo\":" + (weapon?.weaponData != null ? BasicRangedWeapon.GetLoadedAmmo(weapon.weaponData) : 0) +
                    ",\"reloading\":" + (weapon != null && weapon.isReloading ? "true" : "false") + "}";
            }
            catch { return "{}"; }
        }

        public static AiTaskStep Step(AiTaskRequest request)
        {
            if (request == null || !AiSaveContext.Active) return new AiTaskStep("failed", "当前没有已识别的活动存档");
            var player = GameController.instance?.playerCharacter;
            if (player?.characterData == null || player.characterData.deadFlag) return new AiTaskStep("failed", "玩家角色不可操作");
            if (_ownedReload == null && _pendingLootId == 0 && request.Type != "craft_plan" && request.Type != "craft" && request.Type != "flashlight" && request.Type != "sort_containers" && CharacterActionManager.instance?.HasPlayerAction() == true)
                return new AiTaskStep("waiting", "玩家已有制造、治疗或其他读条动作，AI不抢占");
            switch (request.Type)
            {
                case "reload": return Reload(player, request);
                case "loot": return Loot(player, request);
                case "craft_plan": return PlanCraft(player, request);
                case "craft": return AiCraftActions.Execute(player, request);
                case "flashlight": return AiActions.Flashlight(player, request);
                case "sort_containers": return AiContainerSort.Execute(request);
                default: return new AiTaskStep("failed", "此执行器不支持该任务");
            }
        }

        public static void Interrupt()
        {
            // Only interrupt a reload that THIS adapter started in THIS live world.
            // Never cancel a player-owned reload or invoke native objects from an old save.
            var owned = _ownedReload;
            _ownedReload = null;
            try
            {
                if (owned != null && AiSaveContext.Active && _ownedScope == Scope() &&
                    GameController.instance?.playerCharacter?.rangedWeapon?.Pointer == owned.Pointer &&
                    owned.reloadTimeFlag == _reloadToken && (owned.isReloading || owned.isAirCharging)) owned.InterruptReloadOrCharging();
            }
            catch (Exception e) { DropOutlinePlugin.L?.LogWarning("[AI任务] 中断自有上弹失败：" + e.Message); }
            _ownedScope = "";
            // A completed instantaneous pickup is not rolled back. Keep its receipt until
            // the next safe step, so a player-yield cannot cause a duplicate pickup.
        }
        public static void Reset()
        {
            Interrupt(); _pendingLoot = null; _pendingLootId = 0;
            _pendingLootName = ""; _pendingLootStarted = 0;
        }
        private static string Scope() => AiSaveContext.SaveId + ":" + AiSaveContext.Epoch;

        private static AiTaskStep Reload(HumanCharacterController player, AiTaskRequest request)
        {
            var weapon = player.rangedWeapon;
            if (weapon == null || weapon.weaponData == null) return new AiTaskStep("failed", "当前手上没有支持上弹的武器");
            string target = (request.Target ?? "").Trim();
            if (target.Length > 0 && target != "当前" && target != "当前武器" && target != "手持" && target != "手持武器" && target != "current" &&
                target != weapon.weaponData.itemId.ToString(CultureInfo.InvariantCulture) && target != weapon.weaponData.GetItemName())
                return new AiTaskStep("failed", "指定目标不是当前手持武器；AI不会自动换枪或替其他武器上弹");
            if (_ownedReload != null)
            {
                if (_ownedScope != Scope() || weapon.Pointer != _ownedReload.Pointer)
                { Interrupt(); return new AiTaskStep("failed", "世界或手持武器已改变，停止旧上弹任务"); }
                bool busy = weapon.isReloading || weapon.isAirCharging;
                if (busy && weapon.reloadTimeFlag != _reloadToken)
                { _ownedReload = null; _ownedScope = ""; return new AiTaskStep("waiting", "武器进入了新的外部动作，AI放弃旧动作所有权，不中断它"); }
                if (Time.unscaledTime - _reloadStarted > 45f)
                { Interrupt(); return new AiTaskStep("failed", "上弹等待超时，已中止AI发起的动作"); }
                if (busy || Time.unscaledTime - _reloadStarted < 0.5f) return new AiTaskStep("running", "正在通过游戏正常上弹流程执行；玩家操作时让行");
                int after = BasicRangedWeapon.GetLoadedAmmo(weapon.weaponData);
                bool confirmed = after > _reloadAmmo;
                _ownedReload = null; _ownedScope = "";
                if (!confirmed) return new AiTaskStep("failed", "官方入口未产生可确认的上弹过程，可能缺弹药、弹匣或该武器需要其他操作；不假报成功");
                return new AiTaskStep("unit", "一次官方上弹流程已结束，当前装弹数 " + after + "（不代表所有枪械都已完成手动枪机操作）", true);
            }
            if (weapon.isReloading || weapon.isAirCharging) return new AiTaskStep("waiting", "已有玩家/游戏的武器操作进行中，AI不接管或取消它");
            _reloadAmmo = BasicRangedWeapon.GetLoadedAmmo(weapon.weaponData);
            // Reload() validates magazine/ammo/weapon constraints in the game. No key
            // injection, direct property write, or access to private CanStartReloadAction.
            if (!weapon.Reload()) return request.Repeat == "continuous"
                ? new AiTaskStep("waiting", "当前无需上弹或无法上弹，等待弹药/武器状态变化")
                : new AiTaskStep("failed", "游戏拒绝上弹：可能已满、缺弹药/弹匣或当前武器不支持此入口");
            _ownedReload = weapon; _ownedScope = Scope(); _reloadStarted = Time.unscaledTime;
            _reloadToken = weapon.reloadTimeFlag;
            return new AiTaskStep("running", "已发起游戏原生上弹动作，等待完成确认");
        }

        private static AiTaskStep Loot(HumanCharacterController player, AiTaskRequest request)
        {
            if (_pendingLootId != 0)
            {
                if (_pendingLootScope != Scope()) { _pendingLoot = null; _pendingLootId = 0; return new AiTaskStep("failed", "拾取结果已跨世界轮次，丢弃旧收据"); }
                float have = CountCarried(player.characterData, _pendingLootId);
                bool sourceChanged = _pendingLoot == null || _pendingLoot.gameObject == null || !_pendingLoot.gameObject.activeInHierarchy ||
                    _pendingLoot.itemData == null || _pendingLoot.itemData.itemNumberFloat < _pendingLootQuantity - 0.0001f;
                if (sourceChanged && have > _pendingLootHave + 0.0001f)
                {
                    string name = _pendingLootName; _pendingLoot = null; _pendingLootId = 0;
                    return new AiTaskStep("unit", "已确认收进背包：" + name + "（一次为一件地面物品/物品堆）", true);
                }
                if (Time.unscaledTime - _pendingLootStarted < 1.5f) return new AiTaskStep("running", "等待官方拾取结果确认");
                _pendingLoot = null; _pendingLootId = 0;
                return new AiTaskStep("failed", "没有确认物品进入背包；可能空间不足、超重或不可拾取，已停止，未假报成功");
            }
            if (DropOutlinePlugin.WorldSince <= 0f || Time.unscaledTime - DropOutlinePlugin.WorldSince < 8f)
                return new AiTaskStep("waiting", "等待场景地面物品加载稳定");
            MapController map = null;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<MapController>())
                if (candidate != null && candidate.gameObject != null && candidate.gameObject.activeInHierarchy) { map = candidate; break; }
            if (map == null) return new AiTaskStep("waiting", "当前地图不可用");
            var items = map.GetAllGroundItems();
            if (items == null) return new AiTaskStep("waiting", "没有可访问的地面物品");
            var position = player.transform.position;
            BasicItem best = null; float bestDistance = float.MaxValue;
            string target = (request.Target ?? "").Trim();
            bool all = target.Length == 0 || target == "all" || target == "全部" || target == "所有";
            int targetId; bool byId = int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out targetId);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.gameObject == null || !item.gameObject.activeInHierarchy || item.itemData == null ||
                    !item.isOnScreen || !item.isInRange || item.standHiddenFlag || item.submergedFlag || item.stuckHost != null) continue;
                string name = item.itemData.GetItemName();
                if (!all && (byId ? item.itemData.itemId != targetId : !string.Equals(name, target, StringComparison.Ordinal))) continue;
                var ip = item.transform.position;
                if (!map.IsInSightByRaycast(ip)) continue;
                float distance = Vector2.Distance(new Vector2(position.x, position.y), new Vector2(ip.x, ip.y));
                float range = Mathf.Min(2.5f, Mathf.Max(0f, BasicItem.pickRange));
                if (range <= 0f || distance > range || distance >= bestDistance) continue;
                best = item; bestDistance = distance;
            }
            if (best == null) return request.Repeat == "continuous"
                ? new AiTaskStep("waiting", "当前脚边没有符合条件且可见可拾取的物品，不会自动走路或隔墙拾取")
                : new AiTaskStep("done", "附近符合条件且可拾取的地面物品已检查完毕；未自动走路或读取远处物资");
            _pendingLoot = best; _pendingLootId = best.itemData.itemId;
            _pendingLootName = best.itemData.GetItemName(); _pendingLootHave = CountCarried(player.characterData, _pendingLootId);
            _pendingLootStarted = Time.unscaledTime; _pendingLootQuantity = best.itemData.itemNumberFloat; _pendingLootScope = Scope();
            // This is the same game callback exposed to its interaction menu; it owns
            // all capacity checks, stacking, source removal and multiplayer handling.
            best.OnPickUp(null);
            return new AiTaskStep("running", "已请求正常拾取「" + _pendingLootName + "」，等待背包确认");
        }

        private static float CountCarried(CharacterData character, int itemId)
        {
            float count = 0;
            var inventories = character.GetCarriedInventoryList();
            if (inventories != null) for (int i = 0; i < inventories.Count; i++)
            {
                var inventory = inventories[i];
                if (inventory?.itemList == null) continue;
                for (int j = 0; j < inventory.itemList.Count; j++)
                { var item = inventory.itemList[j]; if (item != null && item.itemId == itemId) count += item.itemNumberFloat; }
            }
            return count;
        }

        private static AiTaskStep PlanCraft(HumanCharacterController player, AiTaskRequest request)
        {
            string target = (request.Target ?? "").Trim();
            if (target.Length == 0) return new AiTaskStep("failed", "请给出要规划制造的准确物品名或ID");
            var manager = ItemManager.instance;
            if (manager?.allRecipeList == null) return new AiTaskStep("waiting", "配方资料尚未加载");
            int targetId; bool byId = int.TryParse(target, out targetId);
            var matches = new List<RecipeData>();
            for (int i = 0; i < manager.allRecipeList.Count; i++)
            {
                var recipe = manager.allRecipeList[i];
                if (recipe != null && GameFacts.IsRecipeKnown(recipe.itemId) && (byId ? recipe.itemId == targetId : string.Equals(recipe.GetOutputItemName(), target, StringComparison.Ordinal))) matches.Add(recipe);
            }
            if (matches.Count == 0) return new AiTaskStep("failed", "没有找到该准确物品的配方；请先查询制造知识，不使用模糊匹配擅自选物品");
            var sb = new StringBuilder("制造规划（仅核对当前携带材料，不扣除、不生成物品）：");
            for (int m = 0; m < Math.Min(matches.Count, 4); m++)
            {
                var recipe = matches[m];
                sb.Append("\n方案").Append(m + 1).Append("：").Append(recipe.GetOutputItemName()).Append("；单批产出 ").Append(recipe.outputItemNumber);
                sb.Append("；平台 ").Append(RecipeData.GetCraftPlatformName(recipe.craftPlatform));
                sb.Append("；工具 ").Append(ItemAttr.GetToolName(recipe.toolType));
                sb.Append("；制造时间 ").Append(recipe.craftTime.ToString("0.#", CultureInfo.InvariantCulture)).Append("秒");
                sb.Append("；智力/制造要求 ").Append(recipe.iqReqiared).Append('/').Append(recipe.craftReqiared);
                if (recipe.recipeItems != null) for (int i = 0; i < recipe.recipeItems.Count; i++)
                {
                    var ingredient = recipe.recipeItems[i]; if (ingredient == null) continue;
                    var attr = manager.GetItemAttrById(ingredient.itemId);
                    string name = attr?.itemName_Runtime;
                    if (string.IsNullOrEmpty(name)) name = attr?.itemName ?? ("ID " + ingredient.itemId);
                    float have = CountCarried(player.characterData, ingredient.itemId), need = ingredient.itemNumber;
                    sb.Append("\n- ").Append(name).Append("：需要 ").Append(need).Append("，携带 ").Append(have);
                    if (have < need) sb.Append("，还缺 ").Append(need - have);
                }
            }
            if (matches.Count > 4) sb.Append("\n另有配方未展开，请具体指定平台再规划。");
            sb.Append("\n未核实附近平台、工具耐久与技能可执行条件；材料足够不代表可制造。现在不支持后台实际制造。");
            return new AiTaskStep("done", sb.ToString(), true);
        }
    }
}
