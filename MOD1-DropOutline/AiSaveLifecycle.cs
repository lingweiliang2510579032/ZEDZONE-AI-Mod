using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Dropoutline
{
    // Positive native deletion receipts only. Never infer deletion from world state,
    // inactivity, failed loads, missing directories or a changed data root.
    //
    // A save file only holds progress, and clearing progress also drops the map
    // (GameDataManager.ClearInvalidMapData), so neither can end a conversation.
    // The character card is the only anchor we bind to, and therefore the only
    // deletion that produces a tombstone.
    internal static class AiSaveLifecycle
    {
        internal sealed class Known { public string SaveId, Root, CharacterId; }
        internal sealed class Deleted { public string SaveId, EventId, DeletedAtUtc, AnchorId; }
        private enum Field { Absent, Text, Other }
        internal sealed class Receipt
        {
            public string Root, JournalPath, AnchorId;
            public long RootCreated;
            public Known[] Identities;
        }
        private static readonly List<Known> KnownAnchors = new();
        private static readonly List<Deleted> Events = new();
        private static string _loadedPath = "";
        private static bool _readable, _dirty;
        internal static string FilePath
        {
            get
            {
                try { return Path.Combine(Path.GetDirectoryName(AiCompanion.StateFilePath) ?? "", "ai-save-lifecycle.json"); }
                catch { return ""; }
            }
        }
        /// <summary>The game's data root: it holds GameData, MapData and MpCharacters.</summary>
        private static string Root
        {
            get
            {
                try
                {
                    var saving = Path.GetFullPath(GameConfigs.gameDataSavingPath);
                    var parent = Path.GetDirectoryName(saving);
                    return string.IsNullOrEmpty(parent) ? "" : Canonical(parent);
                }
                catch { return ""; }
            }
        }
        private static string CharacterDir => Root.Length == 0 ? "" : Path.Combine(Root, "MpCharacters");
        private static bool _singleCardCached;
        private static float _singleCardAt = -999f;
        private static bool _singleCardValue;
        private static string _singleCardDir = "";
        /// <summary>角色卡数量可能刚变（删卡）：丢弃缓存，下一次读取重新扫目录。</summary>
        internal static void InvalidateCardCache() { _singleCardCached = false; }
        /// <summary>
        /// True when the game holds exactly one character card. Only then do all
        /// retired progress conversations provably belong to that character, which
        /// is what lets the bridge adopt the most recent one instead of guessing.
        /// Cached for 10 s: this used to enumerate the directory twice a second on
        /// the main thread, which is pointless disk I/O inside the render loop.
        /// </summary>
        internal static bool SingleCharacterCard
        {
            get
            {
                try
                {
                    float now = UnityEngine.Time.unscaledTime;
                    var dir = CharacterDir;
                    // 缓存只对同一个目录有效：存档根变了必须重扫，不能沿用旧答案
                    if (_singleCardCached && dir == _singleCardDir && now - _singleCardAt < 10f) return _singleCardValue;
                    _singleCardCached = true;
                    _singleCardAt = now;
                    _singleCardDir = dir;
                    _singleCardValue = dir.Length != 0 && Directory.Exists(dir)
                        && Directory.GetFiles(dir, "*.mpchar", SearchOption.TopDirectoryOnly).Length == 1;
                    return _singleCardValue;
                }
                catch { _singleCardCached = false; return false; }
            }
        }
        private static bool SafeId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 200 && id != "." && id != ".." && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && id.IndexOfAny(new[] { '/', '\\' }) < 0;
        private static string Canonical(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private static string S(object value) => value as string ?? "";

        private static void Load()
        {
            string path = FilePath;
            if (path.Length == 0 || path == _loadedPath) return;
            _loadedPath = path; _readable = false; _dirty = false; KnownAnchors.Clear(); Events.Clear();
            try
            {
                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent)) return;
                var entries = Directory.GetFiles(parent); // distinguish inaccessible from absent
                if (!entries.Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) { _readable = true; return; }
                var obj = MiniJson.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                if (obj == null || !obj.TryGetValue("schemaVersion", out var version) || Convert.ToDouble(version) != 1 ||
                    !obj.TryGetValue("knownSaves", out var knownValue) || !(knownValue is List<object> known) ||
                    !obj.TryGetValue("events", out var eventsValue) || !(eventsValue is List<object> events)) return;
                // A journal written by the save-bound era describes progress
                // identities that no longer exist. It parses, so it is not
                // corrupt evidence: rewrite it empty instead of staying dead
                // forever, and let the next world re-observe its own character.
                // NOTE: a Dictionary indexer throws for a *missing* key, so the
                // old-schema marker is detected with TryGetValue, not `?[key]`.
                // Legacy means "parses and has every OLD required field, but no
                // new field"; anything else malformed stays fail-closed.
                bool Has(Dictionary<string, object> row, string key) => row.TryGetValue(key, out var value) && S(value).Length > 0;
                // Present-but-malformed is not the same as absent: only a key that
                // is truly missing, or a non-empty string, describes a marker.
                Field Marker(Dictionary<string, object> row, string key)
                {
                    if (!row.TryGetValue(key, out var value)) return Field.Absent;
                    return value is string text && text.Length > 0 ? Field.Text : Field.Other;
                }
                // An empty journal needs no migration: only a file with rows can
                // be shown to be the old save-bound format.
                bool legacy = known.Count + events.Count > 0;
                foreach (var value in known)
                {
                    if (!(value is Dictionary<string, object> row)) return;   // malformed row: fail closed
                    if (!Has(row, "saveId") || !Has(row, "root")) return;
                    // A marker present but not a non-empty string is malformed, not legacy.
                    switch (Marker(row, "characterId"))
                    {
                        case Field.Absent: break;
                        case Field.Text: legacy = false; break;
                        default: return;
                    }
                }
                foreach (var value in events)
                {
                    if (!(value is Dictionary<string, object> row)) return;   // malformed row: fail closed
                    if (!Has(row, "type") || !Has(row, "saveId") || !Has(row, "eventId") || !Has(row, "deletedAtUtc")) return;
                    switch (Marker(row, "anchorId"))
                    {
                        case Field.Absent: break;
                        case Field.Text: legacy = false; break;
                        default: return;
                    }
                }
                if (legacy)
                {
                    // Keep the old evidence beside the new journal, then start clean.
                    try { File.Copy(path, path + ".legacy", true); } catch { }
                    _readable = true; _dirty = true; Save(); return;
                }
                foreach (var value in known)
                {
                    if (!(value is Dictionary<string, object> row)) return;
                    string characterId = S(row["characterId"]);
                    string saveId = S(row["saveId"]), root = S(row["root"]);
                    if (!SafeId(characterId)) return;
                    if (saveId != AiSaveContext.IdentityFor(characterId)) return;
                    if (root.Length == 0 || Canonical(root) != root) return;
                    KnownAnchors.Add(new Known { CharacterId = characterId, SaveId = saveId, Root = root });
                }
                foreach (var value in events)
                {
                    if (!(value is Dictionary<string, object> row) || S(row["type"]) != "deleted") return;
                    string saveId = S(row["saveId"]), eventId = S(row["eventId"]), utc = S(row["deletedAtUtc"]);
                    string anchorId = S(row["anchorId"]);
                    // `anchor` names which anchor kind a tombstone is about; only
                    // the character card can end a conversation.
                    if (!row.TryGetValue("anchor", out var anchor) || S(anchor) != "character") return;
                    if (saveId.Length != 64 || !Guid.TryParseExact(eventId, "N", out _) || !DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) return;
                    if (!SafeId(anchorId)) return;
                    Events.Add(new Deleted { SaveId = saveId, EventId = eventId, DeletedAtUtc = utc, AnchorId = anchorId });
                }
                _readable = true;
            }
            catch { KnownAnchors.Clear(); Events.Clear(); }
        }

        /// <summary>Remember the character card seen in a running world.</summary>
        internal static void Observe(string characterId)
        {
            try
            {
                Load(); if (!_readable || !SafeId(characterId)) return;
                string root = Root;
                if (root.Length == 0) return;
                string saveId = AiSaveContext.IdentityFor(characterId);
                if (saveId.Length == 0) return;
                if (KnownAnchors.Any(x => x.Root == root && x.SaveId == saveId)) return;
                // Complete enumeration must succeed; Exists(false) alone can mean an I/O error.
                Directory.GetFiles(root);
                KnownAnchors.Add(new Known { Root = root, CharacterId = characterId, SaveId = saveId }); _dirty = true; Save();
            }
            catch { }
        }

        internal static Receipt BeforeDeleteCharacter(string characterId)
        {
            try
            {
                Load(); if (!_readable || !SafeId(characterId)) return null;
                string root = Root;
                if (root.Length == 0) return null;
                string dir = CharacterDir;
                if (dir.Length == 0 || !Directory.Exists(dir)) return null;
                // The card must exist before the delete: otherwise "gone afterwards"
                // proves nothing at all.
                if (!TryCharacterFiles(dir, characterId, out var before) || before.Length == 0) return null;
                var identities = KnownAnchors.Where(x => x.Root == root && x.CharacterId == characterId
                    && !Events.Any(e => e.SaveId == x.SaveId)).ToArray();
                if (identities.Length == 0) return null;
                return new Receipt { Root = root, JournalPath = FilePath, AnchorId = characterId, RootCreated = Directory.GetCreationTimeUtc(root).Ticks, Identities = identities };
            }
            catch { return null; }
        }
        /// <summary>
        /// List a card's files. Returns false when the answer is unknown (directory
        /// missing or unreadable) so a failed listing is never mistaken for proof
        /// that the card is gone.
        /// </summary>
        private static bool TryCharacterFiles(string dir, string characterId, out string[] files)
        {
            files = Array.Empty<string>();
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                files = Directory.GetFiles(dir, characterId + ".*", SearchOption.TopDirectoryOnly);
                return true;
            }
            catch { return false; }
        }

        internal static void AfterDeleteCharacter(Receipt receipt)
        {
            InvalidateCardCache();
            if (receipt == null) return;
            try
            {
                Load();
                if (!_readable || FilePath != receipt.JournalPath || Root != receipt.Root) return;
                if (Directory.GetCreationTimeUtc(receipt.Root).Ticks != receipt.RootCreated) return;
                // Only a successful, empty listing proves the card is gone; an
                // unreadable directory proves nothing and must not archive.
                if (!TryCharacterFiles(CharacterDir, receipt.AnchorId, out var remaining)) return;
                // A delete call that left the card in place changed nothing.
                if (remaining.Length > 0) return;
                bool added = false;
                foreach (var identity in receipt.Identities)
                {
                    if (Events.Any(e => e.SaveId == identity.SaveId && e.AnchorId == receipt.AnchorId)) continue;
                    Events.Add(new Deleted
                    {
                        SaveId = identity.SaveId, AnchorId = receipt.AnchorId,
                        EventId = Guid.NewGuid().ToString("N"), DeletedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                    });
                    added = true;
                }
                if (added) { _dirty = true; Save(); }
            }
            catch { }
        }

        internal static void Tick() { try { Load(); if (_readable && _dirty) Save(); } catch { } }

        private static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        private static void Save()
        {
            if (!_readable || !_dirty) return;
            var b = new StringBuilder("{\"schemaVersion\":1,\"events\":[");
            for (int i = 0; i < Events.Count; i++)
            {
                var e = Events[i]; if (i > 0) b.Append(',');
                b.Append("{\"type\":\"deleted\",\"saveId\":").Append(Q(e.SaveId))
                 .Append(",\"anchor\":\"character\"")
                 .Append(",\"anchorId\":").Append(Q(e.AnchorId))
                 .Append(",\"eventId\":").Append(Q(e.EventId))
                 .Append(",\"deletedAtUtc\":").Append(Q(e.DeletedAtUtc)).Append('}');
            }
            b.Append("],\"knownSaves\":[");
            for (int i = 0; i < KnownAnchors.Count; i++)
            {
                var k = KnownAnchors[i]; if (i > 0) b.Append(',');
                b.Append("{\"saveId\":").Append(Q(k.SaveId))
                 .Append(",\"characterId\":").Append(Q(k.CharacterId))
                 .Append(",\"root\":").Append(Q(k.Root)).Append('}');
            }
            b.Append("]}");
            string path = FilePath, tmp = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(tmp, b.ToString(), new UTF8Encoding(false));
            File.Move(tmp, path, true); _dirty = false;
        }
        // Test reset has no production filesystem side effects.
        internal static void ResetForTests() { _loadedPath = ""; _readable = _dirty = false; KnownAnchors.Clear(); Events.Clear(); }
    }
}
