# AI伴侣（测试版v0.3） / AI Companion (Beta v0.3)

《隔离区（ZED ZONE）》AI 伙伴开源模组，现已完整支持**游戏本体全部 10 种官方语言**，提供 **API 直连大模型** 与 **DSH（DeepSeek Harness）本地智能体** 两种接入方式，并可在 **聊天陪伴 / 接管雇佣 NPC / 联机玩家** 三种表现形式之间自由切换。

- **开源协议**：MIT License
- **GitHub 开源仓库**：https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod
- **Steam 创意工坊**：https://steamcommunity.com/sharedfiles/filedetails/?id=3840378952

---

## 一、支持语言 / Supported Languages（10 种）

模组默认**自动跟随游戏本体语言**，也可在模组设置第 1 页「界面语言 / Interface Language」手动切换，AI 的对话语言也会同步适应：

- 简体中文（`zh`）
- English（`en`）
- Deutsch（`de`）
- Español（`es`）
- Français（`fr`）
- 日本語（`ja`）
- 한국어（`ko`）
- Polski（`pl`）
- Português（`pt`）
- Русский（`ru`）

---

## 二、安装方法（二选一）

### 方式 A：Steam 创意工坊安装（推荐）

1. 先订阅必需前置加载器 **[ZED ZONE Code Mod Loader（ID：3834614258）](https://steamcommunity.com/sharedfiles/filedetails/?id=3834614258)**，打开其工坊目录并运行一次 **`install.cmd`** 完成基础框架安装（安装过一次即可）。
2. 在 Steam 创意工坊订阅本模组 **[AI伴侣（测试版v0.3）](https://steamcommunity.com/sharedfiles/filedetails/?id=3840378952)**（ID：`3840378952`）。
3. 进入游戏后，点击主菜单右上角/底部的加载器界面确认启用本模组，重启游戏即生效。

### 方式 B：手动安装包下载（GitHub Releases）

1. 同样先确保已完成前置模组加载器的基础安装。
2. 从 [GitHub Releases](https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod/releases) 下载最新 `ZEDAI.dll`，放入：
   `<ZED ZONE 游戏目录>\BepInEx\plugins\ZEDAI.dll`
3. 如果只使用 **API 直连模式**，只需放入 `ZEDAI.dll` 即可，无需额外安装 DSH 插件；若要使用 **DSH 本地智能体模式**，请再下载同版本的 `zed-ai-dsh-plugin-v0.3.zip` 安装到 DSH 桌面端。

---

## 三、如何打开设置与开始对话

1. **打开模组设置菜单**：
   游戏内按 `ESC` → 打开 **游戏选项（Options）** → 在左侧或底部找到 **AI 伙伴（AI Companion）** 分区 → 点击 **打开 AI 伙伴设置**，打开独立设置弹窗。
2. **四个配置分页一目了然**：
   - **1. 接入与表现**：选择界面语言、总开关、接入模式（API / DSH）、表现形式（聊天陪伴 / 接管雇佣 NPC / 联机玩家）、AI 名字、玩家称呼与性格人设。
   - **2. 模式与联机适配**：按当前表现形式展示对应控制项与单/联机说明（玩家模式含一键开启本地联机房、按房间码进房、召唤/传送与协同姿态）。
   - **3. 接口配置 / DSH 说明**：填写 API 协议、BaseUrl、Model、API Key 并点击「测试连接」验证；DSH 模式提供一键开启联机房入口与状态摘要。
   - **4. 辅助与权限边界**：控制自主行动及丢弃/直接合成/容器整理等权限（全部写操作**默认关闭**，需玩家明确授权）。
3. **打开聊天框和 AI 对话**：
   - **单人模式 & 联机模式**：直接按 **`Enter` 键**打开游戏左侧官方聊天框，输入内容回车即可对话或下达请求；AI 回复会自动在正确句读处断句分页，并在左侧官方聊天栏逐句显示。

---

## 四、两种接入模式配置教程

### 1. API 直连模式（适合多数玩家，开箱即用）

支持 4 种常见大模型接口协议（均在本机直接请求，**API Key 采用 Windows 本机加密保存**，不会明文泄漏）：

- **OpenAI Chat Completions**（兼容 DeepSeek / OpenAI / OneAPI / SiliconFlow 等）
  - BaseUrl 示例：`https://api.deepseek.com/v1`
  - Model 示例：`deepseek-chat`
- **Anthropic Messages**（Claude 系列）
- **OpenAI Responses**
- **Ollama 本地模型**（离线可用，默认地址示例 `http://127.0.0.1:11434/v1`）

填好后点击设置页中的 **测试连接**，提示成功即可进游戏按 `Enter` 开始聊天。

### 2. DSH（DeepSeek Harness）本地智能体模式

适合配合 DSH 桌面端游玩，支持跨地图与跨重置保留同角色档案的长期对话记忆：

1. 将下载的 `zed-ai-dsh-plugin-v0.3.zip` 安装并启用到 DSH 桌面端（插件 ID：`@local/zed-ai`）。
2. 在游戏内打开设置第 1 页，将 **接入模式** 切换为 **DSH 联机陪玩**。
3. 启动游戏并加载存档后，DSH 侧边栏 **ZED AI** 面板会自动同步显示角色、人设、任务与当前状态，无需在模组内填写 API Key。

---

## 五、三种表现形式与单/联机适配

### 1. 聊天陪伴模式（默认）
- 通过左侧官方聊天框自然对话，采用防复读机制避免句式机械重复。
- 会在掉血、饥饿/口渴、手持设备电量低于 20%、有未分配点数、附近出现威胁时主动给出口语提醒。
- **反作弊与公平边界**：AI 只读取已解锁配方、你随身携带及**同楼层视野内已打开检视过**的容器与视野内目标，绝不剧透未探索区域或未解锁配方；可给出合成缺口建议、高亮提示、手电控制以及（经你授权开启后的）整理/制造辅助。
- **单/联机差异**：单人模式可授权全部辅助任务；联机模式下为避免多端冲突，仅允许只读规划、高亮指引与手电操作。

### 2. 接管招募（雇佣）NPC 模式
- 严格遵循游戏官方雇佣队友机制设计：支持 **跟随 / 原地驻守 / 架枪警戒 / 自由开火 / 保护玩家 / 停火 / 远近战武器偏好 / 搜刮附近资源 / 拾取指定物品 / 治疗玩家 / 投掷手雷**。
- **不做越权行为**：游戏官方不支持雇佣 NPC 建造、合成配方或开车，AI 也不会假装代劳，材料不足或超出范围会直接说明原因。
- **单/联机差异**：仅单人或**联机房主**拥有雇佣队友控制权；联机客机端不会越权改写房主端 NPC。

### 3. 联机玩家模式
- 为 AI 创建独立的联机角色档案，像一名普通联机队友一样加入你的房间，并肩作战、搜刮物资与在官方聊天栏互动。
- 支持协同姿态切换：**紧跟玩家 / 积极迎战 / 原地驻守 / 协同拾荒**。
- **单/联机差异**：
  - 单人游玩时，开启「单机自动开启本地联机房」或在设置第 3 页点击「开启联机房」，即可将当前世界变为本地联机房主，让 AI 玩家直接入房陪伴。
  - 在多人房间作为房主或客机时，也可填写房间码或直接与房内队友协同游玩。

---

## Quick Start (English)

**AI Companion (Beta v0.3)** for **ZED ZONE** supports **all 10 official game languages** (auto-following your game language or selectable manually under Options).

1. **Install Prerequisites**: Subscribe to **[ZED ZONE Code Mod Loader (3834614258)](https://steamcommunity.com/sharedfiles/filedetails/?id=3834614258)** first and run its `install.cmd` once, OR place `ZEDAI.dll` (from GitHub Releases) into `<Game Dir>\BepInEx\plugins\ZEDAI.dll`.
2. **Open Settings**: Press `ESC` in-game → **Options** → **AI Companion** → **Open AI Companion Settings**.
3. **Choose Connection Mode**:
   - **API Mode**: Pick Chat Completions / Anthropic Messages / OpenAI Responses / Ollama, enter your Base URL, Model name, and API Key (encrypted locally via Windows DPAPI), then click **Test Connection**.
   - **DSH Mode**: Install `@local/zed-ai` (`zed-ai-dsh-plugin-v0.3.zip`) into DeepSeek Harness Desktop and switch Connection Mode to **DSH**.
4. **Choose Companion Form**:
   - **Chat Companion**: Natural conversation via the left-side game chat box (`Enter`), anti-repetition alerts, and strictly fair advice/helper tasks using only known recipes & inspected containers.
   - **Hired NPC**: Commands recruited NPCs strictly within built-in NPC rules (Singleplayer / Co-op Host only).
   - **Co-op Player**: Joins your game as a dedicated co-op player teammate (can auto-open a local co-op host from singleplayer).
5. **Chat In-Game**: Press **`Enter`** in both Singleplayer and Multiplayer to talk directly with your AI companion in the left chat box.

---

## 开源协议与问题反馈 / License & Feedback

- **开源协议 / License**：[MIT License](LICENSE)
- **GitHub 开源地址**：https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod
- **Steam 创意工坊**：https://steamcommunity.com/sharedfiles/filedetails/?id=3840378952
- 如果在使用中遇到任何 Bug 或有功能建议，欢迎在 **[GitHub Issues](https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod/issues)** 或 **Steam 创意工坊评论区 / 讨论区** 留言反馈！
