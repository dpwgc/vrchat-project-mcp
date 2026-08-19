// =================================================================================================
// McpToolRegistry.cs
// 工具注册表：扫描注册 / 权限门控 / 调用执行
// -------------------------------------------------------------------------------------------------
// 职责：
//   1. 扫描全部程序集，收集所有标注 [McpTool] 的静态方法（扩展方式二）；
//      同时实例化所有 IMcpToolProvider 实现（扩展方式一）并注册其工具；
//   2. 维护唯一名称映射，支持运行时注册 / 重扫（mcp.refresh_tools）；
//   3. 执行工具调用：只读模式下拦截所有 write 类工具；统一包装异常为 isError 结果；
//   4. MainThreadInvoker 扩展点：宿主（Editor）注入主线程调度器，保证 Unity API 在主线程执行。
//
// 【预留扩展位置说明】
//   需要扩展能力时，优先使用以下三种方式（无需修改本文件）：
//   A. 在任意引用 VrchatProjectMcp.Core 的程序集中定义标注 [McpTool] 的静态方法；
//   B. 实现 IMcpToolProvider 接口（适合需要动态决定工具集合的场景）；
//   C. 调用 RegisterTool() 在运行时注册 McpToolDefinition（适合程序化生成工具）。
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Logging;

namespace VrchatProjectMcp.Core.Mcp
{
    /// <summary>
    /// 工具注册表（进程内单例，宿主通过 Instance 访问）。
    /// </summary>
    public sealed class McpToolRegistry
    {
        /// <summary>全局单例。</summary>
        public static readonly McpToolRegistry Instance = new McpToolRegistry();

        private readonly object _lock = new object();
        private readonly List<McpToolDefinition> _tools = new List<McpToolDefinition>();
        private readonly Dictionary<string, McpToolDefinition> _byName = new Dictionary<string, McpToolDefinition>(StringComparer.OrdinalIgnoreCase);
        private bool _scanned;

        /// <summary>权限门控（宿主注入；为 null 时不限制写入）。</summary>
        public IMcpPermissionGate Gate { get; set; }

        /// <summary>日志器（宿主注入；为 null 时静默）。</summary>
        public IMcpLogger Logger { get; set; }

        /// <summary>
        /// 主线程调度注入点（扩展点）：
        /// Editor 端注入「把委托投递到 Unity 主线程并阻塞等待结果」的调度器，
        /// 保证所有工具执行都发生在主线程。
        /// </summary>
        public Func<Func<object>, object> MainThreadInvoker { get; set; }

        /// <summary>MCP 资源列表（宿主注册）。</summary>
        public List<McpResourceDefinition> Resources { get; } = new List<McpResourceDefinition>();

        /// <summary>
        /// 工具清单变化事件（重扫/注册后触发）。
        /// 注意：本事件可能在 HTTP 工作线程触发（如 /mcp 请求导致首次扫描），订阅方如需操作 Unity UI 请自行调度到主线程。
        /// </summary>
        public event Action<IReadOnlyList<McpToolDefinition>> ToolsChanged;

        /// <summary>当前已注册工具数量。</summary>
        public int ToolCount
        {
            get { lock (_lock) { return _tools.Count; } }
        }

        /// <summary>返回按名称排序的工具清单快照。</summary>
        public IReadOnlyList<McpToolDefinition> ListTools()
        {
            lock (_lock)
            {
                var copy = new List<McpToolDefinition>(_tools);
                copy.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return copy;
            }
        }

        /// <summary>按名称（大小写不敏感）查找工具。</summary>
        public McpToolDefinition FindTool(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                _byName.TryGetValue(name, out McpToolDefinition tool);
                return tool;
            }
        }

        /// <summary>运行时注册一个工具（重复名称会输出警告并跳过）。</summary>
        public void RegisterTool(McpToolDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.Name)) return;
            lock (_lock)
            {
                if (_byName.ContainsKey(definition.Name))
                {
                    Logger?.Warn("[注册] 工具名重复，已跳过: " + definition.Name);
                    return;
                }
                _tools.Add(definition);
                _byName[definition.Name] = definition;
            }
            ToolsChanged?.Invoke(ListTools());
        }

        /// <summary>注册一个工具扩展提供者（扩展方式一）。</summary>
        public void RegisterProvider(IMcpToolProvider provider)
        {
            if (provider == null) return;
            foreach (McpToolDefinition tool in provider.RegisterTools()) RegisterTool(tool);
        }

        /// <summary>
        /// 扫描全部已加载程序集并注册所有 MCP 工具。
        /// 只扫描「引用了本程序集」的程序集（即知道 McpToolAttribute 存在的代码），避免遍历 Unity 全部类型。
        /// </summary>
        public void ScanAllAssemblies(bool force = false)
        {
            lock (_lock)
            {
                if (_scanned && !force) return;
                _scanned = true;
                _tools.Clear();
                _byName.Clear();
            }

            string coreAssemblyName = typeof(McpToolAttribute).Assembly.GetName().Name;
            int found = 0;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // 过滤：只处理引用本程序集的程序集（含自身）
                bool referencesCore = false;
                try
                {
                    referencesCore = string.Equals(assembly.GetName().Name, coreAssemblyName, StringComparison.Ordinal)
                        || assembly.GetReferencedAssemblies().Any(r => string.Equals(r.Name, coreAssemblyName, StringComparison.Ordinal));
                }
                catch
                {
                    continue;
                }
                if (!referencesCore) continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // 部分类型加载失败时仍处理已加载的类型
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    try
                    {
                        // 方式二：扫描标注 [McpTool] 的公开静态方法
                        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            McpToolAttribute attr = method.GetCustomAttribute<McpToolAttribute>();
                            if (attr == null) continue;
                            try
                            {
                                McpToolDefinition definition = McpToolDefinition.FromMethod(method);
                                if (definition != null)
                                {
                                    RegisterTool(definition);
                                    found++;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger?.Warn("[扫描] 跳过工具方法 " + type.FullName + "." + method.Name + ": " + ex.Message);
                            }
                        }

                        // 方式一：实例化 IMcpToolProvider 实现
                        if (!type.IsAbstract && !type.IsInterface && typeof(IMcpToolProvider).IsAssignableFrom(type)
                            && type.GetConstructor(Type.EmptyTypes) != null)
                        {
                            try
                            {
                                RegisterProvider((IMcpToolProvider)Activator.CreateInstance(type));
                            }
                            catch (Exception ex)
                            {
                                Logger?.Warn("[扫描] 实例化工具提供者失败 " + type.FullName + ": " + ex.Message);
                            }
                        }
                    }
                    catch
                    {
                        // 单个类型扫描失败不影响整体
                    }
                }
            }

            Logger?.Info("[扫描] 工具注册完成，共 " + ToolCount + " 个工具");
            ToolsChanged?.Invoke(ListTools());
        }

        /// <summary>生成 tools/list 使用的完整工具数组 JSON。</summary>
        public JsonArray BuildToolListJson()
        {
            var array = new JsonArray();
            foreach (McpToolDefinition tool in ListTools()) array.Add(tool.ToJson());
            return array;
        }

        /// <summary>
        /// 执行一个工具调用并返回 CallToolResult JSON（永不抛出工具异常，统一转为 isError 结果）。
        /// 只读模式下拒绝所有 write 类工具。
        /// </summary>
        public JsonObject CallTool(string name, object arguments)
        {
            // 1) 工具存在性检查
            McpToolDefinition tool = FindTool(name);
            if (tool == null)
            {
                Logger?.Warn("[调用] 工具不存在: " + name);
                return BuildErrorResult("工具不存在：「" + name + "」。请使用 tools/list 查看全部可用工具。", "tool_not_found");
            }

            // 2) 只读模式权限门控
            if (tool.Access == McpToolAccess.Write && Gate != null && Gate.Mode == McpAccessMode.ReadOnly)
            {
                string message = "当前为【只读】模式，已拒绝写入类工具调用「" + name + "」。" +
                                 "如需执行写入操作，请在「Tools → VRChat Project MCP → 配置面板」中把访问模式切换为读写，或由用户确认后重试。";
                Logger?.Warn("[拒绝] 只读模式拦截写入工具: " + name);
                var meta = new JsonObject().Set("access", "write").Set("mode", "readonly");
                return BuildErrorResult(message, "permission_denied", meta);
            }

            // 3) 执行（可选主线程调度）
            Logger?.Info("[调用] " + name + "（" + tool.AccessText + "类工具）");
            try
            {
                object rawResult;
                if (MainThreadInvoker != null)
                {
                    rawResult = MainThreadInvoker(() => tool.Handler(arguments));
                }
                else
                {
                    rawResult = tool.Handler(arguments);
                }
                return BuildSuccessResult(rawResult);
            }
            catch (McpToolException ex)
            {
                Logger?.Warn("[失败] " + name + ": " + ex.Message);
                return BuildErrorResult(ex.Message, ex.PermissionDenied ? "permission_denied" : "tool_error");
            }
            catch (Exception ex)
            {
                // 反射调用会把方法内部异常包装为 TargetInvocationException，这里解包提示
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                Logger?.Error("[异常] " + name + ": " + inner.Message);
                string stack = inner.StackTrace ?? string.Empty;
                if (stack.Length > 600) stack = stack.Substring(0, 600) + "…";
                return BuildErrorResult("工具执行异常: " + inner.Message + "\n" + stack, "internal_error");
            }
        }

        /// <summary>构建成功的 CallToolResult（文本 + 结构化结果）。</summary>
        private static JsonObject BuildSuccessResult(object rawResult)
        {
            string text;
            if (rawResult is string s) text = s;
            else if (rawResult == null) text = "null";
            else text = MiniJson.Serialize(rawResult, true);

            var content = new JsonArray().Push(new JsonObject().Set("type", "text").Set("text", text));
            var result = new JsonObject()
                .Set("content", content)
                .Set("isError", false);
            if (rawResult is JsonObject || rawResult is JsonArray) result.Set("structuredContent", rawResult);
            return result;
        }

        /// <summary>构建失败的 CallToolResult。</summary>
        private static JsonObject BuildErrorResult(string message, string code, JsonObject extraMeta = null)
        {
            var content = new JsonArray().Push(new JsonObject().Set("type", "text").Set("text", message));
            var result = new JsonObject()
                .Set("content", content)
                .Set("isError", true);
            JsonObject error = new JsonObject().Set("code", code).Set("message", message);
            if (extraMeta != null)
            {
                // 合并额外信息（如 permission_denied 的 mode/access 字段）
                foreach (KeyValuePair<string, object> kv in extraMeta) error.Set(kv.Key, kv.Value);
            }
            result.Set("structuredContent", new JsonObject().Set("error", error));
            return result;
        }
    }
}
