using System;

namespace Dropoutline
{
    internal sealed class AiTaskRequest
    {
        public string Type = "", Target = "", Repeat = "count";
        public int Count = 1;
    }

    internal sealed class AiTaskStep
    {
        public string State, Message;
        public bool CompletedUnit;
        public AiTaskStep(string state, string message = "", bool completedUnit = false)
        { State = state; Message = message; CompletedUnit = completedUnit; }
    }

    // Pure scheduler. All native access belongs to its main-thread adapter.
    internal sealed class AiTaskScheduler
    {
        public string Id { get; private set; } = "";
        public string Scope { get; private set; } = "";
        public string Status { get; private set; } = "idle";
        public string Message { get; private set; } = "";
        public AiTaskRequest Request { get; private set; }
        public int Completed { get; private set; }
        public bool Active => Status == "running" || Status == "waiting" || Status == "paused" || Status == "yielding";
        private bool _manualPause;
        private double _lastStep = -100;
        private double _started;
        private double _waitSince = -1;
        public bool Start(string id, string scope, AiTaskRequest request, double now)
        {
            if (request == null || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("任务缺少身份");
            if (request.Type != "organize" && request.Type != "reload" && request.Type != "loot" && request.Type != "craft" && request.Type != "craft_plan" && request.Type != "discard" && request.Type != "flashlight" && request.Type != "sort_containers") throw new ArgumentException("不支持的任务类型");
            if (request.Repeat != "count" && request.Repeat != "continuous") throw new ArgumentException("repeat 必须是 count 或 continuous");
            if (request.Count < 1 || request.Count > 100) throw new ArgumentException("count 必须在 1..100");
            if ((request.Type == "organize" || request.Type == "craft_plan" || request.Type == "discard" || request.Type == "flashlight" || request.Type == "sort_containers") && (request.Repeat != "count" || request.Count != 1)) throw new ArgumentException("整理/规划/手电/容器归并仅执行一次");
            if (Active) return false; // Never silently replace an operation that may own a native action.
            Id = id; Scope = scope; Request = new AiTaskRequest { Type = request.Type, Target = request.Target ?? "", Repeat = request.Repeat, Count = request.Count };
            Completed = 0; _manualPause = false; _lastStep = -100; _started = now; _waitSince = -1;
            Status = "running"; Message = "任务已接收，等待安全时机执行"; return true;
        }
        public void Pause() { if (Active) { _manualPause = true; Status = "paused"; Message = "已按要求暂停；说继续任务可恢复"; } }
        public void Resume() { if (Active && _manualPause) { _manualPause = false; Status = "running"; Message = "等待玩家停止操作后恢复"; } }
        public void Cancel(string reason = "任务已取消") { if (Active) { Status = "cancelled"; Message = reason; } }
        public void Tick(string scope, double now, bool playerBusy, Func<AiTaskRequest, AiTaskStep> step, Action interrupt)
        {
            if (!Active) return;
            if (scope != Scope) { interrupt(); Cancel("世界或后端已改变，旧任务已取消"); return; }
            if (now - _started > 1800) { interrupt(); Cancel("已达到30分钟安全上限；需要继续请重新下达任务"); return; }
            if (playerBusy || _manualPause)
            {
                if (Status != "yielding" && Status != "paused") interrupt();
                Status = _manualPause ? "paused" : "yielding";
                if (!_manualPause) Message = "玩家正在操作，AI让行；松开操作后自动继续";
                return;
            }
            // Work is bounded per frame and never sleeps or blocks for native completion.
            if (now - _lastStep < 0.25) return;
            _lastStep = now;
            AiTaskStep result;
            try { result = step(Request) ?? new AiTaskStep("failed", "执行器没有返回结果"); }
            catch (Exception e) { interrupt(); Status = "failed"; Message = "操作失败：" + e.Message; return; }
            Message = result.Message ?? "";
            if (result.State == "failed" || result.State == "done")
            { Status = result.State == "done" ? "completed" : "failed"; if (result.CompletedUnit) Completed++; return; }
            if (result.CompletedUnit)
            {
                Completed++; _waitSince = -1;
                if (Request.Repeat == "count" && Completed >= Request.Count) { Status = "completed"; return; }
            }
            else if (result.State == "waiting") { if (_waitSince < 0) _waitSince = now; }
            else _waitSince = -1;
            Status = result.State == "waiting" ? "waiting" : "running";
            // A blocked task must not retry every frame forever. Continuous service may
            // wait for a relevant change, but a finite task stops after 60 seconds.
            if (Request.Repeat == "count" && _waitSince >= 0 && now - _waitSince >= 60)
            { interrupt(); Status = "failed"; Message = "等待超过60秒，任务停止：" + Message; }
        }
    }
}
