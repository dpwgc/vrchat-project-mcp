// =================================================================================================
// VrcReflection.cs
// VRChat SDK 相关类型的反射访问层
// -------------------------------------------------------------------------------------------------
// 背景：
//   本插件对 VRChat SDK / Modular Avatar / VRCFury 等第三方包没有任何编译期依赖，
//   所有 SDK 类型都通过反射按名称查找。这样即使项目未安装对应包，本插件也能正常编译运行，
//   只是对应工具会返回明确的错误提示。
//
// 反射目标：
//   - VRC.SDK3.Avatars.Components.VRCAvatarDescriptor（VRCSDK3 头像描述符）
//   - VRC.SDKBase.VRC_AvatarDescriptor（VRCSDK2 描述符，兼容旧项目）
//   - VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu（表情菜单资产）
//   - VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters（表情参数资产）
//   - VRC.Core.PipelineManager（蓝图 ID 信息）
//   - VRC.SDKBase.AvatarPerformanceStats（官方性能统计）
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using VrchatProjectMcp.Core.Json;

namespace VrchatProjectMcp.Editor.Tools
{
    /// <summary>
    /// VRChat SDK 反射访问层（内部静态类）。
    /// </summary>
    internal static class VrcReflection
    {
        /// <summary>VRCSDK3 头像描述符类型。</summary>
        public static Type DescriptorType
        {
            get { return ToolHelpers.FindType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"); }
        }

        /// <summary>VRCSDK2 描述符类型（兼容旧项目）。</summary>
        public static Type AvatarDescriptorLegacyType
        {
            get { return ToolHelpers.FindType("VRC.SDKBase.VRC_AvatarDescriptor"); }
        }

        /// <summary>表情菜单资产类型。</summary>
        public static Type MenuType
        {
            get { return ToolHelpers.FindType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu"); }
        }

        /// <summary>表情参数资产类型。</summary>
        public static Type ParametersType
        {
            get { return ToolHelpers.FindType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters"); }
        }

        /// <summary>蓝图信息组件类型。</summary>
        public static Type PipelineManagerType
        {
            get { return ToolHelpers.FindType("VRC.Core.PipelineManager"); }
        }

        /// <summary>官方性能统计类型。</summary>
        public static Type AvatarPerformanceStatsType
        {
            get { return ToolHelpers.FindType("VRC.SDKBase.AvatarPerformanceStats"); }
        }

        /// <summary>是否安装了 VRChat SDK（任一版本描述符存在）。</summary>
        public static bool IsSdkInstalled
        {
            get { return DescriptorType != null || AvatarDescriptorLegacyType != null; }
        }

        /// <summary>从对象层级取指定类型的组件（类型不存在时返回 null，不抛异常）。</summary>
        public static Component GetComponentOrNull(GameObject go, Type type)
        {
            if (go == null || type == null) return null;
            return go.GetComponentInChildren(type, true);
        }

        /// <summary>把性能统计对象（AvatarPerformanceStats）的公共实例字段转成 JSON。</summary>
        public static JsonObject StatsFieldsToJson(object stats)
        {
            var result = new JsonObject();
            if (stats == null) return result;
            Type type = stats.GetType();
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    object value = field.GetValue(stats);
                    if (value == null)
                    {
                        result.Set(field.Name, null);
                    }
                    else if (value is Enum enumValue)
                    {
                        result.Set(field.Name, enumValue.ToString());
                    }
                    else if (value is string || value is bool || value is int || value is long || value is short || value is byte ||
                             value is float || value is double || value is uint || value is ulong || value is ushort)
                    {
                        result.Set(field.Name, value);
                    }
                    else if (value is Vector3 v3)
                    {
                        result.Set(field.Name, new JsonArray().Push(v3.x).Push(v3.y).Push(v3.z));
                    }
                    // 其他复杂类型跳过，避免序列化爆炸
                }
                catch { /* 单字段读取失败不影响整体 */ }
            }
            return result;
        }

        /// <summary>按类名搜索资产（FindAssets 的 t: 过滤器使用 SDK 类型名）。</summary>
        public static List<UnityEngine.Object> FindAssetsByTypeName(string className, string search, int limit)
        {
            var result = new List<UnityEngine.Object>();
            string filter = "t:" + className + (string.IsNullOrEmpty(search) ? "" : " " + search);
            string[] guids = AssetDatabase.FindAssets(filter);
            for (int i = 0; i < guids.Length && result.Count < limit; i++)
            {
                UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (asset != null) result.Add(asset);
            }
            return result;
        }

        /// <summary>读取描述符上绑定的资产引用（如 expressionsMenu / expressionParameters）。</summary>
        public static UnityEngine.Object ReadDescriptorAsset(Component descriptor, string propertyName)
        {
            if (descriptor == null) return null;
            SerializedObject so = new SerializedObject(descriptor);
            SerializedProperty prop = ToolHelpers.TryFindTopLevelProperty(so, propertyName);
            return prop != null ? prop.objectReferenceValue : null;
        }
    }
}
