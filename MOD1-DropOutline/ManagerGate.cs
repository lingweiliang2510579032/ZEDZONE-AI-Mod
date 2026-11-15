using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dropoutline
{
    /// <summary>
    /// 插件管理器的外部启用声明（中英双语）。
    ///
    /// 为什么只给「管理器」加，不给 MOD 本身加：
    ///   MOD 是我们自己的代码，装了就该能用；
    ///   但管理器会去加载/启停**别人的**插件——那些代码不是我们写的、我们不审计，
    ///   一旦第三方插件里带恶意代码，责任不该落在管理器上。
    ///   所以管理器默认关闭，必须由玩家显式声明"我知道风险，我要用"。
    /// </summary>
    internal static class ManagerGate
    {
        public const string FileName = "ZED-插件管理器-启用声明.txt";
        public const string Key = "启用插件管理器";
        public const string KeyEn = "EnablePluginManager";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly Dictionary<string, bool> _values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public static string FilePath
        {
            get
            {
                string root = null;
                try { root = BepInEx.Paths.GameRootPath; } catch { }
                if (string.IsNullOrEmpty(root))
                    root = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(root, FileName);
            }
        }

        private const string Template = @"# ============================================================
#  ZED 插件管理器 · 启用声明与免责说明
#  ZED Plugin Manager · Enablement Declaration & Disclaimer
# ============================================================
#
#  这个文件控制「插件管理器」是否可用。默认关闭。
#  This file controls whether the Plugin Manager is available. Disabled by default.
#
#  -------------------- 重要：请先读完 --------------------
#  -------------------- IMPORTANT: READ FIRST --------------------
#
#  1. 插件管理器的作用
#     · 列出 BepInEx\plugins 下的插件；
#     · 对它们做「启用 / 禁用 / 卸载」三个纯文件操作：
#         启用 = 把 xxx.dll.disabled 改名回 xxx.dll
#         禁用 = 把 xxx.dll 改名为 xxx.dll.disabled
#         卸载 = 直接删除该 dll 文件
#     · 它不会读取、修改、注入任何第三方的代码逻辑。
#
#  1. What the Plugin Manager does
#     - Lists the plugins under BepInEx\plugins;
#     - Performs three pure file operations on them (enable / disable / uninstall):
#         Enable    = rename xxx.dll.disabled back to xxx.dll
#         Disable   = rename xxx.dll to xxx.dll.disabled
#         Uninstall = delete that dll file
#     - It does not read, modify, or inject into any third-party code logic.
#
#  2. 它不做什么
#     · 不扫描、不审计、不保证第三方插件是否安全；
#     · 不拦截第三方插件的网络请求、文件读写或进程行为；
#     · 不能防止恶意插件在你启用它们之后做任何事。
#
#  2. What it does NOT do
#     - It does not scan, audit, or guarantee that third-party plugins are safe;
#     - It does not intercept third-party plugins' network requests, file I/O, or process behavior;
#     - It cannot prevent a malicious plugin from doing anything once you enable it.
#
#  3. 风险提示
#     启用第三方 BepInEx 插件 = 允许别人写的代码在你的电脑上以游戏进程权限运行。
#     本管理器只是开关，不承担第三方插件的安全性责任。
#     请只启用你信任来源的插件；不确定就先禁用。
#
#  3. Risk notice
#     Enabling a third-party BepInEx plugin means allowing someone else's code to run
#     on your computer with the game process's privileges.
#     This manager is only a switch and assumes no responsibility for third-party plugin safety.
#     Only enable plugins from sources you trust; if unsure, keep them disabled.
#
#  4. 如何启用
#     把下面 [开关] 里的「启用插件管理器」改成 true（全小写），
#     保存后重新启动游戏。游戏内进入「设置 → MOD 设置 → MOD 管理」即可使用。
#
#  4. How to enable
#     Set ""启用插件管理器"" under [开关] below to true (lowercase),
#     save the file, and restart the game.
#     In game: Settings -> MOD Settings -> MOD Management.
#
#  不想要它？把这一项改回 false，或直接删除 ZEDAI.dll。
#  Do not want it? Set the value back to false, or simply delete ZEDAI.dll.
# ============================================================

[开关]
启用插件管理器 = false
";

        /// <summary>确保声明文件存在并读取开关。</summary>
        public static bool IsEnabled(string logPrefix = null)
        {
            try
            {
                var path = FilePath;
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, Template, Utf8NoBom);
                    if (logPrefix != null) BepInEx.Logging.Logger.CreateLogSource(logPrefix)
                        .LogInfo($"已生成插件管理器声明：{path}（默认关闭）");
                    return false;
                }

                Load(path);
                if (_values.TryGetValue(Key, out bool v)) return v;
                if (_values.TryGetValue(KeyEn, out bool v2)) return v2;
                return false;
            }
            catch (Exception e)
            {
                if (logPrefix != null) BepInEx.Logging.Logger.CreateLogSource(logPrefix)
                    .LogWarning($"读取插件管理器声明失败: {e.Message}");
                return false;
            }
        }

        private static void Load(string path)
        {
            _values.Clear();
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line.Substring(0, eq).Trim();
                var v = line.Substring(eq + 1).Trim().Trim('"');
                _values[k] = v.Equals("true", StringComparison.OrdinalIgnoreCase)
                          || v.Equals("1", StringComparison.OrdinalIgnoreCase)
                          || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                          || v.Equals("是", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
