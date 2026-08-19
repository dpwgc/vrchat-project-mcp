// =================================================================================================
// McpServerController.cs
// 服务器生命周期控制器（Editor 入口）
// -------------------------------------------------------------------------------------------------
// 职责：
//   1. 组装 Core 层组件：注册表（注入权限门控/日志/主线程调度器）→ JsonRpcCore → HTTP 服务器 → 端点注册；
//   2. 提供启动 / 停止 / 重启 API 与菜单项；
//   3. 注册内置 MCP 资源（状态 / 项目信息 / 控制台日志）；
//   4. 域重载前自动停止服务，避免残留套接字。
//
// 【预留扩展位置说明】
//   需要自定义 HTTP 端点或 MCP 资源时，可在这里（或扩展程序集的初始化代码中）继续调用：
//   - McpServerController.Server.AddHandler("GET", "/你的路径", ctx => { ... });
//   - McpToolRegistry.Instance.Resources.Add(new McpResourceDefinition { ... });
// =================================================================================================

using System;
using UnityEditor;
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Logging;
using VrchatProjectMcp.Core.Mcp;
using VrchatProjectMcp.Core.Net;
using VrchatProjectMcp.Editor.Logging;
using VrchatProjectMcp.Editor.Settings;
using VrchatProjectMcp.Editor.Tools;

namespace VrchatProjectMcp.Editor.Core
{
    /// <summary>
    /// 服务器生命周期控制器。
    /// </summary>
    [InitializeOnLoad]
    public static class McpServerController
    {
        private static SimpleHttpServer _server;
        private static JsonRpcCore _core;
        private static McpToolRegistry _registry;
        private static McpEditorLogger _logger;
        private static McpSettings _settings;
        private static DateTime _startedAt;

        /// <summary>初始化：注册域重载钩子，并按配置自动启动。</summary>
        [InitializeOnLoadMethod]
        private static void Init()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            if (McpSettings.Current.AutoStart)
            {
                // delayCall：等待编辑器完全就绪后再启动
                EditorApplication.delayCall += () =>
                {
                    if (!IsRunning) StartServer();
                };
            }
        }

        /// <summary>服务是否正在运行。</summary>
        public static bool IsRunning
        {
            get { return _server != null && _server.IsRunning; }
        }

        /// <summary>当前 JsonRpcCore（扩展代码可访问）。</summary>
        public static JsonRpcCore Core
        {
            get { return _core; }
        }

        /// <summary>当前 HTTP 服务器（扩展自定义端点用）。</summary>
        public static SimpleHttpServer Server
        {
            get { return _server; }
        }

        /// <summary>实际绑定端口（端口 0 自动分配时取实际值）。</summary>
        public static int BoundPort
        {
            get { return _server != null && _server.BoundPort > 0 ? _server.BoundPort : McpSettings.Current.Port; }
        }

        /// <summary>启动 MCP HTTP 服务（幂等）。</summary>
        public static void StartServer()
        {
            if (IsRunning) return;

            _settings = McpSettings.Current;
            _settings.Load(); // 启动前重新读取，保证配置最新
            _logger = McpEditorLogger.Instance;

            // 组装注册表：注入权限门控、日志器与主线程调度器（保证 Unity API 在主线程执行）
            _registry = McpToolRegistry.Instance;
            _registry.Gate = new EditorPermissionGate(_settings);
            _registry.Logger = _logger;
            _registry.MainThreadInvoker = f => McpMainThreadDispatcher.InvokeBlocking(f, 120000);
            _registry.ScanAllAssemblies(true);

            // 内置 MCP 资源
            RegisterBuiltinResources();

            // 组装协议核心与 HTTP 服务器
            _core = new JsonRpcCore(_registry, _logger);
            _server = new SimpleHttpServer { Logger = _logger };
            McpHttpEndpoints.Register(_server, _core, _logger, BuildStatus);

            try
            {
                _server.Start(_settings.Host, _settings.Port);
                _startedAt = DateTime.Now;
                _logger.Info("[启动] MCP 服务已启动: http://" + _settings.Host + ":" + _server.BoundPort + "/mcp" +
                             " （SSE: http://" + _settings.Host + ":" + _server.BoundPort + "/sse，" +
                             "工具 " + _registry.ToolCount + " 个，模式：" + _settings.AccessModeDisplayName + "）");
            }
            catch (Exception ex)
            {
                _logger.Error("[启动失败] " + ex.Message);
                _server = null;
            }
        }

        /// <summary>停止 MCP HTTP 服务。</summary>
        public static void StopServer()
        {
            if (_server == null) return;
            try { McpHttpEndpoints.CloseAllSessions(); } catch { /* 忽略 */ }
            try { _server.Stop(); } catch (Exception ex) { _logger?.Warn("[停止] " + ex.Message); }
            _server = null;
            _logger?.Info("[停止] MCP 服务已停止");
        }

        /// <summary>重启服务（配置修改后调用，保证监听地址/端口生效）。</summary>
        public static void RestartServer()
        {
            StopServer();
            StartServer();
        }

        /// <summary>域重载前自动停止（防止残留套接字与线程）。</summary>
        private static void Shutdown()
        {
            StopServer();
        }

        /// <summary>注册内置 MCP 资源（resources/list 可见）。</summary>
        private static void RegisterBuiltinResources()
        {
            _registry.Resources.Clear();
            _registry.Resources.Add(new McpResourceDefinition
            {
                Uri = "mcp://status",
                Name = "MCP 服务状态",
                Description = "当前 MCP 服务的运行状态、访问模式与工具清单",
                ReadHandler = () => McpMetaTools.GetStatus(),
            });
            _registry.Resources.Add(new McpResourceDefinition
            {
                Uri = "mcp://project",
                Name = "项目信息",
                Description = "当前 Unity 项目的基础信息（名称/版本/平台/统计）",
                ReadHandler = () => UnityProjectTools.GetProjectInfo(),
            });
            _registry.Resources.Add(new McpResourceDefinition
            {
                Uri = "mcp://console-log",
                Name = "控制台日志",
                Description = "Unity 控制台最近日志（用于排查问题）",
                ReadHandler = () => UnityProjectTools.GetConsoleLogs(200, "All", null, true, 100),
            });
        }

        /// <summary>
        /// 生成服务状态 JSON（供 /health、/、mcp.get_status 使用；会被 HTTP 工作线程调用，勿执行重操作）。
        /// </summary>
        public static JsonObject BuildStatus()
        {
            McpSettings settings = _settings ?? McpSettings.Current;
            string startedAt = _startedAt == default(DateTime) ? "未启动" : _startedAt.ToString("yyyy-MM-dd HH:mm:ss");
            var status = new JsonObject()
                .Set("running", IsRunning)
                .Set("host", settings.Host)
                .Set("port", BoundPort)
                .Set("accessMode", settings.AccessMode == McpAccessMode.ReadOnly ? "ReadOnly" : "ReadWrite")
                .Set("accessModeDisplayName", settings.AccessModeDisplayName)
                .Set("toolCount", _registry != null ? _registry.ToolCount : 0)
                .Set("resourceCount", _registry != null ? _registry.Resources.Count : 0)
                .Set("startedAt", startedAt)
                .Set("endpoint", "http://" + settings.Host + ":" + BoundPort + "/mcp")
                .Set("sseEndpoint", "http://" + settings.Host + ":" + BoundPort + "/sse");
            return status;
        }

        /// <summary>Editor 端权限门控实现：实时读取配置窗口中的访问模式。</summary>
        private sealed class EditorPermissionGate : IMcpPermissionGate
        {
            private readonly McpSettings _settings;

            /// <summary>构造门控。</summary>
            public EditorPermissionGate(McpSettings settings)
            {
                _settings = settings;
            }

            /// <summary>当前访问模式（修改配置立即生效）。</summary>
            public McpAccessMode Mode
            {
                get { return _settings.AccessMode; }
            }
        }

        // ------------------------------------------------------------------
        // 菜单项
        // ------------------------------------------------------------------

        /// <summary>菜单：启动服务器。</summary>
        [MenuItem("Tools/VRChat Project MCP/启动服务器", false, 10)]
        public static void MenuStart()
        {
            StartServer();
        }

        /// <summary>菜单校验：服务未运行时才显示启动项。</summary>
        [MenuItem("Tools/VRChat Project MCP/启动服务器", true, 10)]
        public static bool MenuStartValidate()
        {
            return !IsRunning;
        }

        /// <summary>菜单：停止服务器。</summary>
        [MenuItem("Tools/VRChat Project MCP/停止服务器", false, 11)]
        public static void MenuStop()
        {
            StopServer();
        }

        /// <summary>菜单校验：服务运行时才显示停止项。</summary>
        [MenuItem("Tools/VRChat Project MCP/停止服务器", true, 11)]
        public static bool MenuStopValidate()
        {
            return IsRunning;
        }
    }
}
