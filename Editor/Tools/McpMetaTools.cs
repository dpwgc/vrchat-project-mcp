// =================================================================================================
// McpMetaTools.cs
// MCP 元工具：服务状态查询与工具注册表刷新
// -------------------------------------------------------------------------------------------------
// 对应 MCP 工具（前缀 mcp）：
//   mcp.get_status     查询 服务状态 + 全部工具清单（含读写类型标注）
//   mcp.refresh_tools  查询 重新扫描程序集并刷新工具注册表
// =================================================================================================

using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Mcp;
using VrchatProjectMcp.Editor.Core;

namespace VrchatProjectMcp.Editor.Tools
{
    /// <summary>
    /// MCP 元工具（内部静态类）。
    /// </summary>
    internal static class McpMetaTools
    {
        /// <summary>获取服务状态与全部工具清单（每个工具附带 access 读写标注）。</summary>
        [McpTool("mcp.get_status", McpToolAccess.Query, "mcp", "获取 MCP 服务运行状态、访问模式、全部工具清单（每个工具标注 query 查询 / write 写入类型）")]
        public static object GetStatus()
        {
            JsonObject result = McpServerController.BuildStatus();

            var tools = new JsonArray();
            foreach (McpToolDefinition tool in McpToolRegistry.Instance.ListTools())
            {
                tools.Add(new JsonObject()
                    .Set("name", tool.Name)
                    .Set("access", tool.Access == McpToolAccess.Write ? "write" : "query")
                    .Set("accessText", tool.AccessText)
                    .Set("category", tool.Category)
                    .Set("description", tool.Description));
            }
            result.Set("tools", tools);
            result.Set("endpoints", new JsonArray()
                .Push("POST /mcp（Streamable HTTP）")
                .Push("GET /sse + POST /message（传统 SSE）")
                .Push("GET /health（健康检查）")
                .Push("GET /（信息页）"));
            return result;
        }

        /// <summary>重新扫描全部程序集并刷新工具注册表（安装/卸载扩展后调用）。</summary>
        [McpTool("mcp.refresh_tools", McpToolAccess.Query, "mcp", "重新扫描全部程序集并刷新工具注册表（新增/移除扩展工具后调用一次即可生效）")]
        public static object RefreshTools()
        {
            McpToolRegistry registry = McpToolRegistry.Instance;
            registry.ScanAllAssemblies(true);
            var names = new JsonArray();
            foreach (McpToolDefinition tool in registry.ListTools()) names.Add(tool.Name);
            return new JsonObject()
                .Set("refreshed", true)
                .Set("toolCount", (long)names.Count)
                .Set("tools", names);
        }
    }
}
