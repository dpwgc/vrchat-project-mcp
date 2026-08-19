// =================================================================================================
// UnityProjectTools.cs
// Unity 常规工具：项目信息 / 包信息 / 资源使用 / 控制台日志
// -------------------------------------------------------------------------------------------------
// 对应 MCP 工具（前缀 unity）：
//   unity.get_project_info   查询 项目基础信息
//   unity.get_packages       查询 已安装 UPM 包（含 VRChat 相关包探测）
//   unity.get_resource_usage 查询 编辑器与场景资源使用统计
//   unity.get_console_logs   查询 控制台日志（内存环形缓冲 + Editor.log 文件尾部）
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Mcp;
using VrchatProjectMcp.Editor.Logging;

namespace VrchatProjectMcp.Editor.Tools
{
    /// <summary>
    /// Unity 项目类工具（内部静态类）。
    /// </summary>
    internal static class UnityProjectTools
    {
        // ==================================================================
        // unity.get_project_info
        // ==================================================================

        /// <summary>获取当前 Unity 项目的基础信息（名称/路径/版本/平台/场景与资产统计）。</summary>
        [McpTool("unity.get_project_info", McpToolAccess.Query, "unity", "获取当前 Unity 项目的基础信息（产品名/公司/Unity 版本/平台/构建场景/资产统计）")]
        public static object GetProjectInfo()
        {
            var result = new JsonObject()
                .Set("productName", Application.productName)
                .Set("companyName", Application.companyName)
                .Set("unityVersion", Application.unityVersion)
                .Set("projectPath", Application.dataPath)
                .Set("activeBuildTarget", EditorUserBuildSettings.activeBuildTarget.ToString())
                .Set("colorSpace", PlayerSettings.colorSpace.ToString());

            try { result.Set("bundleVersion", PlayerSettings.bundleVersion); } catch { /* 忽略 */ }

            var buildScenes = new JsonArray();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                buildScenes.Add(new JsonObject().Set("path", scene.path).Set("enabled", scene.enabled));
            }
            result.Set("buildScenes", buildScenes);
            result.Set("currentScene", SceneManager.GetActiveScene().name);

            // 资产统计（FindAssets 只返回 GUID，不加载资产，开销可控）
            try
            {
                result.Set("assetCount", (long)AssetDatabase.FindAssets("").Length);
                result.Set("sceneAssetCount", (long)AssetDatabase.FindAssets("t:Scene").Length);
                result.Set("prefabCount", (long)AssetDatabase.FindAssets("t:Prefab").Length);
                result.Set("scriptCount", (long)AssetDatabase.FindAssets("t:MonoScript").Length);
            }
            catch { /* 统计失败不致命 */ }

            var rootFolders = new JsonArray();
            try
            {
                foreach (string dir in Directory.GetDirectories(Application.dataPath))
                    rootFolders.Add(Path.GetFileName(dir));
            }
            catch { /* 忽略 */ }
            result.Set("assetRootFolders", rootFolders);
            return result;
        }

        // ==================================================================
        // unity.get_packages
        // ==================================================================

        /// <summary>获取已安装的 UPM 包清单（全部包 + VRChat 相关包探测）。</summary>
        [McpTool("unity.get_packages", McpToolAccess.Query, "unity", "获取项目已安装的 UPM 包清单（全部包，并单独列出 VRChat 相关包：SDK/MA/VRCFury/Poiyomi 等）")]
        public static object GetPackages()
        {
            var result = new JsonObject();
            var packages = new JsonArray();
            try
            {
                UnityEditor.PackageManager.PackageInfo[] list =
                    UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                foreach (UnityEditor.PackageManager.PackageInfo p in list.OrderBy(p => p.name))
                {
                    packages.Add(new JsonObject()
                        .Set("name", p.name)
                        .Set("version", p.version)
                        .Set("displayName", p.displayName)
                        .Set("source", p.source.ToString())
                        .Set("category", p.category ?? ""));
                }
                result.Set("packages", packages);
            }
            catch (Exception ex)
            {
                result.Set("packageInfoError", ex.Message);
            }

            // 从 packages 中筛出 VRChat 相关包（按名称关键词匹配）
            var vrcRelated = new JsonArray();
            string[] patterns = { "vrchat", "vrc", "modular-avatar", "nadena", "vrcfury", "fury", "avatar-optimizer", "anatawa", "poiyomi", "liltoon", "dynamic-bone", "wholesome", "gesture" };
            foreach (var entry in packages)
            {
                string name = ((JsonObject)entry).GetString("name") ?? "";
                string display = ((JsonObject)entry).GetString("displayName") ?? "";
                foreach (string pattern in patterns)
                {
                    if (name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0
                        || display.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        vrcRelated.Add(entry);
                        break;
                    }
                }
            }
            result.Set("vrcRelatedPackages", vrcRelated);
            result.Set("vrchatSdkInstalled", VrcReflection.IsSdkInstalled);
            return result;
        }

        // ==================================================================
        // unity.get_resource_usage
        // ==================================================================

        /// <summary>获取编辑器资源使用统计（进程内存/场景对象/资产数量）。</summary>
        [McpTool("unity.get_resource_usage", McpToolAccess.Query, "unity", "获取编辑器资源使用情况（进程内存/托管内存/场景对象与组件统计/各类型资产数量/当前选中）")]
        public static object GetResourceUsage()
        {
            var result = new JsonObject();

            // 进程内存（编辑器环境可用）
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
                {
                    result.Set("processWorkingSetMB", Math.Round(process.WorkingSet64 / 1048576.0, 1));
                    result.Set("processPrivateMB", Math.Round(process.PrivateMemorySize64 / 1048576.0, 1));
                    result.Set("processVirtualMB", Math.Round(process.VirtualMemorySize64 / 1048576.0, 1));
                }
            }
            catch { /* 忽略 */ }

            result.Set("managedMemoryMB", Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1));
            try { result.Set("unityTotalAllocatedMB", Math.Round(Profiler.GetTotalAllocatedMemoryLong() / 1048576.0, 1)); } catch { /* 忽略 */ }

            // 场景统计
            Scene scene = SceneManager.GetActiveScene();
            result.Set("sceneName", scene.name);
            GameObject[] allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Component[] allComponents = UnityEngine.Object.FindObjectsByType<Component>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            result.Set("sceneObjectCount", (long)allObjects.Length);
            result.Set("sceneComponentCount", (long)allComponents.Length);

            var componentCounts = new Dictionary<string, long>();
            foreach (Component component in allComponents)
            {
                if (component == null) continue;
                string typeName = component.GetType().FullName;
                componentCounts.TryGetValue(typeName, out long count);
                componentCounts[typeName] = count + 1;
            }
            var topComponents = new JsonArray();
            var sortedComponents = new List<KeyValuePair<string, long>>(componentCounts);
            sortedComponents.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < sortedComponents.Count && i < 15; i++)
            {
                topComponents.Add(new JsonObject().Set("type", sortedComponents[i].Key).Set("count", sortedComponents[i].Value));
            }
            result.Set("sceneTopComponents", topComponents);

            // 资产数量（按类型）
            try
            {
                var assetCounts = new JsonObject()
                    .Set("textures", (long)AssetDatabase.FindAssets("t:Texture2D").Length)
                    .Set("materials", (long)AssetDatabase.FindAssets("t:Material").Length)
                    .Set("meshes", (long)AssetDatabase.FindAssets("t:Mesh").Length)
                    .Set("animationClips", (long)AssetDatabase.FindAssets("t:AnimationClip").Length)
                    .Set("audioClips", (long)AssetDatabase.FindAssets("t:AudioClip").Length)
                    .Set("models", (long)AssetDatabase.FindAssets("t:Model").Length)
                    .Set("prefabs", (long)AssetDatabase.FindAssets("t:Prefab").Length)
                    .Set("scenes", (long)AssetDatabase.FindAssets("t:Scene").Length);
                result.Set("assetCounts", assetCounts);
            }
            catch { /* 忽略 */ }

            // 当前选中
            var selection = new JsonArray();
            foreach (UnityEngine.Object obj in Selection.objects)
            {
                selection.Add(new JsonObject().Set("name", obj.name).Set("type", obj.GetType().FullName).Set("path", ToolHelpers.GetObjectPath(obj)));
            }
            result.Set("selection", selection);
            return result;
        }

        // ==================================================================
        // unity.get_console_logs
        // ==================================================================

        /// <summary>
        /// 获取控制台日志（内存环形缓冲 + Editor.log 文件尾部），供 Agent 排查问题。
        /// </summary>
        [McpTool("unity.get_console_logs", McpToolAccess.Query, "unity", "阅读 Unity 控制台日志排查问题（内存缓冲最近日志 + Editor.log 文件尾部）")]
        public static object GetConsoleLogs(
            [McpParam("最多返回条数（默认 100）")] int maxLines = 100,
            [McpParam("级别过滤：All/Log/Warning/Error/Exception/Assert（默认 All）")] string level = "All",
            [McpParam("关键字过滤（对消息与堆栈做包含匹配，可留空）")] string search = null,
            [McpParam("是否附带 Editor.log 文件尾部（默认 true）")] bool includeFileTail = true,
            [McpParam("文件尾部行数（默认 100）")] int fileTailLines = 100)
        {
            var result = new JsonObject();

            List<ConsoleLogEntry> entries = McpConsoleCapture.Snapshot(Math.Max(1, maxLines), level, search);
            var captured = new JsonArray();
            foreach (ConsoleLogEntry entry in entries)
            {
                var item = new JsonObject()
                    .Set("time", entry.Time.ToString("yyyy-MM-dd HH:mm:ss"))
                    .Set("level", entry.Level)
                    .Set("message", entry.Message);
                if (!string.IsNullOrEmpty(entry.Stack))
                    item.Set("stack", entry.Stack.Length > 800 ? entry.Stack.Substring(0, 800) + "…" : entry.Stack);
                captured.Add(item);
            }
            result.Set("capturedCount", (long)captured.Count);
            result.Set("captured", captured);

            if (includeFileTail)
            {
                string logPath = ToolHelpers.ResolveEditorLogPath();
                result.Set("logFile", ToolHelpers.ReadFileTail(logPath, Math.Max(1, fileTailLines), 262144));
            }
            return result;
        }
    }
}
