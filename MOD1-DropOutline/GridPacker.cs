using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Dropoutline
{
    // Native-free bounded rectangle packing. The original valid layout is always
    // a candidate: a timeout or heuristic miss can never degrade the layout.
    internal static class GridPacker
    {
        internal sealed class Item
        {
            public int Id, Width, Height, X, Y;
            public bool Rotated, Fixed;
            public Item Copy() => (Item)MemberwiseClone();
        }
        internal sealed class Score
        {
            public int LargestEmpty, FreeComponents, OccupiedRows, Moves;
        }
        internal sealed class Result
        {
            public bool Valid, Improved, Exhausted;
            public string Error = "";
            public List<Item> Items;
            public Score Before, After;
            public int Candidates;
        }
        internal static int ActualWidth(Item item) => item.Rotated ? item.Height : item.Width;
        internal static int ActualHeight(Item item) => item.Rotated ? item.Width : item.Height;
        internal static bool IsBetter(Score a, Score b)
        {
            if (a.LargestEmpty != b.LargestEmpty) return a.LargestEmpty > b.LargestEmpty;
            if (a.FreeComponents != b.FreeComponents) return a.FreeComponents < b.FreeComponents;
            if (a.OccupiedRows != b.OccupiedRows) return a.OccupiedRows < b.OccupiedRows;
            return a.Moves < b.Moves;
        }
        internal static bool Validate(int width, int height, IList<Item> items, out string error)
        {
            error = "";
            if (width <= 0 || height <= 0 || width > 64 || height > 64 || width * height > 2048 || items == null || items.Count > 128)
            { error = "容器尺寸或物品数量超出安全计算上限"; return false; }
            var grid = new bool[width * height];
            var ids = new HashSet<int>();
            foreach (var item in items)
            {
                if (item == null || item.Width <= 0 || item.Height <= 0 || item.Width > 64 || item.Height > 64 || !ids.Add(item.Id))
                { error = "物品尺寸无效或标识重复"; return false; }
                int w = ActualWidth(item), h = ActualHeight(item);
                if (!Fits(grid, width, height, item.X, item.Y, w, h))
                { error = "原布局存在越界或同格物品组；为避免破坏，不自动整理"; return false; }
                Mark(grid, width, item.X, item.Y, w, h, true);
            }
            return true;
        }
        internal static Result Optimize(int width, int height, IList<Item> source, int milliseconds = 8, int maxCandidates = 32)
        {
            var result = new Result();
            if (!Validate(width, height, source, out result.Error)) return result;
            result.Valid = true;
            result.Items = Clone(source);
            result.Before = Evaluate(width, height, source, source);
            result.After = result.Before;
            var watch = Stopwatch.StartNew();
            int budget = Math.Max(1, Math.Min(20, milliseconds));
            int candidateLimit = Math.Max(1, Math.Min(128, maxCandidates));
            for (int attempt = 0; attempt < candidateLimit; attempt++)
            {
                if (watch.ElapsedMilliseconds >= budget) { result.Exhausted = true; break; }
                var candidate = Greedy(width, height, source, attempt, watch, budget);
                if (candidate == null) continue;
                result.Candidates++;
                var score = Evaluate(width, height, candidate, source);
                if (IsBetter(score, result.After)) { result.Items = candidate; result.After = score; result.Improved = true; }
            }
            return result;
        }
        private static List<Item> Clone(IList<Item> source)
        { var copy = new List<Item>(source.Count); foreach (var item in source) copy.Add(item.Copy()); return copy; }
        private static List<Item> Greedy(int width, int height, IList<Item> source, int attempt, Stopwatch watch, int budget)
        {
            var result = Clone(source);
            var grid = new bool[width * height];
            var order = new List<Item>();
            foreach (var item in result)
            {
                if (item.Fixed) Mark(grid, width, item.X, item.Y, ActualWidth(item), ActualHeight(item), true);
                else order.Add(item);
            }
            int orderKind = attempt % 6, variant = attempt / 6;
            order.Sort((a, b) => {
                int cmp;
                switch (orderKind)
                {
                    case 0: cmp = (b.Width * b.Height).CompareTo(a.Width * a.Height); break;
                    case 1: cmp = Math.Max(b.Width, b.Height).CompareTo(Math.Max(a.Width, a.Height)); break;
                    case 2: cmp = Math.Min(b.Width, b.Height).CompareTo(Math.Min(a.Width, a.Height)); break;
                    case 3: cmp = (b.Width + b.Height).CompareTo(a.Width + a.Height); break;
                    case 4: cmp = b.Height.CompareTo(a.Height); break;
                    default: cmp = b.Width.CompareTo(a.Width); break;
                }
                if (cmp == 0) cmp = (b.Width * b.Height).CompareTo(a.Width * a.Height);
                return cmp == 0 ? a.Id.CompareTo(b.Id) : cmp;
            });
            foreach (var item in order)
            {
                if (watch.ElapsedMilliseconds >= budget) return null;
                int bestX = -1, bestY = -1, bestValue = int.MaxValue;
                bool bestRotated = false;
                for (int rotation = 0; rotation < (item.Width == item.Height ? 1 : 2); rotation++)
                {
                    bool rotated = rotation == 1;
                    int w = rotated ? item.Height : item.Width, h = rotated ? item.Width : item.Height;
                    for (int y = 0; y <= height - h; y++)
                    for (int x = 0; x <= width - w; x++)
                    {
                        if (((y * width + x) & 127) == 0 && watch.ElapsedMilliseconds >= budget) return null;
                        if (!Fits(grid, width, height, x, y, w, h)) continue;
                        int contact = Contact(grid, width, height, x, y, w, h);
                        // Several wall/corner strategies intentionally compete. No
                        // type categories: arranging by type can waste rectangles.
                        int axis = variant % 4;
                        int primary = axis == 0 ? y + h : axis == 1 ? x + w : axis == 2 ? height - y : width - x;
                        int secondary = axis == 0 ? x : axis == 1 ? y : axis == 2 ? x : y;
                        int value = primary * 4096 + secondary * 64 - contact * 4;
                        if (value < bestValue)
                        { bestX = x; bestY = y; bestRotated = rotated; bestValue = value; }
                    }
                    if (watch.ElapsedMilliseconds >= budget) return null;
                }
                if (bestX < 0) return null;
                item.X = bestX; item.Y = bestY; item.Rotated = bestRotated;
                Mark(grid, width, item.X, item.Y, ActualWidth(item), ActualHeight(item), true);
            }
            return result;
        }
        private static int Contact(bool[] grid, int width, int height, int x, int y, int w, int h)
        {
            int value = 0;
            for (int i = 0; i < w; i++)
            { if (y == 0 || grid[(y - 1) * width + x + i]) value++; if (y + h == height || grid[(y + h) * width + x + i]) value++; }
            for (int i = 0; i < h; i++)
            { if (x == 0 || grid[(y + i) * width + x - 1]) value++; if (x + w == width || grid[(y + i) * width + x + w]) value++; }
            return value;
        }
        private static bool Fits(bool[] grid, int width, int height, int x, int y, int w, int h)
        {
            if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > width || y + h > height) return false;
            for (int yy = y; yy < y + h; yy++) for (int xx = x; xx < x + w; xx++) if (grid[yy * width + xx]) return false;
            return true;
        }
        private static void Mark(bool[] grid, int width, int x, int y, int w, int h, bool value)
        { for (int yy = y; yy < y + h; yy++) for (int xx = x; xx < x + w; xx++) grid[yy * width + xx] = value; }
        internal static Score Evaluate(int width, int height, IList<Item> items, IList<Item> original)
        {
            var score = new Score(); var grid = new bool[width * height];
            var originals = new Dictionary<int, Item>(); foreach (var item in original) originals[item.Id] = item;
            foreach (var item in items)
            {
                Mark(grid, width, item.X, item.Y, ActualWidth(item), ActualHeight(item), true);
                var old = originals[item.Id]; if (item.X != old.X || item.Y != old.Y || item.Rotated != old.Rotated) score.Moves++;
            }
            var histogram = new int[width];
            // Largest all-empty rectangle, O(W*H), not bounding-box approximation.
            for (int y = 0; y < height; y++)
            {
                bool occupied = false;
                for (int x = 0; x < width; x++) { bool cell = grid[y * width + x]; occupied |= cell; histogram[x] = cell ? 0 : histogram[x] + 1; }
                if (occupied) score.OccupiedRows++;
                var stack = new int[width + 1]; int count = 0;
                for (int x = 0; x <= width; x++)
                {
                    int current = x == width ? 0 : histogram[x];
                    while (count > 0 && histogram[stack[count - 1]] > current)
                    { int h = histogram[stack[--count]], left = count == 0 ? 0 : stack[count - 1] + 1; score.LargestEmpty = Math.Max(score.LargestEmpty, h * (x - left)); }
                    if (x < width) stack[count++] = x;
                }
            }
            var seen = new bool[grid.Length]; var queue = new int[grid.Length];
            for (int start = 0; start < grid.Length; start++)
            {
                if (grid[start] || seen[start]) continue;
                score.FreeComponents++; int head = 0, tail = 0; queue[tail++] = start; seen[start] = true;
                while (head < tail)
                {
                    int p = queue[head++], x = p % width, y = p / width;
                    if (x > 0) Visit(p - 1); if (x + 1 < width) Visit(p + 1); if (y > 0) Visit(p - width); if (y + 1 < height) Visit(p + width);
                }
                void Visit(int p) { if (!grid[p] && !seen[p]) { seen[p] = true; queue[tail++] = p; } }
            }
            return score;
        }
    }
}
