using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dropoutline
{
    /// <summary>
    /// 10-language localization matching the base game (ZED ZONE):
    /// zh, en, de, es, fr, ja, ko, pl, pt, ru.
    /// Automatically follows GameController.instance.gameSettings at runtime,
    /// with optional manual override via CfgUiLanguage ("auto" | code).
    /// </summary>
    internal static class ModLocalization
    {
        public static readonly string[] SupportedCodes =
        {
            "zh", "en", "de", "es", "fr", "ja", "ko", "pl", "pt", "ru",
        };

        public static readonly string[] UiLangCodes =
        {
            "auto", "zh", "en", "de", "es", "fr", "ja", "ko", "pl", "pt", "ru",
        };

        private static readonly string[] NativeSelfNames =
        {
            "简体中文",
            "English",
            "Deutsch",
            "Español",
            "Français",
            "日本語",
            "한국어",
            "Polski",
            "Português",
            "Русский",
        };

        private static readonly Dictionary<string, int> CodeIndexMap = BuildCodeIndexMap();

        private static string _cachedGameLang = "zh";
        private static float _nextGamePollAt = -999f;

        private static Dictionary<string, int> BuildCodeIndexMap()
        {
            var m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < SupportedCodes.Length; i++)
                m[SupportedCodes[i]] = i;

            m["cn"] = 0;
            m["zh-cn"] = 0;
            m["zh_cn"] = 0;
            m["zh-hans"] = 0;
            m["schinese"] = 0;
            m["simplifiedchinese"] = 0;

            m["en-us"] = 1;
            m["en_us"] = 1;
            m["en-gb"] = 1;
            m["english"] = 1;

            m["de-de"] = 2;
            m["german"] = 2;

            m["es-es"] = 3;
            m["es-419"] = 3;
            m["spanish"] = 3;
            m["latam"] = 3;

            m["fr-fr"] = 4;
            m["french"] = 4;

            m["jp"] = 5;
            m["ja-jp"] = 5;
            m["japanese"] = 5;

            m["kr"] = 6;
            m["ko-kr"] = 6;
            m["korean"] = 6;
            m["koreana"] = 6;

            m["pl-pl"] = 7;
            m["polish"] = 7;

            m["pt-br"] = 8;
            m["pt_br"] = 8;
            m["pt-pt"] = 8;
            m["portuguese"] = 8;
            m["brazilian"] = 8;

            m["ru-ru"] = 9;
            m["russian"] = 9;
            return m;
        }

        public static string NormalizeCode(string raw, bool allowAuto = false)
        {
            var s = (raw ?? "").Trim().ToLowerInvariant().Replace('_', '-');
            if (allowAuto && (s.Length == 0 || s == "auto")) return "auto";
            if (CodeIndexMap.TryGetValue(s, out int idx)) return SupportedCodes[idx];
            int dash = s.IndexOf('-');
            if (dash > 0 && CodeIndexMap.TryGetValue(s.Substring(0, dash), out int baseIdx))
                return SupportedCodes[baseIdx];
            return allowAuto ? "auto" : "en";
        }

        public static string Current => ResolveCurrent(false);
        public static bool IsZh => Current == "zh";
        public static int Index => CodeIndexMap.TryGetValue(Current, out int i) ? i : 1;

        public static bool Refresh(bool force = false)
        {
            string before = _cachedGameLang;
            ResolveCurrent(force);
            return before != _cachedGameLang;
        }

        private static string ResolveCurrent(bool force)
        {
            try
            {
                string manual = AiCompanion.CfgUiLanguage != null
                    ? NormalizeCode(AiCompanion.CfgUiLanguage.Value, allowAuto: true)
                    : "auto";
                if (manual != "auto") return manual;
            }
            catch { }

            float now = 0f;
            try { now = Time.unscaledTime; } catch { }
            if (force || now >= _nextGamePollAt || now < 0f)
            {
                _nextGamePollAt = now + 0.4f;
                string detected = PollGameLanguageCode();
                if (!string.IsNullOrEmpty(detected))
                    _cachedGameLang = detected;
            }
            return _cachedGameLang;
        }

        private static string PollGameLanguageCode()
        {
            try
            {
                if (DropOutlinePlugin.WorldTearingDown || DropOutlinePlugin.ShuttingDown) return null;
                var gc = GameController.instance;
                if (gc == null) return null;
                var settings = gc.gameSettings;
                if (settings == null) return null;

                string resolved = null;
                try { resolved = LanguageRegistry.ResolveActiveCode(settings); } catch { }
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    try { resolved = settings.languageCode; } catch { }
                }
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    try
                    {
                        resolved = settings.gameLanguage == GameLanguage.SimplifiedChinese ? "zh" : "en";
                    }
                    catch { }
                }
                return string.IsNullOrWhiteSpace(resolved) ? null : NormalizeCode(resolved, allowAuto: false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Pick string by active language index: 0=zh, 1=en, 2=de, 3=es, 4=fr, 5=ja, 6=ko, 7=pl, 8=pt, 9=ru.</summary>
        public static string Pick(params string[] byIndex)
        {
            if (byIndex == null || byIndex.Length == 0) return "";
            int idx = Index;
            if (idx >= 0 && idx < byIndex.Length && !string.IsNullOrEmpty(byIndex[idx]))
                return byIndex[idx];
            if (byIndex.Length > 1 && !string.IsNullOrEmpty(byIndex[1])) return byIndex[1];
            return byIndex[0] ?? "";
        }

        public static string[] GetUiLangLabels()
        {
            var list = new string[UiLangCodes.Length];
            list[0] = Pick(
                "跟随游戏语言 (Auto)",
                "Follow Game Language (Auto)",
                "Spielsprache folgen (Auto)",
                "Seguir idioma del juego (Auto)",
                "Suivre la langue du jeu (Auto)",
                "ゲームの言語に従う (Auto)",
                "게임 언어 따르기 (Auto)",
                "Język gry (Auto)",
                "Seguir idioma do jogo (Auto)",
                "Язык игры (Auto)");
            for (int i = 0; i < NativeSelfNames.Length; i++)
                list[i + 1] = NativeSelfNames[i];
            return list;
        }

        public static string DefaultAiName() => Pick(
            "阿岳", "Ayue", "Ayue", "Ayue", "Ayue", "アユエ", "아유에", "Ayue", "Ayue", "Аюэ");

        public static string DefaultPlayerTitle() => Pick(
            "队长", "Captain", "Kapitän", "Capitán", "Capitaine", "隊長", "대장", "Kapitan", "Capitão", "Капитан");

        public static string LocalizedDefaultNameOrCustom(string configured, string zhDefault, string zhAltDefault, string localizedDefault)
        {
            var v = (configured ?? "").Trim();
            if (v.Length == 0) return localizedDefault;
            if (!IsZh && (v == zhDefault || (!string.IsNullOrEmpty(zhAltDefault) && v == zhAltDefault)))
                return localizedDefault;
            return v;
        }

        public static string ModDisplayName() => Pick(
            "AI伴侣（测试版v0.2）",
            "AI Companion (Beta v0.2)",
            "KI-Begleiter (Beta v0.2)",
            "Compañero IA (Beta v0.2)",
            "Compagnon IA (Bêta v0.2)",
            "AIパートナー（ベータ v0.2）",
            "AI 동료 (베타 v0.2)",
            "Towarzysz AI (Beta v0.2)",
            "Companheiro IA (Beta v0.2)",
            "ИИ-напарник (Бета v0.2)");

        public static string NavLabel() => Pick(
            "AI 伙伴",
            "AI Companion",
            "KI-Begleiter",
            "Compañero IA",
            "Compagnon IA",
            "AIパートナー",
            "AI 동료",
            "Towarzysz AI",
            "Companheiro IA",
            "ИИ-напарник");

        public static string WinTitle() => ModDisplayName() + Pick(
            " 设置", " Settings", " Einstellungen", " Ajustes", " Paramètres", " 設定", " 설정", " Ustawienia", " Configurações", " Настройки");

        public static string OpenSettingsBtn() => Pick(
            "打开 AI 伙伴设置",
            "Open AI Settings",
            "KI-Einstellungen öffnen",
            "Abrir ajustes de IA",
            "Ouvrir paramètres IA",
            "AI設定を開く",
            "AI 설정 열기",
            "Otwórz ustawienia AI",
            "Abrir configurações de IA",
            "Открыть настройки ИИ");

        public static string EntryRowTitle() => Pick(
            "AI 伙伴设置",
            "AI Companion Settings",
            "KI-Begleiter-Einstellungen",
            "Ajustes de Compañero IA",
            "Paramètres Compagnon IA",
            "AIパートナー設定",
            "AI 동료 설정",
            "Ustawienia Towarzysza AI",
            "Configurações do Companheiro IA",
            "Настройки ИИ-напарника");

        public static string EntryRowDesc() => Pick(
            "点击打开独立设置窗口，配置 API / DSH 接入、表现形式、联机与辅助权限",
            "Open settings to configure API / DSH connection, AI mode, co-op, and permissions",
            "Öffnet Einstellungen für API / DSH, KI-Modus, Koop und Berechtigungen",
            "Abre los ajustes de conexión API / DSH, modo IA, cooperativo y permisos",
            "Ouvrez les paramètres pour configurer API / DSH, mode IA, coop et permissions",
            "設定画面を開き、API / DSH接続・AIモード・マルチ・権限を設定します",
            "설정 창을 열어 API / DSH 연결, AI 모드, 멀티, 권한을 구성합니다",
            "Otwórz ustawienia połączenia API / DSH, trybu AI, koopa i uprawnień",
            "Abre as configurações de conexão API / DSH, modo IA, coop e permissões",
            "Открывает настройки API / DSH, режима ИИ, кооператива и разрешений");

        public static string CloseText() => Pick(
            "关闭", "Close", "Schließen", "Cerrar", "Fermer", "閉じる", "닫기", "Zamknij", "Fechar", "Закрыть");

        public static string BoolOn() => Pick(
            "开启", "On", "Ein", "Sí", "Oui", "オン", "켜짐", "Wł.", "Ligado", "Вкл.");

        public static string BoolOff() => Pick(
            "关闭", "Off", "Aus", "No", "Non", "オフ", "꺼짐", "Wył.", "Desligado", "Выкл.");

        public static string CurrentPrefix() => Pick(
            "当前：", "Mode: ", "Modus: ", "Modo: ", "Mode : ", "現在：", "현재: ", "Tryb: ", "Atual: ", "Режим: ");

        public static string StatusPrefix() => Pick(
            "状态：", "Status: ", "Status: ", "Estado: ", "Statut : ", "状態：", "상태: ", "Status: ", "Status: ", "Статус: ");

        public static string ModalDefaultFooter() => Pick(
            "修改实时自动保存 · 按 ESC 或点击关闭",
            "Changes save automatically · Press ESC or Close",
            "Änderungen werden automatisch gespeichert · ESC oder Schließen",
            "Los cambios se guardan automáticamente · ESC o Cerrar",
            "Modifications enregistrées automatiquement · Échap ou Fermer",
            "変更は自動保存されます · ESC または閉じるで終了",
            "변경 사항은 자동 저장됩니다 · ESC 또는 닫기",
            "Zmiany zapisują się automatycznie · ESC lub Zamknij",
            "Alterações salvas automaticamente · ESC ou Fechar",
            "Изменения сохраняются автоматически · ESC или Закрыть");

        public static string[] RunModeLabels() => new[]
        {
            Pick(
                "API 模式（直连大模型接口）",
                "API Mode (Direct LLM API)",
                "API-Modus (Direkte LLM-API)",
                "Modo API (API de LLM directa)",
                "Mode API (API LLM directe)",
                "API モード（LLM API 直接接続）",
                "API 모드 (LLM API 직접 연결)",
                "Tryb API (Bezpośrednie API LLM)",
                "Modo API (API LLM direta)",
                "Режим API (Прямое API LLM)"),
            Pick(
                "DSH 模式（连接本地 DSH Agent）",
                "DSH Mode (Local DSH Agent Bridge)",
                "DSH-Modus (Lokaler DSH-Agent)",
                "Modo DSH (Agente local DSH)",
                "Mode DSH (Agent local DSH)",
                "DSH モード（ローカル DSH Agent）",
                "DSH 모드 (로컬 DSH 에이전트)",
                "Tryb DSH (Lokalny Agent DSH)",
                "Modo DSH (Agente local DSH)",
                "Режим DSH (Локальный DSH-агент)"),
        };

        public static string[] ApiProtocolLabels() => new[]
        {
            "Chat Completions (/chat/completions)",
            "Anthropic Messages (/v1/messages)",
            "Responses (/responses)",
            Pick(
                "Ollama 本地 (/api/chat)",
                "Ollama Local (/api/chat)",
                "Ollama Lokal (/api/chat)",
                "Ollama Local (/api/chat)",
                "Ollama Local (/api/chat)",
                "Ollama ローカル (/api/chat)",
                "Ollama 로컬 (/api/chat)",
                "Ollama Lokalny (/api/chat)",
                "Ollama Local (/api/chat)",
                "Ollama Локальный (/api/chat)"),
        };

        public static string[] ModeLabels() => new[]
        {
            Pick(
                "聊天陪伴模式（官方聊天 · 提醒 · 非作弊建议）",
                "Chat Companion (Official chat · Reminders · Fair advice)",
                "Chat-Begleiter (Chat · Hinweise · Faire Tipps)",
                "Compañero de chat (Chat oficial · Avisos · Consejos)",
                "Compagnon chat (Chat officiel · Rappels · Conseils)",
                "チャット相棒（公式チャット・通知・助言）",
                "채팅 동반자 (공식 채팅 · 알림 · 조언)",
                "Towarzysz czatu (Oficjalny czat · Wskazówki)",
                "Companheiro de chat (Chat oficial · Avisos · Dicas)",
                "Чат-напарник (Чат · Напоминания · Советы)"),
            Pick(
                "接管雇佣NPC模式（指挥游戏内已招募队友）",
                "Hired NPC Mode (Command recruited NPC teammates)",
                "Söldner-NPC-Modus (Rekrutierte NPCs steuern)",
                "Modo NPC contratado (Comandar aliados NPC)",
                "Mode PNJ recruté (Commander les alliés PNJ)",
                "雇用NPC操作（仲間にしたNPCを指揮）",
                "고용 NPC 모드 (영입한 NPC 동료 지휘)",
                "Tryb najemnego NPC (Dowodzenie zrekrutowanym NPC)",
                "Modo NPC contratado (Comandar aliados NPC)",
                "Режим наёмного НИП (Управление нанятыми НИП)"),
            Pick(
                "玩家模式（AI 创建角色作为正常玩家加入联机）",
                "Co-op Player Mode (AI joins room as a real player)",
                "Spieler-Modus (KI tritt als normaler Koop-Spieler bei)",
                "Modo Jugador Coop (La IA se une como jugador)",
                "Mode Joueur Coop (L'IA rejoint comme joueur)",
                "協力プレイヤーモード（AIがプレイヤーとして参戦）",
                "플레이어 모드 (AI가 일반 멀티 플레이어로 참가)",
                "Tryb Gracza Koop (AI dołącza jako gracz)",
                "Modo Jogador Coop (IA entra como jogador)",
                "Режим игрока (ИИ заходит в комнату как игрок)"),
        };

        public static string AiModeShortName(string mode)
        {
            switch ((mode ?? "companion").Trim().ToLowerInvariant())
            {
                case "npc":
                    return Pick(
                        "接管雇佣NPC模式", "Hired NPC Mode", "Söldner-NPC-Modus", "Modo NPC contratado",
                        "Mode PNJ recruté", "雇用NPCモード", "고용 NPC 모드", "Tryb najemnego NPC",
                        "Modo NPC contratado", "Режим наёмного НИП");
                case "player":
                    return Pick(
                        "玩家模式", "Co-op Player Mode", "Spieler-Modus", "Modo Jugador",
                        "Mode Joueur", "プレイヤーモード", "플레이어 모드", "Tryb Gracza",
                        "Modo Jogador", "Режим игрока");
                default:
                    return Pick(
                        "聊天陪伴模式", "Chat Companion", "Chat-Begleiter", "Compañero de chat",
                        "Compagnon chat", "チャット相棒", "채팅 동반자", "Towarzysz czatu",
                        "Companheiro de chat", "Чат-напарник");
            }
        }

        public static string[] BehaviorLabels() => new[]
        {
            Pick(
                "紧跟玩家（自动清怪/传送跟随）",
                "Follow Player (Stay close & clear threats)",
                "Spieler folgen (Nah bleiben & Feinde klären)",
                "Seguir jugador (Cerca y cubrir amenazas)",
                "Suivre le joueur (Rester proche et couvrir)",
                "プレイヤー追従（護衛・自動追いつき）",
                "플레이어 추적 (근접 호위 및 교전)",
                "Podążaj za graczem (Eskorta i osłona)",
                "Seguir jogador (Perto e cobrir ameaças)",
                "Следовать за игроком (Прикрытие и телепорт)"),
            Pick(
                "积极迎战（主动索敌/自动换弹）",
                "Aggressive Combat (Seek threats & reload)",
                "Aggressiver Kampf (Feinde suchen & nachladen)",
                "Combate activo (Buscar enemigos y recargar)",
                "Combat actif (Chasser menaces et recharger)",
                "積極戦闘（自動索敵・リロード）",
                "적극 전투 (적 탐색 및 자동 재장전)",
                "Aktywna walka (Szukaj wrogów i przeładuj)",
                "Combate ativo (Caçar ameaças e recarregar)",
                "Активный бой (Поиск врагов и перезарядка)"),
            Pick(
                "原地驻守（守卫指定位置）",
                "Hold Position (Guard current spot)",
                "Position halten (Aktuellen Ort bewachen)",
                "Mantener posición (Vigilar este punto)",
                "Tenir la position (Garder cet endroit)",
                "その場で待機（現在地点を警戒）",
                "위치 사수 (현재 위치 경계)",
                "Utrzymaj pozycję (Pilnuj punktu)",
                "Manter posição (Proteger este ponto)",
                "Удерживать позицию (Охрана точки)"),
            Pick(
                "协同拾荒（跟随并警戒周边）",
                "Scavenge & Escort (Follow & watch area)",
                "Plündern & Eskorte (Folgen & Umgebung sichern)",
                "Saquear y escoltar (Seguir y vigilar)",
                "Piller et escorter (Suivre et surveiller)",
                "探索同行（追従しつつ周囲を警戒）",
                "탐색 호위 (따라다니며 주변 경계)",
                "Szabrowanie i eskorta (Śledź i osłaniaj)",
                "Saquear e escoltar (Seguir e vigiar)",
                "Сбор и прикрытие (Осмотр окрестностей)"),
        };

        public static string NetworkRoleLabel(string key)
        {
            switch (key)
            {
                case "lobby_client":
                    return Pick(
                        "联机大厅（客户端）", "Lobby (Client)", "Lobby (Client)", "Sala (Cliente)",
                        "Salon (Client)", "ロビー（クライアント）", "로비 (클라이언트)", "Lobby (Klient)",
                        "Lobby (Cliente)", "Лобби (Клиент)");
                case "lobby_host":
                    return Pick(
                        "联机大厅（房主）", "Lobby (Host)", "Lobby (Host)", "Sala (Anfitrión)",
                        "Salon (Hôte)", "ロビー（ホスト）", "로비 (호스트)", "Lobby (Host)",
                        "Lobby (Host)", "Лобби (Хост)");
                case "menu":
                    return Pick(
                        "主菜单", "Main Menu", "Hauptmenü", "Menú principal",
                        "Menu principal", "メインメニュー", "메인 메뉴", "Menu główne",
                        "Menu principal", "Главное меню");
                case "host":
                    return Pick(
                        "联机房主 (Host)", "Co-op Host", "Koop-Host", "Anfitrión Coop",
                        "Hôte Coop", "マルチ・ホスト", "멀티 호스트", "Host Koop",
                        "Host Coop", "Хост кооператива");
                case "client":
                    return Pick(
                        "联机客户端 (Client)", "Co-op Client", "Koop-Client", "Cliente Coop",
                        "Client Coop", "マルチ・クライアント", "멀티 클라이언트", "Klient Koop",
                        "Cliente Coop", "Клиент кооператива");
                case "online":
                    return Pick(
                        "联机模式", "Online Co-op", "Online-Koop", "En línea",
                        "En ligne", "オンライン", "온라인", "Online",
                        "Online", "Онлайн");
                default:
                    return Pick(
                        "单人游戏 (Offline)", "Singleplayer", "Einzelspieler", "Un jugador",
                        "Solo", "シングルプレイ", "싱글 플레이", "Dla jednego gracza",
                        "Um jogador", "Одиночная игра");
            }
        }

        /// <summary>Instruction added to LLM prompts so AI naturally speaks in the player's language.</summary>
        public static string LanguagePromptInstruction()
        {
            switch (Current)
            {
                case "zh":
                    return "回复语言要求：默认使用自然口语化的简体中文回复（若玩家用其他语言发消息则跟随玩家所用语言）。所有 JSON 字段名必须严格保持英文原样。";
                case "de":
                    return "Antwortsprache: Antworte standardmäßig auf natürlichem, umgangssprachlichem Deutsch im Ton eines Überlebenden (oder passe dich der Sprache der letzten Spielernachricht an). Alle JSON-Schlüsselnamen bleiben exakt wie vorgegeben auf Englisch.";
                case "es":
                    return "Idioma de respuesta: Responde por defecto en español natural y coloquial con tono de superviviente (o usa el idioma del jugador si escribe en otro idioma). Mantén todos los nombres de clave JSON exactamente en inglés.";
                case "fr":
                    return "Langue de réponse : Réponds par défaut en français naturel et familier avec un ton de survivant (ou adapte-toi à la langue du joueur s'il écrit dans une autre langue). Conserve exactement tous les noms de clés JSON en anglais.";
                case "ja":
                    return "返答言語：基本は終末世界の生存者らしい自然な口語の日本語で返答してください（プレイヤーが別の言語で話しかけた場合はその言語に合わせる）。JSONのキー名はすべて英語のまま変更しないでください。";
                case "ko":
                    return "응답 언어: 기본적으로 종말 세계 생존자다운 자연스러운 구어체 한국어로 답하세요(플레이어가 다른 언어로 말하면 그 언어에 맞추세요). 모든 JSON 키 이름은 지정된 영어 그대로 유지하세요.";
                case "pl":
                    return "Język odpowiedzi: Domyślnie odpowiadaj naturalnym, potocznym językiem polskim w tonie ocalałego (lub w języku gracza, jeśli pisze w innym). Wszystkie nazwy kluczy JSON zachowaj dokładnie po angielsku.";
                case "pt":
                    return "Idioma da resposta: Responda por padrão em português natural e coloquial com tom de sobrevivente (ou no idioma do jogador, caso ele escreva em outro). Mantenha todas as chaves JSON exatamente em inglês.";
                case "ru":
                    return "Язык ответа: Отвечай на естественном разговорном русском языке в тоне выжившего (или на языке игрока, если он пишет на другом языке). Все имена ключей JSON оставляй строго на английском.";
                default:
                    return "Reply language: Reply in natural, colloquial English matching a post-apocalyptic survivor tone (or match the player's language if they write in another language). Keep all JSON key names in English exactly as specified.";
            }
        }
    }
}
