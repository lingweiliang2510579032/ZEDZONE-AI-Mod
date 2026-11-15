# ZEDZONE-AI-Mod (`ZED · AI伙伴` v1.6.0)

《隔离区（ZED ZONE）》的 **BepInEx IL2CPP AI 伙伴模组（`ZEDAI.dll`）** 与 **DSH 联动插件（`@local/zed-ai`）**，支持 **API 直连 / DSH 本地 Agent** 两种接入方式，以及 **聊天陪伴 / 接管招募（雇佣）NPC / 联机玩家** 三种表现形式与单人/联机环境自适应。

> 开源协议：本项目采用 [MIT License](./LICENSE) 开源。

---

## 一、核心架构（接入模式 × 表现形式 × 单/联机适配）

### 1. 两种接入模式（思考决策端）
- **`API 模式`（直连大模型）**：模组直接调用配置的模型接口（支持 OpenAI `chat/completions`、`openai responses`、Anthropic `messages` 以及本地 Ollama），无需额外后端即可运行全部三种表现形式。
- **`DSH 模式`（`@local/zed-ai` 本地 Agent 桥接）**：通过游戏根目录 `ZED AI\ai-state.json` 与 `ai-command.json` 与 DeepSeek Harness（DSH）插件协同，具备按角色卡持久化的长记忆会话、`zed_knowledge` 按需查询、以及从主菜单/局内调用 `room_op` 开房进房等深度集成能力。

### 2. 三种表现形式（AI 行为与操作边界）
| 模式代码 | 名称 | 核心行为逻辑与严格边界 |
| :--- | :--- | :--- |
| `companion` | **聊天陪伴模式** | 通过游戏官方聊天框（`NetChatSync`）与玩家自然对话，主动进行血量/饥渴/手电电量/可加点数/视线威胁提醒（内置去复读滑窗抑制机械重复），并提供建议与允许范围内的辅助任务（整理背包、装填、拾取、制造规划/制造、单堆安全丢弃、同楼层已检视容器归并）。**严格反作弊**：绝不读取未解锁配方、未检视箱子、墙后或异层目标。 |
| `npc` | **接管招募（雇佣）NPC 模式** | 仅在**单人离线或联机房主**且玩家已招募雇佣 NPC 队友时接管。严格遵循《隔离区》官方雇佣 NPC 能力范围（`Basic.FollowPlayer` / `Basic.StayAtPosition` / `Basic.CollectResource` / `Basic.CollectSpecifiedItem` / `Combat.FireAtWill` / `Combat.ProtectPlayer` / `Combat.StopFire` / `WeaponPreference.*` / `OneTime.HealPlayer` / `OneTime.ThrowGrenade` 及既有 `GOAPGoal_*` 目标），不越界虚构建造、合配方或驾驶能力。 |
| `player` | **联机玩家模式** | 将 AI 视为**独立正常联机玩家**：自动在官方 `MpCharacters` 中创建/读取 AI 专属联机角色卡（`zedai_<name>`），以独立 `playerId` 注册到 `NetworkManager.members`、`NetLobby.SetLocalProfile` 与 `NetPlayerSync.SpawnRemotePlayer`，具备独立聊天身份、`PushSnap` 网络同步、自动跟随/迎战/驻守/拾荒、自疗与手电联动；单人游玩时可开启「自动转入房主局」将 AI 玩家直接拉入队伍。 |

### 3. 单人 vs 联机（房主 / 客户端）适配矩阵
| 运行环境 | `companion` 聊天陪伴 | `npc` 接管雇佣 NPC | `player` 联机玩家 |
| :--- | :--- | :--- | :--- |
| **单人离线** | 完整对话、提醒与权限内辅助任务 | 完整指挥已雇佣 NPC 队友 | 支持开启自动开房或单人跟打协同 |
| **联机房主 (Host)** | 只读规划建议与本地高亮/手电 | 掌握房主侧雇佣 NPC 完整指挥权威 | 完整管理 AI 玩家同步、`MsgChat` 广播与迟到玩家补同步 |
| **联机客户端 (Client)** | 只读规划与建议 | 尊重房主权威，不越权改写远端 NPC | 大厅自动准备，入图后本机辅助跟打与聊天互动 |

---

## 二、发布文件说明与安装指南

在 GitHub Releases（及仓库 `release/` 目录）提供两个核心发布产物：
1. **`ZEDAI.dll`**：游戏模组本体（BepInEx IL2CPP 插件）。
2. **`zed-ai-dsh-plugin-v1.6.0.zip`**（源码对应 `dsh-plugin/zed-ai/`）：DSH `@local/zed-ai` 联动插件包。

### 安装 `ZEDAI.dll`（必装）
1. 确保游戏已安装 **BepInEx 6 (IL2CPP)**。
2. 将 `ZEDAI.dll` 放入：
   ```text
   <你的游戏目录>\ZED ZONE\BepInEx\plugins\ZEDAI.dll
   ```
3. 启动游戏后打开 **游戏设置 → AI 伙伴 → 打开 AI 伙伴设置**，在 4 页独立配置弹窗中按需设置：
   - **1. 接入与表现形式**：切换 `API / DSH` 与 `聊天陪伴 / 接管雇佣NPC / 联机玩家`。
   - **2. 模式专属与联机**：配置 AI 名字、称呼、人设、玩家姿态与自动开房/快速开房按钮。
   - **3. API / DSH 接口说明与配置**：填写 API 协议 / BaseUrl / 模型 / Key，或查看 DSH 桥接状态。
   - **4. 辅助与权限边界**：独立控制聊天框、自主行动、主动汇报以及丢弃/制造/容器归并写入权限。

### 安装 DSH 插件 `@local/zed-ai`（使用 DSH 模式时安装）
1. 解压 `zed-ai-dsh-plugin-v1.6.0.zip`（或复制仓库 `dsh-plugin/zed-ai/` 下的运行文件）到 DSH Profile 目录：
   ```text
   %USERPROFILE%\.dsh\profiles\desktop\node_modules\@local\zed-ai\
   ```
2. 在 `%USERPROFILE%\.dsh\profiles\desktop\cordis.patch.yml` 的 `insert` 列表中加入（将路径替换为你本机的实际游戏目录）：
   ```yaml
   - id: zed-ai
     name: "@local/zed-ai"
     config:
       stateDir: "D:\\steam\\steamapps\\common\\ZED ZONE\\ZED AI"
       autoConnect: true
       workspacePath: "D:\\steam\\steamapps\\common\\ZED ZONE\\ZED AI"
       gameDir: "D:\\steam\\steamapps\\common\\ZED ZONE"
   ```
3. 重启或刷新 DSH（前端缓存可用 `Ctrl + R`），在左侧打开 **ZED AI** 面板并开启开关即可自动读取当前《隔离区》状态与角色卡记忆。

---

## 三、目录结构与源码构建

```text
.
├── MOD1-DropOutline/          # ZEDAI.dll C# (.NET 6 / BepInEx IL2CPP) 源码
│   ├── DropOutline.csproj
│   ├── DropOutlinePlugin.cs   # Harmony 补丁、主循环挂载与场景卸载保护
│   ├── AiCompanion.cs         # 核心调度：API/DSH 状态机、3 种表现形式、去复读与任务派发
│   ├── AiCoopPlayer.cs        # 联机玩家模式：MpCharacters 角色卡、NetPlayerSync 同步、开房/进房
│   ├── AiPrompt.cs            # 分模式系统提示词与反作弊边界约束
│   ├── AiNetChat.cs           # 官方 NetChatSync 对话接入与联机玩家身份聊天广播
│   ├── ModSettingsUi.cs       # 游戏内 4 分页独立设置弹窗
│   └── ...
├── dsh-plugin/zed-ai/         # DSH 插件 (@local/zed-ai) 源码与 Node 单测
│   ├── index.js               # zed_state / zed_order / zed_chat / zed_knowledge 工具注册
│   ├── bridge.js              # scoped 状态检验、npc_cmd / coop_op / room_op 校验与上下文构建
│   ├── client.js              # DSH 侧边栏与状态详情 React UI
│   ├── connect.js             # 角色卡持久会话映射与恢复
│   ├── paths.js               # Steam 路径识别
│   └── test/                  # Node.js 单元测试 (*.test.mjs)
├── release/                   # 编译产物目录（含 ZEDAI.dll）
├── LICENSE                    # MIT License
└── README.md
```

### 从源码构建
- **编译 `ZEDAI.dll`**（需安装 .NET 6/8 SDK，并指向本机已生成 IL2CPP interop 的《隔离区》目录）：
  ```powershell
  dotnet build MOD1-DropOutline/DropOutline.csproj -c Release -p:GameDir="D:\steam\steamapps\common\ZED ZONE" -p:Deploy=false
  ```
- **运行 DSH 插件单元测试**：
  ```powershell
  node --test dsh-plugin/zed-ai/test/bridge.test.mjs dsh-plugin/zed-ai/test/client.test.mjs dsh-plugin/zed-ai/test/coop-mode.test.mjs dsh-plugin/zed-ai/test/paths.test.mjs
  ```

---

## 四、开源协议

本项目遵循 [MIT License](./LICENSE)。
