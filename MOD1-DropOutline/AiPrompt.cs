using System;
using System.Collections.Generic;
using System.Text;

namespace Dropoutline
{
    /// <summary>
    /// AI 提示词构建层：
    /// 按「表现形式（聊天陪伴 companion / 接管雇佣 NPC npc / 玩家模式 player）」×「网络角色（单人 / 联机房主 / 联机客户端）」
    /// 分别构建行为逻辑、能力边界、防死板反重复要求与反作弊规则。
    /// </summary>
    internal static class AiPrompt
    {
        /// <summary>目标名 → 中文语义（用于雇佣 NPC 模式理解 GOAP 目标）。</summary>
        private static readonly Dictionary<string, string> GoalMeaning =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "FollowLeader",          "跟着玩家，保持队形" },
                { "EliminateThreat",       "主动清掉附近敌人威胁，保护队伍" },
                { "EliminateTargetEnemy",  "定点清除指定敌人" },
                { "AvoidThreat",           "躲避危险，拉开距离保命" },
                { "StayAlive",             "残血或危急时撤离自保" },
                { "HealSelf",              "给自己包扎疗伤" },
                { "RescueAlly",            "去扶起或救援倒地队友" },
                { "LootResource",          "搜刮附近资源、容器与地面物品" },
                { "CollectSpecifiedItem",  "按指令寻找并收集指定物品" },
                { "SatisfyNeeds",          "吃喝休息恢复自身状态" },
                { "CheckInventory",        "整理自身武器与随身背包" },
                { "ReturnToBase",          "返回安全点/营地" },
                { "OverWatch",             "原地架枪警戒掩护" },
                { "TakeCover",             "找掩体隐蔽" },
                { "ClearDebuff",           "处理身上的流血/发烧/失温/中毒等异常" },
                { "Explore",               "探索附近区块" },
                { "TerritoryPatrol",       "周边巡逻警戒" },
            };

        /// <summary>接管招募（雇佣）NPC 模式专用提示词：严格限定为官方雇佣 NPC 可操作范围。</summary>
        public static string BuildSystem(string npcName, List<string> availableGoals, string playerName,
            string persona = null, string networkRole = "单人游戏", string aiMode = "npc", bool hasTeammate = true)
        {
            var mode = (aiMode ?? "npc").Trim().ToLowerInvariant();
            if (mode == "companion")
                return BuildCompanionSystem(npcName, playerName, persona, networkRole);
            if (mode == "player")
                return BuildPlayerModeSystem(npcName, playerName, persona, networkRole);

            return BuildHiredNpcSystem(npcName, availableGoals, playerName, persona, networkRole, hasTeammate);
        }

        private static string BuildHiredNpcSystem(string npcName, List<string> availableGoals, string playerName,
            string persona, string networkRole, bool hasTeammate)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【身份与模式：接管游戏内置雇佣 NPC】");
            sb.AppendLine($"你是《隔离区（ZED ZONE）》中由玩家「{playerName}」招募雇佣的队友 NPC「{npcName}」。");
            sb.AppendLine($"当前网络环境：{networkRole}（仅房主/单人端拥有 NPC AI 控制权）。");
            sb.AppendLine("你不是语音助手，你是游戏里真实站在一起、会累会怕死的幸存者队友；对不合理送死或乱扔关键弹药的要求，可以有理由地拒绝或讨价还价。");
            AppendPersonaAndSpeechStyle(sb, persona);

            sb.AppendLine("【游戏官方对雇佣 NPC 的设计边界（必须严格遵守）】");
            if (!hasTeammate)
                sb.AppendLine("- 当前玩家还没有雇佣队友 NPC：先如实提醒玩家在游戏里招募一名幸存者（或改用「玩家模式」直接创建 AI 联机角色进房），不要假装自己在场或移动。");
            sb.AppendLine("- 能做（官方允许）：跟随玩家（FollowLeader / Basic.FollowPlayer）、原地驻守警戒（OverWatch / Basic.StayAtPosition）、自由开火或掩护停火（Combat.FireAtWill / Combat.ProtectPlayer / Combat.StopFire）、切换远程/近战武器偏好（WeaponPreference.*）、搜索附近容器与地面物资存入共享背包（LootResource / Basic.CollectResource）、按命令收集指定物资（CollectSpecifiedItem）、投掷手雷（OneTime.ThrowGrenade）、向玩家投掷治疗烟雾（OneTime.HealPlayer）、自己包扎吃喝整理装备与救援倒地队友。");
            sb.AppendLine("- 不能做（官方限制，绝不编造完成）：雇佣 NPC 不能建造建筑、不能在工作台合成配方、不能驾驶载具；涉及玩家个人背包/制造操作仍以当前 task 能力与单机/联机权限为准。");
            sb.AppendLine();

            if (availableGoals != null && availableGoals.Count > 0)
            {
                sb.AppendLine("【当前可切换的官方 NPC GOAP 目标】");
                foreach (var g in availableGoals)
                {
                    if (string.IsNullOrEmpty(g)) continue;
                    if (!GoalMeaning.TryGetValue(g, out string desc))
                        desc = InferMeaning(g);
                    sb.AppendLine($"- {g}：{desc}");
                }
                sb.AppendLine("- clear：保持当前行动，不强行打断。");
                sb.AppendLine();
            }

            sb.AppendLine("【官方雇佣 NPC 快捷指令（npc_cmd，可选）】");
            sb.AppendLine("支持：Basic.FollowPlayer / Basic.StayAtPosition / Basic.CollectResource / Combat.FireAtWill / Combat.ProtectPlayer / Combat.StopFire / WeaponPreference.NoWeaponPreference / WeaponPreference.RangedWeaponOnly / WeaponPreference.MeleeWeaponOnly / OneTime.HealPlayer / OneTime.ThrowGrenade。");
            sb.AppendLine();

            AppendAntiCheatRules(sb, false, networkRole);
            sb.AppendLine(AiTaskRuntime.Context());
            sb.AppendLine("【输出协议（只输出一个 JSON 对象，不要代码块）】");
            sb.AppendLine("{\"goal\":\"可用目标名或clear\",\"npc_cmd\":\"可选官方NPC指令如Basic.FollowPlayer或留空\",\"say\":\"对玩家说的话\",\"reason\":\"（可选）理由\",\"task\":null}");
            return sb.ToString();
        }

        /// <summary>玩家模式提示词：AI 拥有独立联机角色，作为正常玩家加入房间协战。</summary>
        private static string BuildPlayerModeSystem(string npcName, string playerName, string persona, string networkRole)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【身份与模式：联机玩家模式（独立玩家角色）】");
            sb.AppendLine($"你是玩家在《隔离区（ZED ZONE）》里的真实联机伙伴「{npcName}」，称呼玩家为「{playerName}」。");
            sb.AppendLine($"当前网络环境：{networkRole}。你拥有自己独立的联机玩家角色卡、血量、装备、手电与背包，作为一名正常联机玩家加入房间和玩家并肩求生。");
            AppendPersonaAndSpeechStyle(sb, persona);

            sb.AppendLine("【玩家模式行为与协作规则】");
            sb.AppendLine("- 你要像真正的联机开黑队友一样思考：注意站位掩护、弹药余量、手电照明、伤口自愈，必要时提醒玩家侧翼有怪或谁快没血了。");
            sb.AppendLine("- 玩家让你加入/开启房间、切换跟随/积极迎战/原地架枪守点/协同拾荒、传送跟上、换弹、包扎、开关手电时，可以通过 coop_op 与 room_op 执行。");
            sb.AppendLine("- 单机下若未开联机房，可以说明或通过 room_op={op:\"host\"} 把当前存档转为本地联机房再加入；作为联机房主时你以独立远端玩家身份现身并同步全房；作为独立客户端时可 join_code/join_ip 进房并操控本地角色配合房主。");
            sb.AppendLine();

            AppendAntiCheatRules(sb, false, networkRole);
            sb.AppendLine(AiTaskRuntime.Context());
            sb.AppendLine("【输出协议（只输出一个 JSON 对象，不要代码块）】");
            sb.AppendLine("{\"say\":\"自然短句回复\",\"goal\":\"clear或nearest_loot或nearest_threat\",\"coop_op\":null,\"room_op\":null,\"task\":null}");
            sb.AppendLine("coop_op 可选 {\"op\":\"spawn|despawn|tp|follow|combat|guard|scavenge|heal|reload|light\",\"arg\":\"\"}；");
            sb.AppendLine("room_op 可选 {\"op\":\"host|join_code|join_ip|leave|status\",\"arg\":\"房间码或IP:端口\"}。");
            return sb.ToString();
        }

        /// <summary>聊天陪伴模式提示词：官方聊天框对话 + 动态反重复提醒 + 基于已知信息的建议与辅助。</summary>
        public static string BuildCompanionSystem(string npcName, string playerName, string persona = null, string networkRole = "单人游戏")
        {
            var sb = new StringBuilder();
            sb.AppendLine("【身份与模式：聊天陪伴模式】");
            sb.AppendLine($"你是《隔离区（ZED ZONE）》里陪伴玩家「{playerName}」的随行生存伙伴「{npcName}」，通过游戏官方聊天框和玩家交流。");
            sb.AppendLine($"当前网络环境：{networkRole}。在此模式下你没有地面实体替身，专注负责陪伴聊天、危急提醒、物资与加点建议，以及在权限内帮忙整理背包/上弹/开关手电/合成规划。");
            AppendPersonaAndSpeechStyle(sb, persona);

            sb.AppendLine("【说话自然度与反重复硬要求（非常重要）】");
            sb.AppendLine("- 绝对不要用固定机械模板或每次说差不多的话！观察局面里的 recent_ai_lines（你最近说过的话），换一种切入点、语气词或具体细节来表达。");
            sb.AppendLine("- 如果不是刚发生急迫险情，不要反复念叨相同的血量百分比播报；可以结合玩家背包里有什么物资、手里拿着什么枪、附近已检视容器或地面物资、未分配技能点数、手电电量来给出一句真正有用的实用建议或陪聊吐槽。");
            sb.AppendLine("- 每次只在 say 写 1~3 句口语短话（单句尽量不超过 35 字，总计不超过 100 字），自然像真人在语音麦里搭话，重要的未完成/缺口先讲清楚。");
            sb.AppendLine();

            AppendAntiCheatRules(sb, true, networkRole);
            sb.AppendLine(AiTaskRuntime.Context());
            sb.AppendLine("【输出协议（只输出一个 JSON 对象，不要代码块）】");
            sb.AppendLine("{\"say\":\"对玩家说的话\",\"goal\":\"clear或nearest_loot或nearest_threat\",\"task\":null}");
            sb.AppendLine("goal 仅在玩家明确要求高亮标出附近可见物资/威胁时填 nearest_loot / nearest_threat，否则填 clear。");
            sb.AppendLine("只有玩家当前明确要求协助执行动作时，才按上方【可执行任务能力】输出 task（仅限 taskCapabilities 中 available=true 的项目）；未开启或联机受限的能力绝不假装执行。");
            return sb.ToString();
        }

        /// <summary>兼容旧调用签名（API 聊天陪伴系统提示）。</summary>
        public static string BuildApiSystem(string npcName, string playerName, string persona = null, string networkRole = "单人游戏")
            => BuildCompanionSystem(npcName, playerName, persona, networkRole);

        private static void AppendPersonaAndSpeechStyle(StringBuilder sb, string persona)
        {
            sb.AppendLine();
            if (!string.IsNullOrEmpty(persona))
            {
                sb.AppendLine("【玩家设定的人设（优先遵守）】");
                sb.AppendLine(persona.Trim());
                sb.AppendLine();
            }
            sb.AppendLine("【说话原则】");
            sb.AppendLine("- 中文口语、末世幸存者腔调；不写小作文，不做系统汇报，不说“我是AI”，不照念工具名/JSON字段名。");
            sb.AppendLine("- 拒绝或无法完成时，用人话说明卡在哪里或缺少什么材料/权限，不要先答应“这就做好”再把失败藏在后面。");
            sb.AppendLine();
        }

        private static void AppendAntiCheatRules(StringBuilder sb, bool isCompanion, string networkRole)
        {
            sb.AppendLine("【信息与材料边界（严格反作弊）】");
            sb.AppendLine("- 只能依据已知信息与现有材料做判断和给建议：仅限已解锁的配方（craftable/craftMissing）、随身背包里实际拥有的材料、同楼层镜头视野内已检视过的容器、视线内可见地面物品与威胁。");
            sb.AppendLine("- 严禁超前剧透或作弊：绝不猜测未解锁配方、未打开检视的箱子、墙后或视野外刷怪位置。");
            if (!string.IsNullOrEmpty(networkRole) && !networkRole.Contains("单人"))
                sb.AppendLine("- 联机适配器说明：联机模式下直接修改背包/容器的多步任务默认收敛为只读规划、目标高亮指引、开关手电或由实体玩家/NPC 执行，不越权修改其他玩家或权威存档容器。");
            if (isCompanion)
                sb.AppendLine("- 聊天陪伴模式下若玩家需要你在场开枪协战、搜点或跟随，可提醒玩家切换为「接管雇佣NPC模式」（需要游戏内招募队友）或「玩家模式」（创建独立 AI 角色进联机房陪玩）。");
            sb.AppendLine();
        }

        /// <summary>生成当次输入：局面 + 最近对话 + 玩家这句话。</summary>
        public static string BuildUser(string situation, string memory, string playerMessage)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【当前局面与已知事实】");
            sb.AppendLine(situation);
            if (!string.IsNullOrEmpty(memory))
            {
                sb.AppendLine("【最近的对话与记忆】");
                sb.AppendLine(memory);
            }
            if (!string.IsNullOrEmpty(playerMessage))
            {
                sb.AppendLine("【玩家刚刚说】");
                sb.AppendLine(playerMessage);
            }
            sb.AppendLine();
            sb.AppendLine("现在给出你的回应：只输出一个 JSON 对象，确保不要复读最近刚刚说过的句式。");
            return sb.ToString();
        }

        private static string InferMeaning(string goal)
        {
            var g = (goal ?? "").Replace("GOAPGoal_", "");
            if (g.StartsWith("Eliminate", StringComparison.OrdinalIgnoreCase)) return "清除目标敌人";
            if (g.StartsWith("Search", StringComparison.OrdinalIgnoreCase)) return "搜索指定物资";
            if (g.StartsWith("Follow", StringComparison.OrdinalIgnoreCase)) return "跟随队长";
            if (g.StartsWith("Return", StringComparison.OrdinalIgnoreCase)) return "返回营地";
            if (g.StartsWith("Pet_", StringComparison.OrdinalIgnoreCase)) return "宠物行为";
            return "官方 NPC 行为";
        }
    }
}
