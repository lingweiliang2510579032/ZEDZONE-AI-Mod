using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace Dropoutline
{
    /// <summary>
    /// 开服可行性测量（仅在带 -zedhost / -zedbench 启动参数时启用，正常游玩完全不触发）。
    ///
    /// 目的：把"这游戏能不能在 4G 服务器上开服"从估计变成数字。
    ///   - 菜单 / 进世界 / 开房 三个阶段分别记录内存构成
    ///   - 记录 Unity 自己统计的引擎内存、托管堆、进程私有内存
    ///   - 顺便尝试调用官方 NetLobby.HostOpen 看无头/自动开房是否可行
    /// 结果写到 ZED AI\host-bench.json，日志里同步打一份。
    /// </summary>
    internal static class AiHostBench
    {
        internal static bool Enabled { get; private set; }
        internal static bool HostAttempt { get; private set; }

        private static float _nextSample;
        private static string _stage = "";
        private static readonly List<string> _stages = new List<string>();
        private static bool _hostTried;
        private static string _hostResult = "未尝试";

        internal static void Init(string[] args)
        {
            try
            {
                foreach (var a in args ?? Array.Empty<string>())
                {
                    if (string.Equals(a, "-zedhost", StringComparison.OrdinalIgnoreCase)) { Enabled = true; HostAttempt = true; }
                    else if (string.Equals(a, "-zedbench", StringComparison.OrdinalIgnoreCase)) { Enabled = true; }
                }
                if (!Enabled) return;
                DropOutlinePlugin.L.LogInfo("[开服测量] 已启用 host=" + HostAttempt);
                DropOutlinePlugin.L.LogInfo("[开服测量] 系统内存 " + SystemInfo.systemMemorySize + "MB / 显存 " + SystemInfo.graphicsMemorySize + "MB / 显卡 " + SystemInfo.graphicsDeviceName);
                Sample("启动完成");
            }
            catch (Exception e) { DropOutlinePlugin.L.LogWarning("[开服测量] 初始化失败: " + e.Message); }
        }

        /// <summary>每帧调用，5 秒采样一次；阶段变化时立即采样。</summary>
        internal static void Tick()
        {
            if (!Enabled) return;
            try
            {
                string stage = CurrentStage();
                if (stage != _stage) { Sample(stage); }
                else if (Time.unscaledTime >= _nextSample) { Sample(stage); }

                if (HostAttempt && !_hostTried && AiSaveContext.Active) TryHostOpen();
                if (HostAttempt && !_autoLoadTried && !AiSaveContext.Active)
                {
                    if (_menuSince < 0f) _menuSince = Time.unscaledTime;
                    else if (Time.unscaledTime - _menuSince > 45f) TryAutoLoad();
                }
            }
            catch (Exception e) { DropOutlinePlugin.L.LogWarning("[开服测量] 采样失败: " + e.Message); }
        }

        private static string CurrentStage()
        {
            try
            {
                if (!AiSaveContext.Active) return "菜单";
                return _hostTried ? "已开房" : "世界中";
            }
            catch { return "未知"; }
        }

        private static void Sample(string stage)
        {
            _nextSample = Time.unscaledTime + 5f;
            _stage = stage;
            string line;
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                long unityAlloc = 0, unityReserved = 0, monoHeap = 0, monoUsed = 0;
                try { unityAlloc = Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024); } catch { }
                try { unityReserved = Profiler.GetTotalReservedMemoryLong() / (1024 * 1024); } catch { }
                try { monoHeap = Profiler.GetMonoHeapSizeLong() / (1024 * 1024); } catch { }
                try { monoUsed = Profiler.GetMonoUsedSizeLong() / (1024 * 1024); } catch { }
                long managed = GC.GetTotalMemory(false) / (1024 * 1024);
                long privateMb = proc.PrivateMemorySize64 / (1024 * 1024);
                long workingMb = proc.WorkingSet64 / (1024 * 1024);
                line = $"{stage}|引擎={unityAlloc}MB 预留={unityReserved}MB 托管堆={monoHeap}MB(已用{monoUsed}MB,GC={managed}MB) 进程私有={privateMb}MB 工作集={workingMb}MB";
            }
            catch (Exception e) { line = stage + "|采样异常:" + e.Message; }
            _stages.Add(line);
            DropOutlinePlugin.L.LogInfo("[开服测量] " + line);
            WriteReport();
        }

        private static bool _autoLoadTried;
        private static float _menuSince = -1f;

        /// <summary>无头模式下没人点"继续游戏"，这里尝试用官方入口自动进世界，失败如实记录。</summary>
        private static void TryAutoLoad()
        {
            _autoLoadTried = true;
            string result;
            try
            {
                var menu = UnityEngine.Object.FindObjectOfType<MainMenuController>();
                if (menu == null) { result = "找不到 MainMenuController 实例"; }
                else
                {
                    DropOutlinePlugin.L.LogInfo("[开服测量] 调用 MainMenuController.OnMainMenuLoad() 尝试进入存档");
                    menu.OnMainMenuLoad();
                    result = "已调用 OnMainMenuLoad，等待 60 秒观察是否进世界";
                }
            }
            catch (Exception e) { result = "自动读档异常: " + e.Message; }
            DropOutlinePlugin.L.LogWarning("[开服测量] 自动读档：" + result);
            _stages.Add("自动读档|" + result);
            WriteReport();
        }
        /// <summary>尝试用官方接口开房：只调用 NetLobby.HostOpen，失败就如实记录。</summary>
        private static void TryHostOpen()
        {
            _hostTried = true;
            try
            {
                var lobby = ZedZone.Net.NetLobby.instance;   // 静态单例，仅用于判空
                if (lobby == null) { _hostResult = "NetLobby.instance 为空"; }
                else if (ZedZone.Net.NetLobby.isOpenAsHost) { _hostResult = "已经在开房状态"; }
                else
                {
                    var player = GameController.instance?.playerCharacter;
                    var character = player?.characterData;
                    if (character == null) _hostResult = "拿不到房主角色数据";
                    else
                    {
                        bool ok = ZedZone.Net.NetLobby.HostOpen(character, 10);
                        _hostResult = "HostOpen 返回 " + ok;
                    }
                }
            }
            catch (Exception e) { _hostResult = "HostOpen 异常: " + e.Message; }
            DropOutlinePlugin.L.LogInfo("[开服测量] 开房尝试：" + _hostResult);
            Sample(_hostResult.StartsWith("HostOpen 返回 True", StringComparison.Ordinal) ? "已开房" : "世界中");
        }

        private static void WriteReport()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(AiCompanion.StateFilePath);
                if (string.IsNullOrEmpty(dir)) return;
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.Append("{\"schemaVersion\":1,\"ts\":\"").Append(DateTime.Now.ToString("HH:mm:ss")).Append("\"")
                  .Append(",\"systemMemoryMb\":").Append(SystemInfo.systemMemorySize)
                  .Append(",\"graphicsMemoryMb\":").Append(SystemInfo.graphicsMemorySize)
                  .Append(",\"graphicsDevice\":\"").Append(MiniJson.Escape(SystemInfo.graphicsDeviceName ?? "")).Append("\"")
                  .Append(",\"hostResult\":\"").Append(MiniJson.Escape(_hostResult)).Append("\"")
                  .Append(",\"stages\":[");
                for (int i = 0; i < _stages.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(MiniJson.Escape(_stages[i])).Append('"');
                }
                sb.Append("]}");
                var path = System.IO.Path.Combine(dir, "host-bench.json");
                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
