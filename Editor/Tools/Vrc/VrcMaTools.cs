// =================================================================================================
// VrcMaTools.cs
// VRChat 专用工具：Modular Avatar Parameters（MA 参数组件）的查询与编辑
// -------------------------------------------------------------------------------------------------
// 对应 MCP 工具（前缀 vrc）：
//   vrc.ma_get_parameters 查询 读取 ModularAvatarParameters 组件的全部参数
//   vrc.ma_set_parameter  写入 新增/修改/删除 MA 参数（syncType 等按名称设置，自动适配 MA 版本）
//
// 实现说明：
//   - 通过 SerializedObject 按组件类型名（包含 "ModularAvatarParameters"）查找与读写，
//     对 Modular Avatar 包无编译期依赖；
//   - 参数字段按名称大小写不敏感查找（nameOrPrefix / syncType / defaultValue / saved /
//     isPrefix / localOnly / internal），兼容 MA 不同版本的字段差异；
//   - 枚举字段（如 syncType）写入失败时会列出该版本 MA 支持的全部取值。
// =================================================================================================

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Mcp;

namespace VrchatProjectMcp.Editor.Tools
{
    /// <summary>
    /// Modular Avatar 参数工具（内部静态类）。
    /// </summary>
    internal static class VrcMaTools
    {
        /// <summary>MA 参数组件类型名关键字。</summary>
        private const string MaParametersTypeName = "ModularAvatarParameters";

        /// <summary>读取目标对象上全部 ModularAvatarParameters 组件的参数配置。</summary>
        [McpTool("vrc.ma_get_parameters", McpToolAccess.Query, "vrc", "读取目标对象上 Modular Avatar Parameters（MA 参数）组件的全部参数配置（nameOrPrefix/syncType/defaultValue/saved 等）")]
        public static object GetMaParameters(
            [McpParam("头像目标（实例ID/场景路径/预制件资产路径）", Required = true)] string target)
        {
            using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(target))
            {
                List<Component> components = ToolHelpers.FindComponentsByTypeName(context.Root, MaParametersTypeName);
                if (components.Count == 0)
                    throw new McpToolException("目标上未找到 ModularAvatarParameters 组件（需安装 Modular Avatar 并在对象上挂载该组件）");

                var componentList = new JsonArray();
                for (int i = 0; i < components.Count; i++)
                {
                    componentList.Add(new JsonObject()
                        .Set("componentIndex", i)
                        .Set("componentPath", ToolHelpers.GetGameObjectPath(components[i].gameObject))
                        .Set("typeName", components[i].GetType().FullName)
                        .Set("parameters", DumpMaParameters(components[i])));
                }
                return new JsonObject().Set("count", (long)componentList.Count).Set("components", componentList);
            }
        }

        /// <summary>新增/修改/删除 MA 参数（parameter 对象支持部分字段更新）。</summary>
        [McpTool("vrc.ma_set_parameter", McpToolAccess.Write, "vrc", "新增/修改/删除 Modular Avatar 参数。action: add/update/remove；parameter 对象字段：nameOrPrefix、syncType（按名称，失败会列出可选值）、defaultValue、saved、isPrefix、localOnly、internal")]
        public static object SetMaParameter(
            [McpParam("头像目标（实例ID/场景路径/预制件资产路径）", Required = true)] string target,
            [McpParam("操作：add 新增 / update 修改 / remove 删除", Required = true)] string action,
            [McpParam("参数名（update/remove 时可用，与 parameterIndex 二选一）")] string nameOrPrefix = null,
            [McpParam("MA 组件序号（多个时指定，默认 0）")] int componentIndex = 0,
            [McpParam("参数序号（update/remove 时可用；-1 表示按 nameOrPrefix 查找）")] int parameterIndex = -1,
            [McpParam("参数定义 JSON 对象（add 时必须提供 nameOrPrefix；update 时只更新提供的字段）")] object parameter = null)
        {
            using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(target))
            {
                Component component = ToolHelpers.FindComponent(context.Root, MaParametersTypeName, componentIndex);
                var so = new SerializedObject(component);
                SerializedProperty parameters = ToolHelpers.TryFindTopLevelProperty(so, "parameters");
                if (parameters == null)
                    throw new McpToolException("该 MA 版本中未找到 parameters 属性（组件类型：" + component.GetType().FullName + "）");

                string act = (action ?? "").Trim().ToLowerInvariant();
                JsonObject paramObj = parameter as JsonObject;

                switch (act)
                {
                    case "add":
                        {
                            if (paramObj == null || string.IsNullOrEmpty(paramObj.GetString("nameOrPrefix")))
                                throw new McpToolException("新增参数必须提供 parameter.nameOrPrefix");
                            parameters.InsertArrayElementAtIndex(parameters.arraySize);
                            ApplyMaParameterFields(parameters.GetArrayElementAtIndex(parameters.arraySize - 1), paramObj, false);
                            break;
                        }
                    case "update":
                        {
                            int idx = ResolveMaParameterIndex(parameters, parameterIndex, nameOrPrefix);
                            ApplyMaParameterFields(parameters.GetArrayElementAtIndex(idx), paramObj, true);
                            break;
                        }
                    case "remove":
                        {
                            int idx = ResolveMaParameterIndex(parameters, parameterIndex, nameOrPrefix);
                            parameters.DeleteArrayElementAtIndex(idx);
                            break;
                        }
                    default:
                        throw new McpToolException("action 参数无效：" + action + "（可选 add / update / remove）");
                }

                so.ApplyModifiedProperties();
                ToolHelpers.MarkSceneDirtyOf(component.gameObject);
                context.MarkDirty();
                context.SaveIfNeeded();

                return new JsonObject()
                    .Set("component", component.GetType().FullName)
                    .Set("saved", true)
                    .Set("parameters", DumpMaParameters(component));
            }
        }

        // ==================================================================
        // 内部辅助
        // ==================================================================

        /// <summary>把 MA 参数组件的全部参数转储为 JSON（通用字段读取，兼容各 MA 版本）。</summary>
        private static JsonArray DumpMaParameters(Component component)
        {
            var result = new JsonArray();
            var so = new SerializedObject(component);
            SerializedProperty parameters = ToolHelpers.TryFindTopLevelProperty(so, "parameters");
            if (parameters == null) return result;

            for (int i = 0; i < parameters.arraySize; i++)
            {
                SerializedProperty element = parameters.GetArrayElementAtIndex(i);
                var item = new JsonObject().Set("index", i);
                // 老版本 MA 字段名为 name，新版本为 nameOrPrefix：读取时做回退兼容
                SerializedProperty nameProp = ToolHelpers.TryFindRelativeProperty(element, "nameOrPrefix")
                    ?? ToolHelpers.TryFindRelativeProperty(element, "name");
                item.Set("nameOrPrefix", nameProp != null ? nameProp.stringValue : null);
                SerializedProperty syncProp = ToolHelpers.TryFindRelativeProperty(element, "syncType");
                if (syncProp != null) item.Set("syncType", ToolHelpers.GetEnumName(syncProp));
                SerializedProperty defaultProp = ToolHelpers.TryFindRelativeProperty(element, "defaultValue");
                if (defaultProp != null) item.Set("defaultValue", defaultProp.floatValue);
                SerializedProperty savedProp = ToolHelpers.TryFindRelativeProperty(element, "saved");
                if (savedProp != null) item.Set("saved", savedProp.boolValue);
                SerializedProperty prefixProp = ToolHelpers.TryFindRelativeProperty(element, "isPrefix");
                if (prefixProp != null) item.Set("isPrefix", prefixProp.boolValue);
                SerializedProperty localProp = ToolHelpers.TryFindRelativeProperty(element, "localOnly");
                if (localProp != null) item.Set("localOnly", localProp.boolValue);
                SerializedProperty internalProp = ToolHelpers.TryFindRelativeProperty(element, "internal");
                if (internalProp != null) item.Set("internal", internalProp.boolValue);
                result.Add(item);
            }
            return result;
        }

        /// <summary>把 parameter JSON 写入 MA 参数元素（updateMode 时只写提供的字段）。</summary>
        private static void ApplyMaParameterFields(SerializedProperty element, JsonObject paramObj, bool updateMode)
        {
            if (paramObj == null) throw new McpToolException("parameter 参数不能为空（JSON 对象）");
            if (!updateMode && string.IsNullOrEmpty(paramObj.GetString("nameOrPrefix")))
                throw new McpToolException("新增参数必须提供 parameter.nameOrPrefix");

            foreach (KeyValuePair<string, object> kv in paramObj)
            {
                switch (kv.Key.ToLowerInvariant())
                {
                    case "nameorprefix":
                    case "name":
                        {
                            // 老版本 MA 字段名为 name，新版本为 nameOrPrefix：写入时做回退兼容
                            SerializedProperty nameProp = ToolHelpers.TryFindRelativeProperty(element, "nameOrPrefix");
                            if (nameProp == null) nameProp = ToolHelpers.FindRelativeProperty(element, "name");
                            nameProp.stringValue = kv.Value == null ? "" : ToolHelpers.ToText(kv.Value);
                            break;
                        }
                    case "synctype":
                        ToolHelpers.SetEnumByName(ToolHelpers.FindRelativeProperty(element, "syncType"), ToolHelpers.ToText(kv.Value));
                        break;
                    case "defaultvalue":
                    case "default":
                        ToolHelpers.FindRelativeProperty(element, "defaultValue").floatValue = (float)ToolHelpers.ToDouble(kv.Value);
                        break;
                    case "saved":
                        ToolHelpers.FindRelativeProperty(element, "saved").boolValue = ToolHelpers.ToBool(kv.Value);
                        break;
                    case "isprefix":
                        ToolHelpers.FindRelativeProperty(element, "isPrefix").boolValue = ToolHelpers.ToBool(kv.Value);
                        break;
                    case "localonly":
                        ToolHelpers.FindRelativeProperty(element, "localOnly").boolValue = ToolHelpers.ToBool(kv.Value);
                        break;
                    case "internal":
                        ToolHelpers.FindRelativeProperty(element, "internal").boolValue = ToolHelpers.ToBool(kv.Value);
                        break;
                    default:
                        throw new McpToolException("未知 MA 参数字段「" + kv.Key + "」。可用字段：nameOrPrefix/syncType/defaultValue/saved/isPrefix/localOnly/internal");
                }
            }
        }

        /// <summary>解析 MA 参数序号（优先用 parameterIndex，否则按 nameOrPrefix 查找）。</summary>
        private static int ResolveMaParameterIndex(SerializedProperty parameters, int parameterIndex, string nameOrPrefix)
        {
            if (parameterIndex >= 0)
            {
                if (parameterIndex >= parameters.arraySize)
                    throw new McpToolException("参数序号 " + parameterIndex + " 超出范围（共 " + parameters.arraySize + " 个参数）");
                return parameterIndex;
            }
            if (!string.IsNullOrEmpty(nameOrPrefix))
            {
                for (int i = 0; i < parameters.arraySize; i++)
                {
                    // 老版本 MA 字段名为 name，新版本为 nameOrPrefix：查找时做回退兼容
                    SerializedProperty nameProp = ToolHelpers.TryFindRelativeProperty(parameters.GetArrayElementAtIndex(i), "nameOrPrefix")
                        ?? ToolHelpers.TryFindRelativeProperty(parameters.GetArrayElementAtIndex(i), "name");
                    if (nameProp != null && nameProp.stringValue.Equals(nameOrPrefix, System.StringComparison.OrdinalIgnoreCase)) return i;
                }
                throw new McpToolException("未找到名称为「" + nameOrPrefix + "」的 MA 参数");
            }
            throw new McpToolException("请提供参数序号 parameterIndex 或参数名 nameOrPrefix");
        }
    }
}
