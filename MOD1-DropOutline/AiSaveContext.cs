using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dropoutline
{
    /// <summary>Read-only game identity. Never writes to GameData or the game's saves.
    ///
    /// The conversation follows the character card, which is the only thing the
    /// game never clears: deleting progress also clears the map (GameDataManager.
    /// ClearInvalidMapData drops map data no save references any more), so both
    /// the save and the map are disposable. The character card survives, and a
    /// new world made with that same card continues the same conversation.
    /// </summary>
    internal static class AiSaveContext
    {
        public static bool Active { get; private set; }
        /// <summary>Conversation identity: hash of the character card's GUID.</summary>
        public static string SaveId { get; private set; } = "";
        /// <summary>Legacy identity: hash of the save's gameId + createdDateTime. Read-only compatibility.</summary>
        public static string LegacyId { get; private set; } = "";
        public static string SaveName { get; private set; } = "";
        /// <summary>Current map only; it is replaced whenever progress is cleared.</summary>
        public static string MapId { get; private set; } = "";
        public static string MapName { get; private set; } = "";
        public static string CharacterId { get; private set; } = "";
        public static string Epoch { get; private set; } = Guid.NewGuid().ToString("N");
        public static bool IsNew { get; private set; }
        private static string _gate = "";
        private static IntPtr _dataPointer;

        public static bool Refresh(bool worldPresent, string gate)
        {
            string id = "", legacy = "", name = "", mapId = "", mapName = "", characterId = "";
            bool isNew = false;
            IntPtr pointer = IntPtr.Zero;
            try
            {
                var gc = worldPresent ? GameController.instance : null;
                // Unity's own null check also reports a destroyed object, which is
                // exactly the state a half-unloaded world leaves behind.
                var data = worldPresent && gc != null && gc.gameObject != null && gc.playerCharacter != null ? gc.gameData : null;
                if (data != null && !string.IsNullOrWhiteSpace(data.id))
                {
                    var map = data.mapConfigData;
                    var character = data.playerData;
                    mapId = map != null ? map.mapId ?? "" : "";
                    mapName = map != null ? Clean(map.mapName) : "";
                    characterId = character != null ? character.characterId ?? "" : "";
                    legacy = LegacyFor(data.id, data.createdDateTime);
                    pointer = data.Pointer;
                    AiSaveLifecycle.Observe(characterId);
                    if (characterId.Length > 0)
                    {
                        // The card is the anchor: progress and map are disposable.
                        id = IdentityFor(characterId);
                        name = DisplayName(character != null ? character.characterName : null, characterId, mapName);
                    }
                    else
                    {
                        // No card to anchor on: keep working on the progress identity.
                        id = legacy;
                        name = "存档 " + (data.saveSlotIndex + 1) + " · " + data.id.Substring(0, Math.Min(8, data.id.Length));
                    }
                    // Conservative: first bridge contact is NOT proof of a newly-created game.
                    // createdDateTime is written by the game as a Windows FILETIME
                    // (UtcNow.ToFileTime), not as .NET ticks. Accept either encoding,
                    // and treat any value that is neither as unknown.
                    try
                    {
                        DateTime created;
                        if (TryDecodeCreated(data.createdDateTime, out created))
                        {
                            var age = DateTime.Now - created;
                            isNew = data.playTime < 120f && age.TotalSeconds >= 0 && age.TotalMinutes < 5;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            bool active = id.Length > 0;
            bool changed = active != Active || id != SaveId || gate != _gate || (active && pointer != _dataPointer);
            if (!changed) return false;
            Active = active; SaveId = id; SaveName = name; IsNew = isNew;
            LegacyId = legacy; MapId = mapId; MapName = mapName; CharacterId = characterId;
            _dataPointer = pointer; _gate = gate;
            // A new world instance (same character, new progress or map) is its own epoch.
            Epoch = Guid.NewGuid().ToString("N");
            return true;
        }

        private static string DisplayName(string characterName, string characterId, string mapName)
        {
            string who = Clean(characterName);
            if (who.Length == 0) who = "角色 " + characterId.Substring(0, Math.Min(8, characterId.Length));
            return mapName.Length == 0 ? who : who + " · " + mapName;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var text = value.Trim();
            foreach (var c in new[] { '\r', '\n', '\t' }) text = text.Replace(c, ' ');
            return text.Length <= 40 ? text : text.Substring(0, 40);
        }

        /// <summary>
        /// createdDateTime 可能是 Windows FILETIME（游戏实测写法）也可能是 .NET ticks。
        /// 两种编码都能被对方的解码器"成功"解析成离谱年份，所以不能靠异常区分，
        /// 而是只接受落在 2000–2100 年之间的那个解释；都不合理就当未知。
        /// </summary>
        internal static bool TryDecodeCreated(long raw, out DateTime created)
        {
            created = default(DateTime);
            if (raw <= 0) return false;
            var min = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var max = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            try
            {
                var asFileTime = DateTime.FromFileTimeUtc(raw);
                if (asFileTime >= min && asFileTime < max) { created = asFileTime.ToLocalTime(); return true; }
            }
            catch { }
            try
            {
                if (raw <= DateTime.MaxValue.Ticks)
                {
                    var asTicks = new DateTime(raw, DateTimeKind.Local);
                    if (asTicks.ToUniversalTime() >= min && asTicks.ToUniversalTime() < max) { created = asTicks; return true; }
                }
            }
            catch { }
            return false;
        }

        internal static string IdentityFor(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return "";
            var identity = "character|" + characterId;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        }

        /// <summary>Pre-1.2.4 identity, kept so an existing conversation can be adopted once.</summary>
        internal static string LegacyFor(string gameId, long createdDateTime)
        {
            if (string.IsNullOrWhiteSpace(gameId) || createdDateTime <= 0) return "";
            var identity = gameId + "|" + createdDateTime.ToString(CultureInfo.InvariantCulture);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        }

        public static bool Matches(string saveId, string epoch) => Active &&
            !string.IsNullOrEmpty(saveId) && saveId == SaveId && !string.IsNullOrEmpty(epoch) && epoch == Epoch;
    }
}
