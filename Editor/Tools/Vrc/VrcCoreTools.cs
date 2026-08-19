// =================================================================================================
// VrcCoreTools.cs
// VRChat 专用工具：头像清单 / 头像详情 / 性能统计 / 插件探测 / 组件通用读写
// -------------------------------------------------------------------------------------------------
// 对应 MCP 工具（前缀 vrc）：
//   vrc.get_avatars           查询 列出场景与项目预制件中的头像
//   vrc.get_avatar_info       查询 头像完整详情（描述符/动画层/菜单/参数/性能/渲染/插件组件）
//   vrc.get_performance_stats 查询 头像性能统计（优先 SDK 官方计算，否则估算）
//   vrc.get_installed_packages 查询 VRChat 相关 SDK/插件版本探测
//   vrc.get_component_info    查询 指定组件（MA/VRCFury 等）完整序列化参数
//   vrc.set_component_property 写入 通用组件序列化字段设置（场景对象/预制件资产，自动保存）
//
// 实现说明：
//   - 头像识别：按组件类型名查找 VRCAvatarDescriptor / VRC_AvatarDescriptor；
//   - 性能统计：优先反射调用 VRC.SDKBase.AvatarPerformanceStats 官方接口；
//     未安装 SDK 或接口不可用时按 VRChat 官方文档阈值做近似估算并明确标注；
//   - MA / VRCFury / DynamicBone / PhysBone 等第三方组件全部按类型名通用读写。
// =================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Mcp;

namespace VrchatProjectMcp.Editor.Tools
{
    /// <summary>
    /// VRChat 核心工具（内部静态类）。
    /// </summary>
    internal static class VrcCoreTools
    {
        /// <summary>插件组件类型名关键字（用于头像上的插件组件探测）。</summary>
        private static readonly string[] PluginTypePatterns =
        {
            "VRCPhysBone", "VRCPhysBoneCollider", "VRCContact", "VRCStation", "VRCPipeline",
            "ModularAvatar", "VRCFury", "DynamicBone", "DynamicBoneCollider",
            "AvatarOptimizer", "Anatawa12", "LilToon", "Poiyomi",
        };

        // ==================================================================
        // vrc.get_avatars
        // ==================================================================

        /// <summary>列出场景与项目预制件中的 VRChat 头像。</summary>
        [McpTool("vrc.get_avatars", McpToolAccess.Query, "vrc", "列出当前场景与项目预制件中的 VRChat 头像（VRCAvatarDescriptor；预制件扫描较慢，可用 limit 限制）")]
        public static object GetAvatars(
            [McpParam("是否扫描项目预制件资产（默认 true，项目很大时可关闭）")] bool includePrefabAssets = true,
            [McpParam("最多返回条数（默认 50）")] int limit = 50)
        {
            var result = new JsonObject();
            var avatars = new JsonArray();

            // 1) 场景中的头像
            try
            {
                Type descriptorType = VrcReflection.DescriptorType;
                Type legacyType = VrcReflection.AvatarDescriptorLegacyType;
                UnityEngine.Object[] found = null;
                if (descriptorType != null) found = FindObjectsByType(descriptorType);
                if (found != null)
                {
                    foreach (UnityEngine.Object obj in found)
                    {
                        if (avatars.Count >= limit) break;
                        var component = obj as Component;
                        if (component != null) avatars.Add(AvatarSummary(component.gameObject, false));
                    }
                }
                if (legacyType != null)
                {
                    GameObject[] allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                    foreach (GameObject go in allObjects)
                    {
                        if (avatars.Count >= limit) break;
                        if (go.GetComponent(legacyType) != null) avatars.Add(AvatarSummary(go, false));
                    }
                }
            }
            catch (Exception ex)
            {
                result.Set("sceneScanError", ex.Message);
            }

            // 2) 预制件资产扫描（加载每个预制件检查描述符，较慢；limit 对总数生效）
            if (includePrefabAssets && avatars.Count < limit)
            {
                Type descriptorType = VrcReflection.DescriptorType;
                Type legacyType = VrcReflection.AvatarDescriptorLegacyType;
                string[] guids = AssetDatabase.FindAssets("t:Prefab");
                foreach (string guid in guids)
                {
                    if (avatars.Count >= limit) { result.Set("prefabScanTruncated", true); break; }
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    GameObject root = null;
                    try { root = PrefabUtility.LoadPrefabContents(path); }
                    catch { continue; }
                    try
                    {
                        Component descriptor = null;
                        if (descriptorType != null) descriptor = root.GetComponentInChildren(descriptorType, true);
                        if (descriptor == null && legacyType != null) descriptor = root.GetComponentInChildren(legacyType, true);
                        if (descriptor != null)
                        {
                            avatars.Add(AvatarSummary(descriptor.gameObject, true).Set("assetPath", path));
                        }
                    }
                    catch { /* 单个预制件失败跳过 */ }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(root);
                    }
                }
            }

            result.Set("count", (long)avatars.Count);
            result.Set("avatars", avatars);
            return result;
        }

        // ==================================================================
        // vrc.get_avatar_info
        // ==================================================================

        /// <summary>获取头像完整详情：描述符/动画层/菜单/参数/性能/渲染/插件组件。</summary>
        [McpTool("vrc.get_avatar_info", McpToolAccess.Query, "vrc", "获取头像完整详情（描述符字段/动画层/表情菜单树/表情参数/性能统计/渲染骨骼统计/MA·VRCFury 等插件组件），供 Agent 输出报告与建议")]
        public static object GetAvatarInfo(
            [McpParam("头像目标（实例ID/场景路径/预制件资产路径）", Required = true)] string target,
            [McpParam("是否包含表情菜单（默认 true）")] bool includeMenu = true,
            [McpParam("是否包含表情参数（默认 true）")] bool includeParameters = true,
            [McpParam("是否包含性能统计（默认 true）")] bool includeStats = true,
            [McpParam("是否包含动画层信息（默认 true）")] bool includeLayers = true,
            [McpParam("菜单递归深度（默认 4）")] int menuDepth = 4)
        {
            using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(target))
            {
                Component descriptor = ToolHelpers.FindAvatarDescriptor(context.Root);
                GameObject avatarRoot = descriptor.gameObject;
                var result = new JsonObject()
                    .Set("name", avatarRoot.name)
                    .Set("path", context.IsPrefabAsset ? context.PrefabPath : ToolHelpers.GetGameObjectPath(avatarRoot))
                    .Set("isPrefabAsset", context.IsPrefabAsset)
                    .Set("descriptorType", descriptor.GetType().FullName);

                // 蓝图 ID（PipelineManager）
                try
                {
                    Component pipeline = VrcReflection.GetComponentOrNull(context.Root, VrcReflection.PipelineManagerType);
                    if (pipeline != null)
                    {
                        var pso = new SerializedObject(pipeline);
                        SerializedProperty blueprintId = pso.FindProperty("blueprintId");
                        if (blueprintId != null) result.Set("blueprintId", blueprintId.stringValue);
                    }
                }
                catch { /* 忽略 */ }

                // 描述符完整序列化字段（ViewPosition / 口型 / 视线 / 自定义动画等）
                try
                {
                    result.Set("descriptor", ToolHelpers.SerializedObjectToJson(new SerializedObject(descriptor), 3));
                }
                catch (Exception ex)
                {
                    result.Set("descriptorError", ex.Message);
                }

                // 动画层
                if (includeLayers) result.Set("animatorLayers", DumpAnimatorLayers(avatarRoot));

                // 表情菜单
                if (includeMenu)
                {
                    try
                    {
                        UnityEngine.Object menu = VrcReflection.ReadDescriptorAsset(descriptor, "expressionsMenu");
                        if (menu != null) result.Set("expressionsMenu", VrcMenuTools.DumpMenuAsset(menu, true, Math.Max(0, menuDepth), new HashSet<UnityEngine.Object>()));
                        else result.Set("expressionsMenu", new JsonObject().Set("error", "未绑定表情菜单"));
                    }
                    catch (Exception ex)
                    {
                        result.Set("menuError", ex.Message);
                    }
                }

                // 表情参数
                if (includeParameters)
                {
                    try
                    {
                        UnityEngine.Object parameters = VrcReflection.ReadDescriptorAsset(descriptor, "expressionParameters");
                        if (parameters != null) result.Set("expressionParameters", VrcParameterTools.DumpParametersAsset(parameters));
                        else result.Set("expressionParameters", new JsonObject().Set("error", "未绑定表情参数"));
                    }
                    catch (Exception ex)
                    {
                        result.Set("parametersError", ex.Message);
                    }
                }

                // 性能统计
                if (includeStats) result.Set("performance", GetPerformanceStatsInternal(avatarRoot, descriptor));

                // 渲染与骨骼统计
                result.Set("renderInfo", CollectRenderInfo(avatarRoot));

                // 插件组件（MA / VRCFury / PhysBone 等）
                result.Set("pluginComponents", CollectPluginComponents(context.Root));

                return result;
            }
        }

        // ==================================================================
        // vrc.get_performance_stats
        // ==================================================================

        /// <summary>获取头像性能统计（SDK 官方计算优先，否则估算）。</summary>
        [McpTool("vrc.get_performance_stats", McpToolAccess.Query, "vrc", "获取头像性能统计（面数/骨骼/材质/PhysBone/碰撞体等计数与等级；优先 SDK 官方计算，SDK 不可用时按官方阈值估算）")]
        public static object GetPerformanceStats(
            [McpParam("头像目标（实例ID/场景路径/预制件资产路径）", Required = true)] string target)
        {
            using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(target))
            {
                Component descriptor = ToolHelpers.FindAvatarDescriptor(context.Root);
                JsonObject stats = GetPerformanceStatsInternal(descriptor.gameObject, descriptor);
                return new JsonObject()
                    .Set("name", descriptor.gameObject.name)
                    .Set("path", context.IsPrefabAsset ? context.PrefabPath : ToolHelpers.GetGameObjectPath(descriptor.gameObject))
                    .Set("performance", stats);
            }
        }

        // ==================================================================
        // vrc.get_installed_packages
        // ==================================================================

        /// <summary>探测项目安装的 VRChat 相关 SDK/插件（UPM 包 + Assets 目录插件）。</summary>
        [McpTool("vrc.get_installed_packages", McpToolAccess.Query, "vrc", "探测项目已安装的 VRChat 相关 SDK/插件版本（VRCSDK/MA/VRCFury/Poiyomi/DynamicBone/AAO 等，来自 UPM 包与 Assets 目录扫描）")]
        public static object GetInstalledPackages()
        {
            var result = new JsonObject();
            var vrcRelated = new JsonArray();
            string[] patterns = { "vrchat", "vrc", "modular-avatar", "nadena", "vrcfury", "fury", "avatar-optimizer", "anatawa", "poiyomi", "liltoon", "dynamic-bone", "wholesome", "gesture" };

            // 1) UPM 注册包
            try
            {
                UnityEditor.PackageManager.PackageInfo[] packages =
                    UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                foreach (UnityEditor.PackageManager.PackageInfo p in packages)
                {
                    string name = (p.name ?? "") + " " + (p.displayName ?? "");
                    foreach (string pattern in patterns)
                    {
                        if (name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            vrcRelated.Add(new JsonObject()
                                .Set("name", p.name)
                                .Set("version", p.version)
                                .Set("displayName", p.displayName)
                                .Set("source", "package:" + p.source)
                                .Set("packageId", p.packageId ?? ""));
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Set("packageError", ex.Message);
            }
            result.Set("packages", vrcRelated);

            // 2) Assets 目录插件探测（非 UPM 安装的插件，如 Poiyomi / DynamicBone）
            var assetPlugins = new JsonArray();
            try
            {
                foreach (string dir in Directory.GetDirectories(Application.dataPath))
                {
                    string folderName = Path.GetFileName(dir);
                    foreach (string pattern in patterns)
                    {
                        if (folderName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            assetPlugins.Add(new JsonObject()
                                .Set("folder", folderName)
                                .Set("path", "Assets/" + folderName)
                                .Set("version", TryReadPackageVersion(dir)));
                            break;
                        }
                    }
                }
            }
            catch { /* 忽略 */ }
            result.Set("assetPlugins", assetPlugins);
            result.Set("vrchatSdkInstalled", VrcReflection.IsSdkInstalled);
            result.Set("notes", "Assets 目录探测仅扫描顶层文件夹名称，深层安装的插件可能遗漏，请以 UPM 包列表（packages）为准");
            return result;
        }

        // ==================================================================
        // vrc.get_component_info / vrc.set_component_property
        // ==================================================================

        /// <summary>获取指定组件（MA/VRCFury 等任意类型）的完整序列化参数。</summary>
        [McpTool("vrc.get_component_info", McpToolAccess.Query, "vrc", "获取目标对象上指定组件（如 ModularAvatarParameters / VRCFury 组件）的完整序列化参数（用于读取 MA/VRCFury 等插件的参数设置）")]
        public static object GetComponentInfo(
            [McpParam("目标（实例ID/场景路径/预制件资产路径）", Required = true)] string target,
            [McpParam("组件类型关键字（包含匹配，如 ModularAvatarParameters / VRCFury / VRCPhysBone）", Required = true)] string componentType,
            [McpParam("组件序号（默认 0）")] int componentIndex = 0)
        {
            using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(target))
            {
                Component component = ToolHelpers.FindComponent(context.Root, componentType, componentIndex);
                return new JsonObject()
                    .Set("target", target)
                    .Set("componentType", component.GetType().FullName)
                    .Set("componentPath", ToolHelpers.GetGameObjectPath(component.gameObject))
                    .Set("componentIndex", componentIndex)
                    .Set("serialized", ToolHelpers.SerializedObjectToJson(new SerializedObject(component), 6));
            }
        }

        /// <summary>通用组件序列化字段设置（覆盖 MA/VRCFury/PhysBone 等任意组件的任意字段）。</summary>
        [McpTool("vrc.set_component_property", McpToolAccess.Write, "vrc", "修改目标上任意组件（MA/VRCFury/VRCPhysBone 等）的序列化字段。propertyPath 支持 parameters.Array.data[0].字段 写法；枚举用名称字符串")]
        public static object SetComponentProperty(
            [McpParam("目标（实例ID/场景路径/预制件资产路径）", Required = true)] string target,
            [McpParam("组件类型关键字（包含匹配）", Required = true)] string componentType,
            [McpParam("组件序号（默认 0）")] int componentIndex = 0,
            [McpParam("属性路径（如 defaultValue 或 parameters.Array.data[0].saved）", Required = true)] string propertyPath = null,
            [McpParam("新值（数字/字符串/布尔/数组/对象；枚举用名称；资源引用用资产路径）", Required = true)] object value = null)
        {
            using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(target))
            {
                Component component = ToolHelpers.FindComponent(context.Root, componentType, componentIndex);
                var so = new SerializedObject(component);
                SerializedProperty prop = ToolHelpers.ResolvePropertyPath(so, propertyPath);
                ToolHelpers.WritePropertyValue(prop, value);
                so.ApplyModifiedProperties();
                ToolHelpers.MarkSceneDirtyOf(component.gameObject);
                context.MarkDirty();
                context.SaveIfNeeded();

                return new JsonObject()
                    .Set("target", target)
                    .Set("component", component.GetType().FullName)
                    .Set("property", propertyPath)
                    .Set("newValue", ToolHelpers.ReadPropertyValue(prop, 2))
                    .Set("saved", true);
            }
        }

        // ==================================================================
        // 内部辅助：性能统计
        // ==================================================================

        /// <summary>性能统计入口：优先 SDK 官方计算，失败回退估算。</summary>
        private static JsonObject GetPerformanceStatsInternal(GameObject avatarRoot, Component descriptor)
        {
            // 1) 尝试 SDK 官方 AvatarPerformanceStats
            try
            {
                Type statsType = VrcReflection.AvatarPerformanceStatsType;
                if (statsType != null)
                {
                    object stats = TryCalculateSdkStats(statsType, avatarRoot, descriptor);
                    if (stats != null)
                    {
                        JsonObject statsJson = VrcReflection.StatsFieldsToJson(stats);
                        var sdkResult = new JsonObject()
                            .Set("source", "SDK")
                            .Set("note", "VRChat SDK 官方计算（AvatarPerformanceStats）")
                            .Set("stats", statsJson);
                        if (statsJson.ContainsKey("avatarPerformanceCategory"))
                            sdkResult.Set("category", statsJson.GetString("avatarPerformanceCategory"));
                        return sdkResult;
                    }
                }
            }
            catch { /* 失败则走估算 */ }

            // 2) 回退：按官方阈值近似估算
            JsonObject estimate = EstimateStats(avatarRoot);
            return new JsonObject()
                .Set("source", "估算")
                .Set("note", "项目未安装 VRChat SDK 或 SDK 未提供统计接口，以下为按 VRChat 官方阈值近似估算的结果，仅供参考，请以 SDK 计算结果为准")
                .Set("stats", estimate)
                .Set("rating", EstimateRating(estimate));
        }

        /// <summary>反射调用 SDK 的性能统计接口（尝试多种签名）。</summary>
        private static object TryCalculateSdkStats(Type statsType, GameObject avatarRoot, Component descriptor)
        {
            Type descriptorType = descriptor.GetType();
            Animator animator = avatarRoot.GetComponentInChildren<Animator>(true);
            Type animatorType = animator != null ? animator.GetType() : typeof(Animator);

            // 候选签名：CalculatePerformanceStats(Animator, Descriptor, out Stats) / (GameObject, out Stats) / (Descriptor, out Stats) / (Animator, out Stats)
            var candidates = new List<Type[]>();
            if (animator != null)
            {
                candidates.Add(new[] { animatorType, descriptorType, statsType.MakeByRefType() });
                candidates.Add(new[] { animatorType, statsType.MakeByRefType() });
            }
            candidates.Add(new[] { descriptorType, statsType.MakeByRefType() });
            candidates.Add(new[] { typeof(GameObject), statsType.MakeByRefType() });
            candidates.Add(new[] { typeof(GameObject), descriptorType, statsType.MakeByRefType() });

            foreach (MethodInfo method in statsType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (method.Name != "CalculatePerformanceStats" && method.Name != "GetPerformanceStats") continue;
                ParameterInfo[] parameters = method.GetParameters();
                foreach (Type[] signature in candidates)
                {
                    if (parameters.Length != signature.Length) continue;
                    bool match = true;
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (!parameters[i].ParameterType.IsAssignableFrom(signature[i]) && signature[i] != parameters[i].ParameterType)
                        {
                            // byref 类型判断：IsAssignableFrom 对 MakeByRefType 不友好，做名称比较
                            string a = parameters[i].ParameterType.FullName ?? "";
                            string b = signature[i].FullName ?? "";
                            if (!a.Equals(b, StringComparison.Ordinal) && !a.Replace("&", "").Equals(b.Replace("&", ""), StringComparison.Ordinal))
                            {
                                match = false;
                                break;
                            }
                        }
                    }
                    if (!match) continue;
                    try
                    {
                        object[] args = new object[signature.Length];
                        for (int i = 0; i < signature.Length; i++)
                        {
                            if (signature[i] == animatorType) args[i] = animator;
                            else if (signature[i] == descriptorType) args[i] = descriptor;
                            else if (signature[i] == typeof(GameObject)) args[i] = avatarRoot;
                            else args[i] = null; // out 参数
                        }
                        object returnValue = method.Invoke(null, args);
                        // out 参数优先，其次返回值
                        for (int i = 0; i < args.Length; i++)
                        {
                            if (args[i] != null && statsType.IsInstanceOfType(args[i])) return args[i];
                        }
                        if (returnValue != null && statsType.IsInstanceOfType(returnValue)) return returnValue;
                    }
                    catch { /* 尝试下一签名 */ }
                }
            }
            return null;
        }

        /// <summary>估算头像各项计数（面数/骨骼/材质/PhysBone/碰撞体等）。</summary>
        private static JsonObject EstimateStats(GameObject avatarRoot)
        {
            Renderer[] renderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            var uniqueMeshes = new HashSet<Mesh>();
            var uniqueMaterials = new HashSet<Material>();
            var boneSet = new HashSet<Transform>();
            long totalVerts = 0;
            long totalTris = 0;
            long skinnedMeshCount = 0;
            long meshRendererCount = 0;
            long materialSlots = 0;
            long dynamicBoneCount = 0;
            long physBoneCount = 0;
            long physBoneColliderCount = 0;
            long contactCount = 0;
            long colliderCount = 0;
            long rigidbodyCount = 0;
            long lightCount = 0;
            long particleSystemCount = 0;
            long audioSourceCount = 0;
            long animatorCount = 0;
            long clothCount = 0;
            long constraintCount = 0;
            long lineRendererCount = 0;
            long trailRendererCount = 0;
            Bounds totalBounds = default(Bounds);
            bool hasBounds = false;

            foreach (Renderer renderer in renderers)
            {
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    skinnedMeshCount++;
                    Mesh mesh = skinned.sharedMesh;
                    if (mesh != null)
                    {
                        uniqueMeshes.Add(mesh);
                        totalVerts += mesh.vertexCount;
                        try { totalTris += mesh.triangles.Length / 3; } catch { /* 网格不可读时跳过 */ }
                    }
                    if (skinned.bones != null) foreach (Transform bone in skinned.bones) { if (bone != null) boneSet.Add(bone); }
                }
                else if (renderer is MeshRenderer)
                {
                    meshRendererCount++;
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null)
                    {
                        uniqueMeshes.Add(filter.sharedMesh);
                        totalVerts += filter.sharedMesh.vertexCount;
                        try { totalTris += filter.sharedMesh.triangles.Length / 3; } catch { /* 忽略 */ }
                    }
                }
                Material[] materials = renderer.sharedMaterials;
                foreach (Material material in materials)
                {
                    if (material != null) uniqueMaterials.Add(material);
                }
                materialSlots += materials != null ? materials.Length : 0;

                // 世界包围盒
                if (!hasBounds) { totalBounds = renderer.bounds; hasBounds = true; }
                else totalBounds.Encapsulate(renderer.bounds);
            }

            // 组件级统计
            Component[] components = avatarRoot.GetComponentsInChildren<Component>(true);
            foreach (Component component in components)
            {
                if (component == null) continue;
                string typeName = component.GetType().FullName;
                if (typeName.IndexOf("VRCPhysBoneCollider", StringComparison.OrdinalIgnoreCase) >= 0) physBoneColliderCount++;
                else if (typeName.IndexOf("VRCPhysBone", StringComparison.OrdinalIgnoreCase) >= 0) physBoneCount++;
                else if (typeName.IndexOf("VRCContact", StringComparison.OrdinalIgnoreCase) >= 0) contactCount++;
                else if (typeName.IndexOf("DynamicBoneCollider", StringComparison.OrdinalIgnoreCase) >= 0) { /* 归入碰撞体 */ colliderCount++; }
                else if (typeName.IndexOf("DynamicBone", StringComparison.OrdinalIgnoreCase) >= 0) dynamicBoneCount++;
                else if (typeName.IndexOf("Constraint", StringComparison.OrdinalIgnoreCase) >= 0) constraintCount++;
                else if (component is Collider) colliderCount++;
                else if (component is Rigidbody) rigidbodyCount++;
                else if (component is Light) lightCount++;
                else if (component is ParticleSystem) particleSystemCount++;
                else if (component is AudioSource) audioSourceCount++;
                else if (component is Animator) animatorCount++;
                else if (component is Cloth) clothCount++;
                else if (component is LineRenderer) lineRendererCount++;
                else if (component is TrailRenderer) trailRendererCount++;
            }

            var stats = new JsonObject()
                .Set("skinnedMeshCount", skinnedMeshCount)
                .Set("meshRendererCount", meshRendererCount)
                .Set("uniqueMeshCount", (long)uniqueMeshes.Count)
                .Set("uniqueMaterialCount", (long)uniqueMaterials.Count)
                .Set("materialSlotCount", materialSlots)
                .Set("totalVertexCount", totalVerts)
                .Set("totalTriangleCount", totalTris)
                .Set("boneCount", (long)boneSet.Count)
                .Set("physBoneCount", physBoneCount)
                .Set("physBoneColliderCount", physBoneColliderCount)
                .Set("contactCount", contactCount)
                .Set("dynamicBoneCount", dynamicBoneCount)
                .Set("colliderCount", colliderCount)
                .Set("rigidbodyCount", rigidbodyCount)
                .Set("lightCount", lightCount)
                .Set("particleSystemCount", particleSystemCount)
                .Set("audioSourceCount", audioSourceCount)
                .Set("animatorCount", animatorCount)
                .Set("clothCount", clothCount)
                .Set("constraintCount", constraintCount)
                .Set("lineRendererCount", lineRendererCount)
                .Set("trailRendererCount", trailRendererCount);
            if (hasBounds)
            {
                stats.Set("boundsSize", new JsonArray().Push(Math.Round(totalBounds.size.x, 3)).Push(Math.Round(totalBounds.size.y, 3)).Push(Math.Round(totalBounds.size.z, 3)));
                stats.Set("worldScale", new JsonArray().Push(avatarRoot.transform.lossyScale.x).Push(avatarRoot.transform.lossyScale.y).Push(avatarRoot.transform.lossyScale.z));
            }
            return stats;
        }

        /// <summary>按 VRChat 官方文档阈值给出近似等级（仅供 SDK 不可用时参考）。</summary>
        private static JsonObject EstimateRating(JsonObject stats)
        {
            long tris = stats.GetLong("totalTriangleCount");
            long bones = stats.GetLong("boneCount");
            long materials = stats.GetLong("uniqueMaterialCount");
            long physBones = stats.GetLong("physBoneCount");

            var rating = new JsonObject()
                .Set("triangles", RateMetric(tris, 7500, 15000, 32000, 70000))
                .Set("bones", RateMetric(bones, 75, 100, 150, 200))
                .Set("materials", RateMetric(materials, 4, 8, 16, 32))
                .Set("physBones", RateMetric(physBones, 16, 32, 64, 128));

            // 综合等级取四项中最差
            string worst = "优秀";
            foreach (KeyValuePair<string, object> kv in rating)
            {
                string level = ((JsonObject)kv.Value).GetString("level");
                if (LevelOrder(level) > LevelOrder(worst)) worst = level;
            }
            rating.Set("overall", worst);
            rating.Set("note", "阈值参考 VRChat 官方文档（优秀/良好/中等/较差/很差），仅为估算值");
            return rating;
        }

        /// <summary>单指标评级（阈值依次为 优秀/良好/中等/较差 上限，超过则为 很差）。</summary>
        private static JsonObject RateMetric(long value, long excellent, long good, long medium, long poor)
        {
            string level = value <= excellent ? "优秀" : value <= good ? "良好" : value <= medium ? "中等" : value <= poor ? "较差" : "很差";
            return new JsonObject().Set("value", value).Set("level", level);
        }

        /// <summary>等级 → 序号（用于取最差等级）。</summary>
        private static int LevelOrder(string level)
        {
            switch (level)
            {
                case "优秀": return 0;
                case "良好": return 1;
                case "中等": return 2;
                case "较差": return 3;
                default: return 4;
            }
        }

        // ==================================================================
        // 内部辅助：动画层 / 渲染 / 插件组件
        // ==================================================================

        /// <summary>转储头像 Animator 的控制器与动画层信息。</summary>
        private static JsonObject DumpAnimatorLayers(GameObject avatarRoot)
        {
            Animator animator = avatarRoot.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.runtimeAnimatorController == null)
                return new JsonObject().Set("error", "未找到 Animator 或 AnimatorController");

            RuntimeAnimatorController controller = animator.runtimeAnimatorController;
            var result = new JsonObject()
                .Set("controllerName", controller.name)
                .Set("layerCount", (long)controller.layers.Length);

            var layers = new JsonArray();
            foreach (AnimatorControllerLayer layer in controller.layers)
            {
                var states = new JsonArray();
                foreach (AnimatorState state in layer.stateMachine.states)
                {
                    states.Add(new JsonObject()
                        .Set("name", state.state.name)
                        .Set("motion", state.state.motion != null ? state.state.motion.name : null)
                        .Set("speed", state.state.speed)
                        .Set("writeDefaults", state.state.writeDefaultValues));
                }
                layers.Add(new JsonObject()
                    .Set("name", layer.name)
                    .Set("weight", layer.defaultWeight)
                    .Set("stateCount", (long)layer.stateMachine.states.Length)
                    .Set("states", states));
            }
            result.Set("layers", layers);
            return result;
        }

        /// <summary>收集渲染信息：渲染器/网格/材质/顶点/三角面/骨骼/包围盒。</summary>
        private static JsonObject CollectRenderInfo(GameObject avatarRoot)
        {
            Renderer[] renderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            var result = new JsonObject().Set("rendererCount", (long)renderers.Length);

            var rendererList = new JsonArray();
            var uniqueMeshes = new HashSet<Mesh>();
            var uniqueMaterials = new HashSet<Material>();
            var boneSet = new HashSet<Transform>();
            long totalVerts = 0;
            long totalTris = 0;

            foreach (Renderer renderer in renderers)
            {
                var item = new JsonObject().Set("path", ToolHelpers.GetGameObjectPath(renderer.gameObject)).Set("type", renderer.GetType().Name);
                Mesh mesh = null;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    mesh = skinned.sharedMesh;
                    item.Set("boneCount", skinned.bones != null ? (long)skinned.bones.Length : 0);
                    if (skinned.bones != null) foreach (Transform bone in skinned.bones) { if (bone != null) boneSet.Add(bone); }
                }
                else if (renderer is MeshRenderer)
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null) mesh = filter.sharedMesh;
                }
                if (mesh != null)
                {
                    uniqueMeshes.Add(mesh);
                    item.Set("mesh", mesh.name);
                    item.Set("vertexCount", (long)mesh.vertexCount);
                    item.Set("blendShapeCount", (long)mesh.blendShapeCount);
                    try { item.Set("triangleCount", (long)(mesh.triangles.Length / 3)); } catch { /* 不可读则跳过 */ }
                    totalVerts += mesh.vertexCount;
                    try { totalTris += mesh.triangles.Length / 3; } catch { /* 忽略 */ }
                }
                var materialNames = new JsonArray();
                Material[] materials = renderer.sharedMaterials;
                foreach (Material material in materials)
                {
                    if (material == null) continue;
                    uniqueMaterials.Add(material);
                    materialNames.Add(material.name);
                }
                item.Set("materials", materialNames);
                rendererList.Add(item);
            }
            result.Set("renderers", rendererList);
            result.Set("uniqueMeshCount", (long)uniqueMeshes.Count);
            result.Set("uniqueMaterialCount", (long)uniqueMaterials.Count);
            result.Set("totalVertexCount", totalVerts);
            result.Set("totalTriangleCount", totalTris);
            result.Set("totalBoneCount", (long)boneSet.Count);
            return result;
        }

        /// <summary>收集头像上的插件组件（MA/VRCFury/PhysBone 等），含简要序列化摘要。</summary>
        private static JsonObject CollectPluginComponents(GameObject avatarRoot)
        {
            var result = new JsonObject();
            Component[] components = avatarRoot.GetComponentsInChildren<Component>(true);
            var counts = new JsonObject();
            var details = new JsonArray();

            foreach (Component component in components)
            {
                if (component == null) continue;
                string typeName = component.GetType().FullName;
                bool matched = false;
                foreach (string pattern in PluginTypePatterns)
                {
                    if (typeName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matched = true;
                        break;
                    }
                }
                if (!matched) continue;

                counts.TryGetValue(typeName, out object existing);
                counts[typeName] = (existing is long count ? count : 0) + 1;

                if (details.Count < 100)
                {
                    var item = new JsonObject()
                        .Set("path", ToolHelpers.GetGameObjectPath(component.gameObject))
                        .Set("type", typeName);
                    try
                    {
                        item.Set("serialized", ToolHelpers.SerializedObjectToJson(new SerializedObject(component), 2));
                    }
                    catch { /* 忽略 */ }
                    details.Add(item);
                }
            }
            result.Set("counts", counts);
            result.Set("components", details);
            return result;
        }

        // ==================================================================
        // 杂项
        // ==================================================================

        /// <summary>按类型查找场景对象（兼容旧版 FindObjectsOfTypeAll 回退）。</summary>
        private static UnityEngine.Object[] FindObjectsByType(Type type)
        {
            try
            {
                return UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None);
            }
            catch
            {
                return Resources.FindObjectsOfTypeAll(type);
            }
        }

        /// <summary>生成头像摘要条目。</summary>
        private static JsonObject AvatarSummary(GameObject go, bool isPrefabAsset)
        {
            return new JsonObject()
                .Set("name", go.name)
                .Set("path", ToolHelpers.GetGameObjectPath(go))
                .Set("instanceId", go.GetInstanceID())
                .Set("isPrefabAsset", isPrefabAsset);
        }

        /// <summary>读取文件夹下的 package.json 版本号（不存在返回 null）。</summary>
        private static string TryReadPackageVersion(string folder)
        {
            try
            {
                string packageJsonPath = Path.Combine(folder, "package.json");
                if (!File.Exists(packageJsonPath)) return null;
                object parsed = MiniJson.Parse(File.ReadAllText(packageJsonPath));
                if (parsed is JsonObject package) return package.GetString("version");
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
