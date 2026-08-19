// =================================================================================================
// McpHttpEndpoints.cs
// MCP HTTP 端点注册（纯 C# 层）
// -------------------------------------------------------------------------------------------------
// 端点一览：
//   GET  /                     —— 中文信息页（服务名/版本/端点/工具数/模式）
//   GET  /health               —— 健康检查 JSON
//   POST /mcp                  —— Streamable HTTP：JSON 请求 → JSON 响应；
//                                 若 Accept 含 text/event-stream 则以 SSE 事件流返回
//   DELETE /mcp                —— 会话结束（本服务无状态，直接 200）
//   GET  /sse                  —— 传统 SSE 传输：建立长连接，返回 endpoint 事件
//   POST /message?sessionId=x  —— 传统 SSE 传输的客户端→服务器通道，响应以 SSE 事件写回
//   OPTIONS 任意路径           —— CORS 预检（由兜底处理器处理）
//
// 兼容说明：
//   - 同时支持 MCP Streamable HTTP（2025-03-26）与传统 HTTP+SSE（2024-11-05）两种传输，
//     以兼容不同 MCP 客户端（MCP Inspector、mcp-remote、自研 Agent 等）。
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Logging;
using VrchatProjectMcp.Core.Mcp;

namespace VrchatProjectMcp.Core.Net
{
    /// <summary>
    /// MCP HTTP 端点注册器。
    /// </summary>
    public static class McpHttpEndpoints
    {
        /// <summary>Streamable HTTP 端点路径。</summary>
        public const string McpPath = "/mcp";

        /// <summary>传统 SSE 端点路径。</summary>
        public const string SsePath = "/sse";

        /// <summary>传统 SSE 消息端点路径。</summary>
        public const string MessagePath = "/message";

        private static readonly object SessionsLock = new object();
        private static readonly Dictionary<string, SseSession> Sessions = new Dictionary<string, SseSession>(StringComparer.Ordinal);

        private static JsonRpcCore _core;
        private static IMcpLogger _logger;
        private static Func<JsonObject> _statusProvider;
        private static SimpleHttpServer _server;

        /// <summary>传统 SSE 会话（GET /sse 建立的长连接）。</summary>
        private sealed class SseSession
        {
            public string Id;
            public HttpContext Context;
            public DateTime CreatedAt;

            /// <summary>会话结束信号：服务器停止或连接断开时置位，解除 GET /sse 处理器阻塞。</summary>
            public ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        /// <summary>注册全部 MCP 端点到服务器。</summary>
        public static void Register(SimpleHttpServer server, JsonRpcCore core, IMcpLogger logger, Func<JsonObject> statusProvider = null)
        {
            _server = server;
            _core = core;
            _logger = logger;
            _statusProvider = statusProvider;

            server.AddHandler("GET", "/", HandleInfoPage);
            server.AddHandler("GET", "/health", HandleHealth);
            server.AddHandler("POST", McpPath, HandleStreamablePost);
            server.AddHandler("DELETE", McpPath, HandleStreamableDelete);
            server.AddHandler("GET", SsePath, HandleSseOpen);
            server.AddHandler("POST", MessagePath, HandleSseMessage);
            server.SetFallback(HandleFallback);
        }

        /// <summary>关闭全部 SSE 会话（服务器停止前调用，解除处理器阻塞）。</summary>
        public static void CloseAllSessions()
        {
            List<SseSession> snapshot;
            lock (SessionsLock)
            {
                snapshot = new List<SseSession>(Sessions.Values);
                Sessions.Clear();
            }
            foreach (SseSession session in snapshot)
            {
                try { session.Done.Set(); } catch { /* 忽略 */ }
            }
        }

        // ------------------------------------------------------------------
        // GET / —— 中文信息页
        // ------------------------------------------------------------------

        /// <summary>信息页处理器。</summary>
        private static void HandleInfoPage(HttpContext context)
        {
            JsonObject status = GetStatusJson();
            var sb = new StringBuilder(2048);
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>VRChat Project MCP</title></head><body>");
            sb.Append("<h1>VRChat Project MCP</h1>");
            sb.Append("<p>VRChat Unity 项目 MCP 服务运行中。版本: ").Append(status.GetString("version")).Append("</p>");
            sb.Append("<table border=\"1\" cellpadding=\"6\">");
            sb.Append("<tr><td>状态</td><td>").Append(status.GetBool("running") ? "运行中" : "已停止").Append("</td></tr>");
            sb.Append("<tr><td>访问模式</td><td>").Append(status.GetString("accessModeDisplayName")).Append("</td></tr>");
            sb.Append("<tr><td>工具数量</td><td>").Append(status.GetLong("toolCount")).Append("</td></tr>");
            sb.Append("<tr><td>Streamable HTTP</td><td>POST /mcp</td></tr>");
            sb.Append("<tr><td>传统 SSE</td><td>GET /sse + POST /message</td></tr>");
            sb.Append("<tr><td>健康检查</td><td>GET /health</td></tr>");
            sb.Append("</table>");
            sb.Append("<p>调用方式：向 POST /mcp 发送 JSON-RPC 2.0 消息（initialize → tools/list → tools/call）。" +
                      "写入类工具在只读模式下会被拒绝。</p>");
            sb.Append("</body></html>");
            _server.WriteResponse(context, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(sb.ToString()));
        }

        // ------------------------------------------------------------------
        // GET /health —— 健康检查
        // ------------------------------------------------------------------

        /// <summary>健康检查处理器。</summary>
        private static void HandleHealth(HttpContext context)
        {
            JsonObject status = GetStatusJson();
            status.Set("status", "ok");
            var endpoints = new JsonArray()
                .Push("POST " + McpPath)
                .Push("GET " + SsePath)
                .Push("POST " + MessagePath)
                .Push("GET /health");
            status.Set("endpoints", endpoints);
            byte[] body = Encoding.UTF8.GetBytes(MiniJson.Serialize(status));
            _server.WriteResponse(context, 200, "application/json; charset=utf-8", body);
        }

        // ------------------------------------------------------------------
        // POST /mcp —— Streamable HTTP
        // ------------------------------------------------------------------

        /// <summary>Streamable HTTP 处理器：JSON 请求 → JSON/SSE 响应。</summary>
        private static void HandleStreamablePost(HttpContext context)
        {
            // 解析请求 JSON（失败按 JSON-RPC -32700 处理）
            object message;
            try
            {
                message = MiniJson.Parse(context.BodyText);
            }
            catch (FormatException ex)
            {
                WriteJsonRpcError(context, null, -32700, "JSON 解析失败: " + ex.Message);
                return;
            }

            object response = _core.HandleMessage(message);

            // 通知类消息（无响应）：按 Streamable HTTP 规范返回 202 Accepted 空体
            if (response == null)
            {
                _server.WriteResponse(context, 202, "text/plain; charset=utf-8", new byte[0]);
                return;
            }

            string json = MiniJson.Serialize(response);
            byte[] body = Encoding.UTF8.GetBytes(json);

            if (context.AcceptsEventStream)
            {
                // 客户端接受 SSE：以事件流形式返回（响应后即关闭，本服务无状态）
                _server.BeginSse(context);
                _server.WriteSse(context, "message", json);
            }
            else
            {
                _server.WriteResponse(context, 200, "application/json; charset=utf-8", body);
            }
        }

        /// <summary>Streamable HTTP 会话终止：无状态实现直接返回 200。</summary>
        private static void HandleStreamableDelete(HttpContext context)
        {
            _server.WriteResponse(context, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("OK"));
        }

        // ------------------------------------------------------------------
        // GET /sse + POST /message —— 传统 HTTP+SSE 传输
        // ------------------------------------------------------------------

        /// <summary>建立 SSE 长连接并下发 endpoint 事件。</summary>
        private static void HandleSseOpen(HttpContext context)
        {
            string sessionId = Guid.NewGuid().ToString("N");
            var session = new SseSession { Id = sessionId, Context = context, CreatedAt = DateTime.Now };

            _server.BeginSse(context);
            try
            {
                _server.WriteSse(context, "endpoint", MessagePath + "?sessionId=" + sessionId);
            }
            catch (Exception ex)
            {
                _logger?.Warn("[SSE] 下发 endpoint 失败: " + ex.Message);
                return;
            }

            lock (SessionsLock) { Sessions[sessionId] = session; }
            _logger?.Info("[连接] SSE 客户端已接入 (session=" + sessionId + ")");

            // 阻塞保持连接，直到服务停止或连接被关闭。
            // 每隔 15 秒写一条 SSE 注释作为心跳：客户端断开时写入会抛 IOException，从而及时发现并清理会话。
            while (!session.Done.Wait(15000))
            {
                try
                {
                    _server.WriteSseComment(context, "keep-alive");
                }
                catch
                {
                    break; // 客户端已断开
                }
            }
            lock (SessionsLock) { Sessions.Remove(sessionId); }
            _logger?.Info("[断开] SSE 客户端断开 (session=" + sessionId + ")");
        }

        /// <summary>传统 SSE 传输的客户端→服务器消息通道。</summary>
        private static void HandleSseMessage(HttpContext context)
        {
            string sessionId = context.GetQuery("sessionId");
            SseSession session = null;
            lock (SessionsLock)
            {
                if (!string.IsNullOrEmpty(sessionId)) Sessions.TryGetValue(sessionId, out session);
            }

            if (session == null)
            {
                byte[] errBody = Encoding.UTF8.GetBytes(MiniJson.Serialize(
                    new JsonObject().Set("error", "SSE 会话不存在或已过期，请先 GET /sse 建立连接")));
                _server.WriteResponse(context, 400, "application/json; charset=utf-8", errBody);
                return;
            }

            // 解析并处理消息，响应通过 SSE 事件写回
            object response;
            try
            {
                object message = MiniJson.Parse(context.BodyText);
                response = _core.HandleMessage(message);
            }
            catch (FormatException ex)
            {
                response = BuildJsonRpcError(null, -32700, "JSON 解析失败: " + ex.Message);
            }

            if (response != null)
            {
                try
                {
                    _server.WriteSse(session.Context, "message", MiniJson.Serialize(response));
                }
                catch (Exception ex)
                {
                    // 客户端已断开：结束会话
                    session.Done.Set();
                    _logger?.Info("[断开] SSE 客户端连接失效 (session=" + sessionId + "): " + ex.Message);
                }
            }

            // SSE 传输约定：POST 返回 202，实际结果经事件流返回
            _server.WriteResponse(context, 202, "text/plain; charset=utf-8", new byte[0]);
        }

        // ------------------------------------------------------------------
        // 兜底：404 与 CORS 预检
        // ------------------------------------------------------------------

        /// <summary>兜底处理器：OPTIONS 预检返回 204，其余返回 404。</summary>
        private static void HandleFallback(HttpContext context)
        {
            if (context.Method == "OPTIONS")
            {
                var headers = new Dictionary<string, string>
                {
                    { "Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS" }
                };
                _server.WriteResponse(context, 204, "text/plain; charset=utf-8", new byte[0], headers);
                return;
            }

            var body = new JsonObject()
                .Set("error", "未找到路径")
                .Set("method", context.Method)
                .Set("path", context.Path)
                .Set("endpoints", new JsonArray()
                    .Push("POST " + McpPath + "（Streamable HTTP）")
                    .Push("GET " + SsePath + " + POST " + MessagePath + "（传统 SSE）")
                    .Push("GET /health")
                    .Push("GET /"));
            byte[] bytes = Encoding.UTF8.GetBytes(MiniJson.Serialize(body, true));
            _server.WriteResponse(context, 404, "application/json; charset=utf-8", bytes);
        }

        // ------------------------------------------------------------------
        // 辅助
        // ------------------------------------------------------------------

        /// <summary>获取状态 JSON（合并宿主提供的信息）。</summary>
        private static JsonObject GetStatusJson()
        {
            JsonObject status = _statusProvider != null ? _statusProvider() : new JsonObject();
            status.Set("name", _core.ServerName);
            status.Set("version", _core.ServerVersion);
            status.Set("protocolVersions", new JsonArray().Push("2024-11-05").Push("2025-03-26").Push("2025-06-18"));
            return status;
        }

        /// <summary>构造 JSON-RPC 错误响应。</summary>
        private static JsonObject BuildJsonRpcError(object id, int code, string message)
        {
            return new JsonObject()
                .Set("jsonrpc", "2.0")
                .Set("id", id)
                .Set("error", new JsonObject().Set("code", code).Set("message", message));
        }

        /// <summary>直接写出 JSON-RPC 错误响应（根据 Accept 选择 JSON 或 SSE）。</summary>
        private static void WriteJsonRpcError(HttpContext context, object id, int code, string message)
        {
            string json = MiniJson.Serialize(BuildJsonRpcError(id, code, message));
            byte[] body = Encoding.UTF8.GetBytes(json);
            if (context.AcceptsEventStream)
            {
                _server.BeginSse(context);
                _server.WriteSse(context, "message", json);
            }
            else
            {
                _server.WriteResponse(context, 200, "application/json; charset=utf-8", body);
            }
        }
    }
}
