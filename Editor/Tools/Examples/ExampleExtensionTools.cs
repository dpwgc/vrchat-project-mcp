// =================================================================================================
// ExampleExtensionTools.cs
// 【扩展示例】如何为插件新增自定义 MCP 工具
// -------------------------------------------------------------------------------------------------
// 方式一（推荐）：属性标注法 —— 在任何引用了 VrchatProjectMcp.Core 的程序集中，
//   给公开静态方法标注 [McpTool] 特性即可。插件启动或调用 mcp.refresh_tools 时
//   会自动扫描注册，无需修改插件源码。
//
// 方式二：接口实现法 —— 实现 IMcpToolProvider 接口（见下方注释示例），
//   适合需要动态决定工具集合、或工具依赖运行时状态（如插件是否安装）的场景。
//
// 工具定义要点：
//   - Name 建议用「命名空间前缀.动作名」命名（如 mytools.do_something）；
//   - Access 必须标明 McpToolAccess.Query（查询）或 McpToolAccess.Write（写入）：
//     写入类工具在服务端【只读】模式下会被自动拒绝；
//   - 方法参数会通过反射自动生成 inputSchema（支持中文 [McpParam] 说明）；
//   - 返回值用 JsonObject / JsonArray / 字符串组织，自动序列化为 MCP 结果。
//
// 本文件是演示代码，可直接删除；保留时 example.hello 会出现在工具清单中。
// =================================================================================================

using System;
using System.Collections.Generic;
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Mcp;

namespace VrchatProjectMcp.Editor.Tools.Examples
{
    /// <summary>
    /// 扩展示例工具（内部静态类，可删除）。
    /// </summary>
    internal static class ExampleExtensionTools
    {
        /// <summary>扩展示例：向 Agent 打个招呼，演示自定义查询工具的注册方式。</summary>
        [McpTool("example.hello", McpToolAccess.Query, "example", "扩展示例：向 Agent 打个招呼（演示如何注册自定义工具，可删除本文件）")]
        public static object Hello(
            [McpParam("称呼（默认 Agent）")] string who = "Agent")
        {
            return new JsonObject()
                .Set("message", "你好，" + (string.IsNullOrEmpty(who) ? "Agent" : who) + "！这是 VRChat Project MCP 的扩展示例工具。")
                .Set("time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        // -------------------------------------------------------------------------------------------
        // 方式二示例：实现 IMcpToolProvider 接口（取消注释即可生效）
        //   接口实现类需要公共无参构造函数；插件扫描时会自动实例化并注册 RegisterTools() 返回的工具。
        // -------------------------------------------------------------------------------------------
        // public sealed class ExampleToolProvider : IMcpToolProvider
        // {
        //     public IEnumerable<McpToolDefinition> RegisterTools()
        //     {
        //         // 手工构造一个写入类工具：演示动态注册 + 只读模式门控
        //         var definition = new McpToolDefinition
        //         {
        //             Name = "example.dynamic_write",
        //             Access = McpToolAccess.Write,
        //             Category = "example",
        //             Description = "动态注册的写入类扩展示例",
        //         };
        //         definition.Parameters.Add(new McpParamDefinition { Name = "message", JsonType = "string", Required = true });
        //         definition.Handler = args =>
        //         {
        //             var jo = args as JsonObject;
        //             return new JsonObject().Set("wrote", jo != null ? jo.GetString("message") : null);
        //         };
        //         yield return definition;
        //     }
        // }
    }
}
