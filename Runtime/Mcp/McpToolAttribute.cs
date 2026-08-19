// =================================================================================================
// McpToolAttribute.cs
// 工具与参数标注特性
// -------------------------------------------------------------------------------------------------
// 使用方式：
//   [McpTool("unity.get_scene_info", McpToolAccess.Query, "unity", "获取当前场景信息")]
//   public static object GetSceneInfo() { ... }
//
// 说明：
//   - 标注了 [McpTool] 的公开静态方法会被工具注册表自动扫描注册（扩展方式二）；
//   - McpToolAccess 标明工具是查询还是写入，写入类工具在只读模式下会被服务端拒绝；
//   - SuggestConfirmation 供 Agent 参考：为 true 表示建议调用前先向用户二次确认。
// =================================================================================================

using System;

namespace VrchatProjectMcp.Core.Mcp
{
    /// <summary>
    /// 标注一个公开静态方法为 MCP 工具。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class McpToolAttribute : Attribute
    {
        /// <summary>工具唯一名称（建议命名规则：命名空间前缀.动作名，如 "vrc.get_avatar_info"）。</summary>
        public string Name { get; }

        /// <summary>工具访问类型：查询（只读安全）或写入（会修改项目）。</summary>
        public McpToolAccess Access { get; }

        /// <summary>工具分类（如 unity / vrc / mcp / 自定义分类），用于分组展示。</summary>
        public string Category { get; }

        /// <summary>工具功能的中文描述（会展示给 Agent）。</summary>
        public string Description { get; }

        /// <summary>是否建议 Agent 在调用前向用户二次确认（仅提示性元数据，默认 true）。</summary>
        public bool SuggestConfirmation { get; set; } = true;

        /// <summary>构造工具特性。</summary>
        public McpToolAttribute(string name, McpToolAccess access, string category, string description)
        {
            Name = name;
            Access = access;
            Category = category;
            Description = description;
        }
    }

    /// <summary>
    /// 标注工具方法参数：提供参数中文说明，并可强制指定为必填。
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class McpParamAttribute : Attribute
    {
        /// <summary>参数中文说明（会进入工具的 inputSchema 供 Agent 阅读）。</summary>
        public string Description { get; set; }

        /// <summary>是否必填（默认：值类型参数必填，引用类型参数可选）。</summary>
        public bool Required { get; set; }

        /// <summary>构造参数特性。</summary>
        public McpParamAttribute()
        {
        }

        /// <summary>带说明的构造。</summary>
        public McpParamAttribute(string description)
        {
            Description = description;
        }
    }
}
