// =================================================================================================
// McpTypes.cs
// MCP 协议基础类型与常量定义
// -------------------------------------------------------------------------------------------------
// 包含：
//   1. McpAccessMode —— 服务端访问模式（只读 / 读写），由宿主（Editor 配置窗口）控制；
//   2. McpToolAccess —— 工具访问类型标注（查询 / 写入），用于 Agent 判定是否需要用户二次确认；
//   3. McpToolException —— 工具执行/权限类异常，携带 JSON-RPC 错误码；
//   4. IMcpPermissionGate —— 权限门控接口（宿主实现）；
//   5. IMcpToolProvider —— 工具扩展点接口（扩展方式一）；
//   6. McpResourceDefinition —— MCP 资源定义；
//   7. McpConstants —— 插件常量。
// =================================================================================================

using System;
using System.Collections.Generic;
using VrchatProjectMcp.Core.Json;

namespace VrchatProjectMcp.Core.Mcp
{
    /// <summary>
    /// MCP 服务访问模式。
    /// </summary>
    public enum McpAccessMode
    {
        /// <summary>只读模式：只允许查询类工具，写入类调用会被服务端直接拒绝。</summary>
        ReadOnly = 0,

        /// <summary>读写模式：查询与写入类工具均允许。</summary>
        ReadWrite = 1,
    }

    /// <summary>
    /// 工具访问类型标注。
    /// 每个 MCP 工具都必须声明自己的类型，Agent 可据此决定是否需要用户二次确认。
    /// </summary>
    public enum McpToolAccess
    {
        /// <summary>查询（只读安全）：不修改项目任何内容。</summary>
        Query = 0,

        /// <summary>写入：会修改场景 / 资产 / 项目文件，只读模式下被拒绝。</summary>
        Write = 1,
    }

    /// <summary>
    /// 插件常量。
    /// </summary>
    public static class McpConstants
    {
        /// <summary>MCP 服务器名称。</summary>
        public const string ServerName = "vrchat-project-mcp";

        /// <summary>插件版本号（与 package.json 保持一致）。</summary>
        public const string ServerVersion = "0.1.0";

        /// <summary>initialize 响应中的说明文本，引导 Agent 正确使用本服务。</summary>
        public const string Instructions =
            "本 MCP 服务器面向 VRChat 模型开发，提供 Unity 项目与 VRChat 头像的查询/写入工具。" +
            "每个工具都标注了访问类型：query=只读查询；write=写入操作（会修改场景/资产）。" +
            "服务端可处于【只读】或【读写】模式：只读模式下所有 write 工具都会被拒绝。" +
            "对 write 类工具的调用建议先获得用户确认。使用 tools/list 查看全部工具，" +
            "使用 unity.get_console_logs 阅读控制台日志排查问题。";
    }

    /// <summary>
    /// 工具执行 / 权限类异常：携带错误码，可被上层转换为 isError 的工具结果。
    /// </summary>
    public sealed class McpToolException : Exception
    {
        /// <summary>JSON-RPC 风格错误码（-32000 区间为自定义服务器错误）。</summary>
        public int Code { get; set; }

        /// <summary>是否为权限拒绝（只读模式拦截）。</summary>
        public bool PermissionDenied { get; set; }

        /// <summary>构造工具异常。</summary>
        public McpToolException(string message, int code = -32000, bool permissionDenied = false)
            : base(message)
        {
            Code = code;
            PermissionDenied = permissionDenied;
        }
    }

    /// <summary>
    /// 权限门控接口：由宿主（Editor 端）实现。
    /// 服务端在执行任何 write 类工具前都会询问该接口当前的访问模式。
    /// </summary>
    public interface IMcpPermissionGate
    {
        /// <summary>当前访问模式。</summary>
        McpAccessMode Mode { get; }
    }

    /// <summary>
    /// 工具扩展点接口（扩展方式一）：
    /// 任意程序集实现该接口并提供公共无参构造函数，插件扫描时会自动实例化并注册其返回的工具。
    /// 扩展方式二见 McpToolAttribute（在静态方法上标注特性，自动被发现）。
    /// </summary>
    public interface IMcpToolProvider
    {
        /// <summary>返回需要注册的工具定义集合。</summary>
        IEnumerable<McpToolDefinition> RegisterTools();
    }

    /// <summary>
    /// MCP 资源定义：可通过 resources/list 与 resources/read 访问的只读数据源。
    /// </summary>
    public sealed class McpResourceDefinition
    {
        /// <summary>资源 URI（如 "mcp://status"）。</summary>
        public string Uri { get; set; }

        /// <summary>资源显示名称。</summary>
        public string Name { get; set; }

        /// <summary>资源描述。</summary>
        public string Description { get; set; }

        /// <summary>MIME 类型。</summary>
        public string MimeType { get; set; } = "application/json";

        /// <summary>读取回调：返回 JSON 可序列化对象或字符串（建议在宿主中包装为主线程执行）。</summary>
        public Func<object> ReadHandler { get; set; }

        /// <summary>转换为 MCP resources/list 条目 JSON。</summary>
        public JsonObject ToJson()
        {
            return new JsonObject()
                .Set("uri", Uri)
                .Set("name", Name ?? Uri)
                .Set("description", Description ?? string.Empty)
                .Set("mimeType", MimeType);
        }
    }
}
