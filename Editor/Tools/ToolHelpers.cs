// =================================================================================================
// ToolHelpers.cs
// 工具公共辅助库
// -------------------------------------------------------------------------------------------------
// 职责：
//   1. 目标解析：把 Agent 传入的目标描述（实例ID "#123"、资产路径 "Assets/xx.prefab"、
//      场景路径 "根/子/孙"、唯一名称）解析为 GameObject，并管理预制件内容的加载/保存/卸载；
//   2. 序列化字段读写：SerializedProperty 的查找（大小写不敏感）、路径解析
//      （支持 "参数.Array.data[i].字段" 写法）、JSON 值 → 属性值写入、属性值 → JSON 读取；
//   3. 组件/类型查找（带缓存）；预制件资产编辑包装；
//   4. 常用 JSON 汇总（Transform / 组件 / GameObject / 组件统计）；
//   5. 日志文件尾部读取等杂项。
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Mcp;

namespace VrchatProjectMcp.Editor.Tools
{
    /// <summary>
    /// 工具公共辅助类（内部静态）。
    /// </summary>
    internal static class ToolHelpers
    {
        // ==================================================================
        // 目标解析
        // ==================================================================

        /// <summary>
        /// 目标解析上下文：封装解析结果，负责修改后的保存与预制件内容的卸载。
        /// 建议使用 using 语句包裹。
        /// </summary>
        public sealed class TargetContext : IDisposable
        {
            /// <summary>解析入口根对象（场景对象或预制件内容根）。</summary>
            public GameObject Root;

            /// <summary>是否由预制件资产加载。</summary>
            public bool IsPrefabAsset;

            /// <summary>预制件资产路径。</summary>
            public string PrefabPath;

            /// <summary>是否发生过修改。</summary>
            public bool Dirty;

            /// <summary>标记发生修改。</summary>
            public void MarkDirty()
            {
                Dirty = true;
            }

            /// <summary>保存修改：预制件资产写回文件；场景对象标记场景脏。</summary>
            public void SaveIfNeeded()
            {
                if (!Dirty || Root == null) return;
                if (IsPrefabAsset && !string.IsNullOrEmpty(PrefabPath))
                {
                    PrefabUtility.SaveAsPrefabAsset(Root, PrefabPath);
                }
                else if (Root.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(Root.scene);
                }
                Dirty = false;
            }

            /// <summary>释放：卸载预制件内容（未保存的修改会丢失，调用前请先 SaveIfNeeded）。</summary>
            public void Dispose()
            {
                if (IsPrefabAsset && Root != null)
                {
                    PrefabUtility.UnloadPrefabContents(Root);
                    Root = null;
                }
            }
        }

        /// <summary>
        /// 解析目标描述为 GameObject 上下文。
        /// 支持三种形式：
        ///   "#12345"         —— 实例 ID；
        ///   "Assets/xx.prefab" —— 预制件资产路径（自动加载预制件内容）；
        ///   "根/子/孙" 或唯一名称 —— 活动场景中的对象。
        /// </summary>
        public static TargetContext ResolveTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) throw new McpToolException("target 参数不能为空");
            target = target.Trim();

            // 形式一：实例 ID
            if (target.StartsWith("#", StringComparison.Ordinal))
            {
                if (!int.TryParse(target.Substring(1), out int instanceId))
                    throw new McpToolException("实例 ID 格式错误：" + target + "（应为 #数字，可通过 unity.get_scene_info 等工具查询）");
                UnityEngine.Object obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj is GameObject go) return new TargetContext { Root = go };
                throw new McpToolException("实例 ID 未找到对象：" + target);
            }

            // 形式二：预制件资产路径
            if (target.StartsWith("Assets/", StringComparison.Ordinal))
            {
                if (!File.Exists(target))
                    throw new McpToolException("资产路径不存在或不是文件（文件夹路径不能作为对象目标）: " + target);
                UnityEngine.Object main = AssetDatabase.LoadMainAssetAtPath(target);
                if (main is GameObject)
                {
                    GameObject contents = PrefabUtility.LoadPrefabContents(target);
                    return new TargetContext { Root = contents, IsPrefabAsset = true, PrefabPath = target };
                }
                throw new McpToolException("该资产不是预制件（类型：" + (main != null ? main.GetType().Name : "未知") +
                                           "），无法作为场景对象解析；请使用 unity.get_asset_info 查看资产信息");
            }

            // 形式三：场景对象（根/子/孙 路径或唯一名称）
            return new TargetContext { Root = FindInScene(target) };
        }

        /// <summary>在活动场景中按路径/唯一名称查找对象。</summary>
        private static GameObject FindInScene(string target)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                throw new McpToolException("当前没有已打开的场景，无法解析场景对象: " + target);

            string[] parts = target.Split('/');
            GameObject current = null;

            // 1) 先从活动场景根对象中匹配首段
            GameObject[] roots = scene.GetRootGameObjects();
            foreach (GameObject root in roots)
            {
                if (root.name == parts[0]) { current = root; break; }
            }

            // 2) 根名不匹配：退化为全场景唯一名称查找
            if (current == null)
            {
                GameObject[] all = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                var matches = new List<GameObject>();
                foreach (GameObject go in all)
                {
                    if (go.name == parts[0]) matches.Add(go);
                }
                if (matches.Count == 1) current = matches[0];
                else if (matches.Count > 1)
                    throw new McpToolException("名称「" + parts[0] + "」匹配到 " + matches.Count + " 个对象，请使用完整路径（根/子/孙）或实例 ID（#数字）");
                else
                    throw new McpToolException("场景中未找到名称「" + parts[0] + "」的对象");
            }

            // 3) 逐级下钻剩余路径段
            for (int i = 1; i < parts.Length; i++)
            {
                Transform next = null;
                foreach (Transform child in current.transform)
                {
                    if (child.name == parts[i]) { next = child; break; }
                }
                if (next == null)
                    throw new McpToolException("路径片段「" + parts[i] + "」未找到（当前对象：" + current.name + "）");
                current = next.gameObject;
            }
            return current;
        }

        // ==================================================================
        // 组件与类型查找
        // ==================================================================

        /// <summary>类型查找缓存。</summary>
        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 按全名或短名查找类型（带缓存）。
        /// 1) 先对所有程序集做全名精确查找（代价低）；2) 失败后仅扫描可能相关的程序集做短名匹配。
        /// </summary>
        public static Type FindType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            lock (TypeCache)
            {
                if (TypeCache.TryGetValue(typeName, out Type cached)) return cached;
            }

            Type found = null;
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = assembly.GetType(typeName, false);
                    if (t != null) { found = t; break; }
                }
                catch { /* 忽略无法访问的程序集 */ }
            }

            if (found == null)
            {
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = assembly.GetName().Name ?? string.Empty;
                    if (!IsCandidateAssembly(name)) continue;
                    try
                    {
                        foreach (Type t in assembly.GetTypes())
                        {
                            if (t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)) { found = t; break; }
                        }
                    }
                    catch { /* 部分类型加载失败则跳过该程序集 */ }
                    if (found != null) break;
                }
            }

            // 只缓存命中的结果：未命中不缓存（域重载/新装包后可再次查找）
            if (found != null)
            {
                lock (TypeCache) { TypeCache[typeName] = found; }
            }
            return found;
        }

        /// <summary>短名匹配时只扫描可能相关的程序集（避免遍历 Unity 全部类型）。</summary>
        private static bool IsCandidateAssembly(string assemblyName)
        {
            string[] keywords = { "vrchat", "vrc", "assembly", "modular", "avatar", "vrcfury", "fury", "nadena", "anatawa", "wholesome", "liltoon", "poiyomi", "dynamic", "mcp" };
            foreach (string keyword in keywords)
            {
                if (assemblyName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>按类型名（大小写不敏感包含匹配）查找目标对象层级中的所有组件。</summary>
        public static List<Component> FindComponentsByTypeName(GameObject root, string typeName)
        {
            var result = new List<Component>();
            if (root == null || string.IsNullOrEmpty(typeName)) return result;
            Component[] all = root.GetComponentsInChildren<Component>(true);
            foreach (Component component in all)
            {
                if (component == null) continue;
                if (component.GetType().FullName.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0) result.Add(component);
            }
            return result;
        }

        /// <summary>按类型名 + 序号查找单个组件（未找到或多选歧义时抛出中文错误）。</summary>
        public static Component FindComponent(GameObject root, string typeName, int index)
        {
            // 类型名留空：取对象自身第 index 个组件（跳过 Transform，避免误改变换属性）
            if (string.IsNullOrEmpty(typeName))
            {
                if (root == null) throw new McpToolException("目标解析失败：对象为空");
                var own = new List<Component>();
                foreach (Component c in root.GetComponents<Component>())
                {
                    if (c != null && !(c is Transform)) own.Add(c);
                }
                if (own.Count == 0)
                    throw new McpToolException("对象「" + root.name + "」上没有可修改的组件（已跳过 Transform）");
                if (index < 0 || index >= own.Count)
                    throw new McpToolException("组件索引 " + index + " 超出范围（对象自身共 " + own.Count + " 个非 Transform 组件）");
                return own[index];
            }

            List<Component> list = FindComponentsByTypeName(root, typeName);
            if (list.Count == 0)
                throw new McpToolException("对象「" + (root != null ? root.name : "?") + "」上未找到类型名包含「" + typeName + "」的组件");
            if (index < 0 || index >= list.Count)
            {
                var names = new List<string>();
                foreach (Component c in list) names.Add(c.GetType().FullName);
                throw new McpToolException("组件索引 " + index + " 超出范围（共 " + list.Count + " 个匹配: " + string.Join(" / ", names) + "）");
            }
            return list[index];
        }

        /// <summary>查找头像描述符组件（优先 VRCSDK3 的 VRCAvatarDescriptor，其次 VRCSDK2 的 VRC_AvatarDescriptor）。</summary>
        public static Component FindAvatarDescriptor(GameObject root)
        {
            if (root == null) throw new McpToolException("未找到头像对象");
            Type descriptorType = VrcReflection.DescriptorType;
            Component descriptor = descriptorType != null ? root.GetComponentInChildren(descriptorType, true) : null;
            if (descriptor == null)
            {
                Type legacyType = VrcReflection.AvatarDescriptorLegacyType;
                descriptor = legacyType != null ? root.GetComponentInChildren(legacyType, true) : null;
            }
            if (descriptor == null)
                throw new McpToolException("未找到 VRCAvatarDescriptor（VRCSDK3）或 VRC_AvatarDescriptor（VRCSDK2）组件：" +
                                           "请确认目标对象是 VRChat 头像，且项目已安装 VRChat SDK");
            return descriptor;
        }

        /// <summary>给对象挂载指定类型名的组件（类型不存在或非 Component 时抛出中文错误）。</summary>
        public static void AddComponentByTypeName(GameObject go, string typeName)
        {
            Type type = FindType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                throw new McpToolException("组件类型未找到或不是 Component 子类：" + typeName);
            if (go.GetComponent(type) != null) return; // 已存在则跳过
            go.AddComponent(type);
        }

        // ==================================================================
        // SerializedProperty 查找与读写
        // ==================================================================

        /// <summary>查找顶层序列化属性（大小写不敏感，找不到返回 null）。</summary>
        public static SerializedProperty TryFindTopLevelProperty(SerializedObject so, string name)
        {
            if (so == null || so.targetObject == null) return null;
            SerializedProperty iterator = so.GetIterator();
            if (iterator.Next(true))
            {
                do
                {
                    if (iterator.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return iterator.Copy();
                } while (iterator.Next(false));
            }
            return null;
        }

        /// <summary>查找顶层序列化属性（大小写不敏感，找不到抛出中文错误）。</summary>
        public static SerializedProperty FindTopLevelProperty(SerializedObject so, string name)
        {
            SerializedProperty found = TryFindTopLevelProperty(so, name);
            if (found == null)
                throw new McpToolException("序列化属性「" + name + "」不存在（对象类型：" +
                                           (so != null && so.targetObject != null ? so.targetObject.GetType().FullName : "?") + "）");
            return found;
        }

        /// <summary>查找子属性（大小写不敏感，找不到返回 null）。</summary>
        public static SerializedProperty TryFindRelativeProperty(SerializedProperty parent, string name)
        {
            if (parent == null) return null;
            SerializedProperty direct = parent.FindPropertyRelative(name);
            if (direct != null) return direct;

            SerializedProperty iterator = parent.Copy();
            SerializedProperty end = parent.GetEndProperty();
            if (iterator.Next(true))
            {
                do
                {
                    if (iterator.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return iterator.Copy();
                } while (iterator.Next(false) && !SerializedProperty.EqualContents(iterator, end));
            }
            return null;
        }

        /// <summary>查找子属性（大小写不敏感，找不到抛出中文错误）。</summary>
        public static SerializedProperty FindRelativeProperty(SerializedProperty parent, string name)
        {
            SerializedProperty found = TryFindRelativeProperty(parent, name);
            if (found == null)
                throw new McpToolException("子属性「" + name + "」不存在（父属性：" + (parent != null ? parent.name : "?") +
                                           (parent != null ? "，类型：" + parent.type : "") + "）");
            return found;
        }

        /// <summary>
        /// 解析属性路径并定位属性。
        /// 支持写法："field"、"a.b.c"、"parameters.Array.data[0].nameOrPrefix"。
        /// </summary>
        public static SerializedProperty ResolvePropertyPath(SerializedObject so, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new McpToolException("propertyPath 不能为空");
            List<string> tokens = SplitPathTokens(path);
            SerializedProperty current = FindTopLevelProperty(so, tokens[0]);
            for (int i = 1; i < tokens.Count; i++)
            {
                string token = tokens[i];
                if (token.StartsWith("Array#", StringComparison.Ordinal))
                {
                    if (!int.TryParse(token.Substring(6), out int index))
                        throw new McpToolException("数组索引格式错误：" + token + "（应形如 Array.data[0]）");
                    current = current.GetArrayElementAtIndex(index);
                }
                else
                {
                    current = FindRelativeProperty(current, token);
                }
            }
            return current;
        }

        /// <summary>拆分属性路径 token，把 "Array.data[i]" 合并为 "Array#i"。</summary>
        private static List<string> SplitPathTokens(string path)
        {
            string[] parts = path.Split('.');
            var tokens = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Equals("Array", StringComparison.OrdinalIgnoreCase)
                    && i + 1 < parts.Length && parts[i + 1].StartsWith("data[", StringComparison.OrdinalIgnoreCase))
                {
                    string s = parts[i + 1];
                    int end = s.IndexOf(']');
                    tokens.Add("Array#" + (end > 5 ? s.Substring(5, end - 5) : "0"));
                    i++;
                }
                else
                {
                    tokens.Add(parts[i]);
                }
            }
            return tokens;
        }

        /// <summary>把整个 SerializedObject 的顶层字段转成 JSON（{属性名: {type, value}} 包装格式）。</summary>
        public static JsonObject SerializedObjectToJson(SerializedObject so, int maxDepth)
        {
            var result = new JsonObject();
            SerializedProperty iterator = so.GetIterator();
            int count = 0;
            if (iterator.Next(true))
            {
                do
                {
                    result.Set(iterator.name, ReadPropertyValue(iterator, maxDepth));
                    if (++count > 300) break; // 防御：字段过多时截断
                } while (iterator.Next(false));
            }
            return result;
        }

        /// <summary>把单个 SerializedProperty 读成 JSON 包装格式 {type, value}。</summary>
        public static JsonObject ReadPropertyValue(SerializedProperty p, int depth)
        {
            var result = new JsonObject();
            result.Set("type", p.propertyType.ToString());
            object value;
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: value = p.longValue; break;
                case SerializedPropertyType.Float: value = p.doubleValue; break;
                case SerializedPropertyType.String: value = p.stringValue; break;
                case SerializedPropertyType.Boolean: value = p.boolValue; break;
                case SerializedPropertyType.Enum:
                    value = p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : p.enumValueIndex.ToString();
                    break;
                case SerializedPropertyType.Color: value = ColorToArray(p.colorValue); break;
                case SerializedPropertyType.Vector2: value = Vector2ToArray(p.vector2Value); break;
                case SerializedPropertyType.Vector3: value = Vector3ToArray(p.vector3Value); break;
                case SerializedPropertyType.Vector4: value = Vector4ToArray(p.vector4Value); break;
                case SerializedPropertyType.Vector2Int: value = new JsonArray().Push(p.vector2IntValue.x).Push(p.vector2IntValue.y); break;
                case SerializedPropertyType.Vector3Int: value = new JsonArray().Push(p.vector3IntValue.x).Push(p.vector3IntValue.y).Push(p.vector3IntValue.z); break;
                case SerializedPropertyType.Quaternion:
                    {
                        Quaternion q = p.quaternionValue;
                        value = new JsonArray().Push(q.x).Push(q.y).Push(q.z).Push(q.w);
                        break;
                    }
                case SerializedPropertyType.Rect:
                    {
                        Rect r = p.rectValue;
                        value = new JsonObject().Set("x", r.x).Set("y", r.y).Set("width", r.width).Set("height", r.height);
                        break;
                    }
                case SerializedPropertyType.RectInt:
                    {
                        RectInt r = p.rectIntValue;
                        value = new JsonObject().Set("x", r.x).Set("y", r.y).Set("width", r.width).Set("height", r.height);
                        break;
                    }
                case SerializedPropertyType.Bounds:
                    {
                        Bounds b = p.boundsValue;
                        value = new JsonObject().Set("center", Vector3ToArray(b.center)).Set("size", Vector3ToArray(b.size));
                        break;
                    }
                case SerializedPropertyType.BoundsInt:
                    {
                        BoundsInt b = p.boundsIntValue;
                        value = new JsonObject().Set("center", new JsonArray().Push(b.center.x).Push(b.center.y).Push(b.center.z))
                                               .Set("size", new JsonArray().Push(b.size.x).Push(b.size.y).Push(b.size.z));
                        break;
                    }
                case SerializedPropertyType.LayerMask: value = p.intValue; break;
                case SerializedPropertyType.Character: value = p.intValue; break;
                case SerializedPropertyType.ObjectReference:
                    if (p.objectReferenceValue != null)
                    {
                        value = new JsonObject()
                            .Set("name", p.objectReferenceValue.name)
                            .Set("path", AssetDatabase.GetAssetPath(p.objectReferenceValue))
                            .Set("type", p.objectReferenceValue.GetType().FullName);
                    }
                    else value = null;
                    break;
                case SerializedPropertyType.ArraySize: value = (long)p.intValue; break;
                case SerializedPropertyType.Generic:
                    if (p.isArray)
                    {
                        value = depth > 0 ? ReadArray(p, depth - 1) : "…(深度限制)";
                    }
                    else
                    {
                        value = depth > 0 ? ReadChildren(p, depth - 1) : "…(深度限制)";
                    }
                    break;
                default:
                    value = p.propertyType + "（暂不支持读取）";
                    break;
            }
            result.Set("value", value);
            return result;
        }

        /// <summary>读取数组属性为 JSON 数组（逐元素递归读取，带数量上限防爆）。</summary>
        private static JsonArray ReadArray(SerializedProperty array, int depth)
        {
            var result = new JsonArray();
            int count = array.arraySize;
            int limit = count < 500 ? count : 500;
            for (int i = 0; i < limit; i++)
            {
                result.Push(ReadPropertyValue(array.GetArrayElementAtIndex(i), depth));
            }
            if (count > limit) result.Push("…(" + (count - limit) + " 个元素已省略)");
            return result;
        }

        /// <summary>读取 Generic 属性的直接子属性为 JSON 对象。</summary>
        private static JsonObject ReadChildren(SerializedProperty parent, int depth)
        {
            var result = new JsonObject();
            SerializedProperty iterator = parent.Copy();
            SerializedProperty end = parent.GetEndProperty();
            if (iterator.Next(true))
            {
                do
                {
                    result.Set(iterator.name, ReadPropertyValue(iterator, depth));
                } while (iterator.Next(false) && !SerializedProperty.EqualContents(iterator, end));
            }
            return result;
        }

        /// <summary>列出 Generic 属性的直接子属性名称（错误提示用）。</summary>
        private static List<string> ListChildNames(SerializedProperty parent)
        {
            var names = new List<string>();
            SerializedProperty iterator = parent.Copy();
            SerializedProperty end = parent.GetEndProperty();
            if (iterator.Next(true))
            {
                do
                {
                    names.Add(iterator.name);
                } while (iterator.Next(false) && !SerializedProperty.EqualContents(iterator, end));
            }
            return names;
        }

        /// <summary>
        /// 把 JSON 值写入 SerializedProperty（支持数值/字符串/布尔/枚举/颜色/向量/对象引用/复合对象）。
        /// 写入后需要调用方对 SerializedObject 执行 ApplyModifiedProperties。
        /// </summary>
        public static void WritePropertyValue(SerializedProperty p, object value)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: p.longValue = ToLong(value); break;
                case SerializedPropertyType.Float: p.floatValue = (float)ToDouble(value); break;
                case SerializedPropertyType.String: p.stringValue = value == null ? "" : ToText(value); break;
                case SerializedPropertyType.Boolean: p.boolValue = ToBool(value); break;
                case SerializedPropertyType.Enum:
                    if (value is string enumName) SetEnumByName(p, enumName);
                    else p.enumValueIndex = (int)ToLong(value);
                    break;
                case SerializedPropertyType.Color: p.colorValue = ParseColor(value); break;
                case SerializedPropertyType.Vector2:
                    {
                        double[] v = ParseNumbers(value, 2);
                        p.vector2Value = new Vector2((float)v[0], (float)v[1]);
                        break;
                    }
                case SerializedPropertyType.Vector3:
                    {
                        double[] v = ParseNumbers(value, 3);
                        p.vector3Value = new Vector3((float)v[0], (float)v[1], (float)v[2]);
                        break;
                    }
                case SerializedPropertyType.Vector4:
                    {
                        double[] v = ParseNumbers(value, 4);
                        p.vector4Value = new Vector4((float)v[0], (float)v[1], (float)v[2], (float)v[3]);
                        break;
                    }
                case SerializedPropertyType.Vector2Int:
                    {
                        double[] v = ParseNumbers(value, 2);
                        p.vector2IntValue = new Vector2Int((int)v[0], (int)v[1]);
                        break;
                    }
                case SerializedPropertyType.Vector3Int:
                    {
                        double[] v = ParseNumbers(value, 3);
                        p.vector3IntValue = new Vector3Int((int)v[0], (int)v[1], (int)v[2]);
                        break;
                    }
                case SerializedPropertyType.Quaternion:
                    {
                        double[] v = ParseNumbers(value, 4);
                        p.quaternionValue = new Quaternion((float)v[0], (float)v[1], (float)v[2], (float)v[3]);
                        break;
                    }
                case SerializedPropertyType.Rect:
                    {
                        JsonObject o = RequireObject(value, "Rect 需要 {x,y,width,height} 对象或数组");
                        p.rectValue = new Rect((float)o.GetDouble("x"), (float)o.GetDouble("y"), (float)o.GetDouble("width"), (float)o.GetDouble("height"));
                        break;
                    }
                case SerializedPropertyType.Bounds:
                    {
                        JsonObject o = RequireObject(value, "Bounds 需要 {center, size} 对象");
                        p.boundsValue = new Bounds(ParseVector3(o.GetArray("center")), ParseVector3(o.GetArray("size")));
                        break;
                    }
                case SerializedPropertyType.LayerMask: p.intValue = (int)ToLong(value); break;
                case SerializedPropertyType.Character: p.intValue = (int)ToLong(value); break;
                case SerializedPropertyType.ObjectReference:
                    if (value == null || (value is string s && (string.IsNullOrEmpty(s) || s == "null")))
                    {
                        p.objectReferenceValue = null;
                    }
                    else
                    {
                        string assetPath = ToText(value);
                        UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                        if (asset == null) throw new McpToolException("资源路径加载失败：" + assetPath);
                        // 注意：部分 Unity 版本对类型不匹配的赋值不抛异常而是静默忽略，
                        // 因此赋值后必须校验实际结果，避免"误报成功"。
                        p.objectReferenceValue = asset;
                        if (p.objectReferenceValue != asset)
                            throw new McpToolException("资源类型不匹配：该字段需要 " + p.type + " 类型的资源，但加载的是 " + asset.GetType().Name + "（" + assetPath + "）");
                    }
                    break;
                case SerializedPropertyType.Generic:
                    {
                        JsonObject o = value as JsonObject;
                        if (o == null) throw new McpToolException("复合属性需要 JSON 对象，可用字段：" + string.Join(", ", ListChildNames(p)));
                        foreach (KeyValuePair<string, object> kv in o)
                        {
                            SerializedProperty child = TryFindRelativeProperty(p, kv.Key);
                            if (child == null) throw new McpToolException("复合属性没有字段「" + kv.Key + "」，可用字段：" + string.Join(", ", ListChildNames(p)));
                            WritePropertyValue(child, kv.Value);
                        }
                        break;
                    }
                default:
                    throw new McpToolException("属性类型 " + p.propertyType + " 暂不支持写入（属性：" + p.name + "）");
            }
        }

        /// <summary>按名称设置枚举值（大小写不敏感，失败时列出全部可用值）。</summary>
        public static void SetEnumByName(SerializedProperty p, string enumName)
        {
            if (string.IsNullOrEmpty(enumName))
                throw new McpToolException("枚举值不能为空（可用值：" + string.Join(" | ", p.enumNames) + "）");
            for (int i = 0; i < p.enumNames.Length; i++)
            {
                if (p.enumNames[i].Equals(enumName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    p.enumValueIndex = i;
                    return;
                }
            }
            throw new McpToolException("枚举值「" + enumName + "」无效，可用值：" + string.Join(" | ", p.enumNames));
        }

        /// <summary>读取枚举名称（非枚举属性返回 null）。</summary>
        public static string GetEnumName(SerializedProperty p)
        {
            if (p == null || p.propertyType != SerializedPropertyType.Enum) return null;
            return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : null;
        }

        // ==================================================================
        // JSON 汇总
        // ==================================================================

        /// <summary>把 Transform 汇总为 JSON（世界与本地位姿）。</summary>
        public static JsonObject TransformToJson(Transform t)
        {
            return new JsonObject()
                .Set("position", Vector3ToArray(t.position))
                .Set("rotationEuler", Vector3ToArray(t.rotation.eulerAngles))
                .Set("scale", Vector3ToArray(t.lossyScale))
                .Set("localPosition", Vector3ToArray(t.localPosition))
                .Set("localRotationEuler", Vector3ToArray(t.localRotation.eulerAngles))
                .Set("localScale", Vector3ToArray(t.localScale))
                .Set("childCount", t.childCount);
        }

        /// <summary>把组件汇总为 JSON（类型/启用状态/序列化字段）。</summary>
        public static JsonObject ComponentToJson(Component component, int depth)
        {
            if (component == null) return null;
            var result = new JsonObject();
            result.Set("type", component.GetType().FullName);
            if (component is Behaviour behaviour) result.Set("enabled", behaviour.enabled);
            else if (component is Renderer renderer) result.Set("enabled", renderer.enabled);
            try
            {
                result.Set("serialized", SerializedObjectToJson(new SerializedObject(component), depth));
            }
            catch (Exception ex)
            {
                result.Set("serializedError", ex.Message);
            }
            return result;
        }

        /// <summary>把 GameObject 汇总为 JSON（路径/激活状态/位姿/组件列表）。</summary>
        public static JsonObject GameObjectToJson(GameObject go, int depth)
        {
            var result = new JsonObject()
                .Set("name", go.name)
                .Set("path", GetGameObjectPath(go))
                .Set("instanceId", go.GetInstanceID())
                .Set("activeSelf", go.activeSelf)
                .Set("activeInHierarchy", go.activeInHierarchy)
                .Set("tag", go.tag)
                .Set("layer", go.layer)
                .Set("transform", TransformToJson(go.transform));
            var components = new JsonArray();
            foreach (Component component in go.GetComponents<Component>())
            {
                if (component == null) continue;
                components.Add(ComponentToJson(component, depth));
            }
            result.Set("components", components);
            return result;
        }

        /// <summary>统计目标对象层级中的组件（按类型分组，取前 topN）。</summary>
        public static JsonObject ComponentsSummary(GameObject root, int topN)
        {
            var result = new JsonObject();
            if (root == null) return result;
            Component[] all = root.GetComponentsInChildren<Component>(true);
            var counts = new Dictionary<string, long>();
            foreach (Component component in all)
            {
                if (component == null) continue;
                string typeName = component.GetType().FullName;
                counts.TryGetValue(typeName, out long count);
                counts[typeName] = count + 1;
            }
            result.Set("totalComponents", (long)all.Length);
            var top = new JsonArray();
            var sorted = new List<KeyValuePair<string, long>>(counts);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < sorted.Count && i < topN; i++)
            {
                top.Add(new JsonObject().Set("type", sorted[i].Key).Set("count", sorted[i].Value));
            }
            result.Set("topTypes", top);
            return result;
        }

        /// <summary>取对象的完整场景路径（根/子/孙）。</summary>
        public static string GetGameObjectPath(GameObject go)
        {
            var parts = new List<string>();
            Transform current = go.transform;
            while (current != null)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }
            return string.Join("/", parts);
        }

        /// <summary>取对象引用路径：资产返回资产路径，场景对象返回场景路径。</summary>
        public static string GetObjectPath(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            string assetPath = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(assetPath)) return assetPath;
            if (obj is GameObject go) return GetGameObjectPath(go);
            if (obj is Component component) return GetGameObjectPath(component.gameObject);
            return obj.name;
        }

        // ==================================================================
        // 数值/文本转换辅助
        // ==================================================================

        /// <summary>把 JSON 值转为 double（失败抛出中文错误）。</summary>
        public static double ToDouble(object value)
        {
            if (value == null) throw new McpToolException("数值不能为空");
            try
            {
                if (value is string s)
                {
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                    throw new McpToolException("无法解析数值: " + s);
                }
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (McpToolException) { throw; }
            catch
            {
                throw new McpToolException("无法转换为数值: " + value);
            }
        }

        /// <summary>把 JSON 值转为 long（失败抛出中文错误）。</summary>
        public static long ToLong(object value)
        {
            if (value == null) throw new McpToolException("整数不能为空");
            try
            {
                if (value is string s)
                {
                    if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
                    return (long)ToDouble(s);
                }
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch (McpToolException) { throw; }
            catch
            {
                throw new McpToolException("无法转换为整数: " + value);
            }
        }

        /// <summary>把 JSON 值转为 bool（支持 true/false/1/0/yes/no，失败抛出中文错误）。</summary>
        public static bool ToBool(object value)
        {
            if (value is bool b) return b;
            string s = ToText(value);
            if (s == null) throw new McpToolException("布尔值不能为空");
            s = s.Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "on") return true;
            if (s == "false" || s == "0" || s == "no" || s == "off") return false;
            throw new McpToolException("无法转换为布尔值: " + value);
        }

        /// <summary>把 JSON 值转为文本（null 返回 null）。</summary>
        public static string ToText(object value)
        {
            if (value == null) return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>要求值为 JsonObject（否则抛错）。</summary>
        private static JsonObject RequireObject(object value, string message)
        {
            JsonObject o = value as JsonObject;
            if (o == null) throw new McpToolException(message);
            return o;
        }

        /// <summary>解析数字列表（支持 JSON 数组或 "x,y,z" 字符串，数量必须匹配）。</summary>
        private static double[] ParseNumbers(object value, int count)
        {
            var numbers = new double[count];
            if (value is JsonArray array)
            {
                if (array.Count != count)
                    throw new McpToolException("需要 " + count + " 个数值，实际 " + array.Count + " 个");
                for (int i = 0; i < count; i++) numbers[i] = ToDouble(array[i]);
                return numbers;
            }
            string text = ToText(value);
            if (text == null) throw new McpToolException("需要 " + count + " 个数值（数组或逗号分隔字符串）");
            string[] parts = text.Split(',');
            if (parts.Length != count)
                throw new McpToolException("需要 " + count + " 个数值，实际 " + parts.Length + " 个: " + text);
            for (int i = 0; i < count; i++) numbers[i] = ToDouble(parts[i].Trim());
            return numbers;
        }

        /// <summary>解析 Vector3（3 个数值）。</summary>
        private static Vector3 ParseVector3(object value)
        {
            double[] v = ParseNumbers(value, 3);
            return new Vector3((float)v[0], (float)v[1], (float)v[2]);
        }

        /// <summary>解析颜色（支持 "#RRGGBB[AA]" 字符串、[r,g,b,a] 数组、{r,g,b,a} 对象）。</summary>
        private static Color ParseColor(object value)
        {
            if (value is string s && s.StartsWith("#", StringComparison.Ordinal))
            {
                if (ColorUtility.TryParseHtmlString(s, out Color parsed)) return parsed;
                throw new McpToolException("颜色字符串无效: " + s);
            }
            if (value is JsonArray array)
            {
                double[] v = ParseNumbers(array, array.Count == 3 ? 3 : 4);
                return new Color((float)v[0], (float)v[1], (float)v[2], v.Length > 3 ? (float)v[3] : 1f);
            }
            JsonObject o = RequireObject(value, "颜色需要 \"#RRGGBB\" 字符串、[r,g,b,a] 数组或 {r,g,b,a} 对象");
            return new Color((float)o.GetDouble("r"), (float)o.GetDouble("g"), (float)o.GetDouble("b"), (float)o.GetDouble("a", 1));
        }

        /// <summary>Vector2 → JSON 数组。</summary>
        private static JsonArray Vector2ToArray(Vector2 v)
        {
            return new JsonArray().Push(v.x).Push(v.y);
        }

        /// <summary>Vector3 → JSON 数组。</summary>
        private static JsonArray Vector3ToArray(Vector3 v)
        {
            return new JsonArray().Push(v.x).Push(v.y).Push(v.z);
        }

        /// <summary>Vector4 → JSON 数组。</summary>
        private static JsonArray Vector4ToArray(Vector4 v)
        {
            return new JsonArray().Push(v.x).Push(v.y).Push(v.z).Push(v.w);
        }

        /// <summary>Color → JSON 数组。</summary>
        private static JsonArray ColorToArray(Color c)
        {
            return new JsonArray().Push(c.r).Push(c.g).Push(c.b).Push(c.a);
        }

        // ==================================================================
        // 文件夹 / 文件杂项
        // ==================================================================

        /// <summary>确保 Assets 下的文件夹存在（逐级创建，返回规范化路径）。</summary>
        public static string EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (folder.StartsWith("Packages/", StringComparison.Ordinal)) return folder; // 包目录不创建
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal))
                throw new McpToolException("路径必须位于 Assets/ 下：" + folder);
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(current, parts[i]);
                    if (string.IsNullOrEmpty(guid))
                        throw new McpToolException("创建文件夹失败：" + next);
                }
                current = next;
            }
            return current;
        }

        /// <summary>读取文件尾部（返回 {path, lines:[...]} 或 {path, error}）。</summary>
        public static JsonObject ReadFileTail(string path, int maxLines, int maxBytes)
        {
            var result = new JsonObject().Set("path", path);
            if (!File.Exists(path))
            {
                result.Set("error", "文件不存在");
                return result;
            }
            try
            {
                string text;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long length = fs.Length;
                    long start = Math.Max(0, length - maxBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    var buffer = new byte[length - start];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = fs.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    text = Encoding.UTF8.GetString(buffer, 0, read);
                    // 丢弃第一条可能被截断的行
                    if (start > 0)
                    {
                        int newline = text.IndexOf('\n');
                        if (newline >= 0) text = text.Substring(newline + 1);
                    }
                }
                string[] lines = text.Split('\n');
                var tail = new JsonArray();
                for (int i = Math.Max(0, lines.Length - maxLines); i < lines.Length; i++) tail.Add(lines[i].TrimEnd('\r'));
                result.Set("lines", tail);
            }
            catch (Exception ex)
            {
                result.Set("error", ex.Message);
            }
            return result;
        }

        /// <summary>获取当前编辑器日志文件路径（优先 Application.consoleLogPath，失败按平台推断）。</summary>
        public static string ResolveEditorLogPath()
        {
            try
            {
                string path = Application.consoleLogPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            }
            catch { /* 忽略 */ }

            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Logs", "Unity", "Editor.log");
                case RuntimePlatform.WindowsEditor:
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "Editor", "Editor.log");
                default:
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config", "unity3d", "Editor.log");
            }
        }

        /// <summary>克隆一个可修改的目标（供多次编辑场景使用）。</summary>
        public static void MarkSceneDirtyOf(GameObject go)
        {
            if (go == null) return;
            if (go.scene.IsValid()) EditorSceneManager.MarkSceneDirty(go.scene);
            EditorUtility.SetDirty(go);
        }
    }
}
