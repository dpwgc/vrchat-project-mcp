// =================================================================================================
// McpSettingsWindow.cs
// 配置面板窗口（Tools → VRChat Project MCP → 配置面板）
// -------------------------------------------------------------------------------------------------
// 功能：
//   1. 配置 MCP 监听地址与端口（修改后需点击「重启」生效）；
//   2. 选择操作权限：只读（拒绝所有写入类工具）/ 读写（允许查询与写入）——修改立即生效；
//   3. 编辑器启动后自动启动服务开关；
//   4. 实时日志框：实时打印连接、调用、拒绝、错误等事件（富文本分色，可自动滚动）；
//   5. 启动 / 停止 / 重启 / 复制端点地址 / 清空日志等快捷操作。
// =================================================================================================

using UnityEditor;
using UnityEngine;
using VrchatProjectMcp.Core.Mcp;
using VrchatProjectMcp.Editor.Core;
using VrchatProjectMcp.Editor.Logging;

namespace VrchatProjectMcp.Editor.Settings
{
    /// <summary>
    /// VRChat Project MCP 配置面板窗口。
    /// </summary>
    public sealed class McpSettingsWindow : EditorWindow
    {
        private McpSettings _settings;
        private McpEditorLogger _logger;
        private Vector2 _scroll;
        private bool _autoScroll = true;

        /// <summary>菜单入口：打开配置面板。</summary>
        [MenuItem("Tools/VRChat Project MCP/配置面板", false, 0)]
        public static void Open()
        {
            McpSettingsWindow window = GetWindow<McpSettingsWindow>("VRChat Project MCP");
            window.minSize = new Vector2(560, 640);
            window.Show();
        }

        /// <summary>窗口启用：载入配置并订阅日志事件。</summary>
        private void OnEnable()
        {
            _settings = McpSettings.Current;
            _settings.Load();
            _logger = McpEditorLogger.Instance;
            _logger.EntryAdded += OnLogAdded;
        }

        /// <summary>窗口停用：退订日志事件。</summary>
        private void OnDisable()
        {
            if (_logger != null) _logger.EntryAdded -= OnLogAdded;
        }

        /// <summary>新日志到达时立即重绘窗口（实现"实时"效果）。</summary>
        private void OnLogAdded(McpLogEntry entry)
        {
            Repaint();
        }

        /// <summary>IMGUI 绘制入口。</summary>
        private void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space(6);
            DrawSettings();
            EditorGUILayout.Space(6);
            DrawButtons();
            EditorGUILayout.Space(6);
            DrawEndpointHelp();
            EditorGUILayout.Space(6);
            DrawLogBox();
        }

        /// <summary>绘制标题与运行状态横幅。</summary>
        private void DrawHeader()
        {
            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15, richText = true };
            EditorGUILayout.LabelField("VRChat Project MCP 配置面板", titleStyle);

            GUIStyle statusStyle = new GUIStyle(EditorStyles.label) { richText = true, wordWrap = true };
            if (McpServerController.IsRunning)
            {
                EditorGUILayout.LabelField(
                    "<color=#5DDB6E>● 服务运行中</color>　端点: <b>http://" + _settings.Host + ":" + McpServerController.BoundPort + "/mcp</b>" +
                    "　模式: " + _settings.AccessModeDisplayName, statusStyle);
            }
            else
            {
                EditorGUILayout.LabelField("<color=#999999>● 服务已停止</color>　配置端口: " + _settings.Port + "　模式: " + _settings.AccessModeDisplayName, statusStyle);
            }
        }

        /// <summary>绘制配置区（地址/端口/权限/自启）。</summary>
        private void DrawSettings()
        {
            EditorGUILayout.LabelField("服务配置", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            _settings.Host = EditorGUILayout.TextField("监听地址", _settings.Host);
            _settings.Port = Mathf.Clamp(EditorGUILayout.IntField("端口", _settings.Port), 0, 65535);

            // 操作权限选择：只读 / 读写（只读模式下 Agent 的所有写入类工具都会被服务端拒绝）
            string[] modes = { "只读（拒绝所有写入类工具）", "读写（允许查询与写入）" };
            int modeIndex = _settings.AccessMode == McpAccessMode.ReadOnly ? 0 : 1;
            modeIndex = EditorGUILayout.Popup("操作权限", modeIndex, modes);
            _settings.AccessMode = modeIndex == 0 ? McpAccessMode.ReadOnly : McpAccessMode.ReadWrite;

            _settings.AutoStart = EditorGUILayout.Toggle("编辑器启动后自动启动服务", _settings.AutoStart);

            if (EditorGUI.EndChangeCheck())
            {
                _settings.Save();
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.HelpBox(
                "· 监听地址/端口修改后需点击「重启」生效；操作权限修改立即生效（只读模式会实时拒绝写入类工具）。\n" +
                "· 端口填 0 表示自动分配（实际端口见顶部状态栏）。\n" +
                "· 安全提示：默认绑定 127.0.0.1 仅本机可访问；改为 0.0.0.0 会把服务暴露到局域网，请谨慎。",
                MessageType.Info);
        }

        /// <summary>绘制操作按钮区。</summary>
        private void DrawButtons()
        {
            EditorGUILayout.LabelField("操作", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();

            GUI.enabled = !McpServerController.IsRunning;
            if (GUILayout.Button("启动服务器", GUILayout.Height(26))) McpServerController.StartServer();
            GUI.enabled = McpServerController.IsRunning;
            if (GUILayout.Button("停止服务器", GUILayout.Height(26))) McpServerController.StopServer();
            if (GUILayout.Button("重启", GUILayout.Height(26))) McpServerController.RestartServer();
            GUI.enabled = true;

            if (GUILayout.Button("复制 MCP 端点", GUILayout.Height(26)))
            {
                GUIUtility.systemCopyBuffer = _settings.EndpointUrl;
                _logger.Info("[操作] 已复制端点地址到剪贴板: " + _settings.EndpointUrl);
            }
            if (GUILayout.Button("清空日志", GUILayout.Height(26))) _logger.Clear();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>绘制端点说明。</summary>
        private void DrawEndpointHelp()
        {
            EditorGUILayout.LabelField("HTTP 端点", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "POST /mcp        Streamable HTTP（JSON 请求；Accept: text/event-stream 时返回 SSE）\n" +
                "GET  /sse        传统 SSE 传输（建立长连接，配合 POST /message?sessionId=xxx 使用）\n" +
                "GET  /health     健康检查（JSON）\n" +
                "GET  /           中文信息页\n" +
                "协议：MCP over JSON-RPC 2.0（initialize → tools/list → tools/call）",
                MessageType.None);
        }

        /// <summary>绘制实时日志框。</summary>
        private void DrawLogBox()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("实时日志（连接 / 调用 / 拒绝 / 错误）", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            _autoScroll = EditorGUILayout.ToggleLeft("自动滚动", _autoScroll, GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();

            GUIStyle logStyle = new GUIStyle(EditorStyles.textArea)
            {
                richText = true,
                wordWrap = true,
                fontSize = 11,
            };

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            string logText = _logger.BuildRichText(500);
            GUILayout.TextArea(logText, logStyle, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            // 自动滚动到底部（下一帧生效）
            if (_autoScroll) _scroll.y = float.MaxValue;
        }
    }
}
