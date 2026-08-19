// =================================================================================================
// UnitySceneTools.cs
// Unity 常规工具：场景 / 对象 / 组件 / 预制件
// -------------------------------------------------------------------------------------------------
// 对应 MCP 工具（前缀 unity）：
//   unity.get_scene_info        查询 当前场景信息
//   unity.list_gameobjects      查询 按名称/组件过滤列出对象
//   unity.get_object_info       查询 对象详细信息（组件+序列化字段）
//   unity.get_selection         查询 当前选中
//   unity.set_selection         写入 设置选中
//   unity.set_object_property   写入 通用序列化字段设置（场景对象/预制件资产）
//   unity.set_transform         写入 设置对象位姿
//   unity.create_gameobject     写入 创建对象
//   unity.destroy_object        写入 销毁对象
//   unity.create_prefab         写入 从场景对象创建预制件
//   unity.instantiate_prefab    写入 实例化预制件
//   unity.open_scene            写入 打开场景
//   unity.save_scene            写入 保存当前场景
//   unity.run_menu_item         写入 执行编辑器菜单项
// =================================================================================================

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Mcp;

namespace VrchatProjectMcp.Editor.Tools
{
    /// <summary>
    /// Unity 场景/对象类工具（内部静态类）。
    /// </summary>
    internal static class UnitySceneTools
    {
        // ==================================================================
        // 查询类
        // ==================================================================

        /// <summary>获取当前活动场景信息（名称/路径/对象统计/根对象列表/组件统计）。</summary>
        [McpTool("unity.get_scene_info", McpToolAccess.Query, "unity", "获取当前活动场景信息（名称/路径/对象与组件统计/根对象列表/组件类型 Top 统计）")]
        public static object GetSceneInfo()
        {
            Scene scene = SceneManager.GetActiveScene();
            var result = new JsonObject();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                result.Set("error", "当前没有已打开的场景");
                return result;
            }

            result.Set("name", scene.name)
                .Set("path", scene.path)
                .Set("isDirty", scene.isDirty)
                .Set("isLoaded", scene.isLoaded)
                .Set("buildIndex", scene.buildIndex);

            GameObject[] roots = scene.GetRootGameObjects();
            result.Set("rootCount", (long)roots.Length);

            GameObject[] allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Component[] allComponents = UnityEngine.Object.FindObjectsByType<Component>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            result.Set("objectCount", (long)allObjects.Length);
            result.Set("componentCount", (long)allComponents.Length);

            // 组件类型 Top 统计
            var counts = new Dictionary<string, long>();
            foreach (Component component in allComponents)
            {
                if (component == null) continue;
                string typeName = component.GetType().FullName;
                counts.TryGetValue(typeName, out long count);
                counts[typeName] = count + 1;
            }
            var top = new JsonArray();
            var sorted = new List<KeyValuePair<string, long>>(counts);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < sorted.Count && i < 20; i++)
            {
                top.Add(new JsonObject().Set("type", sorted[i].Key).Set("count", sorted[i].Value));
            }
            result.Set("topComponents", top);

            // 根对象摘要（最多 100 个）
            var rootsJson = new JsonArray();
            for (int i = 0; i < roots.Length && i < 100; i++)
            {
                GameObject root = roots[i];
                var rootJson = new JsonObject()
                    .Set("name", root.name)
                    .Set("instanceId", root.GetInstanceID())
                    .Set("activeSelf", root.activeSelf)
                    .Set("directChildCount", (long)root.transform.childCount)
                    .Set("recursiveObjectCount", (long)root.GetComponentsInChildren<Transform>(true).Length)
                    .Set("recursiveComponentCount", (long)root.GetComponentsInChildren<Component>(true).Length);
                rootsJson.Add(rootJson);
            }
            result.Set("roots", rootsJson);
            return result;
        }

        /// <summary>按名称/组件过滤列出场景对象（不含预制件资产，支持包含非激活对象）。</summary>
        [McpTool("unity.list_gameobjects", McpToolAccess.Query, "unity", "按名称/组件类型过滤列出活动场景中的对象（返回名称/路径/实例ID/组件名列表）")]
        public static object ListGameObjects(
            [McpParam("名称关键字（包含匹配，可留空）")] string name = null,
            [McpParam("组件类型关键字（包含匹配，如 VRCAvatarDescriptor / ModularAvatar，可留空）")] string component = null,
            [McpParam("是否包含非激活对象（默认 true）")] bool includeInactive = true,
            [McpParam("最多返回条数（默认 100）")] int limit = 100)
        {
            GameObject[] all = UnityEngine.Object.FindObjectsByType<GameObject>(
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            var matches = new JsonArray();
            foreach (GameObject go in all)
            {
                if (matches.Count >= limit) break;
                if (!string.IsNullOrEmpty(name) && go.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (!string.IsNullOrEmpty(component))
                {
                    bool hasComponent = false;
                    foreach (Component c in go.GetComponents<Component>())
                    {
                        if (c != null && c.GetType().FullName.IndexOf(component, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hasComponent = true;
                            break;
                        }
                    }
                    if (!hasComponent) continue;
                }

                var componentNames = new JsonArray();
                foreach (Component c in go.GetComponents<Component>())
                {
                    if (c != null) componentNames.Add(c.GetType().Name);
                }
                matches.Add(new JsonObject()
                    .Set("name", go.name)
                    .Set("path", ToolHelpers.GetGameObjectPath(go))
                    .Set("instanceId", go.GetInstanceID())
                    .Set("activeInHierarchy", go.activeInHierarchy)
                    .Set("layer", go.layer)
                    .Set("components", componentNames));
            }

            return new JsonObject()
                .Set("total", (long)all.Length)
                .Set("count", (long)matches.Count)
                .Set("truncated", matches.Count >= limit)
                .Set("objects", matches);
        }

        /// <summary>获取对象完整信息（组件列表 + 每个组件的序列化字段，支持场景对象与预制件资产）。</summary>
        [McpTool("unity.get_object_info", McpToolAccess.Query, "unity", "获取指定对象完整信息（路径/位姿/组件列表/各组件序列化字段）。target 支持实例ID(#123)、场景路径(根/子)或预制件资产路径(Assets/xx.prefab)")]
        public static object GetObjectInfo([McpParam("目标：实例ID(#123) / 场景路径(根/子/孙) / 预制件资产路径(Assets/xx.prefab) / 唯一名称", Required = true)] string target)
        {
            using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(target))
            {
                JsonObject result = ToolHelpers.GameObjectToJson(context.Root, 4);
                if (context.IsPrefabAsset) result.Set("prefabAssetPath", context.PrefabPath);
                return result;
            }
        }

        /// <summary>获取当前选中对象。</summary>
        [McpTool("unity.get_selection", McpToolAccess.Query, "unity", "获取编辑器当前选中的对象列表")]
        public static object GetSelection()
        {
            var items = new JsonArray();
            foreach (UnityEngine.Object obj in Selection.objects)
            {
                items.Add(new JsonObject()
                    .Set("name", obj.name)
                    .Set("type", obj.GetType().FullName)
                    .Set("path", ToolHelpers.GetObjectPath(obj))
                    .Set("instanceId", obj.GetInstanceID()));
            }
            return new JsonObject().Set("count", (long)items.Count).Set("selection", items);
        }

        // ==================================================================
        // 写入类
        // ==================================================================

        /// <summary>设置编辑器选中对象。</summary>
        [McpTool("unity.set_selection", McpToolAccess.Write, "unity", "设置编辑器选中对象（targets 为资产路径/实例ID(#123)/场景路径的数组）")]
        public static object SetSelection([McpParam("要选中的目标数组（资产路径 / #实例ID / 场景路径）", Required = true)] string[] targets)
        {
            if (targets == null || targets.Length == 0)
            {
                Selection.objects = new UnityEngine.Object[0];
                return new JsonObject().Set("selected", new JsonArray());
            }

            var selected = new List<UnityEngine.Object>();
            var summary = new JsonArray();
            foreach (string target in targets)
            {
                if (target.StartsWith("Assets/", System.StringComparison.Ordinal))
                {
                    UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(target);
                    if (asset == null) throw new McpToolException("资产未找到: " + target);
                    selected.Add(asset);
                }
                else
                {
                    // 场景对象：解析为场景对象（预制件内容对象无法作为持久选中，会按资产处理）
                    using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(target))
                    {
                        selected.Add(context.Root);
                    }
                }
            }
            Selection.objects = selected.ToArray();
            foreach (UnityEngine.Object obj in selected) summary.Add(ToolHelpers.GetObjectPath(obj));
            return new JsonObject().Set("selected", summary);
        }

        /// <summary>通用序列化字段设置：按属性路径写入任意组件的任意字段（场景对象或预制件资产，自动保存）。</summary>
        [McpTool("unity.set_object_property", McpToolAccess.Write, "unity", "按属性路径修改对象上任意组件的序列化字段（支持场景对象与预制件资产，自动保存）。propertyPath 支持 parameters.Array.data[0].字段 写法")]
        public static object SetObjectProperty(
            [McpParam("目标：实例ID(#123) / 场景路径 / 预制件资产路径", Required = true)] string target,
            [McpParam("组件类型关键字（包含匹配，如 ModularAvatarParameters / VRCFury；留空则修改对象自身第一个非 Transform 组件）")] string componentType = null,
            [McpParam("组件序号（同名组件有多个时指定，默认 0）")] int componentIndex = 0,
            [McpParam("属性路径，如 someField 或 parameters.Array.data[0].defaultValue", Required = true)] string propertyPath = null,
            [McpParam("新值（数字/字符串/布尔/数组/对象，枚举用名称字符串，资源引用用资产路径）", Required = true)] object value = null)
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

        /// <summary>设置对象位姿（世界坐标 position / 欧拉角 rotation / 本地 scale）。</summary>
        [McpTool("unity.set_transform", McpToolAccess.Write, "unity", "设置对象位姿（position/rotation 为世界空间，scale 为本地缩放；数值为 [x,y,z] 数组或 \"x,y,z\" 字符串）")]
        public static object SetTransform(
            [McpParam("目标：实例ID / 场景路径 / 预制件资产路径", Required = true)] string target,
            [McpParam("世界坐标 [x,y,z]（可留空）")] double[] position = null,
            [McpParam("世界旋转欧拉角 [x,y,z]（可留空）")] double[] rotation = null,
            [McpParam("本地缩放 [x,y,z]（可留空）")] double[] scale = null)
        {
            using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(target))
            {
                Transform transform = context.Root.transform;
                if (position != null)
                {
                    if (position.Length != 3) throw new McpToolException("position 需要 3 个数值");
                    transform.position = new Vector3((float)position[0], (float)position[1], (float)position[2]);
                }
                if (rotation != null)
                {
                    if (rotation.Length != 3) throw new McpToolException("rotation 需要 3 个数值");
                    transform.rotation = Quaternion.Euler((float)rotation[0], (float)rotation[1], (float)rotation[2]);
                }
                if (scale != null)
                {
                    if (scale.Length != 3) throw new McpToolException("scale 需要 3 个数值");
                    transform.localScale = new Vector3((float)scale[0], (float)scale[1], (float)scale[2]);
                }
                ToolHelpers.MarkSceneDirtyOf(context.Root);
                context.MarkDirty();
                context.SaveIfNeeded();
                return new JsonObject().Set("target", target).Set("transform", ToolHelpers.TransformToJson(transform));
            }
        }

        /// <summary>创建 GameObject（可选父级与初始组件）。</summary>
        [McpTool("unity.create_gameobject", McpToolAccess.Write, "unity", "在场景中创建 GameObject（可指定父级与初始组件类型名列表）")]
        public static object CreateGameObject(
            [McpParam("对象名称", Required = true)] string name,
            [McpParam("父级（实例ID/场景路径，可留空）")] string parent = null,
            [McpParam("初始组件类型名数组（如 UnityEngine.BoxCollider / VRCPhysBone，可留空）")] string[] components = null)
        {
            if (string.IsNullOrEmpty(name)) throw new McpToolException("name 不能为空");
            var go = new GameObject(name);
            if (!string.IsNullOrEmpty(parent))
            {
                using (ToolHelpers.TargetContext parentContext = ToolHelpers.ResolveTarget(parent))
                {
                    go.transform.SetParent(parentContext.Root.transform, false);
                }
            }
            if (components != null)
            {
                foreach (string componentName in components) ToolHelpers.AddComponentByTypeName(go, componentName);
            }
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeObject = go;
            return ToolHelpers.GameObjectToJson(go, 1);
        }

        /// <summary>销毁场景对象（预制件资产默认拒绝销毁；allowDestroyAsset=true 时仅销毁内存中的预制件内容，不写回资产文件）。</summary>
        [McpTool("unity.destroy_object", McpToolAccess.Write, "unity", "销毁场景对象（预制件资产默认拒绝销毁，防止误删）")]
        public static object DestroyObject(
            [McpParam("目标：实例ID / 场景路径", Required = true)] string target,
            [McpParam("是否允许销毁预制件资产（默认 false；true 仅影响内存内容，不会改写资产文件）")] bool allowDestroyAsset = false)
        {
            using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(target))
            {
                if (context.IsPrefabAsset && !allowDestroyAsset)
                    throw new McpToolException("目标是预制件资产，拒绝销毁。请使用组件级工具修改，或使用 unity.delete_asset 删除资产文件");
                string name = context.Root.name;
                Scene scene = context.Root.scene;
                Object.DestroyImmediate(context.Root);
                context.Root = null; // 已销毁：通知上下文跳过卸载，避免对已销毁对象调用 UnloadPrefabContents
                if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
                return new JsonObject().Set("destroyed", name);
            }
        }

        /// <summary>从场景对象创建预制件资产。</summary>
        [McpTool("unity.create_prefab", McpToolAccess.Write, "unity", "把场景对象保存为预制件资产（path 为 Assets/ 下的完整路径，文件夹不存在会自动创建）")]
        public static object CreatePrefabFromSceneObject(
            [McpParam("场景对象（实例ID/场景路径）", Required = true)] string target,
            [McpParam("预制件资产完整路径（Assets/xx/yy.prefab）", Required = true)] string path,
            [McpParam("是否保留场景中的原对象（默认 true）")] bool keepOriginal = true)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", System.StringComparison.Ordinal))
                throw new McpToolException("path 必须是 Assets/ 下的预制件路径（以 .prefab 结尾）");
            if (!path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase)) path += ".prefab";

            using (ToolHelpers.TargetContext context = ToolHelpers.ResolveTarget(target))
            {
                if (context.IsPrefabAsset) throw new McpToolException("目标已是预制件资产，无需再创建预制件");
                string folder = path.Substring(0, path.LastIndexOf('/'));
                ToolHelpers.EnsureFolder(folder);
                PrefabUtility.SaveAsPrefabAsset(context.Root, path);
                if (!keepOriginal)
                {
                    Scene scene = context.Root.scene;
                    Object.DestroyImmediate(context.Root);
                    if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
                }
                AssetDatabase.SaveAssets();
                return new JsonObject()
                    .Set("path", path)
                    .Set("guid", AssetDatabase.AssetPathToGUID(path))
                    .Set("keepOriginal", keepOriginal);
            }
        }

        /// <summary>在场景中实例化预制件。</summary>
        [McpTool("unity.instantiate_prefab", McpToolAccess.Write, "unity", "在活动场景中实例化预制件（可指定父级与初始位置）")]
        public static object InstantiatePrefab(
            [McpParam("预制件资产路径（Assets/xx.prefab）", Required = true)] string prefabPath,
            [McpParam("父级（实例ID/场景路径，可留空）")] string parent = null,
            [McpParam("初始世界坐标 [x,y,z]（可留空）")] double[] position = null)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (asset == null) throw new McpToolException("预制件不存在: " + prefabPath);

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            if (!string.IsNullOrEmpty(parent))
            {
                using (ToolHelpers.TargetContext parentContext = ToolHelpers.ResolveTarget(parent))
                {
                    instance.transform.SetParent(parentContext.Root.transform, false);
                }
            }
            if (position != null)
            {
                if (position.Length != 3) throw new McpToolException("position 需要 3 个数值");
                instance.transform.position = new Vector3((float)position[0], (float)position[1], (float)position[2]);
            }
            EditorSceneManager.MarkSceneDirty(instance.scene);
            Selection.activeObject = instance;
            return ToolHelpers.GameObjectToJson(instance, 0);
        }

        /// <summary>打开场景（可先保存当前场景）。</summary>
        [McpTool("unity.open_scene", McpToolAccess.Write, "unity", "打开指定场景（path 为场景资产路径；saveCurrent=true 时先静默保存当前已保存过的场景）")]
        public static object OpenScene(
            [McpParam("场景资产路径（Assets/xx.unity）", Required = true)] string path,
            [McpParam("打开前是否保存当前场景（默认 true，未保存过的新场景会被跳过）")] bool saveCurrent = true)
        {
            if (!System.IO.File.Exists(path)) throw new McpToolException("场景文件不存在: " + path);
            Scene current = SceneManager.GetActiveScene();
            if (saveCurrent && current.IsValid() && current.isDirty && !string.IsNullOrEmpty(current.path))
            {
                EditorSceneManager.SaveScene(current);
            }
            Scene opened = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            return new JsonObject()
                .Set("name", opened.name)
                .Set("path", opened.path)
                .Set("rootCount", (long)opened.GetRootGameObjects().Length);
        }

        /// <summary>保存当前场景。</summary>
        [McpTool("unity.save_scene", McpToolAccess.Write, "unity", "保存当前活动场景（未保存过的新场景无法保存）")]
        public static object SaveScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) throw new McpToolException("当前没有已打开的场景");
            if (string.IsNullOrEmpty(scene.path)) throw new McpToolException("当前场景尚未保存过（无路径），请先手动另存为");
            if (!EditorSceneManager.SaveScene(scene)) throw new McpToolException("场景保存失败: " + scene.path);
            return new JsonObject().Set("saved", true).Set("path", scene.path).Set("name", scene.name);
        }

        /// <summary>执行编辑器菜单项（等价于点击 Unity 菜单）。</summary>
        [McpTool("unity.run_menu_item", McpToolAccess.Write, "unity", "执行 Unity 编辑器菜单项（menuPath 为菜单完整路径，如 \"GameObject/3D Object/Cube\"）")]
        public static object RunMenuItem([McpParam("菜单完整路径（如 GameObject/3D Object/Cube）", Required = true)] string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath)) throw new McpToolException("menuPath 不能为空");
            bool executed = EditorApplication.ExecuteMenuItem(menuPath);
            if (!executed) throw new McpToolException("菜单项执行失败或不存在: " + menuPath);
            return new JsonObject().Set("executed", true).Set("menuPath", menuPath);
        }
    }
}
