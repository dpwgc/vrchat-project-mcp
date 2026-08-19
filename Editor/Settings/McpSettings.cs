// =================================================================================================
// McpSettings.cs
// 插件配置：监听地址 / 端口 / 访问模式（只读-读写）/ 是否开机自启
// -------------------------------------------------------------------------------------------------
// 持久化方式：EditorPrefs（键名按项目路径做命名空间隔离，不同项目互不影响）。
// 读取时机：配置窗口打开时、服务器启动时（保证外部修改 EditorPrefs 也能生效）。
// 注意：监听地址与端口的修改需要重启服务器才生效；访问模式修改立即生效（权限门控实时读取）。
// =================================================================================================

using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using VrchatProjectMcp.Core.Mcp;

namespace VrchatProjectMcp.Editor.Settings
{
    /// <summary>
    /// 插件配置。
    /// </summary>
    public sealed class McpSettings
    {
        /// <summary>默认监听地址（仅本机访问，安全）。</summary>
        public const string DefaultHost = "127.0.0.1";

        /// <summary>默认端口。</summary>
        public const int DefaultPort = 8765;

        /// <summary>全局当前配置实例。</summary>
        public static readonly McpSettings Current = new McpSettings();

        /// <summary>配置变化事件（保存时触发）。</summary>
        public event Action Changed;

        /// <summary>EditorPrefs 键前缀：按项目路径隔离。</summary>
        private static readonly string KeyPrefix = BuildKeyPrefix();

        /// <summary>监听地址（如 127.0.0.1 / 0.0.0.0）。</summary>
        public string Host { get; set; } = DefaultHost;

        /// <summary>监听端口（1-65535，0 表示自动分配）。</summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>访问模式：只读 / 读写。</summary>
        public McpAccessMode AccessMode { get; set; } = McpAccessMode.ReadWrite;

        /// <summary>编辑器启动后是否自动启动 MCP 服务。</summary>
        public bool AutoStart { get; set; } = true;

        /// <summary>访问模式中文名。</summary>
        public string AccessModeDisplayName
        {
            get { return AccessMode == McpAccessMode.ReadOnly ? "只读" : "读写"; }
        }

        /// <summary>MCP 端点地址。</summary>
        public string EndpointUrl
        {
            get { return "http://" + Host + ":" + Port + "/mcp"; }
        }

        /// <summary>构造键前缀（真实项目名 + 稳定哈希，避免跨项目串配置）。</summary>
        private static string BuildKeyPrefix()
        {
            try
            {
                // Application.dataPath 的最后一段恒为 "Assets"，真实项目名取其上级目录名
                string projectName = Path.GetFileName(Application.dataPath);
                if (string.IsNullOrEmpty(projectName) || projectName == "Assets")
                    projectName = Path.GetFileName(Path.GetDirectoryName(Application.dataPath));
                if (string.IsNullOrEmpty(projectName)) projectName = "project";
                string safeName = projectName.Replace('.', '_').Replace(' ', '_');
                return "VrcProjectMcp." + safeName + "." + StableHash(Application.dataPath) + ".";
            }
            catch
            {
                return "VrcProjectMcp.default.";
            }
        }

        /// <summary>
        /// FNV-1a 32 位稳定哈希：跨会话/跨运行时结果一致。
        /// 不使用 string.GetHashCode()（部分运行时会做进程随机化，键可能漂移）。
        /// </summary>
        private static string StableHash(string text)
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash.ToString("X8");
        }

        /// <summary>生成完整 EditorPrefs 键。</summary>
        private static string Key(string field)
        {
            return KeyPrefix + field;
        }

        /// <summary>从 EditorPrefs 载入配置。</summary>
        public void Load()
        {
            Host = EditorPrefs.GetString(Key("host"), DefaultHost);
            Port = Mathf.Clamp(EditorPrefs.GetInt(Key("port"), DefaultPort), 0, 65535);
            int mode = EditorPrefs.GetInt(Key("mode"), (int)McpAccessMode.ReadWrite);
            AccessMode = mode == (int)McpAccessMode.ReadOnly ? McpAccessMode.ReadOnly : McpAccessMode.ReadWrite;
            AutoStart = EditorPrefs.GetBool(Key("autoStart"), true);
        }

        /// <summary>保存配置到 EditorPrefs 并触发 Changed 事件。</summary>
        public void Save()
        {
            EditorPrefs.SetString(Key("host"), string.IsNullOrEmpty(Host) ? DefaultHost : Host);
            EditorPrefs.SetInt(Key("port"), Mathf.Clamp(Port, 0, 65535));
            EditorPrefs.SetInt(Key("mode"), (int)AccessMode);
            EditorPrefs.SetBool(Key("autoStart"), AutoStart);
            Changed?.Invoke();
        }
    }
}
