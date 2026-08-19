// =================================================================================================
// McpToolDefinition.cs
// 工具定义：名称 / 类型标注 / 参数 Schema / 执行回调
// -------------------------------------------------------------------------------------------------
// 职责：
//   1. McpParamDefinition —— 单个参数的 JSON Schema 描述（类型、说明、必填、默认值）；
//   2. McpToolDefinition.ToJson() —— 生成 MCP tools/list 需要的 Tool 对象
//      （description 中附带【查询】/【写入】前缀，并在 _meta 中给出结构化的 access 字段）；
//   3. McpToolDefinition.FromMethod() —— 通过反射把一个标注了 [McpTool] 的静态方法
//      转换为工具定义，并自动生成参数绑定逻辑；
//   4. BindArguments() —— 把 JSON 参数（对象按名称 / 数组按位置）转换为 CLR 实参。
// =================================================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using VrchatProjectMcp.Core.Json;

namespace VrchatProjectMcp.Core.Mcp
{
    /// <summary>
    /// 单个工具参数的 Schema 描述。
    /// </summary>
    public sealed class McpParamDefinition
    {
        /// <summary>参数名。</summary>
        public string Name { get; set; }

        /// <summary>JSON Schema 类型（string / boolean / integer / number / object / array）。</summary>
        public string JsonType { get; set; } = "string";

        /// <summary>参数中文说明。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>是否必填。</summary>
        public bool Required { get; set; }

        /// <summary>是否有默认值。</summary>
        public bool HasDefault { get; set; }

        /// <summary>默认值（未提供参数时使用）。</summary>
        public object DefaultValue { get; set; }

        /// <summary>CLR 类型（用于 JSON → CLR 参数转换）。</summary>
        public Type ClrType { get; set; }

        /// <summary>生成该参数的 JSON Schema 片段。</summary>
        public JsonObject ToSchemaJson()
        {
            return new JsonObject()
                .Set("type", JsonType)
                .Set("description", Description ?? string.Empty)
                .Set("default", HasDefault ? DefaultValue : null);
        }
    }

    /// <summary>
    /// MCP 工具定义：Schema + 执行回调。
    /// </summary>
    public sealed class McpToolDefinition
    {
        /// <summary>工具唯一名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>访问类型：查询（query）/ 写入（write）。</summary>
        public McpToolAccess Access { get; set; } = McpToolAccess.Query;

        /// <summary>工具分类（unity / vrc / mcp / 自定义）。</summary>
        public string Category { get; set; } = "misc";

        /// <summary>中文功能描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>是否建议 Agent 调用前二次确认。</summary>
        public bool SuggestConfirmation { get; set; } = true;

        /// <summary>参数 Schema 列表。</summary>
        public List<McpParamDefinition> Parameters { get; } = new List<McpParamDefinition>();

        /// <summary>执行回调：接收 JSON 参数（对象/数组），返回 JSON 可序列化结果或字符串。</summary>
        public Func<object, object> Handler { get; set; }

        /// <summary>访问类型的显示名（中文）。</summary>
        public string AccessText
        {
            get { return Access == McpToolAccess.Write ? "写入" : "查询"; }
        }

        /// <summary>生成 MCP tools/list 使用的 Tool 对象（含读写类型标注）。</summary>
        public JsonObject ToJson()
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (McpParamDefinition p in Parameters)
            {
                properties.Set(p.Name, p.ToSchemaJson());
                if (p.Required) required.Add(p.Name);
            }
            return new JsonObject()
                .Set("name", Name)
                .Set("description", "【" + AccessText + "】" + (Description ?? string.Empty) + (SuggestConfirmation && Access == McpToolAccess.Write ? "（建议调用前向用户确认）" : string.Empty))
                .Set("inputSchema", new JsonObject()
                    .Set("type", "object")
                    .Set("properties", properties)
                    .Set("required", required))
                .Set("_meta", new JsonObject()
                    .Set("access", Access == McpToolAccess.Write ? "write" : "query")
                    .Set("category", Category)
                    .Set("suggestConfirmation", SuggestConfirmation));
        }

        /// <summary>
        /// 通过反射把一个标注了 [McpTool] 的公开静态方法转换为工具定义。
        /// 返回 null 表示该方法未标注工具特性。
        /// </summary>
        public static McpToolDefinition FromMethod(MethodInfo method)
        {
            McpToolAttribute attr = method.GetCustomAttribute<McpToolAttribute>();
            if (attr == null) return null;
            if (!method.IsStatic)
            {
                // 非静态方法无法通过反射安全调用，注册表会记录警告并跳过
                throw new McpToolException("工具方法必须是静态方法：" + method.DeclaringType.FullName + "." + method.Name);
            }

            var definition = new McpToolDefinition
            {
                Name = attr.Name,
                Access = attr.Access,
                Category = attr.Category,
                Description = attr.Description,
                SuggestConfirmation = attr.SuggestConfirmation,
            };

            // 逐参数生成 Schema 与绑定信息
            ParameterInfo[] parameters = method.GetParameters();
            foreach (ParameterInfo pi in parameters)
            {
                var paramDef = new McpParamDefinition
                {
                    Name = pi.Name,
                    ClrType = pi.ParameterType,
                    JsonType = MapJsonType(pi.ParameterType),
                };
                McpParamAttribute pa = pi.GetCustomAttribute<McpParamAttribute>();
                if (pa != null)
                {
                    paramDef.Description = pa.Description ?? string.Empty;
                    paramDef.Required = pa.Required;
                }
                else
                {
                    // 默认规则：值类型参数必填；引用类型参数可选
                    paramDef.Required = pi.ParameterType.IsValueType;
                }
                paramDef.HasDefault = pi.HasDefaultValue;
                paramDef.DefaultValue = pi.HasDefaultValue ? pi.DefaultValue : null;
                // C# 默认值参数视为可选；但特性显式声明 Required=true 时以特性为准
                if (pi.HasDefaultValue && !(pa != null && pa.Required)) paramDef.Required = false;

                // 枚举参数在说明中列出全部可选值，方便 Agent 传参
                if (pi.ParameterType.IsEnum)
                {
                    string names = string.Join(" | ", Enum.GetNames(pi.ParameterType));
                    paramDef.Description = (string.IsNullOrEmpty(paramDef.Description) ? "" : paramDef.Description + " ") + "可选值: " + names;
                }
                definition.Parameters.Add(paramDef);
            }

            // 执行回调：绑定 JSON 参数并反射调用
            definition.Handler = args =>
            {
                object[] bound = definition.BindArguments(args);
                return method.Invoke(null, bound);
            };
            return definition;
        }

        /// <summary>
        /// 把 JSON 参数绑定为 CLR 实参数组：
        /// - JsonObject 按参数名（大小写不敏感）绑定；
        /// - JsonArray 按参数声明顺序绑定。
        /// </summary>
        public object[] BindArguments(object arguments)
        {
            var result = new object[Parameters.Count];
            for (int i = 0; i < Parameters.Count; i++)
            {
                McpParamDefinition p = Parameters[i];
                object raw = null;
                bool provided = false;

                if (arguments is JsonObject jo)
                {
                    // 先精确匹配，再大小写不敏感回退
                    if (jo.ContainsKey(p.Name))
                    {
                        raw = jo[p.Name];
                        provided = true;
                    }
                    else
                    {
                        foreach (KeyValuePair<string, object> kv in jo)
                        {
                            if (string.Equals(kv.Key, p.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                raw = kv.Value;
                                provided = true;
                                break;
                            }
                        }
                    }
                }
                else if (arguments is JsonArray ja)
                {
                    if (i < ja.Count)
                    {
                        raw = ja[i];
                        provided = true;
                    }
                }

                if (!provided)
                {
                    if (p.Required)
                    {
                        throw new McpToolException("缺少必需参数「" + p.Name + "」（类型 " + p.JsonType + "）", -32602);
                    }
                    result[i] = p.HasDefault ? p.DefaultValue : DefaultOf(p.ClrType);
                    continue;
                }

                if (raw == null)
                {
                    if (p.Required && p.ClrType.IsValueType)
                    {
                        // 必填值类型传 null 时使用类型默认值，避免反射调用崩溃
                        result[i] = DefaultOf(p.ClrType);
                    }
                    else
                    {
                        result[i] = null;
                    }
                    continue;
                }

                result[i] = ConvertArgument(raw, p);
            }
            return result;
        }

        /// <summary>CLR 类型 → JSON Schema 类型映射。</summary>
        public static string MapJsonType(Type type)
        {
            if (type == typeof(string)) return "string";
            if (type == typeof(bool)) return "boolean";
            if (type == typeof(JsonObject) || type == typeof(IDictionary) || typeof(IDictionary).IsAssignableFrom(type)) return "object";
            if (type == typeof(JsonArray) || type.IsArray || typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string)) return "array";
            if (type.IsEnum) return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
                type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
                return "integer";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
            if (type == typeof(object)) return "object";
            return "string";
        }

        /// <summary>取得类型的默认值（引用类型返回 null）。</summary>
        private static object DefaultOf(Type type)
        {
            if (!type.IsValueType) return null;
            return Activator.CreateInstance(type);
        }

        /// <summary>把单个 JSON 值转换为目标 CLR 类型（失败抛出带中文说明的 McpToolException）。</summary>
        private static object ConvertArgument(object raw, McpParamDefinition p)
        {
            Type type = p.ClrType;
            string name = p.Name;
            try
            {
                // object 类型直接透传
                if (type == typeof(object)) return raw;

                // 本插件数据模型
                if (type == typeof(JsonObject))
                {
                    if (raw is JsonObject jo) return jo;
                    if (raw is IDictionary<string, object> dict) return new JsonObject(dict);
                    throw new InvalidCastException("需要 JSON 对象");
                }
                if (type == typeof(JsonArray))
                {
                    if (raw is JsonArray ja) return ja;
                    if (raw is IEnumerable enumerable)
                    {
                        var arr = new JsonArray();
                        foreach (object item in enumerable) arr.Add(item);
                        return arr;
                    }
                    throw new InvalidCastException("需要 JSON 数组");
                }

                // 字符串
                if (type == typeof(string))
                {
                    if (raw is string s) return s;
                    return Convert.ToString(raw, CultureInfo.InvariantCulture);
                }

                // 布尔
                if (type == typeof(bool))
                {
                    if (raw is bool b) return b;
                    string s = Convert.ToString(raw, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
                    if (s == "true" || s == "1" || s == "yes") return true;
                    if (s == "false" || s == "0" || s == "no") return false;
                    throw new InvalidCastException("无法识别布尔值");
                }

                // 枚举：按名称（大小写不敏感）或数值转换
                if (type.IsEnum)
                {
                    if (raw is string enumName) return Enum.Parse(type, enumName, true);
                    return Enum.ToObject(type, Convert.ToInt64(raw, CultureInfo.InvariantCulture));
                }

                // 数值类型：统一走 Convert.ChangeType（兼容字符串输入）
                if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
                    type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte) ||
                    type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                {
                    return Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
                }

                // 数组：按元素逐个转换
                if (type.IsArray && raw is JsonArray jsonArray)
                {
                    Type elem = type.GetElementType();
                    Array array = Array.CreateInstance(elem, jsonArray.Count);
                    for (int i = 0; i < jsonArray.Count; i++)
                    {
                        object converted = Convert.ChangeType(jsonArray[i], elem, CultureInfo.InvariantCulture);
                        array.SetValue(converted, i);
                    }
                    return array;
                }

                // 兜底：直接返回原始 JSON 值
                return raw;
            }
            catch (McpToolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new McpToolException("参数「" + name + "」类型转换失败（期望 " + type.Name + "，实际值 " + Describe(raw) + "）: " + ex.Message, -32602);
            }
        }

        /// <summary>简要描述一个 JSON 值（用于错误提示）。</summary>
        private static string Describe(object raw)
        {
            if (raw == null) return "null";
            if (raw is JsonObject) return "对象";
            if (raw is JsonArray ja) return "数组(" + ja.Count + " 项)";
            return "'" + Convert.ToString(raw, CultureInfo.InvariantCulture) + "'";
        }
    }
}
