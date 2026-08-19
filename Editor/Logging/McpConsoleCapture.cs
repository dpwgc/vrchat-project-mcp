// =================================================================================================
// McpConsoleCapture.cs
// Unity 控制台日志采集器
// -------------------------------------------------------------------------------------------------
// 职责：
//   1. 订阅 Application.logMessageReceived，把控制台日志存入内存环形缓冲；
//   2. 供 unity.get_console_logs 工具读取（Agent 可据此排查项目报错）；
//   3. 编辑器日志文件（Editor.log）的尾部由工具层另行读取，本类只负责运行时采集。
// =================================================================================================

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VrchatProjectMcp.Editor.Logging
{
    /// <summary>一条控制台日志。</summary>
    public sealed class ConsoleLogEntry
    {
        /// <summary>产生时间。</summary>
        public DateTime Time;

        /// <summary>级别（Log/Warning/Error/Exception/Assert）。</summary>
        public string Level;

        /// <summary>消息内容。</summary>
        public string Message;

        /// <summary>堆栈（可能为空）。</summary>
        public string Stack;
    }

    /// <summary>
    /// 控制台日志采集器（内部静态类）。
    /// </summary>
    internal static class McpConsoleCapture
    {
        /// <summary>环形缓冲最大条数。</summary>
        private const int MaxEntries = 1000;

        private static readonly List<ConsoleLogEntry> Entries = new List<ConsoleLogEntry>();
        private static readonly object Lock = new object();

        /// <summary>初始化：订阅控制台日志事件（域重载后由 InitializeOnLoad 自动重新订阅）。</summary>
        [InitializeOnLoadMethod]
        private static void Init()
        {
            Application.logMessageReceived += OnLogMessage;
        }

        /// <summary>日志回调：写入环形缓冲。</summary>
        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(condition)) return;
            lock (Lock)
            {
                Entries.Add(new ConsoleLogEntry
                {
                    Time = DateTime.Now,
                    Level = type.ToString(),
                    Message = condition,
                    Stack = stackTrace,
                });
                if (Entries.Count > MaxEntries) Entries.RemoveRange(0, Entries.Count - MaxEntries);
            }
        }

        /// <summary>
        /// 取日志快照。
        /// levelFilter："All" 或具体级别名（大小写不敏感）；search：对消息与堆栈做包含过滤（可为空）。
        /// </summary>
        public static List<ConsoleLogEntry> Snapshot(int maxLines, string levelFilter, string search)
        {
            var result = new List<ConsoleLogEntry>();
            lock (Lock)
            {
                foreach (ConsoleLogEntry entry in Entries)
                {
                    if (!string.IsNullOrEmpty(levelFilter) && !string.Equals(levelFilter, "All", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(levelFilter, entry.Level, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(search)
                        && entry.Message.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                        && (entry.Stack == null || entry.Stack.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                    result.Add(entry);
                }
            }
            if (result.Count > maxLines) result.RemoveRange(0, result.Count - maxLines);
            return result;
        }

        /// <summary>清空缓冲。</summary>
        public static void Clear()
        {
            lock (Lock) { Entries.Clear(); }
        }
    }
}
