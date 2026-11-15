using System;
using System.Collections.Generic;
using System.Text;

namespace Dropoutline
{
    /// <summary>容器里一件物品的快照（由适配层从游戏读取）。</summary>
    internal struct SortEntry
    {
        public object Handle;
        public int ItemId;
        public float Quantity;
        public string Category;
        public string Name;
    }

    /// <summary>
    /// 容器世界的最小接口。生产实现包一层游戏 InventoryData，测试用假实现。
    /// 核心逻辑只通过它读写，所以能离线测试"叠堆合并 / 部分合并 / 满箱 / 中转"。
    /// </summary>
    internal interface ISortWorld
    {
        int BoxCount { get; }
        string BoxName(int box);
        bool Locked(int box);
        List<SortEntry> Items(int box);
        float Quantity(int box, int itemId);
        bool Contains(int box, object handle);
        bool TryAdd(int box, object handle);
        void Remove(int box, object handle);
    }

    internal enum MoveOutcome { Ok, Partial, NoSpace, Gone, Locked, Violation }

    internal sealed class SortResult
    {
        public int Moved, Partial, Skipped, Evicted;
        public bool Stopped;
        public string StopReason = "";
        public readonly List<string> Steps = new List<string>();
    }

    /// <summary>
    /// 容器归并的纯逻辑核心。
    ///
    /// 规划：每类物品的归属箱 = 当前该类件数最多的容器（不选缓冲区）；
    ///       空容器作为缓冲区，目标箱放不下时先把箱里"不属于这里"的物品挪进缓冲区。
    /// 执行：每一步都是 RawMove —— 先 TryAdd 进目标，只有"同一对象同时出现在两边"
    ///       才从源移除；无论成功失败都校验该物品在源+目标的总量守恒。
    /// 铁律：任何守恒破坏立即停手，不做任何补偿写入（2026-11-03 铁丝复制事故的教训）。
    /// </summary>
    internal static class ContainerSortCore
    {
        public const float Epsilon = 0.001f;

        /// <summary>选缓冲区：第一个空容器；没有就返回 -1。</summary>
        public static int FindBuffer(ISortWorld world)
        {
            for (int b = 0; b < world.BoxCount; b++)
                if (world.Items(b).Count == 0) return b;
            return -1;
        }

        /// <summary>每类的归属箱：该类件数最多的容器，平局取序号小的；缓冲区不参选。</summary>
        public static Dictionary<string, int> Assign(ISortWorld world, int buffer)
        {
            var counts = new Dictionary<string, int[]>();
            for (int b = 0; b < world.BoxCount; b++)
            {
                if (b == buffer) continue;
                foreach (var e in world.Items(b))
                {
                    var key = e.Category ?? "其他";
                    int[] perBox;
                    if (!counts.TryGetValue(key, out perBox)) { perBox = new int[world.BoxCount]; counts[key] = perBox; }
                    perBox[b]++;
                }
            }
            var result = new Dictionary<string, int>();
            foreach (var pair in counts)
            {
                int best = -1, bestCount = 0;
                for (int b = 0; b < pair.Value.Length; b++)
                    if (pair.Value[b] > bestCount) { bestCount = pair.Value[b]; best = b; }
                if (best >= 0) result[pair.Key] = best;
            }
            return result;
        }

        /// <summary>搬一件，返回结果。只有 Violation 要求调用方立即停手。</summary>
        public static MoveOutcome RawMove(ISortWorld world, int from, int to, SortEntry entry, out string detail)
        {
            detail = "";
            if (from == to) { detail = "同一容器"; return MoveOutcome.Gone; }
            if (world.Locked(from) || world.Locked(to)) { detail = "容器被锁定"; return MoveOutcome.Locked; }
            if (!world.Contains(from, entry.Handle)) { detail = "源里已经没有这件"; return MoveOutcome.Gone; }

            float before = world.Quantity(from, entry.ItemId) + world.Quantity(to, entry.ItemId);
            float fromBefore = world.Quantity(from, entry.ItemId);
            bool added = world.TryAdd(to, entry.Handle);
            bool inFrom = world.Contains(from, entry.Handle);
            bool inTo = world.Contains(to, entry.Handle);
            // 只有同一对象同时挂在两边，才说明游戏只"加"没"摘"，这时才移除源。
            // 叠堆合并（对象被并掉）或部分合并（对象仍在源、数量变少）都不能再 Remove。
            if (added && inFrom && inTo)
            {
                world.Remove(from, entry.Handle);
                inFrom = world.Contains(from, entry.Handle);
            }
            float after = world.Quantity(from, entry.ItemId) + world.Quantity(to, entry.ItemId);
            if (Math.Abs(before - after) > Epsilon)
            {
                detail = "总量 " + Fmt(before) + "→" + Fmt(after);
                return MoveOutcome.Violation;
            }
            if (inFrom && world.Contains(to, entry.Handle))
            {
                detail = "同一件同时在两个容器里";
                return MoveOutcome.Violation;
            }
            if (!inFrom) return MoveOutcome.Ok;
            float fromAfter = world.Quantity(from, entry.ItemId);
            if (fromAfter < fromBefore - Epsilon) { detail = "只并进去一部分"; return MoveOutcome.Partial; }
            detail = "目标放不下";
            return MoveOutcome.NoSpace;
        }

        /// <summary>只出方案，不写任何数据。</summary>
        public static string Describe(ISortWorld world, int maxDetails = 8)
        {
            int buffer = FindBuffer(world);
            var assign = Assign(world, buffer);
            if (assign.Count == 0) return "视野内的容器都是空的";
            var sb = new StringBuilder("目标分布：");
            foreach (var pair in assign) sb.Append(pair.Key).Append("→").Append(world.BoxName(pair.Value)).Append("；");
            if (buffer >= 0) sb.Append("中转箱：").Append(world.BoxName(buffer)).Append("；");
            int misplaced = 0, shown = 0;
            var details = new StringBuilder();
            for (int b = 0; b < world.BoxCount; b++)
            {
                foreach (var e in world.Items(b))
                {
                    int home;
                    if (!assign.TryGetValue(e.Category ?? "其他", out home) || home == b) continue;
                    misplaced++;
                    if (shown < maxDetails)
                    {
                        if (shown > 0) details.Append("、");
                        details.Append(e.Name).Append(' ').Append(world.BoxName(b)).Append("→").Append(world.BoxName(home));
                        shown++;
                    }
                }
            }
            if (misplaced == 0) return sb.Append("已经归好了，不用搬").ToString();
            sb.Append("需要搬 ").Append(misplaced).Append(" 件");
            if (details.Length > 0) sb.Append("。例如：").Append(details).Append(misplaced > shown ? "…" : "");
            return sb.ToString();
        }

        /// <summary>
        /// 执行归并。testOnly=true 时只做第一步搬运（用于验证游戏接口语义）。
        /// maxMoves 计的是实际写入次数（含挪进缓冲区）。
        /// </summary>
        public static SortResult Run(ISortWorld world, int maxMoves, bool testOnly)
        {
            var result = new SortResult();
            int buffer = FindBuffer(world);
            var assign = Assign(world, buffer);
            if (assign.Count == 0) { result.StopReason = "视野内的容器都是空的"; return result; }
            int writes = 0;
            int limit = testOnly ? 1 : Math.Max(1, maxMoves);

            // 最多几轮：每轮先处理非缓冲区的错位物品，最后把缓冲区清回各自归属箱
            for (int round = 0; round < 4 && !result.Stopped && writes < limit; round++)
            {
                bool progress = false;
                var order = new List<int>();
                for (int b = 0; b < world.BoxCount; b++) if (b != buffer) order.Add(b);
                if (buffer >= 0) order.Add(buffer);

                foreach (int b in order)
                {
                    if (result.Stopped || writes >= limit) break;
                    foreach (var e in world.Items(b))
                    {
                        if (result.Stopped || writes >= limit) break;
                        int home;
                        if (!assign.TryGetValue(e.Category ?? "其他", out home) || home == b) continue;
                        string detail;
                        var outcome = RawMove(world, b, home, e, out detail);
                        writes++;
                        if (Handle(result, outcome, e, b, home, detail, world)) progress |= outcome == MoveOutcome.Ok || outcome == MoveOutcome.Partial;
                        if (result.Stopped) break;

                        // 目标放不下：把目标箱里一件"不属于这里"的东西挪进缓冲区，再试一次
                        if (outcome == MoveOutcome.NoSpace && buffer >= 0 && home != buffer && writes < limit)
                        {
                            SortEntry evict;
                            if (FindForeign(world, home, assign, out evict))
                            {
                                var evictOutcome = RawMove(world, home, buffer, evict, out detail);
                                writes++;
                                if (evictOutcome == MoveOutcome.Violation) { Stop(result, evict, detail); break; }
                                if (evictOutcome == MoveOutcome.Ok || evictOutcome == MoveOutcome.Partial)
                                {
                                    result.Evicted++;
                                    result.Steps.Add(evict.Name + " " + world.BoxName(home) + "→" + world.BoxName(buffer) + "（腾位置）");
                                    if (writes < limit)
                                    {
                                        var retry = RawMove(world, b, home, e, out detail);
                                        writes++;
                                        if (Handle(result, retry, e, b, home, detail, world))
                                            progress |= retry == MoveOutcome.Ok || retry == MoveOutcome.Partial;
                                    }
                                    progress = true;
                                }
                            }
                        }
                    }
                }
                if (!progress) break;
            }
            return result;
        }

        private static bool Handle(SortResult result, MoveOutcome outcome, SortEntry e, int from, int to, string detail, ISortWorld world)
        {
            switch (outcome)
            {
                case MoveOutcome.Ok:
                    result.Moved++;
                    result.Steps.Add(e.Name + " " + world.BoxName(from) + "→" + world.BoxName(to));
                    return true;
                case MoveOutcome.Partial:
                    result.Partial++;
                    result.Steps.Add(e.Name + " " + world.BoxName(from) + "→" + world.BoxName(to) + "（部分）");
                    return true;
                case MoveOutcome.Violation:
                    Stop(result, e, detail);
                    return false;
                default:
                    result.Skipped++;
                    return false;
            }
        }

        private static void Stop(SortResult result, SortEntry e, string detail)
        {
            result.Stopped = true;
            result.StopReason = e.Name + " " + detail;
        }

        /// <summary>目标箱里找一件归属不是它的物品（用来腾位置）。</summary>
        private static bool FindForeign(ISortWorld world, int box, Dictionary<string, int> assign, out SortEntry foreign)
        {
            foreach (var e in world.Items(box))
            {
                int home;
                if (assign.TryGetValue(e.Category ?? "其他", out home) && home != box) { foreign = e; return true; }
            }
            foreign = default(SortEntry);
            return false;
        }

        /// <summary>把结果写成一句给玩家看的话。</summary>
        public static string Summarize(SortResult r, bool testOnly)
        {
            if (r.Stopped)
                return "停手了：" + r.StopReason + "。已搬 " + r.Moved + " 件，没做任何补偿写入，建议先别存档并把日志发给作者";
            if (testOnly)
                return r.Moved + r.Partial > 0 ? "试搬 1 件：" + (r.Steps.Count > 0 ? r.Steps[r.Steps.Count - 1] : "") + "，总量守恒已核对"
                                               : "试搬没有成功（" + (r.Skipped > 0 ? "目标放不下或物品已变动" : "无需搬运") + "），没有改动任何东西";
            var sb = new StringBuilder();
            sb.Append("归好了 ").Append(r.Moved).Append(" 件");
            if (r.Partial > 0) sb.Append("，").Append(r.Partial).Append(" 件只并进去一部分");
            if (r.Evicted > 0) sb.Append("，挪了 ").Append(r.Evicted).Append(" 件进中转箱腾位置");
            if (r.Skipped > 0) sb.Append("，").Append(r.Skipped).Append(" 件放不下没动");
            sb.Append("，每一步都核对过总量");
            return sb.ToString();
        }

        private static string Fmt(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}
