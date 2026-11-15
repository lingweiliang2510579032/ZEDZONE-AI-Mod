using System;
using System.Collections.Generic;

namespace Dropoutline
{
    /// <summary>
    /// 容器归并的游戏适配层：把视野内容器包成 ISortWorld，逻辑全部交给
    /// ContainerSortCore（已离线测试叠堆合并、部分合并、满箱中转、复制检测）。
    /// 写入只走官方 InventoryData.TryAddItem / RemoveItem。
    /// </summary>
    internal static class AiContainerSort
    {
        private sealed class GameWorld : ISortWorld
        {
            private readonly List<GameFacts.ContainerRef> _boxes;
            public GameWorld(List<GameFacts.ContainerRef> boxes) { _boxes = boxes; }

            public int BoxCount => _boxes.Count;
            public string BoxName(int box)
            {
                var c = _boxes[box];
                return c.Name + "(" + Math.Round(c.Distance) + "m)";
            }

            public bool Locked(int box)
            {
                try { var inv = _boxes[box].Inventory; return inv == null || inv.isLocked || inv.sortingInProgressFlag; }
                catch { return true; }
            }

            public List<SortEntry> Items(int box)
            {
                var result = new List<SortEntry>();
                try
                {
                    var list = _boxes[box].Inventory?.itemList;
                    if (list == null) return result;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var item = list[i];
                        if (item == null) continue;
                        result.Add(new SortEntry
                        {
                            Handle = item,
                            ItemId = item.itemId,
                            Quantity = Math.Max(1f, item.itemNumberFloat),
                            Category = GameFacts.CategoryOf(item.itemId),
                            Name = GameFacts.ItemDisplayName(item.itemId),
                        });
                    }
                }
                catch { }
                return result;
            }

            public float Quantity(int box, int itemId)
            {
                float total = 0f;
                try
                {
                    var list = _boxes[box].Inventory?.itemList;
                    if (list == null) return 0f;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var item = list[i];
                        if (item != null && item.itemId == itemId) total += Math.Max(1f, item.itemNumberFloat);
                    }
                }
                catch { }
                return total;
            }

            public bool Contains(int box, object handle)
            {
                var target = handle as ItemData;
                if (target == null) return false;
                try
                {
                    IntPtr pointer = target.Pointer;
                    var list = _boxes[box].Inventory?.itemList;
                    if (list == null) return false;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var item = list[i];
                        if (item != null && item.Pointer == pointer) return true;
                    }
                }
                catch { }
                return false;
            }

            public bool TryAdd(int box, object handle)
            {
                var item = handle as ItemData;
                var inv = _boxes[box].Inventory;
                if (item == null || inv == null) return false;
                try { return inv.TryAddItem(item); }
                catch (Exception e) { DropOutlinePlugin.L.LogWarning("[容器归并] TryAddItem 异常: " + e.Message); return false; }
            }

            public void Remove(int box, object handle)
            {
                var item = handle as ItemData;
                var inv = _boxes[box].Inventory;
                if (item == null || inv == null) return;
                try { inv.RemoveItem(item); }
                catch (Exception e) { DropOutlinePlugin.L.LogWarning("[容器归并] RemoveItem 异常: " + e.Message); }
            }
        }

        private const int MaxWritesPerRun = 40;

        /// <summary>只出方案（不写），供"写入未开"时使用。</summary>
        public static string Describe()
        {
            try
            {
                var boxes = GameFacts.CollectVisibleContainers();
                if (boxes.Count == 0) return "视野内没有已检视的容器";
                return ContainerSortCore.Describe(new GameWorld(boxes));
            }
            catch (Exception e) { return "规划失败：" + e.Message; }
        }

        public static AiTaskStep Execute(AiTaskRequest request)
        {
            if (AiCompanion.CfgAllowContainerSort == null || !AiCompanion.CfgAllowContainerSort.Value)
                return new AiTaskStep("failed", "容器归并没在设置里打开");
            List<GameFacts.ContainerRef> boxes;
            try { boxes = GameFacts.CollectVisibleContainers(); }
            catch (Exception e) { return new AiTaskStep("failed", "读取容器失败：" + e.Message); }
            if (boxes.Count == 0) return new AiTaskStep("failed", "视野内没有已检视的容器");

            var world = new GameWorld(boxes);
            bool test = request != null && string.Equals((request.Target ?? "").Trim(), "test", StringComparison.OrdinalIgnoreCase);
            bool write = AiCompanion.CfgAllowContainerSortWrite != null && AiCompanion.CfgAllowContainerSortWrite.Value;
            if (!write)
                return new AiTaskStep("completed", ContainerSortCore.Describe(world) + "。写入开关没开，一件都没动", true);

            var result = ContainerSortCore.Run(world, MaxWritesPerRun, test);
            foreach (var step in result.Steps) DropOutlinePlugin.L.LogInfo("[容器归并] " + step);
            var message = ContainerSortCore.Summarize(result, test);
            if (result.Stopped) DropOutlinePlugin.L.LogWarning("[容器归并] 守恒校验失败，已停手: " + result.StopReason);
            return new AiTaskStep(result.Stopped ? "failed" : "completed", message, !result.Stopped);
        }
    }
}
