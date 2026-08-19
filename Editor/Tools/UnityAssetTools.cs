// =================================================================================================
// UnityAssetTools.cs
// Unity 常规工具：资产管理
// -------------------------------------------------------------------------------------------------
// 对应 MCP 工具（前缀 unity）：
//   unity.list_assets     查询 搜索列出资产
//   unity.get_asset_info  查询 资产详情
//   unity.read_text_asset 查询 读取文本资产内容
//   unity.create_asset    写入 创建资产（ScriptableObject/Material/动画控制器等）
//   unity.create_script   写入 创建 C# 脚本（MonoBehaviour 模板）
//   unity.copy_asset      写入 复制资产
//   unity.delete_asset    写入 删除资产
//   unity.create_folder   写入 创建文件夹
//   unity.refresh_assets  写入 刷新资产数据库
// =================================================================================================

using System;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Mcp;

namespace VrchatProjectMcp.Editor.Tools
{
    /// <summary>
    /// Unity 资产类工具（内部静态类）。
    /// </summary>
    internal static class UnityAssetTools
    {
        // ==================================================================
        // 查询类
        // ==================================================================

        /// <summary>搜索列出资产（type 为 FindAssets 类型过滤器，如 Prefab / Texture2D / Material）。</summary>
        [McpTool("unity.list_assets", McpToolAccess.Query, "unity", "搜索列出项目资产（type 为类型过滤器如 Prefab/Texture2D/Material，folder 限定 Assets 子目录）")]
        public static object ListAssets(
            [McpParam("名称关键字（可留空）")] string search = null,
            [McpParam("类型过滤器（如 Prefab / Texture2D / Material / AnimationClip，可留空）")] string type = null,
            [McpParam("限定文件夹（如 Assets/MyAvatar，可留空）")] string folder = null,
            [McpParam("最多返回条数（默认 100）")] int limit = 100)
        {
            string filter = "";
            if (!string.IsNullOrEmpty(type)) filter += "t:" + type + " ";
            if (!string.IsNullOrEmpty(search)) filter += search;

            string[] folders = folder != null ? new[] { folder } : null;
            string[] guids = AssetDatabase.FindAssets(filter.Trim(), folders);

            var assets = new JsonArray();
            for (int i = 0; i < guids.Length && assets.Count < limit; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                Type mainType = AssetDatabase.GetMainAssetType(path);
                assets.Add(new JsonObject()
                    .Set("name", Path.GetFileName(path))
                    .Set("path", path)
                    .Set("guid", guids[i])
                    .Set("type", mainType != null ? mainType.FullName : "?"));
            }
            return new JsonObject()
                .Set("total", (long)guids.Length)
                .Set("count", (long)assets.Count)
                .Set("truncated", assets.Count >= limit)
                .Set("assets", assets);
        }

        /// <summary>获取资产详情（类型/大小/依赖/导入器/预制件摘要）。</summary>
        [McpTool("unity.get_asset_info", McpToolAccess.Query, "unity", "获取资产详情（类型/文件大小/依赖数/导入器/预制件组件摘要）")]
        public static object GetAssetInfo([McpParam("资产路径（Assets/xx 或 Packages/xx）", Required = true)] string path)
        {
            var result = new JsonObject().Set("path", path);
            if (!File.Exists(path) && AssetDatabase.LoadMainAssetAtPath(path) == null)
                throw new McpToolException("资产不存在: " + path);

            result.Set("guid", AssetDatabase.AssetPathToGUID(path));
            UnityEngine.Object main = AssetDatabase.LoadMainAssetAtPath(path);
            result.Set("type", main != null ? main.GetType().FullName : "未知");
            result.Set("isSubAsset", main != null && AssetDatabase.IsSubAsset(main));

            try { result.Set("fileSizeBytes", new FileInfo(path).Length); } catch { /* 忽略 */ }
            try { result.Set("dependencyCount", (long)AssetDatabase.GetDependencies(path, false).Length); } catch { /* 忽略 */ }

            AssetImporter importer = AssetImporter.GetAtPath(path);
            if (importer != null) result.Set("importer", importer.GetType().Name);

            // 预制件资产：附加组件摘要
            if (main is GameObject && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                GameObject contents = null;
                try
                {
                    contents = PrefabUtility.LoadPrefabContents(path);
                    result.Set("prefabSummary", ToolHelpers.ComponentsSummary(contents, 10));
                }
                catch (Exception ex)
                {
                    result.Set("prefabSummaryError", ex.Message);
                }
                finally
                {
                    if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
                }
            }
            return result;
        }

        /// <summary>读取文本资产内容（仅允许项目内路径，限制读取长度）。</summary>
        [McpTool("unity.read_text_asset", McpToolAccess.Query, "unity", "读取项目内文本文件内容（json/txt/asmdef 等；出于安全仅允许 Assets/、Packages/、ProjectSettings/ 下的路径）")]
        public static object ReadTextAsset(
            [McpParam("项目内文件路径（Assets/、Packages/、ProjectSettings/ 下）", Required = true)] string path,
            [McpParam("最大读取字符数（默认 100000）")] int maxChars = 100000)
        {
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)
                && !path.StartsWith("Packages/", StringComparison.Ordinal)
                && !path.StartsWith("ProjectSettings/", StringComparison.Ordinal))
                throw new McpToolException("出于安全考虑，仅允许读取项目内路径（Assets/、Packages/、ProjectSettings/）: " + path);
            if (!File.Exists(path)) throw new McpToolException("文件不存在: " + path);

            int cap = Math.Min(Math.Max(maxChars, 1), 4 * 1024 * 1024);
            string text;
            bool truncated = false;
            var info = new FileInfo(path);
            if (info.Length > cap)
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buffer = new byte[cap];
                    int read = fs.Read(buffer, 0, cap);
                    text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
                }
                truncated = true;
            }
            else
            {
                text = File.ReadAllText(path);
            }
            return new JsonObject()
                .Set("path", path)
                .Set("truncated", truncated)
                .Set("text", text);
        }

        // ==================================================================
        // 写入类
        // ==================================================================

        /// <summary>
        /// 创建资产：支持 AnimatorController / Material / PhysicMaterial / AnimationClip
        /// 与任意 ScriptableObject 类型（用类型名）。
        /// </summary>
        [McpTool("unity.create_asset", McpToolAccess.Write, "unity", "创建资产（AnimatorController/Material/PhysicMaterial/AnimationClip 或任意 ScriptableObject 类型名；文件夹不存在会自动创建）")]
        public static object CreateAsset(
            [McpParam("资产类型名（如 AnimatorController / Material / VRCExpressionsMenu）", Required = true)] string typeName,
            [McpParam("目标文件夹（Assets/ 下，不存在会自动创建）", Required = true)] string folder,
            [McpParam("资产名称（可留空，默认用类型名）")] string name = null)
        {
            folder = ToolHelpers.EnsureFolder(folder);
            if (string.IsNullOrEmpty(name)) name = typeName;
            string path;

            if (typeName.Equals("AnimatorController", StringComparison.OrdinalIgnoreCase))
            {
                path = folder + "/" + name + ".controller";
                AnimatorController.CreateAnimatorControllerAtPath(path);
            }
            else if (typeName.Equals("Material", StringComparison.OrdinalIgnoreCase))
            {
                path = folder + "/" + name + ".mat";
                Shader shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("HDRP/Lit");
                if (shader == null) throw new McpToolException("未找到可用 Shader，无法创建 Material");
                var material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            else if (typeName.Equals("PhysicMaterial", StringComparison.OrdinalIgnoreCase))
            {
                path = folder + "/" + name + ".physicMaterial";
                var material = new PhysicMaterial { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            else if (typeName.Equals("AnimationClip", StringComparison.OrdinalIgnoreCase))
            {
                path = folder + "/" + name + ".anim";
                var clip = new AnimationClip { name = name };
                AssetDatabase.CreateAsset(clip, path);
            }
            else
            {
                Type type = ToolHelpers.FindType(typeName);
                if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type))
                    throw new McpToolException("类型未找到或不是 ScriptableObject 子类: " + typeName +
                                               "（支持的常见类型：AnimatorController、Material、PhysicMaterial、AnimationClip 及任意 ScriptableObject 类名）");
                path = folder + "/" + name + ".asset";
                ScriptableObject instance = ScriptableObject.CreateInstance(type);
                instance.name = name;
                AssetDatabase.CreateAsset(instance, path);
            }

            AssetDatabase.SaveAssets();
            return new JsonObject()
                .Set("path", path)
                .Set("guid", AssetDatabase.AssetPathToGUID(path))
                .Set("type", typeName);
        }

        /// <summary>复制资产（目标路径重名时自动生成唯一名称）。</summary>
        [McpTool("unity.copy_asset", McpToolAccess.Write, "unity", "复制资产到新路径（重名时自动追加序号；文件夹不存在会自动创建）")]
        public static object CopyAsset(
            [McpParam("源资产路径", Required = true)] string sourcePath,
            [McpParam("目标资产路径（Assets/ 下）", Required = true)] string targetPath)
        {
            if (!File.Exists(sourcePath)) throw new McpToolException("源资产不存在: " + sourcePath);
            if (!targetPath.StartsWith("Assets/", StringComparison.Ordinal)) throw new McpToolException("目标路径必须位于 Assets/ 下");
            int slash = targetPath.LastIndexOf('/');
            if (slash > 0) ToolHelpers.EnsureFolder(targetPath.Substring(0, slash));
            string unique = AssetDatabase.GenerateUniqueAssetPath(targetPath);
            if (!AssetDatabase.CopyAsset(sourcePath, unique))
                throw new McpToolException("复制资产失败: " + sourcePath + " → " + unique);
            AssetDatabase.SaveAssets();
            return new JsonObject()
                .Set("source", sourcePath)
                .Set("target", unique)
                .Set("guid", AssetDatabase.AssetPathToGUID(unique));
        }

        /// <summary>删除资产（默认移入回收站）。</summary>
        [McpTool("unity.delete_asset", McpToolAccess.Write, "unity", "删除资产（moveToTrash=true 移入系统回收站，false 直接删除）")]
        public static object DeleteAsset(
            [McpParam("资产路径", Required = true)] string path,
            [McpParam("是否移入回收站（默认 true）")] bool moveToTrash = true)
        {
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))) throw new McpToolException("资产不存在: " + path);
            if (moveToTrash)
            {
                if (!AssetDatabase.MoveAssetToTrash(path))
                    throw new McpToolException("移入回收站失败: " + path);
            }
            else
            {
                if (!AssetDatabase.DeleteAsset(path))
                    throw new McpToolException("删除资产失败: " + path);
            }
            AssetDatabase.SaveAssets();
            return new JsonObject().Set("deleted", path).Set("movedToTrash", moveToTrash);
        }

        /// <summary>创建文件夹（逐级创建）。</summary>
        [McpTool("unity.create_folder", McpToolAccess.Write, "unity", "在 Assets/ 下创建文件夹（逐级自动创建）")]
        public static object CreateFolder([McpParam("文件夹路径（Assets/xx/yy）", Required = true)] string path)
        {
            string created = ToolHelpers.EnsureFolder(path);
            return new JsonObject().Set("path", created);
        }

        /// <summary>刷新资产数据库（外部改动后重新导入）。</summary>
        [McpTool("unity.refresh_assets", McpToolAccess.Write, "unity", "保存并刷新资产数据库（外部修改资产文件后调用）")]
        public static object RefreshAssets()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return new JsonObject().Set("refreshed", true);
        }

        /// <summary>创建 C# 脚本（默认 MonoBehaviour 模板，可选命名空间）。</summary>
        [McpTool("unity.create_script", McpToolAccess.Write, "unity", "创建 C# 脚本文件（默认 MonoBehaviour 模板；path 为完整 .cs 路径，文件夹不存在会自动创建）")]
        public static object CreateScript(
            [McpParam("脚本完整路径（Assets/xx/MyScript.cs）", Required = true)] string path,
            [McpParam("命名空间（可留空）")] string namespaceName = null)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new McpToolException("path 必须是 Assets/ 下的 .cs 脚本路径");
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) path += ".cs";
            if (File.Exists(path)) throw new McpToolException("脚本已存在: " + path);

            int slash = path.LastIndexOf('/');
            string folder = slash > 0 ? ToolHelpers.EnsureFolder(path.Substring(0, slash)) : "Assets";
            string fileName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny(new[] { ' ', '-', '.', '(', ')' }) >= 0)
                throw new McpToolException("脚本文件名无效（不能包含空格/连字符/括号）: " + fileName);

            var sb = new System.Text.StringBuilder(512);
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(namespaceName))
            {
                sb.AppendLine("namespace " + namespaceName);
                sb.AppendLine("{");
            }
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// " + fileName + "：由 VRChat Project MCP（unity.create_script）生成。");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public class " + fileName + " : MonoBehaviour");
            sb.AppendLine("    {");
            sb.AppendLine("    }");
            if (!string.IsNullOrEmpty(namespaceName)) sb.AppendLine("}");

            File.WriteAllText(folder + "/" + fileName + ".cs", sb.ToString(), System.Text.Encoding.UTF8);
            AssetDatabase.Refresh();
            return new JsonObject()
                .Set("path", folder + "/" + fileName + ".cs")
                .Set("guid", AssetDatabase.AssetPathToGUID(folder + "/" + fileName + ".cs"))
                .Set("className", fileName);
        }
    }
}
