# AI伴侣（测试版v0.1） — 《隔离区（ZED ZONE）》AI 伙伴模组

> **当前版本**：`v0.1`（测试版）｜**版本命名规则**：从 `v0.1` 起步，后续每次更新递增 `+0.1`（`v0.2`、`v0.3`……）  
> **开源协议**：[MIT License](./LICENSE)｜**开源仓库**：https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod｜**创意工坊**：https://steamcommunity.com/sharedfiles/filedetails/?id=3840378952

一款面向《隔离区（ZED ZONE）》（Unity 2023.1 / IL2CPP）的 **AI 伙伴代码模组（`ZEDAI.dll`）**，支持 **API 直连大模型** 与 **DSH 本地 Agent（`@local/zed-ai`）** 两种大脑接入方式，提供 **聊天陪伴 / 接管招募雇佣 NPC / 联机玩家** 三种表现形式，并自适应 **单人离线 / 联机房主 / 联机客户端** 三种网络环境。

---

## 目录

- [一、功能特色一览](#一功能特色一览)
- [二、安装教程](#二安装教程)
  - [方式 A：Steam 创意工坊订阅（推荐）](#方式-asteam-创意工坊订阅推荐)
  - [方式 B：GitHub Releases 手动安装](#方式-bgithub-releases-手动安装)
  - [DSH 插件安装（仅使用 DSH 模式时需要）](#dsh-插件安装仅使用-dsh-模式时需要)
- [三、新手 3 分钟快速上手（API 模式）](#三新手-3-分钟快速上手api-模式)
- [四、三种表现形式与玩法说明](#四三种表现形式与玩法说明)
  - [1. 聊天陪伴模式（默认推荐）](#1-聊天陪伴模式默认推荐)
  - [2. 接管招募（雇佣）NPC 模式](#2-接管招募雇佣npc-模式)
  - [3. 联机玩家模式（AI 作为独立玩家加入房间）](#3-联机玩家模式ai-作为独立玩家加入房间)
- [五、单人模式 vs 联机模式适配矩阵](#五单人模式-vs-联机模式适配矩阵)
- [六、游戏内 4 页设置菜单说明](#六游戏内-4-页设置菜单说明)
- [七、常见问题排查（FAQ）](#七常见问题排查faq)
- [八、目录结构与源码构建](#八目录结构与源码构建)
- [九、问题反馈与开源信息](#九问题反馈与开源信息)

---

## 一、功能特色一览

1. **单人与联机统一原生聊天体验**：
   - 在游戏内直接接入《隔离区》左侧官方聊天栏（`NetChatBar` + `NetChatSync`），**单人模式下也可直接按 `Enter` 打开官方聊天框打字对话**，不再弹出遮挡左下角手表 HUD 的冗余底部浮层。
   - 内置自然分句与防乱切策略：避免把长句硬截断在数字或英文单词中间（例如不会把点数和数字切成两行）。
2. **双接入模式（大脑思考端）**：
   - **API 模式**：模组内直连大模型接口，支持 OpenAI `Chat Completions`、`OpenAI Responses`、`Anthropic Messages` 以及本地 `Ollama`，不依赖外部宿主，装好模组填好 Key 即可直接玩。
   - **DSH 模式**：通过游戏根目录 `ZED AI\ai-state.json` / `ai-command.json` 与 DeepSeek Harness（`@local/zed-ai`）本地 Agent 协同，支持按存档角色卡隔离的持久对话记忆、`zed_knowledge` 按需查阅游戏配方/建造/物品/技能/词条面板数据，以及在主菜单/大厅遥控开房进房。
3. **三种表现形式（AI 身份与操作逻辑）**：
   - **聊天陪伴模式** / **接管招募雇佣 NPC 模式** / **联机玩家模式**，支持随时在二级菜单中切换。
4. **严格反作弊原则**：
   - 所有模式与辅助动作只读取并利用 **已知信息与现有材料**：已解锁配方、随身背包实际携带物品、同楼层镜头视野内已打开检视的容器、视线内可见地面物品与可见威胁。
   - 绝不剧透未解锁配方、未打开过的箱子或墙后/视野外刷怪。所有直接改动背包/合成/丢弃/容器搬运的写权限开关**默认全部关闭**，由玩家自主决定是否开启。
5. **不卡顿、不抢操作**：
   - 后台任务（整理背包、装填、拾取、合成、安全丢弃等）在玩家移动、瞄准、打开背包拖拽、睡觉、驾驶或使用手柄/键鼠操作时自动让行；玩家停止输入并静默 1 秒后才温和推进。

---

## 二、安装教程

### 方式 A：Steam 创意工坊订阅（推荐）

1. **先安装必需前置——代码模组加载器（BepInEx IL2CPP）**：
   - 前往 Steam 创意工坊订阅 **ZED ZONE 代码模组加载器**（工坊 ID：`3834614258`）。
   - 等待 Steam 下载完成后，打开其本地目录：
     ```text
     <你的Steam库>\steamapps\workshop\content\1211600\3834614258\
     ```
   - **退出游戏**，双击运行其中的 `install.cmd`（只需首次运行一次，用来给游戏安装 BepInEx 与加载器 `ZedModsLoader.dll`）。
   - *(可选推荐)* 也可订阅 `Zedtool-模组设置`（工坊 ID：`3838476805`），按 `F7` 统一调节所有 BepInEx 模组参数（本模组自身也已内置游戏选项二级设置菜单，不装 `Zedtool` 也能完整配置）。
2. **订阅本模组「AI伴侣（测试版v0.1）」**：
   - 在创意工坊点击订阅后等待 Steam 下载完成。
   - 从 Steam 库正常启动《隔离区》即可，加载器会自动扫描并加载工坊目录中的 `ZEDAI.dll`（游戏左上角会提示已加载的代码模组清单，12 秒后自动消失或点击关闭）。
   - **注意**：通过工坊订阅时，请勿再额外往 `BepInEx\plugins\` 手动复制旧版 `ZEDAI.dll`，避免新旧版本冲突。

### 方式 B：GitHub Releases 手动安装

1. 如未使用创意工坊加载器，请确保游戏已安装 **BepInEx 6 (IL2CPP)**，并推荐配合 `ZedModsLoader.dll` 或直接放入 plugins 目录。
2. 前往 GitHub Releases 下载 **`ZEDAI.dll`**，放入：
   ```text
   <你的游戏根目录>\ZED ZONE\BepInEx\plugins\ZEDAI.dll
   ```
3. 从 Steam 启动游戏（**注意不要直接双击 `ZEDZONE.exe`**，官方需要通过 Steam 启动，否则会直接退出）。

### DSH 插件安装（仅使用 DSH 模式时需要）

> 如果你只使用 **API 模式** 直连大模型，**不需要安装 DSH 插件**，可跳过本节。

1. 获取 `zed-ai-dsh-plugin-v0.1.zip`（创意工坊订阅目录下的随包文件或 GitHub Releases 均可找到）。
2. 将压缩包解压到 DSH 的桌面 Profile 插件目录（目录结构里应直接包含 `index.js`、`bridge.js`、`package.json` 等文件）：
   ```text
   %USERPROFILE%\.dsh\profiles\desktop\node_modules\@local\zed-ai\
   ```
3. 编辑 `%USERPROFILE%\.dsh\profiles\desktop\cordis.patch.yml`，在 `insert` 列表下添加插件配置（路径按你本机实际游戏位置填写）：
   ```yaml
   - id: zed-ai
     name: "@local/zed-ai"
     config:
       stateDir: "D:\\steam\\steamapps\\common\\ZED ZONE\\ZED AI"
       autoConnect: true
       workspacePath: "D:\\steam\\steamapps\\common\\ZED ZONE\\ZED AI"
       gameDir: "D:\\steam\\steamapps\\common\\ZED ZONE"
   ```
4. 完全退出并重启 DSH 客户端，在左侧边栏打开 **ZED AI** 面板并确认连接开关已开启。

---

## 三、新手 3 分钟快速上手（API 模式）

1. **打开设置弹窗**：
   - 启动游戏 → 按 `ESC`（或主菜单点击）进入 **游戏选项** → 点击右侧导航栏的 **AI 伙伴** → 点击 **打开 AI 伙伴设置**，会弹出居中的 4 分页配置窗口（按 `ESC` 或点右上角 `✕` 关闭）。
2. **第 1 页「1. 接入与表现形式」**：
   - 开启 **启用 AI 伙伴**。
   - **接入模式** 选择 `API 模式（模组直连大模型接口）`。
   - **表现形式** 新手推荐保持默认的 `聊天陪伴模式`。
   - 确保 **使用游戏官方聊天框** 为 **开启**。
   - 可自定义 **AI 名字**（默认 `大肥鱼`）、**玩家称呼**（默认 `用户`）及 **人设（性格与腔调）**。
3. **第 3 页「3. API 接口配置」填写模型信息**：

   | 常用接入源 | API 接口协议 | API 根地址 (`BaseUrl`) 填写示例 | 模型名称 (`Model`) 示例 |
   | :--- | :--- | :--- | :--- |
   | **DeepSeek 官方** | `Chat Completions` | `https://api.deepseek.com/v1` | `deepseek-chat` |
   | **OpenAI / 兼容中转** | `Chat Completions` | `https://api.openai.com/v1`（或中转站 `/v1` 地址） | `gpt-4o-mini` 等 |
   | **硅基流动 / 第三方聚合** | `Chat Completions` | `https://api.siliconflow.cn/v1` | `deepseek-ai/DeepSeek-V3` 等 |
   | **Anthropic Claude** | `Anthropic Messages` | `https://api.anthropic.com` | `claude-sonnet-4-20250514` 等 |
   | **本机 Ollama** | `Ollama 本地` | `http://127.0.0.1:11434` | `qwen2.5:14b` 等（Key 可留空） |

   - 在 **API Key** 输入框填入你的密钥并按 **回车** 保存（模组使用本机 Windows DPAPI 加密存盘，不回显原文）。
   - 点击 **测试连接**，确认右下提示连通成功。
4. **进入存档开始对话**：
   - 加载单人或联机存档进入世界后，按 **`Enter`（回车）** 打开左侧官方聊天输入框，输入想说的话再按 **`Enter`** 发送，AI 会以设定的名字与口吻在左侧聊天框分段回复。

---

## 四、三种表现形式与玩法说明

### 1. 聊天陪伴模式（默认推荐）

以随行生存伙伴身份通过官方聊天框与你实时互动，**不需要招募游戏内雇佣 NPC 即可使用**：
- **自然陪伴与动态防复读提醒**：当血量明显下降、口渴/饥饿加重、手电电量低于 20%、身上有未分配技能/词条点数、或视野内出现新威胁时，会用变化多样的末世口语主动搭话提醒（可在第 2 页关闭“主动陪伴提醒”改为仅一问一答）。
- **基于已知信息的实用建议与辅助任务**：可在聊天中直接让她帮你执行以下操作（受第 4 页权限开关与单机/联机规则约束）：
  - **背包整理**：整理随身背包网格排布，腾出连续空位（不改动物品数量与重量）。
  - **装填弹药 / 开关手电 / 拾取附近掉落**：自动核对视线与距离限制。
  - **标出附近物资或威胁**：视觉高亮（`nearest_loot` / `nearest_threat`）视野内已发现目标。
  - **制造规划与直接合成**：核对已解锁配方与背包现有材料缺口；开启“允许 AI 直接合成”后可把满足条件的配方加入官方制造队列。
  - **安全丢弃建议与单堆丢弃**：默认关闭只给建议；开启“允许 AI 丢弃物品”并明确指定具体非保护物品时，通过游戏官方安全落链丢出指定整堆物品。
  - **同楼层已检视容器归并**：默认关闭；开启规划/搬运开关后，仅对你已打开检视过的同楼层视野内箱子做分类归并规划或逐件守恒搬运。

### 2. 接管招募（雇佣）NPC 模式

- **前提**：你需要在游戏中**实际招募到雇佣 NPC 队友**（若只是测试想临时接管附近友好人类 NPC，可在第 2 页开启“测试：临时接管最近友好NPC”）。
- **严格遵循官方雇佣 NPC 机制**：模组对接游戏原生 `GOAPNPCData` / `GOAPAgent` 命令体系与 `GOAPGoal_*` 目标系统，支持指挥雇佣 NPC 执行：
  - **基础行动**：跟随玩家（`Basic.FollowPlayer`）、原地驻守（`Basic.StayAtPosition`）、搜集资源（`Basic.CollectResource`）、收集指定物品（`Basic.CollectSpecifiedItem`）
  - **战斗与武器偏好**：自由开火（`Combat.FireAtWill`）、保护玩家（`Combat.ProtectPlayer`）、停火（`Combat.StopFire`）、不限武器 / 仅远程武器 / 仅近战武器偏好（`WeaponPreference.*`）
  - **一次性指令**：治疗玩家（`OneTime.HealPlayer`）、投掷手雷（`OneTime.ThrowGrenade`）
- **不会越界**：雇佣 NPC 原生不支持配方合成、建造或驾驶载具，模组不会强行注入违和伪能力；当开启“允许有理由拒绝”且指令极度危险时，NPC 也会在聊天中拒绝并说明原因。

### 3. 联机玩家模式（AI 作为独立玩家加入房间）

- **把 AI 当作真实联机队友**：模组会在官方 `MpCharacters` 目录自动加载或创建 AI 专属联机角色卡（`zedai_<名字>.mpchar`），以独立 `playerId` 注册到 `NetworkManager.members`、`NetLobby` 与 `NetPlayerSync`，拥有独立血量、背包、武器、手电、头像位置与全房广播聊天。
- **单人开房 / 联机进房**：
  - 在单人存档中开启 **单机自动开启本地联机房**（或在菜单点击 **开启联机房**），可将当前单人世界转为本地联机房主局，让 AI 玩家直接作为联机队友参战。
  - 若要让 AI 加入好友房间，可在第 2 页填写 **联机房间码**（或 `IP:端口`）点击 **按码进房**。
- **四种协同姿态**：`紧跟玩家` / `积极迎战` / `原地驻守` / `协同拾荒`。
- **常用聊天快捷指令**（直接打在游戏聊天框即可秒响应）：
  - `召唤` / `上线` / `进房陪玩` / `生成化身`
  - `过来` / `传送到我` / `到我身边`
  - `跟我走` / `紧跟我` / `回来跟着`
  - `原地待命` / `守在这里` / `别动` / `驻守`
  - `开启联机` / `开个房间` / `开房间` · `退出房间` / `断开联机`

---

## 五、单人模式 vs 联机模式适配矩阵

| 运行环境 | `聊天陪伴模式` | `接管雇佣NPC模式` | `联机玩家模式` |
| :--- | :--- | :--- | :--- |
| **单人离线** | 完整聊天、动态提醒与权限内所有辅助任务（整理/制造/丢弃/容器归并） | 完整接管并指挥已招募的雇佣 NPC 队友 | 支持开启自动本地开房或快速转为房主局协同作战 |
| **联机房主 (Host)** | 只读规划建议、视野内高亮与手电控制（保护联机库存事务） | 掌握房主侧雇佣 NPC 完整指挥权威 | 完整权威同步 AI 玩家位置/动作、全房 `MsgChat` 广播及迟到玩家补同步 |
| **联机客户端 (Client)** | 只读规划与建议，不越权修改房主或他人存档容器 | 尊重房主权威，不越权改写远端雇佣 NPC | 大厅自动准备，入图后支持本机角色自动协同跟打与聊天互动 |

---

## 六、游戏内 4 页设置菜单说明

| 分页 | 核心配置项 |
| :--- | :--- |
| **1. 接入与表现形式** | 启用 AI 伙伴、接入模式（API / DSH）、表现形式（聊天陪伴 / 接管雇佣NPC / 联机玩家）、使用游戏官方聊天框、AI 名字、玩家称呼、人设 |
| **2. 模式专属内容** | 随第 1 页选中的表现形式动态切换：<br>• 陪伴模式：主动陪伴提醒、有理由拒绝、自动关闭全屏对话框<br>• NPC 模式：NPC 自主决策、临时接管测试开关、有理由拒绝<br>• 玩家模式：自动进入房间、单机自动开本地房、召唤/传送、默认协同姿态、客户端自动协同、房间码与进房按钮 |
| **3. 接口与连接配置** | • API 模式：协议、BaseUrl、Model、API Key（DPAPI 加密）与一键测试连通性<br>• DSH 模式：当前桥接目录状态与快捷「开启联机房」入口 |
| **4. 辅助与权限边界** | 当前网络环境检查、AI 自主行动（定时决策）、允许 AI 丢弃物品、允许 AI 直接合成、允许 AI 归并容器（方案规划 / 实际搬运）、左下角 MOD 辅助聊天日志开关 |

> 所有配置同时支持通过 `Zedtool-模组设置`（`F7`）或编辑 `<游戏目录>\BepInEx\config\zed.ai.cfg` 进行查看与修改。

---

## 七、常见问题排查（FAQ）

1. **进了游戏打开「游戏选项」找不到 `AI 伙伴` / 左上角没显示加载模组？**
   - 检查是否已在《隔离区》游戏根目录装好前置 `ZED ZONE 代码模组加载器`（`3834614258` 里的 `install.cmd` 是否已成功执行）。
   - 请务必通过 **Steam 启动** 游戏，不要直接双击 `ZEDZONE.exe`。
   - 查看 `<游戏目录>\BepInEx\LogOutput.log` 检查是否有报错。
2. **单人模式下按回车（Enter）没有弹出聊天框？**
   - 确认“启用 AI 伙伴”与“使用游戏官方聊天框”均为 **开启**，且游戏当前未处于设置弹窗或暂停菜单状态。
   - 若曾手动复制过旧版 `ZEDAI.dll` 到 `BepInEx\plugins\`，请删除旧副本，保证只加载 `v0.1`（内部版本号 `0.1.0`）最新版。
3. **API 测试连接失败或对话没反应？**
   - 确认 `BaseUrl` 只填到 `/v1`（例如 `https://api.deepseek.com/v1`，**不要**多填 `/chat/completions`），并确认在填写 `API Key` 后按了 **回车** 保存。
   - 若在游玩过程中刚改完 Key/模型，回到游戏世界按回车发一条消息即可按新配置请求。
4. **使用 DSH 模式时面板提示未连接或没回复？**
   - 确认已安装 `@local/zed-ai` 到 `%USERPROFILE%\.dsh\profiles\desktop\node_modules\@local\zed-ai\` 并正确配置 `cordis.patch.yml` 中的游戏与 `ZED AI` 路径，修改后需**完全重启 DSH**。
   - 检查第 1 页接入模式是否切为 `DSH 模式`，且 DSH 侧边栏 `ZED AI` 开关已开启。
5. **如何完全卸载？**
   - 退订创意工坊订阅，或删除手动放入的 `BepInEx\plugins\ZEDAI.dll`。如需同时清理模组生成的数据与记忆，可按需删除 `<游戏目录>\ZED AI\` 与 `<游戏目录>\BepInEx\config\zed.ai.cfg`。

---

## 八、目录结构与源码构建

```text
.
├── MOD1-DropOutline/          # ZEDAI.dll C# (.NET 6 / BepInEx IL2CPP) 源码
│   ├── DropOutline.csproj
│   ├── DropOutlinePlugin.cs   # 插件入口（AI伴侣（测试版v0.1））、Harmony 补丁与场景保护
│   ├── AiCompanion.cs         # 核心调度、API/DSH 双后端状态机与任务/发言分句协调
│   ├── AiCoopPlayer.cs        # 联机玩家模式：MpCharacters 角色卡、NetPlayerSync 同步与开/进房
│   ├── AiNetChat.cs           # 官方 NetChatSync/NetChatBar 接入（支持单人与联机双环境）
│   ├── AiSpeechQueue.cs       # 按句队列展示与自然标点/数字不断词分句器
│   ├── AiPrompt.cs            # 分模式系统提示词与反作弊边界约束
│   ├── ModSettingsUi.cs       # 游戏内 4 分页独立二级设置弹窗
│   └── ...
├── dsh-plugin/zed-ai/         # DSH 插件 (@local/zed-ai) 源码与 Node 单元测试
├── publishing/                # Steam 创意工坊打包目录、封面生成脚本与发布工具
├── release/                   # 编译产物目录（ZEDAI.dll、DSH 插件压缩包）
├── LICENSE                    # MIT License
└── README.md
```

- **编译 `ZEDAI.dll`**（需 .NET 6/8 SDK，并指向本机已生成 IL2CPP interop 的游戏目录）：
  ```powershell
  dotnet build MOD1-DropOutline/DropOutline.csproj -c Release -p:GameDir="D:\steam\steamapps\common\ZED ZONE" -p:Deploy=false
  ```
- **运行 DSH 插件测试**：
  ```powershell
  node --test dsh-plugin/zed-ai/test/bridge.test.mjs dsh-plugin/zed-ai/test/client.test.mjs dsh-plugin/zed-ai/test/coop-mode.test.mjs dsh-plugin/zed-ai/test/paths.test.mjs
  ```

---

## 九、问题反馈与开源信息

- **开源协议**：本项目遵循 [MIT License](./LICENSE) 完全开源。
- **GitHub 开源地址**：https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod
- **反馈渠道**：如果在使用过程中遇到 Bug、兼容性问题或有任何功能建议，欢迎在以下任意渠道反馈：
  - **GitHub Issues**：https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod/issues
  - **Steam 创意工坊评论区 / 讨论区**：在 [Steam 创意工坊页面](https://steamcommunity.com/sharedfiles/filedetails/?id=3840378952) 下方留言
- 反馈问题时建议附上 `<游戏目录>\BepInEx\LogOutput.log` 及模组版本、复现步骤（**上传前请务必删除或打码个人 API Key 等隐私信息**）。
