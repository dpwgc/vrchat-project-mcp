// =================================================================================================
// VrcParameterTools.cs
// VRChat 专用工具：Expression Parameters（表情参数）的查询 / 新建 / 复制 / 编辑
// -------------------------------------------------------------------------------------------------
// 对应 MCP 工具（前缀 vrc）：
//   vrc.list_expression_parameters   查询 列出项目中的表情参数资产
//   vrc.get_expression_parameters    查询 读取参数列表（名称/类型/默认值/是否保存）
//   vrc.create_expression_parameters 写入 新建表情参数资产
//   vrc.copy_expression_parameters   写入 复制表情参数资产
//   vrc.set_parameter                写入 新增/修改/删除参数
//
// 实现说明：
//   与菜单工具相同，全部通过 SerializedObject + 反射操作，对 VRCSDK3 无编译期依赖。
// =================================================================================================

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Mcp;

namespace VrchatProjectMcp.Editor.Tools
{
    /// <summary>
    /// VRChat 表情参数工具（内部静态类）。
    /// </summary>
    internal static class VrcParameterTools
    {
        // ==================================================================
        // 查询类
        // ==================================================================

        /// <summary>列出项目中的表情参数资产。</summary>
        [McpTool("vrc.list_expression_parameters", McpToolAccess.Query, "vrc", "列出项目中的 VRCExpressionParameters（表情参数）资产")]
        public static object ListExpressionParameters(
            [McpParam("名称关键字（可留空）")] string search = null,
            [McpParam("最多返回条数（默认 100）")] int limit = 100)
        {
            if (VrcReflection.ParametersType == null)
                throw new McpToolException("项目未安装 VRChat SDK3（找不到 VRCExpressionParameters 类型）");
            List<UnityEngine.Object> assets = VrcReflection.FindAssetsByTypeName("VRCExpressionParameters", search, limit);
            var items = new JsonArray();
            foreach (UnityEngine.Object asset in assets)
            {
                items.Add(new JsonObject()
                    .Set("name", asset.name)
                    .Set("path", AssetDatabase.GetAssetPath(asset))
                    .Set("guid", AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset))));
            }
            return new JsonObject().Set("count", (long)items.Count).Set("parametersFiles", items);
        }

        /// <summary>读取表情参数资产内容（参数名/类型/默认值/是否保存）。</summary>
        [McpTool("vrc.get_expression_parameters", McpToolAccess.Query, "vrc", "读取表情参数资产内容（参数名/类型 Int·Float·Bool/默认值/是否保存）")]
        public static object GetExpressionParameters(
            [McpParam("参数资产路径（与 avatarTarget 二选一）")] string parametersPath = null,
            [McpParam("头像目标（实例ID/场景路径/预制件路径；读取其绑定的参数资产）")] string avatarTarget = null)
        {
            UnityEngine.Object asset = null;
            if (!string.IsNullOrEmpty(parametersPath))
            {
                asset = VrcMenuTools.LoadParametersAsset(parametersPath);
            }
            else if (!string.IsNullOrEmpty(avatarTarget))
            {
                using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(avatarTarget))
                {
                    Component descriptor = ToolHelpers.FindAvatarDescriptor(context.Root);
                    asset = VrcReflection.ReadDescriptorAsset(descriptor, "expressionParameters");
                }
                if (asset == null) throw new McpToolException("该头像未绑定 Expression Parameters（expressionParameters 为空）");
            }
            else
            {
                throw new McpToolException("请提供 parametersPath 或 avatarTarget 之一");
            }
            return DumpParametersAsset(asset);
        }

        // ==================================================================
        // 写入类
        // ==================================================================

        /// <summary>新建表情参数资产。</summary>
        [McpTool("vrc.create_expression_parameters", McpToolAccess.Write, "vrc", "新建 VRCExpressionParameters（表情参数）资产（path 为文件夹或完整 .asset 路径）")]
        public static object CreateExpressionParameters(
            [McpParam("资产路径（Assets/ 下文件夹，或完整 xx.asset 路径）", Required = true)] string path,
            [McpParam("参数文件名称（path 为文件夹时必填）")] string name = null)
        {
            string fullPath = VrcMenuTools.BuildAssetPath(path, name, "Parameters");
            ScriptableObject asset = VrcMenuTools.CreateSdkAsset("VRCExpressionParameters", VrcReflection.ParametersType);
            if (asset == null) throw new McpToolException("创建失败：项目未安装 VRChat SDK3（找不到 VRCExpressionParameters 类型）");
            asset.name = System.IO.Path.GetFileNameWithoutExtension(fullPath);
            AssetDatabase.CreateAsset(asset, fullPath);
            AssetDatabase.SaveAssets();
            return new JsonObject()
                .Set("path", fullPath)
                .Set("guid", AssetDatabase.AssetPathToGUID(fullPath))
                .Set("name", asset.name);
        }

        /// <summary>复制表情参数资产。</summary>
        [McpTool("vrc.copy_expression_parameters", McpToolAccess.Write, "vrc", "复制表情参数资产到新路径（重名自动追加序号）")]
        public static object CopyExpressionParameters(
            [McpParam("源参数文件路径", Required = true)] string sourcePath,
            [McpParam("目标路径（Assets/ 下）", Required = true)] string targetPath)
        {
            VrcMenuTools.LoadParametersAsset(sourcePath); // 校验源存在且类型正确
            int slash = targetPath.LastIndexOf('/');
            if (slash > 0) ToolHelpers.EnsureFolder(targetPath.Substring(0, slash));
            string unique = AssetDatabase.GenerateUniqueAssetPath(targetPath);
            if (!AssetDatabase.CopyAsset(sourcePath, unique)) throw new McpToolException("复制参数文件失败: " + sourcePath + " → " + unique);
            AssetDatabase.SaveAssets();
            return new JsonObject().Set("source", sourcePath).Set("target", unique);
        }

        /// <summary>新增/修改/删除表情参数（parameter 对象支持部分字段更新）。</summary>
        [McpTool("vrc.set_parameter", McpToolAccess.Write, "vrc", "新增/修改/删除表情参数。action: add/update/remove；parameter 对象字段：name、valueType(Int/Float/Bool)、defaultValue、saved")]
        public static object SetParameter(
            [McpParam("参数资产路径", Required = true)] string parametersPath,
            [McpParam("操作：add 新增 / update 修改 / remove 删除", Required = true)] string action,
            [McpParam("参数名（update/remove 时可用，与 index 二选一）")] string name = null,
            [McpParam("参数序号（update/remove 时可用；-1 表示按 name 查找）")] int index = -1,
            [McpParam("参数定义 JSON 对象（add 时必须提供 name；update 时只更新提供的字段）")] object parameter = null)
        {
            UnityEngine.Object asset = VrcMenuTools.LoadParametersAsset(parametersPath);
            var so = new SerializedObject(asset);
            SerializedProperty parameters = ToolHelpers.FindTopLevelProperty(so, "parameters");
            string act = (action ?? "").Trim().ToLowerInvariant();
            JsonObject paramObj = parameter as JsonObject;

            switch (act)
            {
                case "add":
                    {
                        if (paramObj == null || string.IsNullOrEmpty(paramObj.GetString("name")))
                            throw new McpToolException("新增参数必须提供 parameter.name");
                        parameters.InsertArrayElementAtIndex(parameters.arraySize);
                        SerializedProperty element = parameters.GetArrayElementAtIndex(parameters.arraySize - 1);
                        ApplyParameterFields(element, paramObj, false);
                        break;
                    }
                case "update":
                    {
                        int idx = ResolveParameterIndex(parameters, index, name);
                        ApplyParameterFields(parameters.GetArrayElementAtIndex(idx), paramObj, true);
                        break;
                    }
                case "remove":
                    {
                        int idx = ResolveParameterIndex(parameters, index, name);
                        parameters.DeleteArrayElementAtIndex(idx);
                        break;
                    }
                default:
                    throw new McpToolException("action 参数无效：" + action + "（可选 add / update / remove）");
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return DumpParametersAsset(asset);
        }

        // ==================================================================
        // 内部辅助
        // ==================================================================

        /// <summary>把参数资产完整转储为 JSON。</summary>
        internal static JsonObject DumpParametersAsset(UnityEngine.Object asset)
        {
            if (asset == null) return new JsonObject().Set("error", "参数文件为空");
            string path = AssetDatabase.GetAssetPath(asset);
            var result = new JsonObject()
                .Set("path", path)
                .Set("name", asset.name);

            var so = new SerializedObject(asset);
            SerializedProperty parameters = ToolHelpers.TryFindTopLevelProperty(so, "parameters");
            if (parameters == null)
            {
                result.Set("error", "未找到 parameters 属性（SDK 版本差异？）");
                return result;
            }

            var list = new JsonArray();
            for (int i = 0; i < parameters.arraySize; i++)
            {
                SerializedProperty element = parameters.GetArrayElementAtIndex(i);
                var item = new JsonObject().Set("index", i);
                SerializedProperty nameProp = ToolHelpers.TryFindRelativeProperty(element, "name");
                item.Set("name", nameProp != null ? nameProp.stringValue : null);
                SerializedProperty typeProp = ToolHelpers.TryFindRelativeProperty(element, "valueType");
                item.Set("valueType", ToolHelpers.GetEnumName(typeProp));
                SerializedProperty defaultProp = ToolHelpers.TryFindRelativeProperty(element, "defaultValue");
                if (defaultProp != null) item.Set("defaultValue", defaultProp.floatValue);
                SerializedProperty savedProp = ToolHelpers.TryFindRelativeProperty(element, "saved");
                if (savedProp != null) item.Set("saved", savedProp.boolValue);
                SerializedProperty syncedProp = ToolHelpers.TryFindRelativeProperty(element, "networkSynced");
                if (syncedProp != null) item.Set("networkSynced", syncedProp.boolValue);
                list.Add(item);
            }
            result.Set("parameterCount", (long)list.Count);
            result.Set("parameters", list);
            return result;
        }

        /// <summary>把 parameter JSON 写入参数元素（updateMode 时只写提供的字段）。</summary>
        private static void ApplyParameterFields(SerializedProperty element, JsonObject paramObj, bool updateMode)
        {
            if (paramObj == null) throw new McpToolException("parameter 参数不能为空（JSON 对象）");
            if (!updateMode && string.IsNullOrEmpty(paramObj.GetString("name")))
                throw new McpToolException("新增参数必须提供 parameter.name");

            foreach (KeyValuePair<string, object> kv in paramObj)
            {
                switch (kv.Key.ToLowerInvariant())
                {
                    case "name":
                        ToolHelpers.FindRelativeProperty(element, "name").stringValue = kv.Value == null ? "" : ToolHelpers.ToText(kv.Value);
                        break;
                    case "valuetype":
                    case "type":
                        ToolHelpers.SetEnumByName(ToolHelpers.FindRelativeProperty(element, "valueType"), ToolHelpers.ToText(kv.Value));
                        break;
                    case "defaultvalue":
                    case "default":
                        ToolHelpers.FindRelativeProperty(element, "defaultValue").floatValue = (float)ToolHelpers.ToDouble(kv.Value);
                        break;
                    case "saved":
                        ToolHelpers.FindRelativeProperty(element, "saved").boolValue = ToolHelpers.ToBool(kv.Value);
                        break;
                    case "networksynced":
                        {
                            SerializedProperty synced = ToolHelpers.TryFindRelativeProperty(element, "networkSynced");
                            if (synced != null) synced.boolValue = ToolHelpers.ToBool(kv.Value);
                            break;
                        }
                    default:
                        throw new McpToolException("未知参数字段「" + kv.Key + "」。可用字段：name/valueType/defaultValue/saved");
                }
            }
        }

        /// <summary>解析参数序号（优先用 index，否则按 name 查找）。</summary>
        private static int ResolveParameterIndex(SerializedProperty parameters, int index, string name)
        {
            if (index >= 0)
            {
                if (index >= parameters.arraySize)
                    throw new McpToolException("参数序号 " + index + " 超出范围（共 " + parameters.arraySize + " 个参数）");
                return index;
            }
            if (!string.IsNullOrEmpty(name))
            {
                for (int i = 0; i < parameters.arraySize; i++)
                {
                    SerializedProperty nameProp = ToolHelpers.TryFindRelativeProperty(parameters.GetArrayElementAtIndex(i), "name");
                    if (nameProp != null && nameProp.stringValue.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
                }
                throw new McpToolException("未找到名称为「" + name + "」的参数");
            }
            throw new McpToolException("请提供参数序号 index 或参数名 name");
        }
    }
}
