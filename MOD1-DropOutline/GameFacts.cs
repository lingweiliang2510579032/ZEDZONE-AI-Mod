using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dropoutline
{
    /// <summary>
    /// 给 AI 搭档读的「玩家身上的事实」：背包里有什么、能合成什么。
    ///
    /// 反作弊原则：这里只读取**玩家自己背包里的东西**和**玩家打开合成面板就能看到的配方**，
    /// 不读地图外的东西、不读未探索区域、不读敌人位置。
    /// </summary>
    internal static class GameFacts
    {
        internal static bool IsRecipeKnown(int outputItemId)
        {
            if (!AiSaveContext.Active) return false;
            try { return TechNodeManager.instance != null && TechNodeManager.instance.IsItemUnlocked(outputItemId); }
            catch { return false; }
        }
        /// <summary>背包摘要：重量 + 主要物品（按数量排序，最多 12 种）。</summary>
        public static string InventorySummary()
        {
            try
            {
                var gc = GameController.instance;
                if (gc == null || gc.playerCharacter == null) return "";
                var cd = gc.playerCharacter.characterData;
                if (cd == null) return "";

                var counts = new Dictionary<int, (string name, int n)>();
                try
                {
                    var invs = cd.GetCarriedInventoryList();
                    if (invs != null)
                    {
                        for (int i = 0; i < invs.Count; i++)
                        {
                            var inv = invs[i];
                            if (inv == null || inv.itemList == null) continue;
                            for (int k = 0; k < inv.itemList.Count; k++)
                            {
                                var item = inv.itemList[k];
                                if (item == null) continue;
                                int id = item.itemId;
                                if (id <= 0) continue;
                                string nm = null;
                                try { nm = item.GetItemName(); } catch { }
                                if (string.IsNullOrEmpty(nm)) continue;
                                counts.TryGetValue(id, out var cur);
                                int n = cur.n + Math.Max(1, (int)item.itemNumberFloat);
                                counts[id] = (nm, n);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    DropOutlinePlugin.L.LogWarning($"[事实] 读背包失败: {e.Message}");
                }

                var list = new List<KeyValuePair<int, (string name, int n)>>(counts);
                list.Sort((a, b) => b.Value.n.CompareTo(a.Value.n));

                var sb = new StringBuilder();
                float w = 0f, max = 0f;
                try { w = cd.totalEquipmentWeight; max = cd.maxCarryWeight; } catch { }
                sb.Append($"背包 {w:F1}/{max:F1} kg（{counts.Count} 种）");
                if (list.Count > 0)
                {
                    sb.Append("：");
                    int shown = 0;
                    foreach (var kv in list)
                    {
                        if (shown++ > 0) sb.Append('、');
                        sb.Append(kv.Value.name);
                        if (kv.Value.n > 1) sb.Append('x').Append(kv.Value.n);
                        if (shown >= 12) break;
                    }
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[事实] 背包摘要异常: {e.Message}");
                return "";
            }
        }

        /// <summary>
        /// 视野内「已检视」容器里装了什么（只读）。判定链与归并完全共用
        /// CollectVisibleContainers：对象池排除、已检视、非尸体、非生产设施、
        /// 父节点楼层 == 玩家 zLevel、相机视野内、20m 内。未检视的容器不读内容。
        /// </summary>
        public static string ContainerSummary(int maxContainers = 16, int maxKinds = 5, float maxDistance = 20f)
        {
            try
            {
                var boxes = CollectVisibleContainers(maxDistance);
                float zoom = 0f;
                try { var cam = UnityEngine.Camera.main; if (cam != null) zoom = cam.orthographicSize; } catch { }
                var lines = new List<string>();
                foreach (var box in boxes)
                {
                    var counts = new Dictionary<int, int>();
                    var list = box.Inventory?.itemList;
                    if (list != null)
                        for (int i = 0; i < list.Count; i++)
                        {
                            var item = list[i];
                            if (item == null) continue;
                            counts.TryGetValue(item.itemId, out int n);
                            counts[item.itemId] = n + Math.Max(1, (int)item.itemNumberFloat);
                        }
                    var groups = new Dictionary<string, List<string>>();
                    var order = new List<string>();
                    foreach (var kv in counts)
                    {
                        var category = CategoryOf(kv.Key);
                        if (!groups.TryGetValue(category, out var bucket)) { bucket = new List<string>(); groups[category] = bucket; order.Add(category); }
                        if (bucket.Count < 4) bucket.Add(ItemName(kv.Key) + "×" + kv.Value);
                    }
                    var parts = new List<string>();
                    foreach (var category in order)
                    {
                        if (parts.Count >= maxKinds) { parts.Add("等"); break; }
                        parts.Add(category + "(" + string.Join("、", groups[category]) + ")");
                    }
                    lines.Add(box.Name + "(" + Math.Round(box.Distance) + "m)：" + (parts.Count == 0 ? "（空）" : string.Join(" ", parts)));
                    if (lines.Count >= maxContainers) break;
                }
                if (boxes.Count == 0) return "";
                var header = "视野内已检视容器 " + boxes.Count + " 个（镜头缩放 " + zoom.ToString("0.#", CultureInfo.InvariantCulture) + "）";
                return header + "：" + string.Join("；", lines);
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[事实] 容器摘要异常: {e.Message}");
                return "";
            }
        }

        /// <summary>
        /// 诊断：一行汇总被算进来/被排除的容器数量（只在数字变化时由调用方打印）。
        /// 楼层根因已查清（对象池），这里不再打印逐条探针。
        /// </summary>
        public static string ContainerDebugDump(float maxDistance = 20f)
        {
            try
            {
                int pooled = 0, corpse = 0, station = 0, uninspected = 0, otherFloor = 0;
                var player = GameController.instance?.playerCharacter;
                int playerVz = int.MinValue;
                try { if (player?.characterData != null) playerVz = player.characterData.zLevel; } catch { }
                foreach (var container in UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject_Furniture_Inventory>())
                {
                    if (container == null || container.gameObject == null) continue;
                    if (!IsLiveTerrainObject(container)) { pooled++; continue; }
                    string cn = null;
                    try { cn = container.attr != null ? container.attr.chineseName : null; } catch { }
                    if (!string.IsNullOrEmpty(cn) && cn.IndexOf("尸体", StringComparison.Ordinal) >= 0) { corpse++; continue; }
                    if (!string.IsNullOrEmpty(cn) && IsCraftStation(cn)) { station++; continue; }
                    int floor = FloorFromParent(container);
                    if (playerVz != int.MinValue && floor != int.MinValue && floor != playerVz) { otherFloor++; continue; }
                    bool inspected = false;
                    try { inspected = container.isInventoryInspected; } catch { }
                    if (!inspected) uninspected++;
                }
                int counted = CollectVisibleContainers(maxDistance).Count;
                return "计入 " + counted + "；排除 对象池" + pooled + " 尸体" + corpse + " 设施" + station
                    + " 异层" + otherFloor + " 未检视" + uninspected + "（其余为视野外/太远）";
            }
            catch (Exception e) { return "dump_error:" + e.Message; }
        }

        /// <summary>
        /// 是否是"当前楼层真正存在的物体"。
        /// 关键实证：FindObjectsOfTypeAll 会把对象池里的回收实例也翻出来，它们挂在
        /// `TerrainObjectPoolManager(Clone)/DisabledTerrainObjectParent` 下，
        /// 表现就是"地下室里读到地面的车辆残骸/尸体"。
        /// 当前层的真实物体挂在 `GlobalTerrainObjectsParent/TerrainObjectParent_&lt;楼层&gt;_&lt;区块&gt;`，
        /// 父节点名里带楼层，正好可以和玩家 zLevel 对账。
        /// </summary>
        private static bool IsLiveTerrainObject(TerrainObject container)
        {
            try
            {
                if (!container.gameObject.activeInHierarchy) return false;
            }
            catch { }
            string parent = null, root = null;
            try { var t = container.transform; if (t != null) { var p = t.parent; if (p != null) parent = p.name; var r = t.root; if (r != null) root = r.name; } }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(root) && root.IndexOf("PoolManager", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (!string.IsNullOrEmpty(parent) && parent.IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
            catch { }
            return true;
        }

        /// <summary>从父节点名 `TerrainObjectParent_-1_9` 里取楼层；取不到返回 int.MinValue。</summary>
        private static int FloorFromParent(TerrainObject container)
        {
            try
            {
                var t = container.transform;
                var parent = t != null ? t.parent : null;
                string name = parent != null ? parent.name : null;
                if (string.IsNullOrEmpty(name)) return int.MinValue;
                int underscore = name.IndexOf('_');
                if (underscore < 0) return int.MinValue;
                int next = name.IndexOf('_', underscore + 1);
                if (next < 0) return int.MinValue;
                string floorText = name.Substring(underscore + 1, next - underscore - 1);
                int floor;
                if (int.TryParse(floorText, out floor)) return floor;
            }
            catch { }
            return int.MinValue;
        }


        /// <summary>生产设施（工作台、弹药压床、灶台、熔炉…）：能打开，但不算仓库容器。</summary>
        private static bool IsCraftStation(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string[] keys = { "工作台", "压床", "压弹", "灶台", "熔炉", "打印机", "发电机", "烤炉", "烤箱", "冶炼", "烘干", "晾晒" };
            for (int i = 0; i < keys.Length; i++)
                if (name.IndexOf(keys[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>视野内一个可整理的容器（已通过全部判据）。</summary>
        internal sealed class ContainerRef
        {
            public TerrainObject Object;
            public InventoryData Inventory;
            public string Name = "";
            public float Distance;
        }

        /// <summary>
        /// 收集"当前楼层、视野内、已检视、非尸体、非生产设施"的容器。
        /// 判定链与容器摘要完全一致，搬运直接用这份结果，避免两套逻辑走偏。
        /// </summary>
        public static List<ContainerRef> CollectVisibleContainers(float maxDistance = 20f)
        {
            var result = new List<ContainerRef>();
            try
            {
                var player = GameController.instance?.playerCharacter;
                if (player == null) return result;
                UnityEngine.Vector3 playerPos;
                try { playerPos = player.transform.position; } catch { return result; }
                UnityEngine.Camera camera = null;
                try { camera = UnityEngine.Camera.main; } catch { }
                int playerVz = 0;
                bool haveVz = false;
                try
                {
                    var cd0 = player.characterData;
                    if (cd0 != null) { playerVz = cd0.zLevel; haveVz = true; }
                }
                catch { }
                foreach (var container in UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject_Furniture_Inventory>())
                {
                    if (container == null || container.gameObject == null) continue;
                    if (!IsLiveTerrainObject(container)) continue;
                    bool inspected = false;
                    try { inspected = container.isInventoryInspected; } catch { }
                    if (!inspected) continue;
                    try
                    {
                        var attr = container.attr;
                        string cn = attr != null ? attr.chineseName : null;
                        if (!string.IsNullOrEmpty(cn) && cn.IndexOf("尸体", StringComparison.Ordinal) >= 0) continue;
                        if (attr != null && attr.isProductionObject)
                        {
                            string kind = "";
                            try { kind = attr.productionObjectType.ToString() ?? ""; } catch { }
                            if (!string.Equals(kind, "StorageBox", StringComparison.Ordinal)) continue;
                        }
                        if (!string.IsNullOrEmpty(cn) && IsCraftStation(cn)) continue;
                    }
                    catch { }
                    UnityEngine.Vector3 worldPos;
                    try { worldPos = container.transform.position; } catch { continue; }
                    float distance;
                    try { distance = UnityEngine.Vector2.Distance(playerPos, worldPos); } catch { continue; }
                    if (distance > maxDistance) continue;
                    if (haveVz)
                    {
                        int floor = FloorFromParent(container);
                        if (floor != int.MinValue && floor != playerVz) continue;
                    }
                    if (!InCameraView(camera, worldPos)) continue;
                    InventoryData inv = null;
                    try { inv = container.objectData != null ? container.objectData.inventoryData : null; } catch { }
                    if (inv == null) continue;
                    string name = null;
                    try
                    {
                        var attr2 = container.attr;
                        if (attr2 != null) name = attr2.chineseName;
                    }
                    catch { }
                    if (string.IsNullOrEmpty(name)) { try { name = container.gameObject.name; } catch { name = "容器"; } }
                    result.Add(new ContainerRef { Object = container, Inventory = inv, Name = name, Distance = distance });
                }
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[事实] 容器收集异常: {e.Message}");
            }
            return result;
        }

        /// <summary>名字像家具/储物设施（柜、睡袋、床铺、桌椅、冰箱、水壶）就归家具。</summary>
        private static bool AdditiveFurniture(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // 注意：保险箱不算家具——它是加密的背包类物品，按 itemType 归穿戴。
            return name.IndexOf("柜", StringComparison.Ordinal) >= 0
                || name.IndexOf("睡袋", StringComparison.Ordinal) >= 0 || name.IndexOf("床", StringComparison.Ordinal) >= 0
                || name.IndexOf("桌", StringComparison.Ordinal) >= 0 || name.IndexOf("椅", StringComparison.Ordinal) >= 0
                || name.IndexOf("冰箱", StringComparison.Ordinal) >= 0 || name.IndexOf("水壶", StringComparison.Ordinal) >= 0;
        }

        /// <summary>镜头视野判定：正交相机直接用视口坐标，缩放（滚轮）会自然改变范围。</summary>
        private static bool InCameraView(UnityEngine.Camera camera, UnityEngine.Vector3 worldPos)
        {
            if (camera == null) return true;                      // 没相机时退化为只按距离
            try
            {
                var viewport = camera.WorldToViewportPoint(worldPos);
                if (viewport.z <= 0f) return false;
                return viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;
            }
            catch { return true; }
        }

        internal static string ItemDisplayName(int itemId) => ItemName(itemId);

        private static string ItemName(int itemId)
        {
            try
            {
                var manager = ItemManager.instance;
                if (manager != null)
                {
                    var attr = manager.GetItemAttrById(itemId);
                    if (attr != null)
                    {
                        var runtime = attr.itemName_Runtime;
                        if (!string.IsNullOrEmpty(runtime)) return runtime;
                        if (!string.IsNullOrEmpty(attr.itemName)) return attr.itemName;
                    }
                }
            }
            catch { }
            return "ID " + itemId;
        }

        /// <summary>
        /// 把场景里**所有**储物容器导出成一张表（containers.json）：位置、楼层、
        /// 是否已检视、是不是生产台/载具、以及已检视容器的内容。
        /// 未检视的容器只登记、不读内容（玩家自己定的边界）。
        /// </summary>
        public static string WriteContainerCatalog(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return "no_dir";
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                var player = GameController.instance?.playerCharacter;
                UnityEngine.Vector3 playerPos = UnityEngine.Vector3.zero;
                bool havePlayer = false;
                try { if (player != null) { playerPos = player.transform.position; havePlayer = true; } } catch { }
                UnityEngine.Camera camera = null;
                try { camera = UnityEngine.Camera.main; } catch { }
                var sb = new StringBuilder();
                sb.Append("{\"schemaVersion\":2,\"ts\":\"").Append(DateTime.Now.ToString("HH:mm:ss")).Append("\",\"containers\":[");
                int total = 0, inspectedCount = 0, production = 0, vehicles = 0, first = 1;
                foreach (var container in UnityEngine.Resources.FindObjectsOfTypeAll<TerrainObject_Furniture_Inventory>())
                {
                    if (container == null || container.gameObject == null) continue;
                    UnityEngine.Vector3 worldPos;
                    try { worldPos = container.transform.position; } catch { continue; }
                    string name = null, rawName = null;
                    try { name = container.attr != null ? container.attr.chineseName : null; } catch { }
                    try { rawName = container.gameObject.name; } catch { }
                    if (string.IsNullOrEmpty(name)) name = rawName;
                    if (!string.IsNullOrEmpty(name)) name = name.Replace("(Clone)", "").Trim();
                    bool inspected = false; try { inspected = container.isInventoryInspected; } catch { }
                    bool isProduction = false;
                    string productionKind = "";
                    try
                    {
                        var attr = container.attr;
                        isProduction = attr != null && attr.isProductionObject;
                        if (attr != null) productionKind = attr.productionObjectType.ToString() ?? "";
                    }
                    catch { }
                    bool isVehicle = false; try { isVehicle = container.GetComponent<BasicVehicle>() != null; } catch { }
                    float distance = -1f;
                    try { if (havePlayer) distance = UnityEngine.Vector2.Distance(playerPos, worldPos); } catch { }
                    bool live = IsLiveTerrainObject(container);
                    if (!live) continue;                           // 对象池回收实例不进清单
                    int floor = FloorFromParent(container);
                    int playerFloor = int.MinValue;
                    try { if (player?.characterData != null) playerFloor = player.characterData.zLevel; } catch { }
                    bool sameFloor = floor == int.MinValue || playerFloor == int.MinValue || floor == playerFloor;
                    bool inView = InCameraView(camera, worldPos);

                    total++;
                    if (inspected) inspectedCount++;
                    if (isProduction) production++;
                    if (isVehicle) vehicles++;

                    if (first == 0) sb.Append(',');
                    first = 0;
                    sb.Append("{\"name\":\"").Append(MiniJson.Escape(name ?? "?")).Append("\"")
                      .Append(",\"raw\":\"").Append(MiniJson.Escape(rawName ?? "")).Append("\"")
                      .Append(",\"x\":").Append(worldPos.x.ToString("0.#", CultureInfo.InvariantCulture))
                      .Append(",\"y\":").Append(worldPos.y.ToString("0.#", CultureInfo.InvariantCulture))
                      .Append(",\"z\":").Append(worldPos.z.ToString("0.##", CultureInfo.InvariantCulture))
                      .Append(",\"distance\":").Append(distance.ToString("0.#", CultureInfo.InvariantCulture))
                      .Append(",\"floor\":").Append(floor == int.MinValue ? "null" : floor.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"sameFloor\":").Append(sameFloor ? "true" : "false")
                      .Append(",\"inView\":").Append(inView ? "true" : "false")
                      .Append(",\"inspected\":").Append(inspected ? "true" : "false")
                      .Append(",\"production\":").Append(isProduction ? "true" : "false")
                      .Append(",\"productionKind\":\"").Append(MiniJson.Escape(productionKind)).Append("\"")
                      .Append(",\"vehicle\":").Append(isVehicle ? "true" : "false")
                      .Append(",\"items\":[");
                    // 只有已检视的容器才读内容
                    if (inspected)
                    {
                        InventoryData inv = null;
                        try { inv = container.objectData != null ? container.objectData.inventoryData : null; } catch { }
                        var counts = new Dictionary<int, int>();
                        if (inv?.itemList != null)
                            for (int i = 0; i < inv.itemList.Count; i++)
                            {
                                var item = inv.itemList[i];
                                if (item == null) continue;
                                counts.TryGetValue(item.itemId, out int n);
                                counts[item.itemId] = n + Math.Max(1, (int)item.itemNumberFloat);
                            }
                        int firstItem = 1;
                        foreach (var kv in counts)
                        {
                            if (firstItem == 0) sb.Append(',');
                            firstItem = 0;
                            sb.Append("{\"id\":").Append(kv.Key)
                              .Append(",\"name\":\"").Append(MiniJson.Escape(ItemName(kv.Key))).Append("\"")
                              .Append(",\"count\":").Append(kv.Value)
                              .Append(",\"category\":\"").Append(MiniJson.Escape(CategoryOf(kv.Key))).Append("\"}");
                        }
                    }
                    sb.Append("]}");
                }
                sb.Append("]}");
                var path = System.IO.Path.Combine(dir, "containers.json");
                var tmp = path + ".tmp";
                System.IO.File.WriteAllText(tmp, sb.ToString(), new System.Text.UTF8Encoding(false));
                System.IO.File.Move(tmp, path, true);
                return "容器 " + total + " 个（已检视 " + inspectedCount + "，生产台 " + production + "，载具 " + vehicles + "）";
            }
            catch (Exception e) { return "write_error:" + e.Message; }
        }

        /// <summary>
        /// 把游戏里**所有**物品的类型、特征与分类导出成一张表（item-categories.json），
        /// 供外部核对与后续搬运使用。这样遇到没见过的物品也能先看表，而不是靠运行期猜。
        /// 只读游戏数据，不改变任何状态。
        /// </summary>
        public static string WriteItemCategories(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return "no_dir";
                var manager = ItemManager.instance;
                if (manager?.itemAttrDic == null) return "no_attr";
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.Append("{\"schemaVersion\":1,\"ts\":\"").Append(DateTime.Now.ToString("HH:mm:ss")).Append("\",\"items\":[");
                int total = 0, first = 1;
                var counts = new Dictionary<string, int>();
                var others = new List<string>();
                foreach (var pair in manager.itemAttrDic)
                {
                    var attr = pair.Value;
                    if (attr == null) continue;
                    int id = pair.Key;
                    string name = ItemName(id);
                    string type = ""; try { type = attr.itemType.ToString() ?? ""; } catch { }
                    string tool = ""; try { tool = attr.toolType.ToString() ?? ""; } catch { }
                    var features = new List<string>();
                    try
                    {
                        var list = attr.itemFeatures;
                        if (list != null) for (int i = 0; i < list.Count; i++) { var text = list[i].ToString(); if (!string.IsNullOrEmpty(text)) features.Add(text); }
                    }
                    catch { }
                    string category = CategoryOf(id);
                    if (first == 0) sb.Append(',');
                    first = 0;
                    sb.Append("{\"id\":").Append(id)
                      .Append(",\"name\":\"").Append(MiniJson.Escape(name)).Append("\"")
                      .Append(",\"type\":\"").Append(MiniJson.Escape(type)).Append("\"")
                      .Append(",\"tool\":\"").Append(MiniJson.Escape(tool)).Append("\"")
                      .Append(",\"features\":[");
                    for (int i = 0; i < features.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(MiniJson.Escape(features[i])).Append('"');
                    }
                    sb.Append("],\"category\":\"").Append(MiniJson.Escape(category)).Append("\"}");
                    total++;
                    counts.TryGetValue(category, out int n);
                    counts[category] = n + 1;
                    if (category == "其他" && others.Count < 40) others.Add(name);
                }
                sb.Append("]}");
                var path = System.IO.Path.Combine(dir, "item-categories.json");
                var tmp = path + ".tmp";
                System.IO.File.WriteAllText(tmp, sb.ToString(), new System.Text.UTF8Encoding(false));
                System.IO.File.Move(tmp, path, true);
                var summary = new StringBuilder();
                foreach (var kv in counts) summary.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
                summary.Append("；其他样例：").Append(others.Count == 0 ? "无" : string.Join("、", others.ToArray(), 0, Math.Min(12, others.Count)));
                return "物品 " + total + " 件 " + summary;
            }
            catch (Exception e) { return "write_error:" + e.Message; }
        }

        /// <summary>
        /// 玩家定的存放分区（按游戏真实枚举判定，不靠猜）：
        /// 电源=电池与吃电设备；家具=可放置家具；工具=有工具类型或名字像工具；
        /// 武器=枪/近战/弹药/弹匣/弓弹/枪械配件/投掷物；医疗=绷带与药品；
        /// 食物=Food；穿戴=衣物面罩披风背包装饰；材料=Material/Container/Liqiud 与可加工物。
        /// </summary>
        internal static string CategoryOf(int itemId)
        {
            string type = "", tool = "";
            var features = new List<string>();
            try
            {
                var attr = ItemManager.instance?.GetItemAttrById(itemId);
                if (attr != null)
                {
                    try { type = attr.itemType.ToString() ?? ""; } catch { }
                    try { tool = attr.toolType.ToString() ?? ""; } catch { }
                    try
                    {
                        var list = attr.itemFeatures;
                        if (list != null)
                            for (int i = 0; i < list.Count; i++)
                            {
                                var text = list[i].ToString();
                                if (!string.IsNullOrEmpty(text)) features.Add(text);
                            }
                    }
                    catch { }
                }
            }
            catch { }
            string lower = type.ToLowerInvariant();
            var name = ItemName(itemId);
            bool HasFeature(string featureName) => features.Exists(f => string.Equals(f, featureName, StringComparison.OrdinalIgnoreCase));
            bool HasFeatureLike(string part) => features.Exists(f => f.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);

            // 1) 食物优先：果实/肉类/罐头即使带电池特征也还是吃的（实测"辐射果实"被误判成电源）
            if (lower == "food" || HasFeature("PetFood") || HasFeature("BBQItem")
                || name.IndexOf("果实", StringComparison.Ordinal) >= 0 || name.IndexOf("肉", StringComparison.Ordinal) >= 0
                || name.IndexOf("鱼", StringComparison.Ordinal) >= 0 || name.IndexOf("罐头", StringComparison.Ordinal) >= 0)
                return "食物";

            // 2) 电源：电池本体 + 一切吃电/供电器（手电、充气筒、供电模块、车电池）
            if (HasFeature("Battery") || HasFeature("BatteryBox") || HasFeature("BatteryConsuming")
                || HasFeature("LightingTool") || HasFeature("HandheldDevice") || HasFeature("TirePump")
                || HasFeature("PortableCharger") || HasFeature("SolarCharger") || HasFeature("VehicleBattery"))
                return "电源";

            // 3) 家具：可放置的储物/生活设施（保险箱、睡袋、便携炉等）
            if (AdditiveFurniture(name)) return "家具";
            if (lower == "deployable")
            {
                // 可放置但属于武器/设施的东西不算家具：塔、陷阱、烟火、爆炸物、诱饵
                if (name.IndexOf("礼花", StringComparison.Ordinal) >= 0 || name.IndexOf("烟花", StringComparison.Ordinal) >= 0
                    || name.IndexOf("爆", StringComparison.Ordinal) >= 0 || name.IndexOf("炸", StringComparison.Ordinal) >= 0
                    || name.IndexOf("雷", StringComparison.Ordinal) >= 0 || name.IndexOf("塔", StringComparison.Ordinal) >= 0
                    || name.IndexOf("陷阱", StringComparison.Ordinal) >= 0 || name.IndexOf("刺", StringComparison.Ordinal) >= 0
                    || name.IndexOf("诱饵", StringComparison.Ordinal) >= 0)
                    return "武器";
                return "家具";
            }
            if (HasFeature("PortableStove") || HasFeature("PortableFridge") || HasFeature("Kettle")) return "家具";

            // 4) 工具：名字像工具，或带工具类型但不是武器类的家伙
            if (name.IndexOf("锤", StringComparison.Ordinal) >= 0 || name.IndexOf("镐", StringComparison.Ordinal) >= 0
                || name.IndexOf("钳", StringComparison.Ordinal) >= 0 || name.IndexOf("扳手", StringComparison.Ordinal) >= 0
                || name.IndexOf("改锥", StringComparison.Ordinal) >= 0 || name.IndexOf("螺丝刀", StringComparison.Ordinal) >= 0
                || name.IndexOf("撬", StringComparison.Ordinal) >= 0 || name.IndexOf("铲", StringComparison.Ordinal) >= 0
                || name.IndexOf("工具", StringComparison.Ordinal) >= 0)
                return "工具";
            if ((lower == "meleeweapon" || lower == "rangedweapon") && !string.IsNullOrEmpty(tool) && tool.ToLowerInvariant() != "none")
                return "武器";                                     // 刀斧枪属于武器，不因为能当工具就改类

            // 5) 医疗：绷带/急救/治疗，以及"药"但排除火药/弹药/炸药
            if (name.IndexOf("绷带", StringComparison.Ordinal) >= 0 || name.IndexOf("急救", StringComparison.Ordinal) >= 0
                || name.IndexOf("治疗", StringComparison.Ordinal) >= 0
                || (name.IndexOf("药", StringComparison.Ordinal) >= 0
                    && name.IndexOf("火药", StringComparison.Ordinal) < 0 && name.IndexOf("弹药", StringComparison.Ordinal) < 0
                    && name.IndexOf("炸药", StringComparison.Ordinal) < 0))
                return "医疗";

            // 6) 武器
            if (lower == "rangedweapon" || lower == "meleeweapon" || lower == "ammo" || lower == "magazine"
                || lower == "arrow" || lower == "gunpart" || lower == "throwable")
                return "武器";

            // 6) 食物
            if (lower == "food" || HasFeature("PetFood") || HasFeature("BBQItem")) return "食物";

            // 6) 食物（走到这里说明不是果实/肉类那种先行判定过的）
            if (lower == "food") return "食物";

            // 7) 容器类：包中包（保险箱这类，能放进背包里再装东西，但不能穿戴）
            if (name.IndexOf("保险箱", StringComparison.Ordinal) >= 0) return "容器";

            // 8) 穿戴
            if (lower == "clothing" || lower == "facemask" || lower == "cloak" || lower == "backpack"
                || HasFeature("HeadDecoration") || HasFeature("PetVest") || HasFeature("PetCollar") || HasFeature("ExoskeletonEquipment"))
                return "穿戴";

            // 9) 材料：原料、容器（铁罐、水瓶）、可加工物
            if (lower == "material" || lower == "container" || lower == "liqiud"
                || HasFeature("Combustible") || HasFeature("Ash") || HasFeature("Meltable")
                || HasFeature("Fertilizer") || HasFeature("Crop"))
                return "材料";

            if (!string.IsNullOrEmpty(tool) && tool.ToLowerInvariant() != "none") return "工具";
            if (HasFeatureLike("Liquid")) return "材料";
            return "其他";
        }

        /// <summary>
        /// 吃电设备的电量：扫「手持设备槽 + 手持中的物品 + 随身背包里的物品」，
        /// 找 itemPropertyPairs 里的 BatterySlot0/1（格式 "电池ID|剩余|耐久"）。
        /// 不只盯着手持槽——实测手电可能亮着但手持槽是空的。
        /// </summary>
        public static string BatterySummary()
        {
            try
            {
                var player = GameController.instance?.playerCharacter;
                if (player?.characterData == null) return "";
                var parts = new List<string>();
                var scanned = new HashSet<IntPtr>();

                try { CollectBattery(player.HoldingItemData, parts, scanned); } catch { }
                var eq = player.characterData.characterEquipmentData;
                if (eq != null)
                {
                    int slots = 2;
                    try { slots = Math.Max(1, Math.Min(8, GameConfigs.handheldDeviceSlotCount)); } catch { }
                    for (int i = 0; i < slots; i++)
                    {
                        ItemData item = null;
                        try { item = eq.GetHandheldDevice(i); } catch { }
                        CollectBattery(item, parts, scanned);
                    }
                }
                try
                {
                    var invs = player.characterData.GetCarriedInventoryList();
                    if (invs != null)
                        for (int i = 0; i < invs.Count; i++)
                        {
                            var inv = invs[i];
                            if (inv?.itemList == null) continue;
                            for (int k = 0; k < inv.itemList.Count; k++) CollectBattery(inv.itemList[k], parts, scanned);
                        }
                }
                catch { }
                return parts.Count == 0 ? "" : string.Join("；", parts);
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[事实] 电量摘要异常: {e.Message}");
                return "";
            }
        }

        /// <summary>从一个物品上读出所有电池槽的电量；没有电池槽就什么都不加。</summary>
        private static void CollectBattery(ItemData item, List<string> parts, HashSet<IntPtr> scanned)
        {
            if (item == null || parts.Count >= 6) return;
            IntPtr pointer;
            try { pointer = item.Pointer; } catch { return; }
            if (pointer == IntPtr.Zero || !scanned.Add(pointer)) return;
            Il2CppSystem.Collections.Generic.List<KeyValueDataPair> pairs;
            try { pairs = item.itemPropertyPairs; } catch { return; }
            if (pairs == null) return;
            string deviceName = ItemName(item.itemId);
            for (int k = 0; k < pairs.Count; k++)
            {
                var pair = pairs[k];
                if (pair == null) continue;
                string key = null, value = null;
                try { key = pair.key; value = pair.value; } catch { }
                if (string.IsNullOrEmpty(key) || !key.StartsWith("BatterySlot", StringComparison.Ordinal)) continue;
                var fields = (value ?? "").Split('|');
                if (fields.Length < 2) continue;
                float remaining;
                int batteryId;
                if (!int.TryParse(fields[0], out batteryId)) continue;
                if (!float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out remaining)) continue;
                string label = PercentLabel(remaining, BatteryCapacity(batteryId));
                parts.Add(deviceName + (key == "BatterySlot0" ? " 电池1 " : " 电池2 ") + label);
            }
        }

        /// <summary>电池容量：读不到条目字段时用实测容量表（81→4、82→10、83→24）。</summary>
        private static float BatteryCapacity(int batteryId)
        {
            switch (batteryId)
            {
                case 81: return 4f;
                case 82: return 10f;
                case 83: return 24f;
                default: return 0f;
            }
        }

        private static string PercentLabel(float remaining, float max)
        {
            if (max <= 0f) return remaining.ToString("0.#", CultureInfo.InvariantCulture) + "（容量未知）";
            int percent = (int)Math.Round(Math.Max(0f, Math.Min(1f, remaining / max)) * 100f);
            if (percent <= 5) return percent + "%（基本没电）";
            if (percent <= 20) return percent + "%（电量偏低）";
            return percent + "%";
        }

        /// <summary>
        /// 诊断：把已装备手持设备（手电等）的属性对原样打出来，用来确认电池电量
        /// 到底存在哪个键里、值是什么格式。只读，不改游戏状态。
        /// </summary>
        public static string EquippedDeviceDump()
        {
            try
            {
                var player = GameController.instance?.playerCharacter;
                var eq = player?.characterData?.characterEquipmentData;
                if (eq == null) return "eq=null";
                var sb = new StringBuilder();
                int slots = 2;
                try { slots = Math.Max(1, Math.Min(8, GameConfigs.handheldDeviceSlotCount)); } catch { }
                for (int i = 0; i < slots; i++)
                {
                    ItemData item = null;
                    try { item = eq.GetHandheldDevice(i); } catch { }
                    if (item == null) { sb.Append("[").Append(i).Append("]=空;"); continue; }
                    string name = null;
                    try { var attr = ItemManager.instance?.GetItemAttrById(item.itemId); name = attr?.itemName_Runtime ?? attr?.itemName; } catch { }
                    sb.Append("[").Append(i).Append("] id=").Append(item.itemId)
                      .Append(" name=").Append(name ?? "?")
                      .Append(" dur=").Append(item.durability.ToString("0.##", CultureInfo.InvariantCulture))
                      .Append(" props=");
                    if (item.properties == null) sb.Append("null");
                    else
                    {
                        sb.Append('[');
                        for (int k = 0; k < item.properties.Length; k++)
                        {
                            if (k > 0) sb.Append(',');
                            sb.Append(item.properties[k].ToString("0.###", CultureInfo.InvariantCulture));
                        }
                        sb.Append(']');
                    }
                    sb.Append(" pairs=");
                    var pairs = item.itemPropertyPairs;
                    if (pairs == null) sb.Append("null");
                    else
                    {
                        sb.Append('{');
                        for (int k = 0; k < pairs.Count; k++)
                        {
                            var p = pairs[k];
                            if (p == null) continue;
                            if (k > 0) sb.Append('|');
                            string key = null, value = null;
                            try { key = p.key; value = p.value; } catch { }
                            sb.Append(key).Append('=').Append(value);
                        }
                        sb.Append('}');
                    }
                    sb.Append(';');
                }
                try { var light = player.handFlashLight; sb.Append("light=").Append(light == null ? "null" : (light.enabled ? "on" : "off")); } catch { sb.Append("light=?"); }
                return sb.ToString();
            }
            catch (Exception e) { return "dump_error:" + e.Message; }
        }

        /// <summary>
        /// 可分配点数（属性/技能/词条）。面板顶上那三个数字，玩家一眼能看到，
        /// 这里原样告诉外部大脑，好在有点数时主动给建议。
        /// </summary>
        public static string ProgressSummary()
        {
            try
            {
                var gc = GameController.instance;
                var cd = gc != null && gc.playerCharacter != null ? gc.playerCharacter.characterData : null;
                if (cd == null) return "";
                int attr = cd.characterAttrPoint, skill = cd.characterSkillPoint, perk = cd.characterPerkPoint;
                if (attr <= 0 && skill <= 0 && perk <= 0) return "";
                var parts = new List<string>();
                if (attr > 0) parts.Add("属性点 " + attr);
                if (skill > 0) parts.Add("技能点 " + skill);
                if (perk > 0) parts.Add("词条点 " + perk);
                return "可分配点数：" + string.Join("、", parts);
            }
            catch { return ""; }
        }

        /// <summary>
        /// 携带材料还差的配方：直接说清"缺哪几样、差多少"，避免模型只会念配方路线。
        /// 只列接近可做的（缺口种类≤3），最多若干条，控制状态体积。
        /// </summary>
        public static string CraftDeficitSummary(int limit = 5)
        {
            try
            {
                var counts = CarriedCounts();
                if (counts.Count == 0) return "";
                var im = ItemManager.instance;
                if (im == null || im.allRecipeList == null) return "";

                var lines = new List<(int missingKinds, string text)>();
                for (int i = 0; i < im.allRecipeList.Count; i++)
                {
                    var r = im.allRecipeList[i];
                    if (r == null || r.recipeItems == null || r.recipeItems.Count == 0) continue;
                    if (!IsRecipeKnown(r.itemId)) continue;
                    var parts = new List<string>();
                    for (int k = 0; k < r.recipeItems.Count; k++)
                    {
                        var ing = r.recipeItems[k];
                        if (ing == null) continue;
                        counts.TryGetValue(ing.itemId, out int have);
                        float need = Math.Max(1f, ing.itemNumber);
                        if (have >= need) continue;
                        string name = null;
                        try { var attr = im.GetItemAttrById(ing.itemId); name = attr?.itemName_Runtime ?? attr?.itemName; } catch { }
                        if (string.IsNullOrEmpty(name)) name = "ID " + ing.itemId;
                        parts.Add(name + "×" + Math.Ceiling(need - have).ToString("0", CultureInfo.InvariantCulture));
                    }
                    if (parts.Count == 0 || parts.Count > 3) continue;
                    string output = null;
                    try { output = r.GetOutputItemName(); } catch { }
                    if (string.IsNullOrEmpty(output)) continue;
                    lines.Add((parts.Count, "做" + output + "缺" + string.Join("、", parts)));
                }
                if (lines.Count == 0) return "";
                lines.Sort((a, b) => a.missingKinds.CompareTo(b.missingKinds));
                var sb = new StringBuilder();
                for (int i = 0; i < Math.Min(limit, lines.Count); i++)
                {
                    if (i > 0) sb.Append('；');
                    sb.Append(lines[i].text);
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[事实] 缺口摘要异常: {e.Message}");
                return "";
            }
        }

        private static Dictionary<int, int> CarriedCounts()
        {
            var counts = new Dictionary<int, int>();
            try
            {
                var gc = GameController.instance;
                if (gc == null || gc.playerCharacter == null) return counts;
                var cd = gc.playerCharacter.characterData;
                if (cd == null) return counts;
                var invs = cd.GetCarriedInventoryList();
                if (invs == null) return counts;
                for (int i = 0; i < invs.Count; i++)
                {
                    var inv = invs[i];
                    if (inv == null || inv.itemList == null) continue;
                    for (int k = 0; k < inv.itemList.Count; k++)
                    {
                        var item = inv.itemList[k];
                        if (item == null) continue;
                        counts.TryGetValue(item.itemId, out int n);
                        counts[item.itemId] = n + Math.Max(1, (int)item.itemNumberFloat);
                    }
                }
            }
            catch { }
            return counts;
        }

        /// <summary>当前材料够做的配方（玩家在合成面板里能看到的那些）。</summary>
        public static string CraftableSummary(int limit = 6)
        {
            try
            {
                var counts = new Dictionary<int, int>();
                var gc = GameController.instance;
                if (gc == null || gc.playerCharacter == null) return "";
                var cd = gc.playerCharacter.characterData;
                if (cd == null) return "";

                var invs = cd.GetCarriedInventoryList();
                if (invs != null)
                {
                    for (int i = 0; i < invs.Count; i++)
                    {
                        var inv = invs[i];
                        if (inv == null || inv.itemList == null) continue;
                        for (int k = 0; k < inv.itemList.Count; k++)
                        {
                            var item = inv.itemList[k];
                            if (item == null) continue;
                            counts.TryGetValue(item.itemId, out int n);
                            counts[item.itemId] = n + Math.Max(1, (int)item.itemNumberFloat);
                        }
                    }
                }
                if (counts.Count == 0) return "";

                var im = ItemManager.instance;
                if (im == null || im.allRecipeList == null) return "";

                var names = new List<string>();
                int total = 0;
                for (int i = 0; i < im.allRecipeList.Count; i++)
                {
                    var r = im.allRecipeList[i];
                    if (r == null || r.recipeItems == null || !IsRecipeKnown(r.itemId)) continue;

                    bool ok = true;
                    for (int k = 0; k < r.recipeItems.Count; k++)
                    {
                        var ing = r.recipeItems[k];
                        if (ing == null) continue;
                        counts.TryGetValue(ing.itemId, out int have);
                        if (have < Math.Max(1, (int)ing.itemNumber)) { ok = false; break; }
                    }
                    if (!ok) continue;

                    total++;
                    if (names.Count < limit)
                    {
                        try { names.Add(r.GetOutputItemName()); }
                        catch { names.Add("?"); }
                    }
                }

                if (total == 0) return "";
                var sb = new StringBuilder();
                sb.Append($"材料够做的配方 {total} 项");
                if (names.Count > 0) sb.Append("：").Append(string.Join("、", names));
                return sb.ToString();
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[事实] 配方摘要异常: {e.Message}");
                return "";
            }
        }

        /// <summary>
        /// 把「制造配方 / 物品资料 / 技能」写成一个知识文件，供 DSH 侧的 zed_knowledge 工具按需查询。
        /// 这些都等价于玩家在游戏里打开对应面板就能看到的内容，不构成作弊。
        /// </summary>
        public static void WriteKnowledge(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !AiSaveContext.Active) return;
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

                var im = ItemManager.instance;
                if (im == null) return;

                var sb = new StringBuilder();
                sb.Append("{\"schemaVersion\":2,\"saveId\":\"").Append(AiSaveContext.SaveId).Append("\",\"worldEpoch\":\"").Append(AiSaveContext.Epoch).Append("\",\"ts\":\"").Append(DateTime.Now.ToString("HH:mm:ss")).Append("\",");

                // 材料库存（按 itemId 计数）
                var have = new Dictionary<int, int>();
                try
                {
                    var gc = GameController.instance;
                    var cd = gc != null && gc.playerCharacter != null ? gc.playerCharacter.characterData : null;
                    var invs = cd != null ? cd.GetCarriedInventoryList() : null;
                    if (invs != null)
                        for (int i = 0; i < invs.Count; i++)
                        {
                            var inv = invs[i];
                            if (inv == null || inv.itemList == null) continue;
                            for (int k = 0; k < inv.itemList.Count; k++)
                            {
                                var item = inv.itemList[k];
                                if (item == null) continue;
                                have.TryGetValue(item.itemId, out int n);
                                have[item.itemId] = n + Math.Max(1, (int)item.itemNumberFloat);
                            }
                        }
                }
                catch { }

                string NameOf(int id)
                {
                    try
                    {
                        if (im.itemAttrDic != null && im.itemAttrDic.ContainsKey(id))
                        {
                            var attr = im.itemAttrDic[id];
                            if (attr != null)
                            {
                                var n = attr.itemName_Runtime;
                                if (string.IsNullOrEmpty(n)) n = attr.itemName;
                                if (!string.IsNullOrEmpty(n)) return n;
                            }
                        }
                    }
                    catch { }
                    return "id" + id;
                }

                // 1) 配方
                sb.Append("\"recipes\":[");
                bool firstR = true;
                if (im.allRecipeList != null)
                {
                    for (int i = 0; i < im.allRecipeList.Count; i++)
                    {
                        var r = im.allRecipeList[i];
                        if (r == null || r.recipeItems == null || !IsRecipeKnown(r.itemId)) continue;

                        string outName;
                        try { outName = r.GetOutputItemName(); } catch { outName = NameOf(r.itemId); }
                        if (string.IsNullOrEmpty(outName)) continue;

                        var need = new StringBuilder();
                        bool ok = true;
                        int n2 = 0;
                        for (int k = 0; k < r.recipeItems.Count; k++)
                        {
                            var ing = r.recipeItems[k];
                            if (ing == null) continue;
                            int cnt = Math.Max(1, (int)ing.itemNumber);
                            have.TryGetValue(ing.itemId, out int owned);
                            if (owned < cnt) ok = false;
                            if (n2++ > 0) need.Append('+');
                            need.Append(NameOf(ing.itemId)).Append('x').Append(cnt);
                        }

                        if (!firstR) sb.Append(',');
                        firstR = false;
                        sb.Append("{\"name\":\"").Append(MiniJson.Escape(outName)).Append("\",");
                        sb.Append("\"platform\":\"").Append(r.craftPlatform).Append("\",");
                        sb.Append("\"need\":\"").Append(MiniJson.Escape(need.ToString())).Append("\",");
                        sb.Append("\"time\":").Append(r.craftTime.ToString("0.#")).Append(',');
                        sb.Append("\"fatigue\":").Append(r.fatigueCost.ToString("0.#")).Append(',');
                        sb.Append("\"ok\":").Append(ok ? "true" : "false").Append('}');
                    }
                }
                sb.Append("],");

                // 2) 物品资料（所有物品，名字/描述/价格/重量）
                sb.Append("\"items\":[");
                bool firstI = true;
                try
                {
                    if (im.itemList != null)
                    {
                        for (int i = 0; i < im.itemList.Count; i++)
                        {
                            var attr = im.itemList[i];
                            if (attr == null) continue;
                            string n = null;
                            try { n = attr.itemName_Runtime; } catch { }
                            if (string.IsNullOrEmpty(n)) { try { n = attr.itemName; } catch { } }
                            if (string.IsNullOrEmpty(n)) continue;
                            string desc = null;
                            try { desc = attr.runtimeItemDescription; } catch { }
                            if (string.IsNullOrEmpty(desc)) { try { desc = attr.itemDescription_WithLanguage; } catch { } }
                            if (string.IsNullOrEmpty(desc)) { try { desc = attr.itemDescription; } catch { } }
                            if (desc != null && desc.Length > 80) desc = desc.Substring(0, 80);

                            if (!firstI) sb.Append(',');
                            firstI = false;
                            sb.Append("{\"name\":\"").Append(MiniJson.Escape(n)).Append("\",");
                            sb.Append("\"desc\":\"").Append(MiniJson.Escape(desc ?? "")).Append("\",");
                            sb.Append("\"price\":").Append(attr.itemPrice.ToString("0.#")).Append(',');
                            sb.Append("\"weight\":").Append(attr.weight.ToString("0.##")).Append('}');
                        }
                    }
                }
                catch { }
                sb.Append("],");

                // 3) 建造（可放置的结构/家具，等价于游戏里「建造」面板）
                sb.Append("\"build\":[");
                bool firstB = true;
                try
                {
                    var gc2 = GameController.instance;
                    var buildList = gc2 != null ? gc2.GetAvailableTerrainObjectRecipeList() : null;
                    if (buildList != null)
                    {
                        for (int i = 0; i < buildList.Count; i++)
                        {
                            var r = buildList[i];
                            if (r == null || r.recipeItems == null) continue;
                            string outName;
                            try { outName = r.GetOutputItemName(); } catch { outName = NameOf(r.itemId); }
                            if (string.IsNullOrEmpty(outName)) continue;

                            var need = new StringBuilder();
                            bool ok = true;
                            int n2 = 0;
                            for (int k = 0; k < r.recipeItems.Count; k++)
                            {
                                var ing = r.recipeItems[k];
                                if (ing == null) continue;
                                int cnt = Math.Max(1, (int)ing.itemNumber);
                                have.TryGetValue(ing.itemId, out int owned);
                                if (owned < cnt) ok = false;
                                if (n2++ > 0) need.Append('+');
                                need.Append(NameOf(ing.itemId)).Append('x').Append(cnt);
                            }

                            if (!firstB) sb.Append(',');
                            firstB = false;
                            sb.Append("{\"name\":\"").Append(MiniJson.Escape(outName)).Append("\",");
                            sb.Append("\"need\":\"").Append(MiniJson.Escape(need.ToString())).Append("\",");
                            sb.Append("\"ok\":").Append(ok ? "true" : "false").Append('}');
                        }
                    }
                }
                catch (Exception e)
                {
                    DropOutlinePlugin.L.LogWarning($"[知识] 读建造列表失败: {e.Message}");
                }
                sb.Append("],");

                // 4) 词条（Perk）
                sb.Append("\"perks\":{");
                try
                {
                    var owner = new HashSet<int>();
                    int points = 0;
                    try
                    {
                        var gc3 = GameController.instance;
                        var cd3 = gc3 != null && gc3.playerCharacter != null ? gc3.playerCharacter.characterData : null;
                        if (cd3 != null)
                        {
                            points = cd3.characterPerkPoint;
                            if (cd3.perkList != null)
                                for (int i = 0; i < cd3.perkList.Count; i++) owner.Add(cd3.perkList[i]);
                        }
                    }
                    catch { }

                    sb.Append("\"points\":").Append(points).Append(',');
                    sb.Append("\"owned\":[");
                    bool firstO = true;
                    foreach (var id in owner)
                    {
                        if (!firstO) sb.Append(',');
                        firstO = false;
                        sb.Append(id);
                    }
                    sb.Append("],\"all\":[");

                    bool firstP = true;
                    var mgr = CharacterPerkAttrManager.instance;
                    if (mgr != null && mgr.perkList != null)
                    {
                        for (int i = 0; i < mgr.perkList.Count; i++)
                        {
                            var perk = mgr.perkList[i];
                            if (perk == null) continue;
                            if (!firstP) sb.Append(',');
                            firstP = false;
                            string pname = null;
                            try { pname = perk.name; } catch { }
                            sb.Append("{\"id\":").Append(perk.perkId).Append(',');
                            sb.Append("\"name\":\"").Append(MiniJson.Escape(pname ?? $"词条{perk.perkId}")).Append("\",");
                            sb.Append("\"owned\":").Append(owner.Contains(perk.perkId) ? "true" : "false").Append(',');
                            sb.Append("\"hp\":").Append(perk.hpGain.ToString("0.#")).Append(',');
                            sb.Append("\"gunDmg\":").Append(perk.gunDamageGain.ToString("0.#")).Append(',');
                            sb.Append("\"meleeDmg\":").Append(perk.meleeWeaponDamageGain.ToString("0.#")).Append('}');
                        }
                    }
                    sb.Append("]");
                }
                catch (Exception e)
                {
                    DropOutlinePlugin.L.LogWarning($"[知识] 读词条失败: {e.Message}");
                }
                sb.Append("},");

                // 5) 技能
                sb.Append("\"skills\":{");
                try
                {
                    var gc = GameController.instance;
                    var cd = gc != null && gc.playerCharacter != null ? gc.playerCharacter.characterData : null;
                    if (cd != null)
                    {
                        sb.Append("\"points\":").Append(cd.characterSkillPoint).Append(',');
                        sb.Append("\"attrPoints\":").Append(cd.characterAttrPoint).Append(',');
                        sb.Append("\"perkPoints\":").Append(cd.characterPerkPoint).Append(',');
                        sb.Append("\"unlocked\":").Append(cd.unlockedSkillNodeList != null ? cd.unlockedSkillNodeList.Count : 0).Append(',');
                        sb.Append("\"entries\":[");
                        bool firstS = true;
                        if (cd.skillDataList != null)
                        {
                            for (int i = 0; i < cd.skillDataList.Count; i++)
                            {
                                var s = cd.skillDataList[i];
                                if (s == null) continue;
                                if (!firstS) sb.Append(',');
                                firstS = false;
                                sb.Append("{\"id\":").Append(s.skillID).Append(",\"exp\":").Append(s.skillExperience.ToString("0")).Append('}');
                            }
                        }
                        sb.Append("]");
                    }
                }
                catch { }
                sb.Append("}}");

                var path = System.IO.Path.Combine(dir, "ai-knowledge.json");
                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[知识] 写文件失败: {e.Message}");
            }
        }
    }
}

