# AI伴侣（测试版v0.1） — 使用与安装说明

- **模组名称**：AI伴侣（测试版v0.1）
- **当前版本**：`v0.1`（测试版；后续每次更新按 `+0.1` 递增：`v0.2`、`v0.3`……）
- **开源协议**：MIT License
- **GitHub 开源地址**：https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod
- **问题反馈**：可在 GitHub Issues（https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod/issues ）或 Steam 创意工坊页面评论区留言反馈。

---

## 一、前置要求

1. **必需前置**：**ZED ZONE 代码模组加载器（BepInEx IL2CPP / DLL Mods）**，Steam 创意工坊 ID：`3834614258`
   - 首次安装该前置后，需进入工坊目录 `steamapps\workshop\content\1211600\3834614258\`，关闭游戏后双击运行 `install.cmd` 完成 BepInEx 注入安装。
   - 前置安装完成后，订阅本模组并**通过 Steam 启动游戏**即可自动加载（请勿重复手动复制另一份 `ZEDAI.dll` 到 `BepInEx\plugins\`）。
2. **可选搭配**：`Zedtool-模组设置`（工坊 ID：`3838476805`，支持游戏内按 `F7` 查看全部代码模组配置）。不安装也不影响本模组使用自带的二级设置菜单。

---

## 二、3 分钟快速上手（API 模式直连）

1. 启动游戏 → 进入 **游戏选项（ESC → 游戏选项）** → 右侧点击 **AI 伙伴** → 点击 **打开 AI 伙伴设置**。
2. 在第 1 页 **「1. 接入与表现形式」**：
   - **启用 AI 伙伴**：开启
   - **接入模式**：选 `API 模式（模组直连大模型接口）`
   - **表现形式**：默认可选 `聊天陪伴模式`（随时可切换到接管雇佣 NPC 或联机玩家模式）
   - **使用游戏官方聊天框**：开启（单人及联机均使用左侧官方聊天框，按 `Enter` 打字对话）
   - 按个人喜好修改 **AI 名字**、**玩家称呼** 与 **人设**。
3. 在第 3 页 **「3. API 接口配置」** 填写你的大模型参数并测试连接：
   - **DeepSeek 官方**：协议选 `Chat Completions`｜BaseUrl 填 `https://api.deepseek.com/v1`｜模型填 `deepseek-chat`
   - **OpenAI / 兼容接口**：协议选 `Chat Completions`｜BaseUrl 填 `https://api.openai.com/v1`（或中转 `/v1` 地址）｜模型填对应模型名
   - **Anthropic Claude**：协议选 `Anthropic Messages`｜BaseUrl 填 `https://api.anthropic.com`｜模型填 Claude 模型名
   - **本地 Ollama**：协议选 `Ollama 本地`｜BaseUrl 填 `http://127.0.0.1:11434`｜模型填本地模型名（API Key 可留空）
   - 在 **API Key** 输入框填好后按 **回车** 保存（本地 DPAPI 加密储存，不回显），再点 **测试连接**。
4. 进存档后按 **`Enter`** 打开左侧官方聊天栏，输入内容回车即可对话。

---

## 三、DSH 本地 Agent 模式（可选进阶）

本包随附了 `zed-ai-dsh-plugin-v0.1.zip`（对应 `@local/zed-ai` 插件）：
1. 若你使用 DeepSeek Harness（DSH），可将本目录里的 `zed-ai-dsh-plugin-v0.1.zip` 解压到：
   `%USERPROFILE%\.dsh\profiles\desktop\node_modules\@local\zed-ai\`
2. 在 `%USERPROFILE%\.dsh\profiles\desktop\cordis.patch.yml` 的 `insert` 中注册 `@local/zed-ai`，将 `stateDir` 与 `workspacePath` 指向 `<游戏根目录>\ZED AI`，`gameDir` 指向 `<游戏根目录>`，然后完全重启 DSH。
3. 在游戏内设置菜单第 1 页将接入模式切换为 `DSH 模式（连接本地大肥鱼 Agent）` 即可。

---

## 四、三种表现形式与辅助权限边界

1. **聊天陪伴模式**：无需招募 NPC 即可通过左侧官方聊天框聊天、获得防复读的血量/饥渴/手电电量/技能点/视线威胁提醒与实用建议，并在玩家允许的权限下安全执行背包整理、装填、开关手电、拾取视线内掉落、已解锁配方合成、单堆安全丢弃、同楼层已检视容器归并（联机下自动收敛为只读规划与高亮/手电）。
2. **接管招募（雇佣）NPC 模式**：当你在游戏中实际招募了雇佣 NPC 队友后，由 AI 严格按照官方雇佣 NPC 的能力范围下达指挥（跟随/驻守/搜集/开火停火/远近战偏好/治疗/手雷等），仅单人或联机房主生效。
3. **联机玩家模式**：为 AI 创建专属 `MpCharacters` 联机角色卡并以独立 `playerId` 加入房间协同作战（单人可开启“单机自动开启本地联机房”自动转为本地房主局，也可用房间码/聊天指令 `召唤`、`过来`、`跟我走`、`原地待命`、`开启联机` 等快捷互动）。

> **严格反作弊原则**：AI 绝不读取未解锁配方、未打开检视的箱子或墙后/视野外敌人；所有写操作权限（丢弃/直接合成/归并容器）在第 4 页「4. 辅助与权限边界」中默认关闭，按需开启。

---

## 五、问题排查与反馈

- 若未能加载，请检查是否已运行前置 `3834614258` 的 `install.cmd`，并检查 `<游戏目录>\BepInEx\LogOutput.log`。
- 请务必从 Steam 启动游戏，不要直接双击 `ZEDZONE.exe`。
- 遇到任何 Bug 或有改进建议，欢迎前往 **GitHub Issues**（https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod/issues ）或 **Steam 创意工坊评论区** 反馈（提交日志前请自行隐藏 API Key）。
