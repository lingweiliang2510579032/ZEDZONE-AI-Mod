# AI伴侣（测试版v0.2） / AI Companion (Beta v0.2) · 使用说明

## 一、前置要求（非常重要）

1. 使用本模组前，必须先在 Steam 创意工坊订阅前置模组 **ZED ZONE Code Mod Loader（ID：`3834614258`）**，并在该前置模组的工坊目录中运行一次 `install.cmd` 完成加载框架安装（只需运行一次）。
2. 如为手动安装，将本模组 `ZEDAI.dll` 放入 `<游戏目录>\BepInEx\plugins\ZEDAI.dll` 即可。

## 二、怎么打开设置与切换语言

1. 进入游戏后按 `ESC` → 打开 **游戏选项（Options）** → 找到 **AI 伙伴（AI Companion）** → 点击 **打开 AI 伙伴设置**。
2. 模组完整支持**游戏本体全部 10 种官方语言**（简体中文 / English / Deutsch / Español / Français / 日本語 / 한국어 / Polski / Português / Русский），第 1 页第一行「界面语言」默认自动跟随游戏语言，也可随时手动指定。

## 三、两种接入模式

### 1. API 模式（直连大模型，无需安装 DSH 插件）
- 打开设置第 3 页 **API 接口配置**：
  1. 选择 **API 接口协议**（OpenAI Chat Completions / Anthropic Messages / OpenAI Responses / Ollama 本地）。
  2. 填写 **API 根地址（BaseUrl）**（例如 `https://api.deepseek.com/v1`）和 **模型名称**（例如 `deepseek-chat`）。
  3. 填写 **API Key** 并按回车（本机 Windows DPAPI 加密保存，不回显、不存明文）。
  4. 点击 **测试连接**，看到连接成功提示即可开始游玩。

### 2. DSH 模式（配合 DeepSeek Harness 本地智能体）
- 仅使用 DSH 模式才需要安装配套插件：从 GitHub Releases 下载同版本 `zed-ai-dsh-plugin-v0.2.zip` 并安装到 DSH 桌面端，在设置第 1 页将 **接入模式** 切换为 **DSH** 即可联动。

## 四、三种表现形式（单人与联机自适应）

1. **聊天陪伴模式（默认）**
   - 单人与联机模式均可按 **`Enter`** 打开左侧官方聊天框与 AI 自然对话、提问或求助。
   - AI 会根据掉血、饥渴、手电低电量（低于 20%）、未加点数及视野内敌人主动提醒，并内置防复读机制避免句式重复。
   - **公平边界**：AI 仅基于你已解锁的配方、随身物品和**同楼层视野内已打开检视过**的容器信息行动，绝不作弊剧透；第 4 页中的丢弃/直接合成/容器整理权限**全部默认关闭**，需你主动开启。
2. **接管雇佣 NPC 模式**
   - 仅在单人或联机房主且已雇佣 NPC 队友时生效，严格按游戏内置机制控制：跟随、驻守、架枪警戒、开火/停火、武器偏好、搜刮、拾取、手雷与治疗烟雾；不可代劳建造或合成。
3. **联机玩家模式**
   - 为 AI 创建独立联机角色作为正常玩家加入房间协同战斗、拾荒与聊天。
   - 单人游玩时，可在设置第 2 页开启「单机自动开启本地联机房」或直接一键开房让 AI 玩家进房；也支持填房间码加入好友房间。

---

## Quick Start (English)

1. Subscribe to **[ZED ZONE Code Mod Loader (3834614258)](https://steamcommunity.com/sharedfiles/filedetails/?id=3834614258)** and run its `install.cmd` once before using this mod.
2. In-game press `ESC` → **Options** → **AI Companion** → **Open AI Companion Settings**. All 10 official base-game languages are supported.
3. Choose **API Mode** (fill Base URL / Model / Key in Tab 3 and click **Test Connection**) or **DSH Mode** (install `zed-ai-dsh-plugin-v0.2.zip` in DSH Desktop).
4. Pick your preferred form (**Chat Companion / Hired NPC / Co-op Player**) and press **`Enter`** in solo or co-op to chat via the left-side game chat box.

---

## 开源与反馈 / Open Source & Feedback

- **开源协议 / License**：MIT License
- **GitHub 开源仓库**：https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod
- **Steam 创意工坊**：https://steamcommunity.com/sharedfiles/filedetails/?id=3840378952
- 如有 Bug 反馈或建议，欢迎前往 **GitHub Issues** 或 **Steam 创意工坊评论区** 留言！
