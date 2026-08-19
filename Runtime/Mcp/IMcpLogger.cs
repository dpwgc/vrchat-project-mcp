// =================================================================================================
// IMcpLogger.cs
// 日志接口：Core（纯 C# 协议层）不依赖 Unity API，通过该接口把日志交给宿主实现。
// -------------------------------------------------------------------------------------------------
// Editor 端的实现（McpEditorLogger）会把日志同时写入：
//   1. 配置窗口的实时日志框（富文本，带颜色分级）；
//   2. Unity 控制台（Debug.Log / LogWarning / LogError）。
// =================================================================================================

namespace VrchatProjectMcp.Core.Logging
{
    /// <summary>
    /// MCP 日志接口：记录连接、调用、拒绝、错误等事件。
    /// </summary>
    public interface IMcpLogger
    {
        /// <summary>记录普通信息。</summary>
        void Info(string message);

        /// <summary>记录警告。</summary>
        void Warn(string message);

        /// <summary>记录错误。</summary>
        void Error(string message);
    }
}
