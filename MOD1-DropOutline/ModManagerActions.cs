using System;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace Dropoutline
{
    /// <summary>
    /// MOD 管理动作：卸载。
    ///
    /// 为什么不在面板里直接删文件：DLL 正被游戏进程占用，删不掉；
    /// 所以生成一个 .bat，让玩家退出游戏后双击执行——
    /// 这样既安全（不碰运行中的进程），又不需要玩家自己找路径。
    /// </summary>
    internal static class ModManagerActions
    {
        public static void MakeUninstaller()
        {
            try
            {
                var pluginPath = typeof(DropOutlinePlugin).Assembly.Location;
                var pluginDir = Path.GetDirectoryName(pluginPath) ?? Paths.PluginPath;
                var cfgFile = AiCompanion.ConfigFilePathForUninstall();
                var cfgDir = Path.GetDirectoryName(cfgFile) ?? Paths.ConfigPath;
                var stateDir = Path.Combine(cfgDir, "DropOutline");
                var gameRoot = Directory.GetParent(pluginDir)?.Parent?.FullName ?? AppDomain.CurrentDomain.BaseDirectory;

                var batPath = Path.Combine(gameRoot, "卸载AI伙伴MOD.bat");
                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("chcp 65001 >nul");
                sb.AppendLine("echo 正在卸载《隔离区》AI伙伴 MOD...");
                sb.AppendLine("timeout /t 2 >nul");
                sb.AppendLine($"del /f /q \"{pluginPath}\" 2>nul");
                sb.AppendLine($"del /f /q \"{cfgFile}\" 2>nul");
                sb.AppendLine($"rmdir /s /q \"{stateDir}\" 2>nul");
                sb.AppendLine("echo 已删除插件、配置与记忆文件。");
                sb.AppendLine("echo 请重新启动游戏（BepInEx 不会再加载本 MOD）。");
                sb.AppendLine("pause");

                File.WriteAllText(batPath, sb.ToString(), new UTF8Encoding(false));
                DropOutlinePlugin.L.LogInfo($"[MOD管理] 已生成卸载脚本: {batPath}");

                try
                {
                    GUIUtility.systemCopyBuffer = batPath;
                    DropOutlinePlugin.L.LogInfo("[MOD管理] 卸载脚本路径已复制到剪贴板");
                }
                catch { }

                ChatInput.Log("MOD管理", "卸载脚本已生成，退出游戏后双击运行即可");
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[MOD管理] 生成卸载脚本失败: {e.Message}");
                try { ChatInput.Log("MOD管理", "生成卸载脚本失败，详见日志"); } catch { }
            }
        }
    }
}

