using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dropoutline
{
    // Called only by the main-thread task dispatcher after its player-input gate.
    // Planning uses managed copies; committing never removes/adds/drops items,
    // changes stack sizes, crosses containers or opens UI.
    internal static class AiInventoryTasks
    {
        private sealed class Snapshot
        {
            public ItemData Item;
            public IntPtr Pointer;
            public int Id, X, Y;
            public float Quantity, Durability;
            public bool Rotated, Fixed;
        }
        private static readonly HashSet<IntPtr> _processed = new HashSet<IntPtr>();
        private static int _improved, _unchanged, _skipped, _largestGain;
        private static string _firstSkip = "";
        public static void Reset()
        { _processed.Clear(); _improved = _unchanged = _skipped = _largestGain = 0; _firstSkip = ""; }
        public static string FactsJson() => "{\"organize\":{\"scope\":\"carried_containers\",\"background\":true,\"crossContainer\":false,\"mergeStacks\":false,\"objective\":\"largest_empty_rectangle_then_fragmentation\",\"boundedHeuristic\":true}}";
        public static AiTaskStep Step(AiTaskRequest request)
        {
            if (request == null || request.Type != "organize") return new AiTaskStep("failed", "背包执行器只接受整理任务");
            var target = (request.Target ?? "").Trim();
            if (target.Length != 0 && target != "背包" && target != "当前背包" && target != "全部随身容器" && target != "carried")
                return new AiTaskStep("failed", "整理目前只支持随身背包；未执行不明确的目标：" + target);
            var gc = GameController.instance;
            var cd = gc != null && gc.playerCharacter != null ? gc.playerCharacter.characterData : null;
            if (cd == null) return new AiTaskStep("failed", "没有可操作的玩家背包");
            var carried = cd.GetCarriedInventoryList();
            if (carried == null) return new AiTaskStep("failed", "随身容器列表不可用");
            var mouse = MouseController.instance;
            if (mouse != null && mouse.isDraggingItemUI) return new AiTaskStep("waiting", "玩家正在拖动物品，整理让行");
            for (int i = 0; i < carried.Count; i++)
            {
                var inventory = carried[i];
                if (inventory == null || _processed.Contains(inventory.Pointer)) continue;
                _processed.Add(inventory.Pointer);
                try { OrganizeOne(inventory, cd); }
                catch (Exception error) { Skip("整理安全校验未通过：" + error.Message); }
                // One container per scheduler step; native Unity calls never run
                // on worker threads and large sets cannot consume one full frame.
                return new AiTaskStep("running", $"已检查{i + 1}/{carried.Count}个随身容器；优化{_improved}个，保留{_unchanged}个，跳过{_skipped}个");
            }
            string detail = _skipped > 0 ? "；跳过原因示例：" + _firstSkip : "";
            return new AiTaskStep("done", $"整理完成：优化{_improved}个容器，{_unchanged}个无需调整，安全跳过{_skipped}个；最大连续空矩形合计增加{_largestGain}格。未丢弃、拆分、合并或跨容器搬物品；不是全局数学最优保证。" + detail, true);
        }
        private static void Skip(string reason) { _skipped++; if (_firstSkip.Length == 0) _firstSkip = reason; }
        private static void OrganizeOne(InventoryData inventory, CharacterData cd)
        {
            if (inventory.isLocked || inventory.sortingInProgressFlag) { Skip("容器已锁定或官方整理正在执行"); return; }
            var items = inventory.itemList;
            if (items == null || items.Count == 0) { _unchanged++; return; }
            int width = inventory.inventorySizeX, height = inventory.inventorySizeY;
            if (items.Count > 128 || width <= 0 || height <= 0 || width > 64 || height > 64 || width * height > 2048)
            { Skip("超出单容器128件/2048格安全预算"); return; }
            var manager = ItemManager.instance;
            if (manager == null || manager.itemAttrDic == null) { Skip("物品尺寸资料不可用"); return; }
            var snapshot = new List<Snapshot>(items.Count);
            var rectangles = new List<GridPacker.Item>(items.Count);
            var pointers = new HashSet<IntPtr>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || !pointers.Add(item.Pointer) || !manager.itemAttrDic.ContainsKey(item.itemId))
                { Skip("包含空物品、重复引用或未知尺寸物品"); return; }
                var attr = manager.itemAttrDic[item.itemId];
                if (attr == null) { Skip("未知物品尺寸"); return; }
                var pos = item.inventoryCoordinate;
                bool pinned = IsReserved(cd, item);
                snapshot.Add(new Snapshot { Item = item, Pointer = item.Pointer, Id = item.itemId,
                    Quantity = item.itemNumberFloat, Durability = item.durability, X = pos.x, Y = pos.y,
                    Rotated = item.rotateFlag, Fixed = pinned });
                rectangles.Add(new GridPacker.Item { Id = i, Width = attr.itemSize.x, Height = attr.itemSize.y,
                    X = pos.x, Y = pos.y, Rotated = item.rotateFlag, Fixed = pinned });
            }
            // No native references are passed to the solver. A valid existing
            // same-cell food group is intentionally left untouched in this v1.
            var result = GridPacker.Optimize(width, height, rectangles, 8, 32);
            if (!result.Valid) { Skip(result.Error); return; }
            if (!result.Improved) { _unchanged++; return; }
            if (!StillMatches(inventory, snapshot, width, height, cd)) { Skip("规划期间容器/物品/快捷栏已改变，放弃旧方案"); return; }
            var rollback = inventory.CaptureLayoutSnapshot();
            if (rollback == null) { Skip("无法获取官方布局回滚快照"); return; }
            try
            {
                foreach (var placement in result.Items)
                {
                    var original = snapshot[placement.Id];
                    if (original.Fixed && (placement.X != original.X || placement.Y != original.Y || placement.Rotated != original.Rotated))
                        throw new InvalidOperationException("方案移动了已装备/快捷栏保留物品");
                    original.Item.inventoryCoordinate = new Vector2Int(placement.X, placement.Y);
                    original.Item.rotateFlag = placement.Rotated;
                }
                inventory.SetOccupyDataDirty();
                inventory.RefreshOccupyData();
                inventory.NotifyDataChanged(true, null, false);
                // Check no side-effect callback unexpectedly changed contents.
                if (!ContentsMatch(inventory, snapshot)) throw new InvalidOperationException("提交回调改变了物品列表或数量");
                foreach (var placement in result.Items)
                {
                    var current = snapshot[placement.Id].Item;
                    var coordinate = current.inventoryCoordinate;
                    if (coordinate.x != placement.X || coordinate.y != placement.Y || current.rotateFlag != placement.Rotated)
                        throw new InvalidOperationException("提交回调改变了规划位置，已放弃本次优化");
                }
                _improved++; _largestGain += result.After.LargestEmpty - result.Before.LargestEmpty;
            }
            catch
            {
                // LayoutSnapshot includes itemNumbers; never use it to roll back
                // quantity changed by an official callback. Restore coordinates
                // only, and only for originals still present in this container.
                var currentItems = inventory.itemList;
                if (currentItems != null) foreach (var original in snapshot)
                {
                    for (int i = 0; i < currentItems.Count; i++)
                    {
                        var current = currentItems[i];
                        if (current == null || current.Pointer != original.Pointer) continue;
                        current.inventoryCoordinate = new Vector2Int(original.X, original.Y);
                        current.rotateFlag = original.Rotated;
                        break;
                    }
                }
                inventory.SetOccupyDataDirty(); inventory.RefreshOccupyData();
                inventory.NotifyDataChanged(true, null, false);
                throw;
            }
        }
        private static bool ContentsMatch(InventoryData inventory, List<Snapshot> snapshot)
        {
            var items = inventory.itemList;
            if (items == null || items.Count != snapshot.Count) return false;
            for (int i = 0; i < snapshot.Count; i++)
            {
                var current = items[i]; var old = snapshot[i];
                if (current == null || current.Pointer != old.Pointer || current.itemId != old.Id ||
                    current.itemNumberFloat != old.Quantity || current.durability != old.Durability) return false;
            }
            return true;
        }
        private static bool StillMatches(InventoryData inventory, List<Snapshot> snapshot, int width, int height, CharacterData cd)
        {
            if (inventory.isLocked || inventory.sortingInProgressFlag || inventory.inventorySizeX != width || inventory.inventorySizeY != height || !ContentsMatch(inventory, snapshot)) return false;
            var mouse = MouseController.instance;
            if (mouse != null && mouse.isDraggingItemUI) return false;
            for (int i = 0; i < snapshot.Count; i++)
            {
                var old = snapshot[i]; var current = inventory.itemList[i]; var pos = current.inventoryCoordinate;
                if (pos.x != old.X || pos.y != old.Y || current.rotateFlag != old.Rotated || IsReserved(cd, current) != old.Fixed) return false;
            }
            return true;
        }
        private static bool IsReserved(CharacterData cd, ItemData item)
        {
            var equipment = cd.characterEquipmentData;
            if (equipment != null)
            {
                bool Same(ItemData other) => other != null && other.Pointer == item.Pointer;
                if (Same(equipment.currentWeaponItemData) || Same(equipment.backpackItemData) || Same(equipment.hatItemData) ||
                    Same(equipment.faceMaskItemData) || Same(equipment.torseItemData) || Same(equipment.legItemData) ||
                    Same(equipment.feetItemData) || Same(equipment.cloakItemData) || Same(equipment.headDecorationItemData) ||
                    Same(equipment.throwableItemData) || Same(equipment.exoskeletonItemData) || Same(equipment.thrusterItemData) ||
                    Same(equipment.shieldItemData) || Same(equipment.specialEquipmentItemData) || Same(equipment.lightingToolItemData) ||
                    Same(cd.equippedGrappleHookItemData)) return true;
                var weapons = equipment.weaponItemDataAry;
                if (weapons != null) for (int i = 0; i < weapons.Length; i++) if (Same(weapons[i])) return true;
                var handheld = equipment.handheldDeviceItemDataAry;
                if (handheld != null) for (int i = 0; i < handheld.Length; i++) if (Same(handheld[i])) return true;
                var torso = equipment.torseItemList;
                if (torso != null) for (int i = 0; i < torso.Count; i++) if (Same(torso[i])) return true;
                var legs = equipment.legItemList;
                if (legs != null) for (int i = 0; i < legs.Count; i++) if (Same(legs[i])) return true;
            }
            var shortcuts = cd.shortcutDataList;
            if (shortcuts != null) for (int i = 0; i < shortcuts.Count; i++)
            {
                var shortcut = shortcuts[i];
                if (shortcut != null && shortcut.itemData != null && shortcut.itemData.Pointer == item.Pointer) return true;
            }
            return false;
        }
    }
}
