// =================================================================================================
// JsonRpcCore.cs
// JSON-RPC 2.0 分发核心（MCP 协议层，纯 C#，无 Unity 依赖）
// -------------------------------------------------------------------------------------------------
// 支持的方法：
//   initialize            —— MCP 握手，返回协议版本 / 服务器能力 / 服务器信息
//   notifications/initialized —— 客户端握手完成通知（无响应）
//   ping                  —— 心跳
//   tools/list            —— 列出全部工具（含读写类型标注）
//   tools/call            —— 调用工具（含只读模式门控）
//   resources/list        —— 列出资源
//   resources/read        —— 读取资源
//   resources/templates/list —— 资源模板（返回空列表）
//   prompts/list          —— 提示词（返回空列表）
//   logging/setLevel      —— 客户端日志级别设置（接受并忽略）
//
// 兼容说明：
//   - 支持单条消息与批量数组两种形式；
//   - 对 2024-11-05 / 2025-03-26 / 2025-06-18 等协议版本一视同仁（回显客户端版本）；
//   - 未知方法返回 -32601；参数错误返回 -32602；内部错误返回 -32603。
// =================================================================================================

using System;
using System.Collections.Generic;
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Logging;

namespace VrchatProjectMcp.Core.Mcp
{
    /// <summary>
    /// JSON-RPC 分发核心：把 MCP 消息分发给注册表 / 资源系统。
    /// </summary>
    public sealed class JsonRpcCore
    {
        /// <summary>工具注册表。</summary>
        public McpToolRegistry Registry { get; }

        /// <summary>日志器（可为 null）。</summary>
        public IMcpLogger Logger { get; }

        /// <summary>服务器名称（initialize 响应用）。</summary>
        public string ServerName { get; set; } = McpConstants.ServerName;

        /// <summary>服务器版本（initialize 响应用）。</summary>
        public string ServerVersion { get; set; } = McpConstants.ServerVersion;

        /// <summary>initialize 响应中的使用说明。</summary>
        public string Instructions { get; set; } = McpConstants.Instructions;

        /// <summary>构造分发核心。</summary>
        public JsonRpcCore(McpToolRegistry registry, IMcpLogger logger = null)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Logger = logger;
        }

        /// <summary>
        /// 处理一条 JSON-RPC 消息（已解析的 JSON 对象 / 批量数组）。
        /// 返回响应对象（JsonObject / JsonArray），无响应时返回 null。
        /// </summary>
        public object HandleMessage(object rawMessage)
        {
            // 批量请求：逐条处理，收集有 id 的响应
            if (rawMessage is JsonArray batch)
            {
                // JSON-RPC 2.0 规范：空批量数组是无效请求，返回单个 -32600 错误
                if (batch.Count == 0)
                {
                    return ErrorResponse(null, -32600, "无效请求：批量数组不能为空");
                }
                var responses = new JsonArray();
                foreach (object item in batch)
                {
                    object response = HandleSingle(item);
                    if (response != null) responses.Add(response);
                }
                return responses.Count > 0 ? responses : null;
            }
            return HandleSingle(rawMessage);
        }

        /// <summary>处理单条 JSON-RPC 消息。</summary>
        private object HandleSingle(object rawMessage)
        {
            if (!(rawMessage is JsonObject message))
            {
                return ErrorResponse(null, -32600, "无效请求：消息必须是 JSON 对象");
            }

            bool hasId = message.ContainsKey("id");
            object id = hasId ? message["id"] : null;
            object methodValue = message.ContainsKey("method") ? message["method"] : null;

            // method 缺失或不是字符串 → 无效请求 -32600
            if (!(methodValue is string method))
            {
                return hasId ? ErrorResponse(id, -32600, "无效请求：method 字段缺失或不是字符串") : null;
            }
            if (string.IsNullOrEmpty(method))
            {
                return hasId ? ErrorResponse(id, -32600, "无效请求：method 字段为空") : null;
            }

            // 通知类消息（无 id）不产生响应；未知方法且无 id 直接忽略
            try
            {
                switch (method)
                {
                    case "initialize":
                        return hasId ? HandleInitialize(message, id) : null;

                    case "notifications/initialized":
                    case "notifications/cancelled":
                    case "notifications/roots/list_changed":
                        return null; // 通知，无需响应

                    case "ping":
                        return hasId ? OkResponse(id, new JsonObject()) : null;

                    case "tools/list":
                        return hasId ? HandleToolsList(id) : null;

                    case "tools/call":
                        return hasId ? HandleToolsCall(message, id) : null;

                    case "resources/list":
                        return hasId ? HandleResourcesList(id) : null;

                    case "resources/read":
                        return hasId ? HandleResourcesRead(message, id) : null;

                    case "resources/templates/list":
                        return hasId ? OkResponse(id, new JsonObject().Set("resourceTemplates", new JsonArray())) : null;

                    case "prompts/list":
                        return hasId ? OkResponse(id, new JsonObject().Set("prompts", new JsonArray())) : null;

                    case "logging/setLevel":
                        return hasId ? OkResponse(id, new JsonObject()) : null;

                    case "completion/complete":
                        return hasId ? ErrorResponse(id, -32601, "completion/complete 暂不支持") : null;

                    default:
                        return hasId ? ErrorResponse(id, -32601, "未知方法: " + method) : null;
                }
            }
            catch (McpToolException ex)
            {
                Logger?.Warn("[MCP] 处理 " + method + " 失败: " + ex.Message);
                return hasId ? ErrorResponse(id, ex.Code, ex.Message) : null;
            }
            catch (Exception ex)
            {
                Logger?.Error("[MCP] 处理 " + method + " 异常: " + ex);
                return hasId ? ErrorResponse(id, -32603, "内部错误: " + ex.Message) : null;
            }
        }

        /// <summary>MCP 握手：initialize（协议版本协商：支持客户端请求的已知版本时回显，否则返回最新版本）。</summary>
        private object HandleInitialize(JsonObject message, object id)
        {
            JsonObject parameters = message.GetObject("params") ?? new JsonObject();
            JsonObject clientInfo = parameters.GetObject("clientInfo") ?? new JsonObject();
            string requested = parameters.GetString("protocolVersion", "2025-03-26");

            // 本服务对 2024-11-05 / 2025-03-26 / 2025-06-18 一视同仁；
            // 客户端请求已知版本时按规范回显，请求未知版本时返回本服务支持的最新版本。
            string protocolVersion = requested;
            if (requested != "2024-11-05" && requested != "2025-03-26" && requested != "2025-06-18")
            {
                protocolVersion = "2025-06-18";
            }

            Logger?.Info("[MCP] initialize: 客户端=" + clientInfo.GetString("name", "未知") +
                         " " + clientInfo.GetString("version", "") + "，协议版本=" + protocolVersion +
                         "（客户端请求 " + requested + "）");

            var result = new JsonObject()
                .Set("protocolVersion", protocolVersion)
                .Set("capabilities", new JsonObject()
                    .Set("tools", new JsonObject().Set("listChanged", false))
                    .Set("resources", new JsonObject().Set("subscribe", false).Set("listChanged", false))
                    .Set("logging", new JsonObject()))
                .Set("serverInfo", new JsonObject()
                    .Set("name", ServerName)
                    .Set("version", ServerVersion))
                .Set("instructions", Instructions);
            return OkResponse(id, result);
        }

        /// <summary>列出全部工具（自动触发首次扫描）。</summary>
        private object HandleToolsList(object id)
        {
            Registry.ScanAllAssemblies(false); // 首次调用时自动扫描（幂等）
            return OkResponse(id, new JsonObject().Set("tools", Registry.BuildToolListJson()));
        }

        /// <summary>调用工具：arguments 支持对象（按名称）与数组（按位置）。</summary>
        private object HandleToolsCall(JsonObject message, object id)
        {
            JsonObject parameters = message.GetObject("params");
            if (parameters == null)
            {
                return ErrorResponse(id, -32602, "缺少 params 参数");
            }
            string name = parameters.GetString("name");
            if (string.IsNullOrEmpty(name))
            {
                return ErrorResponse(id, -32602, "缺少工具名称 params.name");
            }
            object arguments = parameters.ContainsKey("arguments") ? parameters["arguments"] : new JsonObject();
            JsonObject callResult = Registry.CallTool(name, arguments);
            return OkResponse(id, callResult);
        }

        /// <summary>列出资源。</summary>
        private object HandleResourcesList(object id)
        {
            var array = new JsonArray();
            foreach (McpResourceDefinition resource in Registry.Resources) array.Add(resource.ToJson());
            return OkResponse(id, new JsonObject().Set("resources", array));
        }

        /// <summary>读取资源。</summary>
        private object HandleResourcesRead(JsonObject message, object id)
        {
            JsonObject parameters = message.GetObject("params");
            string uri = parameters != null ? parameters.GetString("uri") : null;
            if (string.IsNullOrEmpty(uri))
            {
                return ErrorResponse(id, -32602, "缺少资源 URI params.uri");
            }

            McpResourceDefinition resource = null;
            foreach (McpResourceDefinition r in Registry.Resources)
            {
                if (string.Equals(r.Uri, uri, StringComparison.OrdinalIgnoreCase))
                {
                    resource = r;
                    break;
                }
            }
            if (resource == null)
            {
                return ErrorResponse(id, -32001, "资源不存在: " + uri);
            }

            try
            {
                object raw;
                if (Registry.MainThreadInvoker != null)
                {
                    raw = Registry.MainThreadInvoker(() => resource.ReadHandler());
                }
                else
                {
                    raw = resource.ReadHandler();
                }
                string text = raw is string s ? s : MiniJson.Serialize(raw, true);
                var contents = new JsonArray().Push(new JsonObject()
                    .Set("uri", uri)
                    .Set("mimeType", resource.MimeType)
                    .Set("text", text));
                return OkResponse(id, new JsonObject().Set("contents", contents));
            }
            catch (Exception ex)
            {
                Logger?.Warn("[MCP] 读取资源失败 " + uri + ": " + ex.Message);
                return ErrorResponse(id, -32003, "资源读取失败: " + ex.Message);
            }
        }

        /// <summary>构造成功响应。</summary>
        private static object OkResponse(object id, JsonObject result)
        {
            return new JsonObject().Set("jsonrpc", "2.0").Set("id", id).Set("result", result);
        }

        /// <summary>构造错误响应。</summary>
        private static object ErrorResponse(object id, int code, string message)
        {
            return new JsonObject()
                .Set("jsonrpc", "2.0")
                .Set("id", id)
                .Set("error", new JsonObject().Set("code", code).Set("message", message));
        }
    }
}
