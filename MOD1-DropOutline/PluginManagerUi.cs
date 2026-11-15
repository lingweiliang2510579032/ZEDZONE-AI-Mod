using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

namespace Dropoutline
{
    /// <summary>
    /// 内置 BepInEx 插件管理器（从「MOD 设置 → 插件管理器」按钮打开，不占用任何热键）。
    ///
    /// 游戏自带的 MOD管理器只认 .zedmod 内容包，不认代码插件；这里自己扫描
    /// BepInEx\plugins 下的 DLL：启用/禁用 = 改扩展名（下次启动生效），卸载 = 删文件。
    /// </summary>
    internal static class PluginManagerUi
    {
        private sealed class Entry
        {
            public string path;
            public string name;
            public long size;
            public bool enabled;
        }

        private static GameObject _root;
        private static RectTransform _list;
        private static Text _status;
        private static Text _titleText;
        private static Text _hintText;
        private static Text _refreshText;
        private static readonly List<Entry> _entries = new List<Entry>();
        private static int _page;
        private const int PageSize = 9;
        private static Font _font;

        public static bool IsOpen => _root != null && _root.activeInHierarchy;

        public static void Toggle()
        {
            if (IsOpen) { Close(); return; }
            Open();
        }

        public static void Open()
        {
            try
            {
                if (_root == null) Build();
                _root.SetActive(true);
                _page = 0;
                Refresh();
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[管理器] 打开失败: {e.Message}");
            }
        }

        public static void Close()
        {
            if (_root != null) _root.SetActive(false);
        }

        public static void Refresh()
        {
            _entries.Clear();
            try
            {
                var roots = new List<string>();
                try { roots.Add(Paths.PluginPath); } catch { }
                var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx", "plugins");
                if (Directory.Exists(local) && !roots.Contains(local)) roots.Add(local);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories))
                    {
                        var full = Path.GetFullPath(f);
                        if (seen.Add(full)) Add(full, true);
                    }
                    foreach (var f in Directory.GetFiles(root, "*.dll.disabled", SearchOption.AllDirectories))
                    {
                        var full = Path.GetFullPath(f);
                        if (seen.Add(full)) Add(full, false);
                    }
                }
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[管理器] 扫描失败: {e.Message}");
            }
            _entries.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            Render();
        }

        /// <summary>DLL 文件名 → 玩家能看懂的 MOD 名。</summary>
        private static string DisplayName(string fileName)
        {
            var lower = fileName.ToLowerInvariant();
            if (lower.Contains("zedai") || lower.Contains("dropoutline") || lower.Contains("yuyeai")) return ModLocalization.ModDisplayName();
            if (lower.Contains("zedhighlight") || lower.Contains("yuyehighlight"))
                return ModLocalization.Pick("ZED · 持续高亮轮廓", "ZED · Persistent Outline", "ZED · Dauerhafte Kontur",
                    "ZED · Contorno persistente", "ZED · Contour persistant", "ZED · 常時ハイライト",
                    "ZED · 상시 윤곽 강조", "ZED · Trwały obrys", "ZED · Contorno contínuo", "ZED · Постоянная подсветка");
            if (lower.Contains("yuyepluginmanager"))
                return ModLocalization.Pick("插件管理器", "Plugin Manager", "Plugin-Manager", "Gestor de plugins",
                    "Gestionnaire de plugins", "プラグイン管理", "플러그인 관리자", "Menedżer wtyczek", "Gerenciador de plugins", "Менеджер плагинов");
            return fileName;
        }

        private static void Add(string path, bool enabled)
        {
            var name = DisplayName(Path.GetFileName(path));
            if (!enabled && name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".disabled".Length);
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            _entries.Add(new Entry { path = path, name = name, size = size, enabled = enabled });
        }

        private static void ToggleEntry(Entry e)
        {
            try
            {
                if (e.enabled)
                {
                    File.Move(e.path, e.path + ".disabled");
                    SetStatus(string.Format(ModLocalization.Pick(
                        "已禁用 {0}（重启游戏生效）", "Disabled {0} (restart to apply)", "{0} deaktiviert (Neustart nötig)",
                        "{0} desactivado (reinicia)", "{0} désactivé (redémarrer)", "{0} を無効化（再起動後に有効）",
                        "{0} 비활성화 (재시작 필요)", "Wyłączono {0} (uruchom ponownie)", "{0} desativado (reinicie)", "Отключено: {0} (нужен перезапуск)"), e.name));
                }
                else
                {
                    File.Move(e.path, e.path.Substring(0, e.path.Length - ".disabled".Length));
                    SetStatus(string.Format(ModLocalization.Pick(
                        "已启用 {0}（重启游戏生效）", "Enabled {0} (restart to apply)", "{0} aktiviert (Neustart nötig)",
                        "{0} activado (reinicia)", "{0} activé (redémarrer)", "{0} を有効化（再起動後に有効）",
                        "{0} 활성화 (재시작 필요)", "Włączono {0} (uruchom ponownie)", "{0} ativado (reinicie)", "Включено: {0} (нужен перезапуск)"), e.name));
                }
                Refresh();
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(ModLocalization.Pick(
                    "操作失败：{0}（请先退出游戏）", "Action failed: {0} (exit the game first)", "Aktion fehlgeschlagen: {0} (Spiel erst beenden)",
                    "Error: {0} (sal del juego primero)", "Échec : {0} (quittez le jeu d'abord)", "操作失敗：{0}（先にゲームを終了してください）",
                    "작업 실패: {0} (먼저 게임을 종료하세요)", "Błąd: {0} (najpierw wyjdź z gry)", "Falha: {0} (saia do jogo primeiro)", "Ошибка: {0} (сначала выйдите из игры)"), ex.Message));
            }
        }

        private static void Uninstall(Entry e)
        {
            try
            {
                File.Delete(e.path);
                SetStatus(ModLocalization.Pick("已删除 ", "Deleted ", "Gelöscht: ", "Eliminado: ", "Supprimé : ", "削除しました: ", "삭제됨: ", "Usunięto: ", "Excluído: ", "Удалено: ") + e.name);
                Refresh();
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(ModLocalization.Pick(
                    "删除失败：{0}（请先退出游戏）", "Delete failed: {0} (exit the game first)", "Löschen fehlgeschlagen: {0} (Spiel erst beenden)",
                    "Error al eliminar: {0} (sal del juego primero)", "Échec suppression : {0} (quittez le jeu)", "削除失敗：{0}（先にゲームを終了）",
                    "삭제 실패: {0} (먼저 게임을 종료)", "Błąd usuwania: {0} (wyjdź z gry)", "Falha ao excluir: {0} (saia do jogo)", "Ошибка удаления: {0} (выйдите из игры)"), ex.Message));
            }
        }

        private static void Build()
        {
            _root = new GameObject("YuyePluginManagerCanvas");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();

            var bg = NewRect(_root.transform, "BG", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            bg.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.85f);

            var panel = NewRect(_root.transform, "Panel", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(960, 720));
            panel.gameObject.AddComponent<Image>().color = new Color(0.07f, 0.07f, 0.07f, 0.98f);

            _titleText = NewText(panel, "Title", ModLocalization.Pick(
                "BepInEx 插件管理器", "BepInEx Plugin Manager", "BepInEx Plugin-Manager", "Gestor de plugins BepInEx",
                "Gestionnaire BepInEx", "BepInEx プラグイン管理", "BepInEx 플러그인 관리자", "Menedżer wtyczek BepInEx",
                "Gerenciador BepInEx", "Менеджер плагинов BepInEx"), 34, TextAnchor.MiddleLeft);
            _titleText.rectTransform.anchoredPosition = new Vector2(24, 314);
            _titleText.rectTransform.sizeDelta = new Vector2(600, 48);

            _hintText = NewText(panel, "Hint", ModLocalization.Pick(
                "禁用/启用：改扩展名，重启游戏生效；卸载：直接删文件",
                "Toggle: rename extension (applies on restart); Uninstall: deletes file",
                "De-/Aktivieren: Dateiendung ändern (Neustart); Deinstallieren: löscht Datei",
                "Activar/Desactivar: renombra archivo (reinicia); Desinstalar: borra archivo",
                "Activer/Désactiver : renomme l'extension (redémarrage) ; Désinstaller : supprime",
                "有効/無効：拡張子を変更（再起動後有効）；削除：ファイルを削除",
                "켜기/끄기: 확장자 변경 (재시작 필요); 제거: 파일 삭제",
                "Wł./Wył.: zmienia rozszerzenie (po restarcie); Odinstaluj: usuwa plik",
                "Ativar/Desativar: renomeia extensão (ao reiniciar); Desinstalar: exclui arquivo",
                "Вкл./Выкл.: переименование файла (после перезапуска); Удалить: удаляет файл"), 20, TextAnchor.MiddleLeft);
            _hintText.color = new Color(0.7f, 0.75f, 0.7f);
            _hintText.rectTransform.anchoredPosition = new Vector2(24, 280);
            _hintText.rectTransform.sizeDelta = new Vector2(780, 28);

            var close = NewButton(panel, "Close", "✕", new Vector2(430, 314), new Vector2(64, 44), Close);
            close.GetComponent<Image>().color = new Color(0.35f, 0.08f, 0.08f);
            var refresh = NewButton(panel, "Refresh", ModLocalization.Pick(
                "刷新", "Refresh", "Aktualisieren", "Actualizar", "Actualiser", "更新", "새로고침", "Odśwież", "Atualizar", "Обновить"),
                new Vector2(330, 314), new Vector2(96, 44), Refresh);
            _refreshText = refresh.GetComponentInChildren<Text>();
            refresh.GetComponent<Image>().color = new Color(0.1f, 0.28f, 0.1f);

            _list = NewRect(panel, "List", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -10), new Vector2(900, 480));

            NewButton(panel, "Prev", "▲", new Vector2(-430, -290), new Vector2(70, 44), () => { if (_page > 0) { _page--; Render(); } });
            NewButton(panel, "Next", "▼", new Vector2(-350, -290), new Vector2(70, 44), () => { _page++; Render(); });

            _status = NewText(panel, "Status", "", 20, TextAnchor.MiddleLeft);
            _status.color = new Color(0.55f, 0.9f, 0.6f);
            _status.rectTransform.anchoredPosition = new Vector2(24, -322);
            _status.rectTransform.sizeDelta = new Vector2(780, 28);
        }

        private static void Render()
        {
            if (_list == null) return;
            var f = ChatInput.UiFont;
            if (_titleText != null)
            {
                if (f != null) _titleText.font = f;
                _titleText.text = ModLocalization.Pick(
                    "BepInEx 插件管理器", "BepInEx Plugin Manager", "BepInEx Plugin-Manager", "Gestor de plugins BepInEx",
                    "Gestionnaire BepInEx", "BepInEx プラグイン管理", "BepInEx 플러그인 관리자", "Menedżer wtyczek BepInEx",
                    "Gerenciador BepInEx", "Менеджер плагинов BepInEx");
            }
            if (_hintText != null)
            {
                if (f != null) _hintText.font = f;
                _hintText.text = ModLocalization.Pick(
                    "禁用/启用：改扩展名，重启游戏生效；卸载：直接删文件",
                    "Toggle: rename extension (applies on restart); Uninstall: deletes file",
                    "De-/Aktivieren: Dateiendung ändern (Neustart); Deinstallieren: löscht Datei",
                    "Activar/Desactivar: renombra archivo (reinicia); Desinstalar: borra archivo",
                    "Activer/Désactiver : renomme l'extension (redémarrage) ; Désinstaller : supprime",
                    "有効/無効：拡張子を変更（再起動後有効）；削除：ファイルを削除",
                    "켜기/끄기: 확장자 변경 (재시작 필요); 제거: 파일 삭제",
                    "Wł./Wył.: zmienia rozszerzenie (po restarcie); Odinstaluj: usuwa plik",
                    "Ativar/Desativar: renomeia extensão (ao reiniciar); Desinstalar: exclui arquivo",
                    "Вкл./Выкл.: переименование файла (после перезапуска); Удалить: удаляет файл");
            }
            if (_refreshText != null)
            {
                if (f != null) _refreshText.font = f;
                _refreshText.text = ModLocalization.Pick(
                    "刷新", "Refresh", "Aktualisieren", "Actualizar", "Actualiser", "更新", "새로고침", "Odśwież", "Atualizar", "Обновить");
            }
            if (_status != null && f != null) _status.font = f;

            for (int i = _list.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_list.GetChild(i).gameObject);

            string modName = ModLocalization.ModDisplayName();
            string modDesc = ModLocalization.Pick(
                "AI 搭档：对话陪伴、读取局面、可接管队友或联机陪玩",
                "AI companion: chat, situation awareness, hired NPC or coop player",
                "KI-Begleiter: Chat, Lageerkennung, NPC-Steuerung oder Koop-Spieler",
                "Compañero IA: chat, percepción, NPC contratado o jugador cooperativo",
                "Compagnon IA : discussion, perception, PNJ ou joueur coopératif",
                "AI相棒：会話・状況認識・雇用NPCや協力プレイヤー操作",
                "AI 동반자: 대화, 상황 인식, 고용 NPC 및 협동 플레이어 지원",
                "Towarzysz AI: rozmowa, ocena sytuacji, przejęcie NPC lub tryb kooperacji",
                "Companheiro IA: conversa, percepção, NPC ou jogador cooperativo",
                "ИИ-напарник: общение, оценка обстановки, наёмник или кооп-игрок");
            RenderOwnMod(modName, modDesc, AiCompanion.CfgAiEnabled, 180);

            string lblEnabled = ModLocalization.Pick("启用中", "Active", "Aktiv", "Activo", "Actif", "有効", "활성", "Aktywny", "Ativo", "Вкл.");
            string lblDisabled = ModLocalization.Pick("已禁用", "Disabled", "Aus", "Inactivo", "Inactif", "無効", "비활성", "Wył.", "Inativo", "Выкл.");
            string btnDisable = ModLocalization.Pick("禁用", "Disable", "Aus", "Desact.", "Désact.", "無効化", "끄기", "Wyłącz", "Desativar", "Выкл.");
            string btnEnable = ModLocalization.Pick("启用", "Enable", "Ein", "Activar", "Activer", "有効化", "켜기", "Włącz", "Ativar", "Вкл.");
            string btnUninstall = ModLocalization.Pick("卸载", "Remove", "Entfernen", "Quitar", "Suppr.", "削除", "제거", "Usuń", "Remover", "Удалить");

            int total = _entries.Count;
            int pages = Mathf.Max(1, Mathf.CeilToInt(total / (float)PageSize));
            if (_page >= pages) _page = pages - 1;
            if (_page < 0) _page = 0;

            int start = _page * PageSize;
            for (int i = 0; i < PageSize && start + i < total; i++)
            {
                var e = _entries[start + i];
                if (e.name == modName) continue;
                float y = 100 - i * 50;

                var label = NewText(_list, "Name", e.name, 22, TextAnchor.MiddleLeft);
                label.rectTransform.anchoredPosition = new Vector2(-180, y);
                label.rectTransform.sizeDelta = new Vector2(420, 40);
                label.color = e.enabled ? new Color(0.92f, 0.92f, 0.92f) : new Color(0.55f, 0.55f, 0.55f);

                var size = NewText(_list, "Size", $"{e.size / 1024}KB", 18, TextAnchor.MiddleRight);
                size.rectTransform.anchoredPosition = new Vector2(120, y);
                size.rectTransform.sizeDelta = new Vector2(90, 40);
                size.color = new Color(0.6f, 0.6f, 0.6f);

                var state = NewText(_list, "State", e.enabled ? lblEnabled : lblDisabled, 19, TextAnchor.MiddleCenter);
                state.rectTransform.anchoredPosition = new Vector2(215, y);
                state.rectTransform.sizeDelta = new Vector2(90, 40);
                state.color = e.enabled ? new Color(0.5f, 0.95f, 0.55f) : new Color(0.9f, 0.7f, 0.35f);

                var entry = e;
                var toggle = NewButton(_list, "Toggle", e.enabled ? btnDisable : btnEnable, new Vector2(320, y), new Vector2(84, 38), () => ToggleEntry(entry));
                toggle.GetComponent<Image>().color = e.enabled ? new Color(0.35f, 0.22f, 0.05f) : new Color(0.1f, 0.3f, 0.12f);

                var del = NewButton(_list, "Del", btnUninstall, new Vector2(410, y), new Vector2(84, 38), () => Uninstall(entry));
                del.GetComponent<Image>().color = new Color(0.38f, 0.08f, 0.08f);
            }

            if (total == 0)
            {
                var empty = NewText(_list, "Empty", ModLocalization.Pick(
                    "（没有找到插件）", "(No plugins found)", "(Keine Plugins gefunden)", "(Sin plugins)", "(Aucun plugin)",
                    "（プラグインが見つかりません）", "(플러그인을 찾지 못했습니다)", "(Nie znaleziono wtyczek)", "(Nenhum plugin encontrado)", "(Плагины не найдены)"), 22, TextAnchor.MiddleCenter);
                empty.rectTransform.anchoredPosition = Vector2.zero;
                empty.rectTransform.sizeDelta = new Vector2(600, 40);
                empty.color = new Color(0.6f, 0.6f, 0.6f);
            }

            SetStatus(string.Format(ModLocalization.Pick(
                "{0} 个插件 · 第 {1}/{2} 页", "{0} plugins · Page {1}/{2}", "{0} Plugins · Seite {1}/{2}",
                "{0} plugins · Pág. {1}/{2}", "{0} plugins · Page {1}/{2}", "{0} 個 · {1}/{2} ページ",
                "{0}개 플러그인 · {1}/{2} 페이지", "{0} wtyczek · Strona {1}/{2}", "{0} plugins · Pág. {1}/{2}", "{0} плагинов · Стр. {1}/{2}"), total, _page + 1, pages));
        }

        /// <summary>渲染我们自己的两个 MOD（共用一个 DLL，但各自有独立开关）。</summary>
        private static void RenderOwnMod(string name, string desc, BepInEx.Configuration.ConfigEntry<bool> flag, float y)
        {
            bool on = flag != null && flag.Value;
            string lblEnabled = ModLocalization.Pick("启用中", "Active", "Aktiv", "Activo", "Actif", "有効", "활성", "Aktywny", "Ativo", "Вкл.");
            string lblDisabled = ModLocalization.Pick("已禁用", "Disabled", "Aus", "Inactivo", "Inactif", "無効", "비활성", "Wył.", "Inativo", "Выкл.");
            string btnDisable = ModLocalization.Pick("禁用", "Disable", "Aus", "Desact.", "Désact.", "無効化", "끄기", "Wyłącz", "Desativar", "Выкл.");
            string btnEnable = ModLocalization.Pick("启用", "Enable", "Ein", "Activar", "Activer", "有効化", "켜기", "Włącz", "Ativar", "Вкл.");
            string btnUninstall = ModLocalization.Pick("卸载", "Remove", "Entfernen", "Quitar", "Suppr.", "削除", "제거", "Usuń", "Remover", "Удалить");

            var label = NewText(_list, "Own", name, 22, TextAnchor.MiddleLeft);
            label.rectTransform.anchoredPosition = new Vector2(-180, y);
            label.rectTransform.sizeDelta = new Vector2(420, 26);
            label.color = on ? new Color(0.95f, 0.95f, 0.95f) : new Color(0.55f, 0.55f, 0.55f);

            var sub = NewText(_list, "OwnDesc", desc, 16, TextAnchor.MiddleLeft);
            sub.rectTransform.anchoredPosition = new Vector2(-180, y - 18);
            sub.rectTransform.sizeDelta = new Vector2(420, 20);
            sub.color = new Color(0.62f, 0.66f, 0.62f);

            long size = 0;
            try
            {
                var dll = Path.Combine(Paths.PluginPath, "ZEDAI.dll");
                if (File.Exists(dll)) size = new FileInfo(dll).Length;
            }
            catch { }
            var sizeText = NewText(_list, "OwnSize", size > 0 ? $"{size / 1024}KB" : "-", 18, TextAnchor.MiddleRight);
            sizeText.rectTransform.anchoredPosition = new Vector2(120, y);
            sizeText.rectTransform.sizeDelta = new Vector2(90, 40);
            sizeText.color = new Color(0.6f, 0.6f, 0.6f);

            var state = NewText(_list, "OwnState", on ? lblEnabled : lblDisabled, 19, TextAnchor.MiddleCenter);
            state.rectTransform.anchoredPosition = new Vector2(215, y);
            state.rectTransform.sizeDelta = new Vector2(90, 40);
            state.color = on ? new Color(0.5f, 0.95f, 0.55f) : new Color(0.9f, 0.7f, 0.35f);

            var toggle = NewButton(_list, "OwnToggle", on ? btnDisable : btnEnable, new Vector2(320, y), new Vector2(84, 38), () =>
            {
                if (flag != null) flag.Value = !flag.Value;
                bool nowOn = flag != null && flag.Value;
                SetStatus(string.Format(nowOn
                    ? ModLocalization.Pick("已启用 {0}（立即生效）", "Enabled {0} (active now)", "{0} aktiviert", "{0} activado", "{0} activé", "{0} を有効化", "{0} 활성화됨", "Włączono {0}", "{0} ativado", "Включено: {0}")
                    : ModLocalization.Pick("已禁用 {0}（立即生效）", "Disabled {0} (active now)", "{0} deaktiviert", "{0} desactivado", "{0} désactivé", "{0} を無効化", "{0} 비활성화됨", "Wyłączono {0}", "{0} desativado", "Отключено: {0}"), name));
                Render();
            });
            toggle.GetComponent<Image>().color = on ? new Color(0.35f, 0.22f, 0.05f) : new Color(0.1f, 0.3f, 0.12f);

            var del = NewButton(_list, "OwnDel", btnUninstall, new Vector2(410, y), new Vector2(84, 38), () =>
            {
                ModManagerActions.MakeUninstaller();
                SetStatus(ModLocalization.Pick(
                    "已生成卸载脚本（退出游戏后双击运行）",
                    "Uninstall script created (run after exiting the game)",
                    "Deinstallationsskript erstellt (nach Spielende ausführen)",
                    "Script de desinstalación creado (ejecútalo al salir)",
                    "Script de désinstallation créé (à lancer après fermeture)",
                    "アンインストール用スクリプトを作成（終了後に実行）",
                    "제거 스크립트 생성됨 (게임 종료 후 실행)",
                    "Utworzono skrypt usuwania (uruchom po wyjściu z gry)",
                    "Script de remoção criado (execute após fechar o jogo)",
                    "Скрипт удаления создан (запустите после выхода)"));
            });
            del.GetComponent<Image>().color = new Color(0.38f, 0.08f, 0.08f);
        }
        private static void SetStatus(string text)
        {
            if (_status != null) _status.text = text ?? "";
        }

        private static Font Font()
        {
            if (_font != null) return _font;
            try
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<Text>())
                    if (t != null && t.font != null) { _font = t.font; return _font; }
            }
            catch { }
            try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            return _font;
        }

        private static RectTransform NewRect(Transform parent, string name, Vector2 amin, Vector2 amax, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        private static Text NewText(Transform parent, string name, string content, int size, TextAnchor anchor)
        {
            var rt = NewRect(parent, name, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(600, 40));
            var t = rt.gameObject.AddComponent<Text>();
            t.text = content;
            t.font = ChatInput.UiFont;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = Color.white;
            t.raycastTarget = false;
            return t;
        }

        private static GameObject NewButton(Transform parent, string name, string label, Vector2 pos, Vector2 size, Action onClick)
        {
            var rt = NewRect(parent, name, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0.18f, 0.18f, 0.18f, 1f);
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(new Action(onClick));
            var txt = NewText(rt, "Text", label, 20, TextAnchor.MiddleCenter);
            txt.rectTransform.anchorMin = Vector2.zero;
            txt.rectTransform.anchorMax = Vector2.one;
            txt.rectTransform.sizeDelta = Vector2.zero;
            txt.rectTransform.anchoredPosition = Vector2.zero;
            return rt.gameObject;
        }
    }
}








