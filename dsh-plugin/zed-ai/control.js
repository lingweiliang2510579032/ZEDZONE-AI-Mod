/** Session-independent bridge state, shared by the panel and /zed commands. */
export function createZedControl(connect, setTarget, getSnapshot = () => null) {
  let enabled = false;
  let target = null;
  let connecting = false;
  let error = "";
  let revision = 0;
  let pending = null;
  let disposed = false;

  const status = () => ({
    enabled, connected: enabled && !!target, connecting,
    sessionId: target?.id || null, error, revision,
    game: getSnapshot(),
  });

  const setEnabled = async (next) => {
    if (typeof next !== "boolean") throw new TypeError("enabled must be boolean");
    if (disposed) throw new Error("ZED AI 已卸载");
    if (!next) {
      revision++;
      enabled = false; connecting = false; error = ""; target = null;
      setTarget(null);
      return status();
    }
    if (enabled && target) return status();
    const token = ++revision;
    enabled = true; connecting = true; error = "";
    // Share an in-flight ensure operation: rapid off/on must not create duplicate sessions.
    if (!pending) {
      const operation = Promise.resolve().then(connect);
      pending = operation;
      const clear = () => { if (pending === operation) pending = null; };
      operation.then(clear, clear);
    }
    try {
      const result = await pending;
      if (token !== revision || disposed) return status();
      if (!result?.id) throw new Error("ZED AI 连接未返回有效会话");
      target = result; connecting = false;
      setTarget(result);
      return status();
    } catch (cause) {
      if (token !== revision || disposed) return status();
      enabled = false; connecting = false; target = null;
      error = cause?.message || String(cause);
      setTarget(null);
      throw cause;
    }
  };

  return {
    status, setEnabled,
    replaceTarget(next, previous) {
      if (disposed || !enabled || target !== previous) return false;
      revision++; target = next; setTarget(next); return true;
    },
    dispose() {
      disposed = true; revision++;
      enabled = false; connecting = false; target = null;
      setTarget(null);
    },
  };
}

/** Exact API routes use Connection's existing admission + owner-only Fetch dispatcher.
 * Do not use rpc.handle: its cross-service owner shadow cannot access webServer
 * on this DSH build. No chat session, private Cordis symbol or host patch needed.
 */
export function registerZedControlRpc(ctx, control) {
  for (const action of ["status", "setEnabled"]) {
    const method = `zed-ai/${action}`;
    ctx.connection.fetch.register({
      path: `/api/${method}`,
      methods: ["POST"],
      requestBody: "buffered",
      async fetch(request) {
        if (request.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase() !== "application/json") {
          return new Response("content type must be application/json", { status: 415 });
        }
        let message;
        try { message = await request.json(); }
        catch { return new Response("body is not JSON", { status: 400 }); }
        const fail = (code, text) => ({ ok: false, error: { code, message: text, details: {} } });
        let result;
        try {
          request.signal.throwIfAborted();
          if (!message || message.type !== "client-request" || typeof message.rpcId !== "string" || !message.rpcId
            || message.method !== method) throw new TypeError("无效的 ZED AI RPC 请求");
          const payload = message.payload;
          if (!payload || typeof payload !== "object" || Array.isArray(payload)) throw new TypeError("无效的 ZED AI 请求");
          if (action === "status" && Object.keys(payload).length === 0) result = { ok: true, value: control.status() };
          else if (action === "setEnabled" && Object.keys(payload).length === 1 && Object.hasOwn(payload, "enabled")) {
            result = { ok: true, value: await control.setEnabled(payload.enabled) };
          } else result = fail("zed-ai/invalid-request", "未知的 ZED AI 操作或参数");
        } catch (cause) {
          result = fail("zed-ai/control-failed", cause?.message || String(cause));
        }
        return Response.json({ type: "server-response", rpcId: typeof message?.rpcId === "string" ? message.rpcId : "invalid-request", result });
      },
    });
  }
}

