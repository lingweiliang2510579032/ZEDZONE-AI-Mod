using System;
using System.Collections.Generic;

namespace Dropoutline
{
    /// <summary>
    /// 直接操作类动作（手电开关等）。原则同丢弃/合成：只调游戏自己的方法，
    /// 不模拟键鼠；动作后用可观察状态校验，没生效就如实说，不谎报。
    /// </summary>
    internal static class AiActions
    {
        internal static AiTaskStep Flashlight(HumanCharacterController player, AiTaskRequest request)
        {
            if (player == null) return new AiTaskStep("failed", ModLocalization.Pick(
                "玩家角色不可操作", "Player character cannot be controlled", "Spielercharakter nicht steuerbar",
                "No se puede controlar al jugador", "Personnage du joueur incontrôlable", "プレイヤーを操作できません",
                "플레이어 캐릭터를 조작할 수 없습니다", "Nie można sterować postacią gracza",
                "Não é possível controlar o jogador", "Персонаж игрока недоступен"));
            bool? want = ParseWanted(request.Target);
            bool before;
            if (!TryReadLight(player, out before))
                return new AiTaskStep("failed", ModLocalization.Pick(
                    "读不到手电状态；这次没有操作，也没有假装成功",
                    "Could not read flashlight state; took no action",
                    "Taschenlampen-Status unlesbar; keine Aktion",
                    "No se pudo leer el estado de la linterna; sin cambios",
                    "État de la lampe illisible ; aucune action effectuée",
                    "ライトの状態を読めなかったため操作しませんでした",
                    "손전등 상태를 읽지 못해 동작하지 않았습니다",
                    "Nie udało się odczytać stanu latarki; brak akcji",
                    "Não foi possível ler o estado da lanterna; nenhuma ação",
                    "Не удалось прочитать состояние фонарика; без действий"));

            bool target = want ?? !before;
            if (target == before)
                return new AiTaskStep("done", target
                    ? ModLocalization.Pick("手电本来就是开着的，不用再开", "Flashlight is already on", "Taschenlampe ist bereits an",
                        "La linterna ya está encendida", "La lampe est déjà allumée", "ライトはすでに点いています",
                        "손전등이 이미 켜져 있습니다", "Latarka jest już włączona", "A lanterna já está ligada", "Фонарик уже включён")
                    : ModLocalization.Pick("手电本来就是关着的", "Flashlight is already off", "Taschenlampe ist bereits aus",
                        "La linterna ya está apagada", "La lampe est déjà éteinte", "ライトはすでに消えています",
                        "손전등이 이미 꺼져 있습니다", "Latarka jest już wyłączona", "A lanterna já está desligada", "Фонарик уже выключен"), true);

            try
            {
                if (target) player.TurnOnLight();
                else player.TurnOffLight();
            }
            catch (Exception error)
            {
                return new AiTaskStep("failed", ModLocalization.Pick(
                    "调用手电开关失败：", "Flashlight toggle failed: ", "Taschenlampe umschalten fehlgeschlagen: ",
                    "Error al cambiar linterna: ", "Échec lampe torche : ", "ライト切替失敗: ",
                    "손전등 전환 실패: ", "Błąd przełączania latarki: ", "Falha na lanterna: ", "Ошибка фонарика: ") + error.Message);
            }

            bool after;
            if (!TryReadLight(player, out after) || after != target)
                return new AiTaskStep("failed", after
                    ? ModLocalization.Pick(
                        "手电没有按预期切换（当前开）；可能是手持设备槽没有手电，请先装备手电",
                        "Flashlight did not toggle as expected (currently on); make sure a flashlight is equipped",
                        "Taschenlampe hat nicht umgeschaltet (aktuell an); bitte Taschenlampe ausrüsten",
                        "La linterna no cambió (ahora encendida); equipa una linterna primero",
                        "La lampe n'a pas basculé (allumée) ; équipez d'abord une lampe",
                        "ライトを切り替わりませんでした（現在オン）。懐中電灯を装備してください",
                        "손전등 전환 실패(현재 켜짐); 손전등을 먼저 장착하세요",
                        "Latarka nie przełączyła się (obecnie wł.); wyposaż latarkę",
                        "A lanterna não alternou (ligada); equipe uma lanterna primeiro",
                        "Фонарик не переключился (сейчас вкл.); сперва экипируйте фонарик")
                    : ModLocalization.Pick(
                        "手电没有按预期切换（当前关）；可能是手持设备槽没有手电，请先装备手电",
                        "Flashlight did not toggle as expected (currently off); make sure a flashlight is equipped",
                        "Taschenlampe hat nicht umgeschaltet (aktuell aus); bitte Taschenlampe ausrüsten",
                        "La linterna no cambió (ahora apagada); equipa una linterna primero",
                        "La lampe n'a pas basculé (éteinte) ; équipez d'abord une lampe",
                        "ライトを切り替わりませんでした（現在オフ）。懐中電灯を装備してください",
                        "손전등 전환 실패(현재 꺼짐); 손전등을 먼저 장착하세요",
                        "Latarka nie przełączyła się (obecnie wył.); wyposaż latarkę",
                        "A lanterna não alternou (desligada); equipe uma lanterna primeiro",
                        "Фонарик не переключился (сейчас выкл.); сперва экипируйте фонарик"));
            return new AiTaskStep("done", target
                ? ModLocalization.Pick("手电已打开", "Flashlight turned on", "Taschenlampe eingeschaltet",
                    "Linterna encendida", "Lampe allumée", "ライトを点けました",
                    "손전등을 켰습니다", "Latarka włączona", "Lanterna ligada", "Фонарик включён")
                : ModLocalization.Pick("手电已关闭", "Flashlight turned off", "Taschenlampe ausgeschaltet",
                    "Linterna apagada", "Lampe éteinte", "ライトを消しました",
                    "손전등을 껐습니다", "Latarka wyłączona", "Lanterna desligada", "Фонарик выключен"), true);
        }

        private static bool? ParseWanted(string target)
        {
            var text = (target ?? "").Trim().ToLowerInvariant();
            if (text.Length == 0) return null;
            if (text == "on" || text == "开" || text == "打开" || text == "亮" || text == "an" || text == "ein" || text == "on." || text == "encender" || text == "allumer" || text == "вкл") return true;
            if (text == "off" || text == "关" || text == "关闭" || text == "灭" || text == "aus" || text == "off." || text == "apagar" || text == "éteindre" || text == "выкл") return false;
            return null;
        }

        /// <summary>手电是否亮着：优先看 Light2D.enabled，读不到再看强度。</summary>
        private static bool TryReadLight(HumanCharacterController player, out bool on)
        {
            on = false;
            try
            {
                var light = player.handFlashLight;
                if (light == null) return false;
                try { on = light.enabled; return true; } catch { }
                try { on = light.intensity > 0.001f; return true; } catch { }
                return false;
            }
            catch { return false; }
        }

        // ── 轻量目标高亮（供 API 基础模式使用：只标一个目标，10 秒自动回收） ──
        private static UnityEngine.GameObject _activeHighlight;
        private static float _highlightUntil;

        internal static void ClearHighlight()
        {
            if (_activeHighlight == null) return;
            try
            {
                var gc = GameController.instance;
                if (gc != null) gc.PushItemHighlightObject(_activeHighlight);
                else _activeHighlight.SetActive(false);
            }
            catch { }
            _activeHighlight = null;
            _highlightUntil = 0f;
        }

        internal static void TickHighlight()
        {
            if (_activeHighlight == null) return;
            if (DropOutlinePlugin.WorldTearingDown || UnityEngine.Time.unscaledTime >= _highlightUntil)
                ClearHighlight();
        }

        internal static bool HighlightTarget(string kind, out string summary)
        {
            summary = "";
            if (DropOutlinePlugin.WorldTearingDown) return false;

            var k = (kind ?? "").Trim().ToLowerInvariant();
            if (k == "clear" || k == "none" || k == "cancel")
            {
                ClearHighlight();
                summary = ModLocalization.Pick("已取消目标标记", "Target highlight cleared", "Zielmarkierung aufgehoben",
                    "Marcador quitado", "Marquage annulé", "ターゲット表示を解除しました",
                    "대상 강조 표시 해제", "Usunięto oznaczenie celu", "Marcação removida", "Метка цели снята");
                return true;
            }

            try
            {
                var gc = GameController.instance;
                var player = gc != null ? gc.playerCharacter : null;
                if (gc == null || player == null) return false;

                var mc = MapController.instance;
                var cam = UnityEngine.Camera.main;
                UnityEngine.Vector2 pPos = player.transform.position;
                bool wantThreat = k == "nearest_threat" || k == "threat" || k == "enemy";
                UnityEngine.Vector3? found = null;
                string label = "";

                if (wantThreat)
                {
                    float best = 36f;
                    int pCamp = player.characterData != null ? player.characterData.camp : 0;
                    int? pFloor = player.characterData != null ? (int?)player.characterData.zLevel : null;
                    var list = gc.characterList;
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            var c = list[i];
                            if (c == null || c == player || c.deadFlag || c.characterData == null) continue;
                            if (c.characterData.deadFlag || c.characterData.camp == pCamp) continue;
                            if (pFloor.HasValue && c.characterData.zLevel != pFloor.Value) continue;
                            UnityEngine.Vector3 cp = c.transform.position;
                            if (cam != null)
                            {
                                var vp = cam.WorldToViewportPoint(cp);
                                if (vp.z <= 0f || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) continue;
                            }
                            if (mc != null && !mc.IsInSightByRaycast(cp)) continue;
                            float d = UnityEngine.Vector2.Distance(new UnityEngine.Vector2(cp.x, cp.y), pPos);
                            if (d < best)
                            {
                                best = d;
                                found = cp;
                                label = string.IsNullOrEmpty(c.characterData.characterName)
                                    ? ModLocalization.Pick("敌对目标", "Hostile", "Feindziel", "Hostil", "Cible hostile", "敵対目標", "적대 목표", "Wróg", "Alvo hostil", "Враждебная цель")
                                    : c.characterData.characterName;
                            }
                        }
                    }
                    if (!found.HasValue)
                    {
                        summary = ModLocalization.Pick("周围 36 米内暂无可见威胁", "No visible threats within 36m",
                            "Keine sichtbaren Bedrohungen in 36 m", "Sin amenazas visibles en 36 m", "Aucune menace visible dans 36 m",
                            "周囲36m以内に目視できる脅威はありません", "주변 36m 이내에 보이는 위협이 없습니다",
                            "Brak widocznych zagrożeń w promieniu 36 m", "Nenhuma ameaça visível em 36 m", "В радиусе 36 м угроз не видно");
                        return false;
                    }
                    label = ModLocalization.IsZh
                        ? $"{label}（约 {best:F0} 米）"
                        : $"{label} (~{best:F0} m)";
                }
                else
                {
                    float best = 32f;
                    var items = mc != null ? mc.allGroundItems : null;
                    if (items != null)
                    {
                        for (int i = 0; i < items.Count; i++)
                        {
                            var it = items[i];
                            if (it == null || it.itemData == null || it.gameObject == null || !it.gameObject.activeInHierarchy) continue;
                            if (!it.isOnScreen || !it.isInRange || it.standHiddenFlag || it.submergedFlag) continue;
                            UnityEngine.Vector3 ip = it.transform.position;
                            if (!mc.IsInSightByRaycast(ip)) continue;
                            float d = UnityEngine.Vector2.Distance(new UnityEngine.Vector2(ip.x, ip.y), pPos);
                            if (d < best)
                            {
                                best = d;
                                found = ip;
                                try { label = it.itemData.GetItemName(); } catch { }
                                if (string.IsNullOrEmpty(label))
                                    label = ModLocalization.Pick("地面物资", "Ground item", "Bodenfund", "Objeto en suelo", "Objet au sol", "地面の物資", "바닥 물자", "Przedmiot", "Item no chão", "Предмет на земле");
                            }
                        }
                    }
                    if (!found.HasValue)
                    {
                        summary = ModLocalization.Pick("附近暂无视线内可标记的地面物资", "No visible ground loot nearby",
                            "Keine sichtbare Beute in der Nähe", "Sin botín visible cerca", "Aucun butin visible à proximité",
                            "近くに目視できる物資はありません", "주변 시야 내 표시할 물자가 없습니다",
                            "Brak widocznych łupów w pobliżu", "Nenhum item visível por perto", "Рядом не видно предметов");
                        return false;
                    }
                    label = ModLocalization.IsZh
                        ? $"{label}（约 {best:F0} 米）"
                        : $"{label} (~{best:F0} m)";
                }

                ClearHighlight();
                var go = gc.PullItemHighlightObject();
                if (go == null)
                {
                    summary = ModLocalization.Pick("当前无法创建高亮标记", "Cannot create highlight marker now",
                        "Markierung derzeit nicht möglich", "No se pudo crear el marcador", "Impossible de créer le marqueur",
                        "現在マーカーを作成できません", "지금은 강조 표시를 만들 수 없습니다",
                        "Nie można utworzyć znacznika", "Não foi possível criar o marcador", "Не удалось создать метку");
                    return false;
                }
                go.transform.position = found.Value;
                go.SetActive(true);
                _activeHighlight = go;
                _highlightUntil = UnityEngine.Time.unscaledTime + 10f;
                summary = ModLocalization.Pick("已标出：", "Marked: ", "Markiert: ", "Marcado: ", "Marqué : ",
                    "マークしました：", "표시 완료: ", "Oznaczono: ", "Marcado: ", "Отмечено: ") + label;
                return true;
            }
            catch (Exception e)
            {
                summary = ModLocalization.Pick("标记失败：", "Highlight failed: ", "Markierung fehlgeschlagen: ", "Error al marcar: ",
                    "Échec du marquage : ", "マーク失敗：", "표시 실패: ", "Błąd oznaczania: ", "Falha ao marcar: ", "Ошибка метки: ") + e.Message;
                return false;
            }
        }
    }
}
