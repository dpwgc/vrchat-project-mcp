// =================================================================================================
// VrcMenuTools.cs
// VRChat 专用工具：Expressions Menu（表情菜单）的查询 / 新建 / 复制 / 编辑 / 绑定
// -------------------------------------------------------------------------------------------------
// 对应 MCP 工具（前缀 vrc）：
//   vrc.list_expressions_menus   查询 列出项目中的表情菜单资产
//   vrc.get_expressions_menu     查询 读取菜单结构（控件树，支持递归子菜单）
//   vrc.create_expressions_menu  写入 新建表情菜单资产
//   vrc.copy_expressions_menu    写入 复制表情菜单资产
//   vrc.set_menu_control         写入 新增/修改/删除菜单控件（按钮/开关/子菜单/径向操控等）
//   vrc.bind_expressions         写入 把菜单/参数资产绑定到头像描述符
//
// 实现说明：
//   全部通过 SerializedObject + 反射操作 SDK 资产，本插件对 VRCSDK3 无编译期依赖；
//   未安装 SDK 时工具会返回明确的错误提示。
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
    /// VRChat 表情菜单工具（内部静态类）。
    /// </summary>
    internal static class VrcMenuTools
    {
        // ==================================================================
        // 查询类
        // ==================================================================

        /// <summary>列出项目中的表情菜单资产。</summary>
        [McpTool("vrc.list_expressions_menus", McpToolAccess.Query, "vrc", "列出项目中的 VRCExpressionsMenu（表情菜单）资产")]
        public static object ListExpressionsMenus(
            [McpParam("名称关键字（可留空）")] string search = null,
            [McpParam("最多返回条数（默认 100）")] int limit = 100)
        {
            if (VrcReflection.MenuType == null)
                throw new McpToolException("项目未安装 VRChat SDK3（找不到 VRCExpressionsMenu 类型）");
            List<UnityEngine.Object> assets = VrcReflection.FindAssetsByTypeName("VRCExpressionsMenu", search, limit);
            var items = new JsonArray();
            foreach (UnityEngine.Object asset in assets)
            {
                items.Add(new JsonObject()
                    .Set("name", asset.name)
                    .Set("path", AssetDatabase.GetAssetPath(asset))
                    .Set("guid", AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset))));
            }
            return new JsonObject().Set("count", (long)items.Count).Set("menus", items);
        }

        /// <summary>读取表情菜单结构（支持递归展开子菜单）。</summary>
        [McpTool("vrc.get_expressions_menu", McpToolAccess.Query, "vrc", "读取表情菜单结构（控件列表：名称/类型/参数/值/图标/子菜单/标签；recursive=true 时递归展开子菜单）")]
        public static object GetExpressionsMenu(
            [McpParam("菜单资产路径（如 Assets/MyMenu.asset；与 avatarTarget 二选一）")] string menuPath = null,
            [McpParam("头像目标（实例ID/场景路径/预制件路径；读取其绑定的菜单）")] string avatarTarget = null,
            [McpParam("是否递归展开子菜单（默认 true）")] bool recursive = true,
            [McpParam("子菜单递归最大深度（默认 5）")] int maxDepth = 5)
        {
            UnityEngine.Object menu = null;
            if (!string.IsNullOrEmpty(menuPath))
            {
                menu = LoadMenuAsset(menuPath);
            }
            else if (!string.IsNullOrEmpty(avatarTarget))
            {
                using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(avatarTarget))
                {
                    Component descriptor = ToolHelpers.FindAvatarDescriptor(context.Root);
                    menu = VrcReflection.ReadDescriptorAsset(descriptor, "expressionsMenu");
                }
                if (menu == null) throw new McpToolException("该头像未绑定 Expressions Menu（expressionsMenu 为空）");
            }
            else
            {
                throw new McpToolException("请提供 menuPath 或 avatarTarget 之一");
            }
            return DumpMenuAsset(menu, recursive, Math.Max(0, maxDepth), new HashSet<UnityEngine.Object>());
        }

        // ==================================================================
        // 写入类
        // ==================================================================

        /// <summary>新建表情菜单资产。</summary>
        [McpTool("vrc.create_expressions_menu", McpToolAccess.Write, "vrc", "新建 VRCExpressionsMenu（表情菜单）资产（path 为文件夹或完整 .asset 路径）")]
        public static object CreateExpressionsMenu(
            [McpParam("资产路径（Assets/ 下文件夹，或完整 xx.asset 路径）", Required = true)] string path,
            [McpParam("菜单名称（path 为文件夹时必填）")] string name = null)
        {
            string fullPath = BuildAssetPath(path, name, "Menu");
            ScriptableObject menu = CreateSdkAsset("VRCExpressionsMenu", VrcReflection.MenuType);
            if (menu == null) throw new McpToolException("创建失败：项目未安装 VRChat SDK3（找不到 VRCExpressionsMenu 类型）");
            menu.name = System.IO.Path.GetFileNameWithoutExtension(fullPath);
            AssetDatabase.CreateAsset(menu, fullPath);
            AssetDatabase.SaveAssets();
            return new JsonObject()
                .Set("path", fullPath)
                .Set("guid", AssetDatabase.AssetPathToGUID(fullPath))
                .Set("name", menu.name);
        }

        /// <summary>复制表情菜单资产。</summary>
        [McpTool("vrc.copy_expressions_menu", McpToolAccess.Write, "vrc", "复制表情菜单资产到新路径（重名自动追加序号）")]
        public static object CopyExpressionsMenu(
            [McpParam("源菜单路径", Required = true)] string sourcePath,
            [McpParam("目标路径（Assets/ 下）", Required = true)] string targetPath)
        {
            LoadMenuAsset(sourcePath); // 校验源存在且类型正确
            int slash = targetPath.LastIndexOf('/');
            if (slash > 0) ToolHelpers.EnsureFolder(targetPath.Substring(0, slash));
            string unique = AssetDatabase.GenerateUniqueAssetPath(targetPath);
            if (!AssetDatabase.CopyAsset(sourcePath, unique)) throw new McpToolException("复制菜单失败: " + sourcePath + " → " + unique);
            AssetDatabase.SaveAssets();
            return new JsonObject().Set("source", sourcePath).Set("target", unique);
        }

        /// <summary>新增/修改/删除菜单控件（按钮/开关/子菜单/径向操控等）。</summary>
        [McpTool("vrc.set_menu_control", McpToolAccess.Write, "vrc", "新增/修改/删除表情菜单控件。action: add/update/remove；control 对象字段：name、type(Button/Toggle/SubMenu/TwoAxisPuppet/FourAxisPuppet/RadialPuppet)、parameter(参数名或{name,input})、value、icon(图标资源路径)、subMenu(子菜单资产路径)、labels(字符串数组或[{name,icon}])、subParameters")]
        public static object SetMenuControl(
            [McpParam("菜单资产路径", Required = true)] string menuPath,
            [McpParam("操作：add 新增 / update 修改 / remove 删除", Required = true)] string action,
            [McpParam("控件序号（update/remove 时可用；-1 表示按 name 查找）")] int index = -1,
            [McpParam("控件定义 JSON 对象（add 时必须提供 name 字段）")] object control = null)
        {
            UnityEngine.Object menu = LoadMenuAsset(menuPath);
            var so = new SerializedObject(menu);
            SerializedProperty controls = ToolHelpers.FindTopLevelProperty(so, "controls");
            string act = (action ?? "").Trim().ToLowerInvariant();
            JsonObject ctrl = control as JsonObject;

            switch (act)
            {
                case "add":
                    {
                        controls.InsertArrayElementAtIndex(controls.arraySize);
                        SerializedProperty element = controls.GetArrayElementAtIndex(controls.arraySize - 1);
                        ApplyControlFields(element, ctrl, false);
                        break;
                    }
                case "update":
                    {
                        int idx = ResolveControlIndex(controls, index, ctrl != null ? ctrl.GetString("name") : null);
                        ApplyControlFields(controls.GetArrayElementAtIndex(idx), ctrl, true);
                        break;
                    }
                case "remove":
                    {
                        int idx = ResolveControlIndex(controls, index, ctrl != null ? ctrl.GetString("name") : null);
                        controls.DeleteArrayElementAtIndex(idx);
                        break;
                    }
                default:
                    throw new McpToolException("action 参数无效：" + action + "（可选 add / update / remove）");
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(menu);
            AssetDatabase.SaveAssets();
            return DumpMenuAsset(menu, false, 0, new HashSet<UnityEngine.Object>());
        }

        /// <summary>把菜单/参数资产绑定到头像描述符（场景对象或预制件资产，自动保存）。</summary>
        [McpTool("vrc.bind_expressions", McpToolAccess.Write, "vrc", "把表情菜单/表情参数资产绑定到头像描述符（menuPath 与 parametersPath 至少提供其一；场景对象与预制件资产均支持）")]
        public static object BindExpressions(
            [McpParam("头像目标（实例ID/场景路径/预制件资产路径）", Required = true)] string avatarTarget,
            [McpParam("菜单资产路径（可留空）")] string menuPath = null,
            [McpParam("参数资产路径（可留空）")] string parametersPath = null)
        {
            if (string.IsNullOrEmpty(menuPath) && string.IsNullOrEmpty(parametersPath))
                throw new McpToolException("请至少提供 menuPath 或 parametersPath 之一");

            using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(avatarTarget))
            {
                Component descriptor = ToolHelpers.FindAvatarDescriptor(context.Root);
                var so = new SerializedObject(descriptor);
                var bound = new JsonObject();

                if (!string.IsNullOrEmpty(menuPath))
                {
                    SerializedProperty prop = ToolHelpers.TryFindTopLevelProperty(so, "expressionsMenu");
                    if (prop == null) throw new McpToolException("描述符中未找到 expressionsMenu 属性（SDK 版本差异？）");
                    prop.objectReferenceValue = LoadMenuAsset(menuPath);
                    bound.Set("menuPath", menuPath);
                }
                if (!string.IsNullOrEmpty(parametersPath))
                {
                    SerializedProperty prop = ToolHelpers.TryFindTopLevelProperty(so, "expressionParameters");
                    if (prop == null) throw new McpToolException("描述符中未找到 expressionParameters 属性（SDK 版本差异？）");
                    prop.objectReferenceValue = LoadParametersAsset(parametersPath);
                    bound.Set("parametersPath", parametersPath);
                }

                so.ApplyModifiedProperties();
                ToolHelpers.MarkSceneDirtyOf(context.Root);
                context.MarkDirty();
                context.SaveIfNeeded();
                bound.Set("avatar", context.Root.name);
                bound.Set("saved", true);
                return bound;
            }
        }

        // ==================================================================
        // 内部辅助
        // ==================================================================

        /// <summary>加载菜单资产（校验类型名包含 ExpressionsMenu）。</summary>
        internal static UnityEngine.Object LoadMenuAsset(string path)
        {
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) throw new McpToolException("菜单资产不存在: " + path);
            if (asset.GetType().FullName.IndexOf("ExpressionsMenu", StringComparison.OrdinalIgnoreCase) < 0)
                throw new McpToolException("资产不是 VRCExpressionsMenu 类型（实际: " + asset.GetType().FullName + "）: " + path);
            return asset;
        }

        /// <summary>加载参数资产（校验类型名包含 ExpressionParameters）。</summary>
        internal static UnityEngine.Object LoadParametersAsset(string path)
        {
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) throw new McpToolException("参数资产不存在: " + path);
            if (asset.GetType().FullName.IndexOf("ExpressionParameters", StringComparison.OrdinalIgnoreCase) < 0)
                throw new McpToolException("资产不是 VRCExpressionParameters 类型（实际: " + asset.GetType().FullName + "）: " + path);
            return asset;
        }

        /// <summary>创建 SDK ScriptableObject（先用类名字符串，失败后回退反射类型）。</summary>
        internal static ScriptableObject CreateSdkAsset(string className, Type fallbackType)
        {
            try
            {
                ScriptableObject created = ScriptableObject.CreateInstance(className);
                if (created != null) return created;
            }
            catch { /* 类名不存在则走反射回退 */ }
            if (fallbackType != null) return ScriptableObject.CreateInstance(fallbackType);
            return null;
        }

        /// <summary>把菜单资产完整转储为 JSON（controls + 可选递归子菜单）。</summary>
        internal static JsonObject DumpMenuAsset(UnityEngine.Object menu, bool recursive, int maxDepth, HashSet<UnityEngine.Object> visited)
        {
            if (menu == null) return new JsonObject().Set("error", "菜单为空");
            string path = AssetDatabase.GetAssetPath(menu);
            var result = new JsonObject()
                .Set("path", path)
                .Set("name", menu.name);

            if (visited.Contains(menu))
            {
                result.Set("note", "循环引用，已跳过展开");
                return result;
            }
            visited.Add(menu);

            var so = new SerializedObject(menu);
            SerializedProperty controls = ToolHelpers.TryFindTopLevelProperty(so, "controls");
            if (controls == null)
            {
                result.Set("error", "未找到 controls 属性（SDK 版本差异？）");
                return result;
            }

            var controlList = new JsonArray();
            for (int i = 0; i < controls.arraySize; i++)
            {
                SerializedProperty element = controls.GetArrayElementAtIndex(i);
                controlList.Add(DumpControl(element, i));
            }
            result.Set("controlCount", (long)controlList.Count);
            result.Set("controls", controlList);

            // 递归展开子菜单
            if (recursive && maxDepth > 0)
            {
                var subMenus = new JsonArray();
                for (int i = 0; i < controls.arraySize; i++)
                {
                    SerializedProperty subMenuProp = ToolHelpers.TryFindRelativeProperty(controls.GetArrayElementAtIndex(i), "subMenu");
                    UnityEngine.Object subMenu = subMenuProp != null ? subMenuProp.objectReferenceValue : null;
                    if (subMenu != null) subMenus.Add(DumpMenuAsset(subMenu, true, maxDepth - 1, visited));
                }
                if (subMenus.Count > 0) result.Set("subMenus", subMenus);
            }
            return result;
        }

        /// <summary>转储单个控件为 JSON。</summary>
        private static JsonObject DumpControl(SerializedProperty element, int index)
        {
            var control = new JsonObject().Set("index", index);
            SerializedProperty nameProp = ToolHelpers.TryFindRelativeProperty(element, "name");
            control.Set("name", nameProp != null ? nameProp.stringValue : null);
            SerializedProperty typeProp = ToolHelpers.TryFindRelativeProperty(element, "type");
            control.Set("type", ToolHelpers.GetEnumName(typeProp));

            SerializedProperty parameterProp = ToolHelpers.TryFindRelativeProperty(element, "parameter");
            if (parameterProp != null)
            {
                var parameter = new JsonObject();
                SerializedProperty pName = ToolHelpers.TryFindRelativeProperty(parameterProp, "name");
                parameter.Set("name", pName != null ? pName.stringValue : null);
                SerializedProperty pInput = ToolHelpers.TryFindRelativeProperty(parameterProp, "input");
                if (pInput != null) parameter.Set("input", ToolHelpers.GetEnumName(pInput));
                control.Set("parameter", parameter);
            }

            SerializedProperty valueProp = ToolHelpers.TryFindRelativeProperty(element, "value");
            if (valueProp != null) control.Set("value", valueProp.floatValue);

            SerializedProperty iconProp = ToolHelpers.TryFindRelativeProperty(element, "icon");
            if (iconProp != null && iconProp.objectReferenceValue != null)
                control.Set("iconPath", AssetDatabase.GetAssetPath(iconProp.objectReferenceValue));

            SerializedProperty labelsProp = ToolHelpers.TryFindRelativeProperty(element, "labels");
            if (labelsProp != null)
            {
                var labels = new JsonArray();
                for (int i = 0; i < labelsProp.arraySize; i++)
                {
                    SerializedProperty label = labelsProp.GetArrayElementAtIndex(i);
                    var labelJson = new JsonObject();
                    SerializedProperty labelName = ToolHelpers.TryFindRelativeProperty(label, "name");
                    labelJson.Set("name", labelName != null ? labelName.stringValue : null);
                    SerializedProperty labelIcon = ToolHelpers.TryFindRelativeProperty(label, "icon");
                    if (labelIcon != null && labelIcon.objectReferenceValue != null)
                        labelJson.Set("iconPath", AssetDatabase.GetAssetPath(labelIcon.objectReferenceValue));
                    labels.Add(labelJson);
                }
                control.Set("labels", labels);
            }

            SerializedProperty subMenuProp = ToolHelpers.TryFindRelativeProperty(element, "subMenu");
            if (subMenuProp != null && subMenuProp.objectReferenceValue != null)
                control.Set("subMenuPath", AssetDatabase.GetAssetPath(subMenuProp.objectReferenceValue));

            SerializedProperty subParametersProp = ToolHelpers.TryFindRelativeProperty(element, "subParameters");
            if (subParametersProp != null)
            {
                var subParameters = new JsonArray();
                for (int i = 0; i < subParametersProp.arraySize; i++)
                {
                    SerializedProperty subParam = subParametersProp.GetArrayElementAtIndex(i);
                    var subParamJson = new JsonObject();
                    SerializedProperty spName = ToolHelpers.TryFindRelativeProperty(subParam, "name");
                    subParamJson.Set("name", spName != null ? spName.stringValue : null);
                    SerializedProperty spInput = ToolHelpers.TryFindRelativeProperty(subParam, "input");
                    if (spInput != null) subParamJson.Set("input", ToolHelpers.GetEnumName(spInput));
                    subParameters.Add(subParamJson);
                }
                control.Set("subParameters", subParameters);
            }
            return control;
        }

        /// <summary>把 control JSON 写入控件元素（updateMode 时只写提供的字段）。</summary>
        private static void ApplyControlFields(SerializedProperty element, JsonObject ctrl, bool updateMode)
        {
            if (ctrl == null) throw new McpToolException("control 参数不能为空（JSON 对象）");
            if (!updateMode && string.IsNullOrEmpty(ctrl.GetString("name")))
                throw new McpToolException("新增控件必须提供 name 字段");

            foreach (KeyValuePair<string, object> kv in ctrl)
            {
                string key = kv.Key;
                object value = kv.Value;
                switch (key.ToLowerInvariant())
                {
                    case "name":
                        {
                            SerializedProperty p = ToolHelpers.FindRelativeProperty(element, "name");
                            p.stringValue = value == null ? "" : ToolHelpers.ToText(value);
                            break;
                        }
                    case "type":
                        ToolHelpers.SetEnumByName(ToolHelpers.FindRelativeProperty(element, "type"), ToolHelpers.ToText(value));
                        break;
                    case "parameter":
                        {
                            SerializedProperty parameter = ToolHelpers.FindRelativeProperty(element, "parameter");
                            SerializedProperty pName = ToolHelpers.FindRelativeProperty(parameter, "name");
                            if (value is string simpleName)
                            {
                                pName.stringValue = simpleName;
                            }
                            else if (value is JsonObject paramObj)
                            {
                                if (paramObj.ContainsKey("name")) pName.stringValue = paramObj.GetString("name");
                                SerializedProperty pInput = ToolHelpers.TryFindRelativeProperty(parameter, "input");
                                if (pInput != null && paramObj.ContainsKey("input"))
                                    ToolHelpers.SetEnumByName(pInput, paramObj.GetString("input"));
                            }
                            else throw new McpToolException("parameter 字段需要字符串或 {name, input} 对象");
                            break;
                        }
                    case "value":
                        ToolHelpers.FindRelativeProperty(element, "value").floatValue = (float)ToolHelpers.ToDouble(value);
                        break;
                    case "icon":
                    case "iconpath":
                        {
                            SerializedProperty icon = ToolHelpers.FindRelativeProperty(element, "icon");
                            icon.objectReferenceValue = value == null ? null
                                : AssetDatabase.LoadAssetAtPath<Texture2D>(ToolHelpers.ToText(value));
                            if (icon.objectReferenceValue == null && value != null)
                                throw new McpToolException("图标资源加载失败: " + value);
                            break;
                        }
                    case "labels":
                        {
                            SerializedProperty labels = ToolHelpers.FindRelativeProperty(element, "labels");
                            labels.ClearArray();
                            JsonArray items = value as JsonArray;
                            if (items == null) throw new McpToolException("labels 需要数组（字符串数组或 [{name,icon}] 对象数组）");
                            for (int i = 0; i < items.Count; i++)
                            {
                                labels.InsertArrayElementAtIndex(labels.arraySize);
                                SerializedProperty label = labels.GetArrayElementAtIndex(labels.arraySize - 1);
                                if (items[i] is string labelName)
                                {
                                    ToolHelpers.FindRelativeProperty(label, "name").stringValue = labelName;
                                }
                                else if (items[i] is JsonObject labelObj)
                                {
                                    SerializedProperty ln = ToolHelpers.FindRelativeProperty(label, "name");
                                    ln.stringValue = labelObj.GetString("name") ?? "";
                                    SerializedProperty li = ToolHelpers.TryFindRelativeProperty(label, "icon");
                                    if (li != null && labelObj.ContainsKey("icon"))
                                        li.objectReferenceValue = AssetDatabase.LoadAssetAtPath<Texture2D>(labelObj.GetString("icon"));
                                }
                            }
                            break;
                        }
                    case "submenu":
                    case "submenupath":
                        {
                            SerializedProperty subMenu = ToolHelpers.FindRelativeProperty(element, "subMenu");
                            subMenu.objectReferenceValue = value == null ? null : LoadMenuAsset(ToolHelpers.ToText(value));
                            break;
                        }
                    case "subparameters":
                        {
                            SerializedProperty subParameters = ToolHelpers.FindRelativeProperty(element, "subParameters");
                            subParameters.ClearArray();
                            JsonArray items = value as JsonArray;
                            if (items == null) throw new McpToolException("subParameters 需要数组（字符串数组或 [{name,input}] 对象数组）");
                            for (int i = 0; i < items.Count; i++)
                            {
                                subParameters.InsertArrayElementAtIndex(subParameters.arraySize);
                                SerializedProperty subParam = subParameters.GetArrayElementAtIndex(subParameters.arraySize - 1);
                                if (items[i] is string spName)
                                {
                                    ToolHelpers.FindRelativeProperty(subParam, "name").stringValue = spName;
                                }
                                else if (items[i] is JsonObject spObj)
                                {
                                    ToolHelpers.FindRelativeProperty(subParam, "name").stringValue = spObj.GetString("name") ?? "";
                                    SerializedProperty spInput = ToolHelpers.TryFindRelativeProperty(subParam, "input");
                                    if (spInput != null && spObj.ContainsKey("input"))
                                        ToolHelpers.SetEnumByName(spInput, spObj.GetString("input"));
                                }
                            }
                            break;
                        }
                    default:
                        throw new McpToolException("未知控件字段「" + key + "」。可用字段：name/type/parameter/value/icon/subMenu/labels/subParameters");
                }
            }
        }

        /// <summary>解析控件序号（优先用 index，否则按 name 查找）。</summary>
        private static int ResolveControlIndex(SerializedProperty controls, int index, string name)
        {
            if (index >= 0)
            {
                if (index >= controls.arraySize)
                    throw new McpToolException("控件序号 " + index + " 超出范围（共 " + controls.arraySize + " 个控件）");
                return index;
            }
            if (!string.IsNullOrEmpty(name))
            {
                for (int i = 0; i < controls.arraySize; i++)
                {
                    SerializedProperty nameProp = ToolHelpers.TryFindRelativeProperty(controls.GetArrayElementAtIndex(i), "name");
                    if (nameProp != null && nameProp.stringValue.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
                }
                throw new McpToolException("未找到名称为「" + name + "」的控件");
            }
            throw new McpToolException("请提供控件序号 index 或控件名称 name");
        }

        /// <summary>根据 path + name 生成资产完整路径。</summary>
        internal static string BuildAssetPath(string path, string name, string defaultName)
        {
            if (string.IsNullOrEmpty(path)) throw new McpToolException("path 不能为空");
            if (path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)) throw new McpToolException("path 必须位于 Assets/ 下");
                int slash = path.LastIndexOf('/');
                if (slash > 0) ToolHelpers.EnsureFolder(path.Substring(0, slash));
                return path;
            }
            string folder = ToolHelpers.EnsureFolder(path);
            string assetName = string.IsNullOrEmpty(name) ? defaultName : name;
            return folder + "/" + assetName + ".asset";
        }
    }
}
