using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dropoutline
{
    /// <summary>
    /// 直接合成：只走游戏自己的合成面板与制造队列，不模拟鼠标、不自行扣材料、
    /// 不自行生成物品。流程固定为「核对配方 → 核对随身材料 → 打开官方面板 →
    /// 选中配方 → 加入队列 → 关闭面板 → 校验队列确实增加」。
    ///
    /// 任何一步不符合预期就原样退出并如实说明，绝不猜。
    /// </summary>
    internal static class AiCraftActions
    {
        internal static AiTaskStep Execute(HumanCharacterController player, AiTaskRequest request)
        {
            if (player?.characterData == null) return new AiTaskStep("failed", "玩家角色不可操作");
            if (AiCompanion.CfgAllowDirectCraft?.Value != true)
                return new AiTaskStep("failed", "直接合成开关未开启；请在游戏设置里打开「允许AI直接合成」，我只会做制造规划");

            var manager = ItemManager.instance;
            if (manager?.allRecipeList == null) return new AiTaskStep("waiting", "配方资料尚未加载");

            string target = (request.Target ?? "").Trim();
            if (target.Length == 0) return new AiTaskStep("failed", "请给出要制造的准确物品名或ID");
            int targetId; bool byId = int.TryParse(target, out targetId);
            RecipeData recipe = null;
            for (int i = 0; i < manager.allRecipeList.Count; i++)
            {
                var candidate = manager.allRecipeList[i];
                if (candidate == null || !GameFacts.IsRecipeKnown(candidate.itemId)) continue;
                bool hit;
                try { hit = byId ? candidate.itemId == targetId : string.Equals(candidate.GetOutputItemName(), target, StringComparison.Ordinal); }
                catch { hit = false; }
                if (!hit) continue;
                if (recipe != null) return new AiTaskStep("failed", "「" + target + "」有多个配方，请用配方ID指定");
                recipe = candidate;
            }
            if (recipe == null) return new AiTaskStep("failed", "没有找到「" + target + "」的已解锁配方；不猜相似物品");

            int count = request.Count <= 0 ? 1 : Math.Min(100, request.Count);
            // 材料必须是「单批需求 × 数量」，前一次核对已经把「够不够」讲清楚。
            var deficit = Deficit(manager, player, recipe, count);
            if (deficit.Length > 0)
                return new AiTaskStep("failed", "做" + count + "个" + OutputName(recipe) + "材料不够，缺" + deficit);

            var panel = CraftPanel.instance;
            bool openedInventory = false;
            if (panel == null)
            {
                // 玩家按 TAB 打开背包时，合成界面才会被游戏创建。这里走同一个官方
                // 入口把它建出来，用完再关掉，不模拟按键。
                try
                {
                    var inventory = PlayerInventoryPanel.instance;
                    if (inventory != null) { inventory.OpenPanel(); openedInventory = true; }
                }
                catch { }
                panel = CraftPanel.instance;
            }
            if (panel == null)
            {
                if (openedInventory) { try { PlayerInventoryPanel.instance?.ClosePanel(); } catch { } }
                return new AiTaskStep("failed", "合成面板还没被游戏创建；请先按 TAB 打开一次背包，之后我就能直接做");
            }

            int before = 0;
            try { var list = panel.GetWorkSequenceList(); before = list?.Count ?? 0; } catch { before = 0; }

            bool opened = false;
            try
            {
                panel.OpenPanel(recipe.craftPlatform, null);
                opened = true;
                panel.OpenCraftDetail(recipe);
                if (panel.selectedItemId != recipe.itemId)
                    throw new InvalidOperationException("面板没有选中目标配方");
                for (int i = 0; i < count; i++) panel.AddWorkSequence(i == count - 1);
            }
            catch (Exception error)
            {
                SafeClose(panel, opened);
                if (openedInventory) { try { PlayerInventoryPanel.instance?.ClosePanel(); } catch { } }
                return new AiTaskStep("failed", "没能加入制造队列：" + error.Message);
            }

            int after = before;
            try { var list = panel.GetWorkSequenceList(); after = list?.Count ?? before; } catch { }
            SafeClose(panel, true);
            if (openedInventory) { try { PlayerInventoryPanel.instance?.ClosePanel(); } catch { } }
            if (after - before != count)
                return new AiTaskStep("failed", "制造队列没有按预期增加（" + (after - before) + "/" + count + "），已停止，不重试");

            return new AiTaskStep("done", "已按官方流程把" + count + "个" + OutputName(recipe) + "加入制造队列", true);
        }

        private static string OutputName(RecipeData recipe)
        {
            try { var name = recipe.GetOutputItemName(); if (!string.IsNullOrEmpty(name)) return name; } catch { }
            return "物品";
        }

        private static void SafeClose(CraftPanel panel, bool opened)
        {
            if (!opened) return;
            try { panel.ClosePanel(); } catch { }
        }

        /// <summary>按「单批需求 × 数量」算缺口，返回“布×2、纤维×4”这样的短文本；够了就返回空串。</summary>
        private static string Deficit(ItemManager manager, HumanCharacterController player, RecipeData recipe, int count)
        {
            var parts = new List<string>();
            try
            {
                var counts = new Dictionary<int, int>();
                var invs = player.characterData.GetCarriedInventoryList();
                if (invs != null)
                    for (int i = 0; i < invs.Count; i++)
                    {
                        var inv = invs[i];
                        if (inv?.itemList == null) continue;
                        for (int k = 0; k < inv.itemList.Count; k++)
                        {
                            var item = inv.itemList[k];
                            if (item == null) continue;
                            counts.TryGetValue(item.itemId, out int have);
                            counts[item.itemId] = have + Math.Max(1, (int)item.itemNumberFloat);
                        }
                    }
                if (recipe.recipeItems != null)
                    for (int i = 0; i < recipe.recipeItems.Count; i++)
                    {
                        var ingredient = recipe.recipeItems[i];
                        if (ingredient == null) continue;
                        float need = Math.Max(1f, ingredient.itemNumber) * count;
                        counts.TryGetValue(ingredient.itemId, out int have);
                        if (have >= need) continue;
                        string name = null;
                        try { var attr = manager.GetItemAttrById(ingredient.itemId); name = attr?.itemName_Runtime ?? attr?.itemName; } catch { }
                        if (string.IsNullOrEmpty(name)) name = "ID " + ingredient.itemId;
                        parts.Add(name + "×" + Math.Ceiling(need - have).ToString("0", CultureInfo.InvariantCulture));
                    }
            }
            catch { return ""; }
            return string.Join("、", parts);
        }
    }
}
