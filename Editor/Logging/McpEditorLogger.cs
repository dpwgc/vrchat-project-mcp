// =================================================================================================
// McpEditorLogger.cs
// 插件日志器：Core 的 IMcpLogger 实现
// -------------------------------------------------------------------------------------------------
// 日志去向：
//   1. 环形缓冲（内存，最多 MaxEntries 条）→ 供配置窗口实时日志框读取；
//   2. Unity 控制台（Debug.Log / LogWarning / LogError）；
//   3. EntryAdded 事件 → 配置窗口收到后立即 Repaint，实现"实时日志"。
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using VrchatProjectMcp.Core.Logging;

namespace VrchatProjectMcp.Editor.Logging
{
    /// <summary>日志级别。</summary>
    public enum McpLogLevel
    {
        /// <summary>普通信息。</summary>
        Info = 0,

        /// <summary>警告。</summary>
        Warn = 1,

        /// <summary>错误。</summary>
        Error = 2,
    }

    /// <summary>单条日志记录。</summary>
    public sealed class McpLogEntry
    {
        /// <summary>记录时间。</summary>
        public DateTime Time;

        /// <summary>级别。</summary>
        public McpLogLevel Level;

        /// <summary>内容。</summary>
        public string Message;
    }

    /// <summary>
    /// 插件日志器（单例）。
    /// </summary>
    public sealed class McpEditorLogger : IMcpLogger
    {
        /// <summary>环形缓冲最大条数。</summary>
        public const int MaxEntries = 2000;

        private static McpEditorLogger _instance;

        /// <summary>单例访问。</summary>
        public static McpEditorLogger Instance
        {
            get
            {
                if (_instance == null) _instance = new McpEditorLogger();
                return _instance;
            }
        }

        private readonly List<McpLogEntry> _entries = new List<McpLogEntry>();
        private readonly object _lock = new object();

        /// <summary>新日志事件（配置窗口订阅后实时刷新）。</summary>
        public event Action<McpLogEntry> EntryAdded;

        /// <summary>记录普通信息。</summary>
        public void Info(string message)
        {
            Add(McpLogLevel.Info, message);
        }

        /// <summary>记录警告。</summary>
        public void Warn(string message)
        {
            Add(McpLogLevel.Warn, message);
        }

        /// <summary>记录错误。</summary>
        public void Error(string message)
        {
            Add(McpLogLevel.Error, message);
        }

        /// <summary>写入日志（线程安全，HTTP 工作线程也会调用）。</summary>
        private void Add(McpLogLevel level, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var entry = new McpLogEntry { Time = DateTime.Now, Level = level, Message = message };

            lock (_lock)
            {
                _entries.Add(entry);
                if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }

            // 转发到 Unity 控制台（Debug.Log 系列自带主线程安全性）
            switch (level)
            {
                case McpLogLevel.Warn:
                    Debug.LogWarning("[VrcProjectMCP] " + message);
                    break;
                case McpLogLevel.Error:
                    Debug.LogError("[VrcProjectMCP] " + message);
                    break;
                default:
                    Debug.Log("[VrcProjectMCP] " + message);
                    break;
            }

            // EntryAdded 事件（如配置窗口的 Repaint）必须回到主线程触发：
            // HTTP 工作线程也可能调用本日志器，直接触发会把 EditorWindow 操作带到非主线程。
            try
            {
                VrchatProjectMcp.Editor.Core.McpMainThreadDispatcher.Post(() => EntryAdded?.Invoke(entry));
            }
            catch
            {
                EntryAdded?.Invoke(entry); // 调度器不可用时兜底直发
            }
        }

        /// <summary>取最近 maxLines 条日志快照。</summary>
        public List<McpLogEntry> Snapshot(int maxLines)
        {
            lock (_lock)
            {
                int start = Math.Max(0, _entries.Count - maxLines);
                return _entries.GetRange(start, _entries.Count - start);
            }
        }

        /// <summary>清空日志缓冲。</summary>
        public void Clear()
        {
            lock (_lock) { _entries.Clear(); }
        }

        /// <summary>生成带颜色的富文本（配置窗口实时日志框使用，自动截取最近 maxLines 条）。</summary>
        public string BuildRichText(int maxLines)
        {
            List<McpLogEntry> list = Snapshot(maxLines);
            var sb = new StringBuilder(list.Count * 64);
            foreach (McpLogEntry entry in list)
            {
                string color;
                switch (entry.Level)
                {
                    case McpLogLevel.Warn: color = "#E8C24A"; break;
                    case McpLogLevel.Error: color = "#FF7B72"; break;
                    default: color = "#D0D0D0"; break;
                }
                sb.Append("<color=#777777>[")
                  .Append(entry.Time.ToString("HH:mm:ss"))
                  .Append("]</color> ")
                  .Append("<color=").Append(color).Append(">")
                  .Append(EscapeRichText(entry.Message))
                  .Append("</color>\n");
            }
            return sb.ToString();
        }

        /// <summary>转义富文本标记（插入零宽空格防止消息内容被当成标签解析）。</summary>
        private static string EscapeRichText(string text)
        {
            return text == null ? string.Empty : text.Replace("<", "\u200B<");
        }
    }
}
