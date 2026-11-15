using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Dropoutline
{
    internal enum LlmProtocol
    {
        ChatCompletions,   // OpenAI 兼容：POST /chat/completions（DeepSeek、Kimi、中转站、LM Studio…）
        AnthropicMessages, // Anthropic：POST /v1/messages
        Responses,         // OpenAI Responses：POST /responses
        Ollama             // 本地：POST /api/chat
    }

    internal sealed class LlmSettings
    {
        public LlmProtocol Protocol = LlmProtocol.ChatCompletions;
        public string BaseUrl = "https://api.deepseek.com/v1";
        public string Model = "deepseek-chat";
        public string ApiKey = "";
        public float Temperature = 0.7f;
        public int MaxTokens = 400;
        public int TimeoutSec = 30;

        public bool NeedsKey => Protocol != LlmProtocol.Ollama;
        public bool IsConfigured => !string.IsNullOrEmpty(BaseUrl) && !string.IsNullOrEmpty(Model) &&
                                    (!NeedsKey || !string.IsNullOrEmpty(ApiKey));
    }

    internal sealed class LlmResult
    {
        public bool Ok;
        public string Text;      // 模型返回的正文
        public string Goal;      // 解析出的目标名（可空）
        public string Say;       // 解析出的台词（可空）
        public string Reason;    // 解析出的理由（可空）
        public string NpcCmd;    // 官方雇佣 NPC 指令（如 Basic.FollowPlayer / Combat.FireAtWill）
        public string CoopOp;    // 玩家模式实体操作（spawn/tp/follow/combat/guard/scavenge/heal/reload/light）
        public string CoopArg;
        public string RoomOp;    // 联机房间操作（host/join_code/join_ip/leave/status）
        public string RoomArg;
        public AiTaskRequest Task;
        public string ReplyTo;
        public string Error;
        public int LatencyMs;
    }

    /// <summary>
    /// 多协议 LLM 客户端：一套决策文本，四种常见接口。
    /// 只做"同步请求 + 解析"，必须运行在后台线程（不要在里面碰 Unity API）。
    /// </summary>
    internal static class LlmClient
    {
        public static LlmProtocol ParseProtocol(string s)
        {
            var t = (s ?? "").Trim().ToLowerInvariant();
            switch (t)
            {
                case "anthropic":
                case "anthropic-messages":
                case "messages":
                case "claude":
                    return LlmProtocol.AnthropicMessages;
                case "responses":
                case "openai-responses":
                    return LlmProtocol.Responses;
                case "ollama":
                case "local":
                    return LlmProtocol.Ollama;
                default:
                    return LlmProtocol.ChatCompletions;
            }
        }

        public static string ProtocolName(LlmProtocol p)
        {
            switch (p)
            {
                case LlmProtocol.AnthropicMessages: return "Anthropic Messages (/v1/messages)";
                case LlmProtocol.Responses: return "OpenAI Responses (/responses)";
                case LlmProtocol.Ollama: return "Ollama 本地 (/api/chat)";
                default: return "Chat Completions (/chat/completions)";
            }
        }

        public static LlmResult Ask(LlmSettings st, string systemPrompt, string userPrompt)
        {
            var res = new LlmResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (st == null || !st.IsConfigured)
                {
                    res.Error = "LLM 未配置（BaseUrl/模型/Key）";
                    return res;
                }

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                string url, body;
                var headers = new Dictionary<string, string>();
                BuildRequest(st, systemPrompt, userPrompt, out url, out body, headers);

                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = st.TimeoutSec * 1000;
                req.ReadWriteTimeout = st.TimeoutSec * 1000;
                foreach (var kv in headers) req.Headers[kv.Key] = kv.Value;

                var bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);

                string text;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rs = resp.GetResponseStream())
                using (var reader = new StreamReader(rs, Encoding.UTF8))
                    text = reader.ReadToEnd();

                res.Text = ExtractReplyText(st.Protocol, text);
                res.Ok = !string.IsNullOrEmpty(res.Text);
                if (!res.Ok) res.Error = "响应里没有文本: " + Truncate(text, 300);
                ParseDecision(res.Text, res);
            }
            catch (WebException we)
            {
                string detail = we.Message;
                try
                {
                    if (we.Response is HttpWebResponse r)
                        using (var rs = r.GetResponseStream())
                        using (var rd = new StreamReader(rs, Encoding.UTF8))
                            detail = $"{(int)r.StatusCode} {rd.ReadToEnd()}";
                }
                catch { }
                res.Error = "HTTP 失败: " + Truncate(detail, 400);
            }
            catch (Exception e)
            {
                res.Error = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                sw.Stop();
                res.LatencyMs = (int)sw.ElapsedMilliseconds;
            }
            return res;
        }

        private static void BuildRequest(LlmSettings st, string system, string user,
            out string url, out string body, Dictionary<string, string> headers)
        {
            var baseUrl = (st.BaseUrl ?? "").TrimEnd('/');
            switch (st.Protocol)
            {
                case LlmProtocol.AnthropicMessages:
                    url = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                        ? baseUrl + "/messages"
                        : baseUrl + "/v1/messages";
                    headers["x-api-key"] = st.ApiKey;
                    headers["anthropic-version"] = "2023-06-01";
                    body =
                        "{\"model\":\"" + MiniJson.Escape(st.Model) + "\"," +
                        "\"max_tokens\":" + st.MaxTokens + "," +
                        "\"temperature\":" + st.Temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "," +
                        "\"system\":\"" + MiniJson.Escape(system) + "\"," +
                        "\"messages\":[{\"role\":\"user\",\"content\":\"" + MiniJson.Escape(user) + "\"}]}";
                    break;

                case LlmProtocol.Responses:
                    url = baseUrl + "/responses";
                    headers["Authorization"] = "Bearer " + st.ApiKey;
                    body =
                        "{\"model\":\"" + MiniJson.Escape(st.Model) + "\"," +
                        "\"input\":\"" + MiniJson.Escape(system + "\n\n" + user) + "\"," +
                        "\"max_output_tokens\":" + st.MaxTokens + "}";
                    break;

                case LlmProtocol.Ollama:
                    url = baseUrl + "/api/chat";
                    body =
                        "{\"model\":\"" + MiniJson.Escape(st.Model) + "\",\"stream\":false," +
                        "\"options\":{\"temperature\":" + st.Temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "}," +
                        "\"messages\":[{\"role\":\"system\",\"content\":\"" + MiniJson.Escape(system) + "\"}," +
                        "{\"role\":\"user\",\"content\":\"" + MiniJson.Escape(user) + "\"}]}";
                    break;

                default: // Chat Completions
                    url = baseUrl + "/chat/completions";
                    headers["Authorization"] = "Bearer " + st.ApiKey;
                    body =
                        "{\"model\":\"" + MiniJson.Escape(st.Model) + "\"," +
                        "\"temperature\":" + st.Temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "," +
                        "\"max_tokens\":" + st.MaxTokens + "," +
                        "\"messages\":[{\"role\":\"system\",\"content\":\"" + MiniJson.Escape(system) + "\"}," +
                        "{\"role\":\"user\",\"content\":\"" + MiniJson.Escape(user) + "\"}]}";
                    break;
            }
        }

        private static string ExtractReplyText(LlmProtocol p, string json)
        {
            var root = MiniJson.Parse(json);
            switch (p)
            {
                case LlmProtocol.AnthropicMessages:
                {
                    var content = MiniJson.Get(root, "content");
                    var arr = MiniJson.AsList(content);
                    if (arr != null)
                    {
                        var sb = new StringBuilder();
                        foreach (var item in arr)
                        {
                            var t = MiniJson.Get(item, "text") as string;
                            if (!string.IsNullOrEmpty(t)) sb.Append(t);
                        }
                        if (sb.Length > 0) return sb.ToString();
                    }
                    break;
                }
                case LlmProtocol.Responses:
                {
                    var direct = MiniJson.Get(root, "output_text") as string;
                    if (!string.IsNullOrEmpty(direct)) return direct;
                    var output = MiniJson.AsList(MiniJson.Get(root, "output"));
                    if (output != null)
                    {
                        var sb = new StringBuilder();
                        foreach (var item in output)
                        {
                            var contentArr = MiniJson.AsList(MiniJson.Get(item, "content"));
                            if (contentArr == null) continue;
                            foreach (var c in contentArr)
                            {
                                var t = MiniJson.Get(c, "text") as string;
                                if (!string.IsNullOrEmpty(t)) sb.Append(t);
                            }
                        }
                        if (sb.Length > 0) return sb.ToString();
                    }
                    break;
                }
                case LlmProtocol.Ollama:
                {
                    var t = MiniJson.Get(root, "message", "content") as string;
                    if (!string.IsNullOrEmpty(t)) return t;
                    break;
                }
                default:
                {
                    var t = MiniJson.Get(root, "choices", 0, "message", "content") as string;
                    if (!string.IsNullOrEmpty(t)) return t;
                    break;
                }
            }
            return null;
        }

        private static void ParseOpField(object raw, out string op, out string arg)
        {
            op = null;
            arg = "";
            if (raw is string s)
            {
                op = s.Trim();
                return;
            }
            var obj = MiniJson.AsObj(raw);
            if (obj == null) return;
            op = (MiniJson.Get(obj, "op") as string ?? "").Trim();
            arg = (MiniJson.Get(obj, "arg") as string ?? MiniJson.Get(obj, "code") as string ?? MiniJson.Get(obj, "target") as string ?? "").Trim();
        }

        /// <summary>从模型回复里提取 {"goal":"...","say":"..."}；没有则整段当台词。</summary>
        private static void ParseDecision(string text, LlmResult res)
        {
            if (string.IsNullOrEmpty(text)) return;
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var json = text.Substring(start, end - start + 1);
                try
                {
                    var obj = MiniJson.Parse(json);
                    res.Goal = MiniJson.Get(obj, "goal") as string;
                    res.Say = MiniJson.Get(obj, "say") as string;
                    res.Reason = MiniJson.Get(obj, "reason") as string;
                    res.NpcCmd = (MiniJson.Get(obj, "npc_cmd") as string ?? MiniJson.Get(obj, "npcCommand") as string ?? "").Trim();
                    ParseOpField(MiniJson.Get(obj, "coop_op"), out res.CoopOp, out res.CoopArg);
                    ParseOpField(MiniJson.Get(obj, "room_op"), out res.RoomOp, out res.RoomArg);
                    res.Task = AiTaskRuntime.Parse(MiniJson.Get(obj, "task"));
                    if (string.IsNullOrEmpty(res.Say)) res.Say = MiniJson.Get(obj, "reply") as string;
                    if (!string.IsNullOrEmpty(res.Goal) || !string.IsNullOrEmpty(res.Say) || !string.IsNullOrEmpty(res.NpcCmd) ||
                        !string.IsNullOrEmpty(res.CoopOp) || !string.IsNullOrEmpty(res.RoomOp) || res.Task != null) return;
                }
                catch (Exception e)
                {
                    res.Goal = null; res.Task = null; res.Reason = "任务响应校验失败：" + e.Message;
                    res.Say = "这次指令格式无效，任务没有执行，请重新说明。";
                    return;
                }
            }
            res.Say = text.Trim();
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        // ───────── API Key：Windows DPAPI 加密（当前用户） ─────────

        public static string ProtectKey(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var data = Encoding.UTF8.GetBytes(plain);
                var inBlob = new DataBlob(data);
                if (!CryptProtectData(ref inBlob, "ZedAI", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var outBlob))
                    return "";
                try
                {
                    var enc = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, enc, 0, enc.Length);
                    return "dpapi:" + Convert.ToBase64String(enc);
                }
                finally { LocalFree(outBlob.pbData); }
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[AI队友] 加密 Key 失败: {e.Message}");
                return "";
            }
        }

        public static string UnprotectKey(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored; // 兼容手填明文
            try
            {
                var enc = Convert.FromBase64String(stored.Substring(6));
                var inBlob = new DataBlob(enc);
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var outBlob))
                    return "";
                try
                {
                    var dec = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, dec, 0, dec.Length);
                    return Encoding.UTF8.GetString(dec);
                }
                finally { LocalFree(outBlob.pbData); }
            }
            catch (Exception e)
            {
                DropOutlinePlugin.L.LogWarning($"[AI队友] 解密 Key 失败: {e.Message}");
                return "";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;

            public DataBlob(byte[] data)
            {
                cbData = data.Length;
                pbData = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, pbData, data.Length);
            }
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob pDataIn, string szDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}

