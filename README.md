# VRChat Project MCP

面向 **VRChat 模型开发** 的 Unity 编辑器 MCP（Model Context Protocol）插件。

### 注意：本MCP插件可通过该 [DSH Agent 预设](https://github.com/dpwgc/vrchat-project-dsh-preset) 直接接入 DeepSeek Harness，使用 DeepSeek Harness 辅助改模。建议使用只读模式，如需使用读写模式，请务必确保模型存在备份。

通过内置 HTTP 服务（JSON-RPC 2.0 / SSE）向外部 AI Agent 暴露 **49 个工具**，覆盖：

- **常规 Unity 项目能力**：项目信息、场景/对象/组件查询与编辑、资产管理、控制台日志排查（对齐现有 unity-mcp 类插件的 `manage_scene / manage_gameobject / manage_asset / manage_editor` 能力）；
- **VRChat 专用能力**：头像详情报告（菜单/参数/绑定/性能/资源使用/已装插件）、编辑 MA / VRCFury 等组件参数、新建/复制/编辑/绑定表情菜单与表情参数文件。

**纯 C# 实现，零第三方依赖**（不引入 Python / JS / Newtonsoft.Json 等任何库），兼容 **Unity 2022.3 与 Unity 6**（Windows / macOS / Linux 编辑器）。

---

## 目录

1. [核心特性](#核心特性)
2. [安装](#安装)
3. [快速开始](#快速开始)
4. [配置面板](#配置面板)
5. [HTTP 端点与协议](#http-端点与协议)
6. [工具清单](#工具清单)
7. [只读 / 读写权限模式](#只读--读写权限模式)
8. [客户端接入示例](#客户端接入示例)
9. [扩展指南](#扩展指南)
10. [兼容性与已知限制](#兼容性与已知限制)
11. [安全提示](#安全提示)
12. [项目结构](#项目结构)
13. [FAQ](#faq)

---

## 核心特性

| 特性 | 说明 |
| --- | --- |
| **HTTP 端口服务** | 内置手写 HTTP/1.1 服务器（基于 `TcpListener`，规避 Unity .NET Standard 2.1 下 `HttpListener` 不可用的问题），支持 Streamable HTTP（`POST /mcp`）与传统 SSE（`GET /sse` + `POST /message`）双传输 |
| **零依赖** | 纯 C#；JSON 解析/序列化为内置实现；不依赖任何第三方 Unity 包或外部运行时 |
| **兼容性** | Unity 2022.3（.NET Standard 2.1 / C# 9）与 Unity 6；编辑器专用，不影响运行时构建 |
| **工具类型标注** | 每个工具标注 `query`（查询）或 `write`（写入），通过 `tools/list` 的 `description` 前缀与 `_meta.access` 字段暴露给 Agent，供其判定是否需要用户二次确认 |
| **权限门控** | 配置面板可切换 **只读 / 读写** 模式；只读模式下服务端直接拒绝所有写入类工具（返回 `permission_denied`） |
| **主线程安全** | 所有 Unity API 调用经主线程调度器执行，HTTP 工作线程永不直接触碰 Unity API |
| **VRChat 无编译期依赖** | 对 VRCSDK3 / Modular Avatar / VRCFury 的读写全部走 `SerializedObject` + 反射；未安装对应包时插件照常编译运行，仅相关工具返回明确错误 |
| **实时日志** | 配置面板内置实时日志框（连接/调用/拒绝/错误，分色显示），同时转发 Unity 控制台 |
| **可扩展** | 属性标注（`[McpTool]`）+ 提供者接口（`IMcpToolProvider`）+ 运行时注册三种扩展方式，详见[扩展指南](#扩展指南) |

---

## 安装

### 方式一：UPM 本地包（推荐）

1. 把本仓库复制到任意位置（例如项目旁的 `../vrchat-project-mcp`）；
2. 在 Unity 项目中打开 `Window → Package Manager → + → Add package from disk…`，选择本目录下的 `package.json`；
3. 或直接在项目的 `Packages/manifest.json` 中追加：

```json
{
  "dependencies": {
    "com.vrchat-project.mcp": "file:../../vrchat-project-mcp"
  }
}
```

### 方式二：直接放入 Assets

把整个文件夹复制到项目 `Assets/` 下（例如 `Assets/vrchat-project-mcp/`），Unity 会自动编译。`package.json` 可保留也可删除。

### 方式三：Git URL（UPM）

把仓库推到 Git 服务后，在 Package Manager 中选择 `Add package from git URL…`，填入仓库地址。

> 安装完成后，菜单栏出现 **Tools → VRChat Project MCP**（配置面板 / 启动服务器 / 停止服务器）。

---

## 快速开始

1. 打开 **Tools → VRChat Project MCP → 配置面板**；
2. 确认默认监听地址 `127.0.0.1:8765`、操作权限（默认读写）；
3. 点击 **启动服务器**（若开启了「编辑器启动后自动启动服务」则已自动运行）；
4. 浏览器打开 <http://127.0.0.1:8765/> 可看到中文信息页，`GET /health` 返回 JSON 状态；
5. 让你的 Agent 通过 HTTP 调用（示例见[客户端接入示例](#客户端接入示例)）：

```
POST http://127.0.0.1:8765/mcp
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","clientInfo":{"name":"my-agent","version":"1.0"}}}
```

然后 `tools/list` 查看全部工具，`tools/call` 执行。Agent 可以先调 `mcp.get_status` 了解服务模式与工具清单，用 `unity.get_console_logs` 排查报错，用 `vrc.get_avatar_info` 输出头像报告。

---

## 配置面板

`Tools → VRChat Project MCP → 配置面板`：

| 配置项 | 说明 |
| --- | --- |
| 监听地址 | 默认 `127.0.0.1`（仅本机可访问）；可改为 `0.0.0.0` 暴露局域网（注意安全） |
| 端口 | 默认 `8765`；填 `0` 自动分配（实际端口见顶部状态栏） |
| **操作权限** | **只读（拒绝所有写入类工具）** / **读写（允许查询与写入）**。修改立即生效，服务端实时拦截 |
| 自动启动 | 编辑器启动后自动启动服务 |
| 实时日志框 | 实时打印连接、调用、拒绝、错误事件，支持自动滚动与清空 |
| 快捷操作 | 启动 / 停止 / 重启 / 复制 MCP 端点 / 清空日志 |

> 监听地址与端口的修改需点击「重启」生效；权限模式修改即时生效。所有配置按项目隔离保存在 EditorPrefs 中。

---

## HTTP 端点与协议

| 端点 | 方法 | 说明 |
| --- | --- | --- |
| `/mcp` | POST | **Streamable HTTP**（MCP 2025-03-26）：JSON 请求 → JSON 响应；若请求头 `Accept` 含 `text/event-stream` 则以 SSE 事件流返回 |
| `/mcp` | DELETE | 会话结束（本服务无状态，直接 200） |
| `/sse` | GET | **传统 HTTP+SSE**（MCP 2024-11-05）：建立长连接，下发 `endpoint` 事件（携带 sessionId） |
| `/message?sessionId=x` | POST | 传统 SSE 传输的客户端→服务器通道；返回 202，结果经 SSE 事件写回 |
| `/health` | GET | 健康检查 JSON（状态/模式/工具数/端点列表） |
| `/` | GET | 中文信息页 |

- 协议：MCP over JSON-RPC 2.0，流程 `initialize → notifications/initialized → tools/list → tools/call`；
- 支持批量数组请求；协议版本兼容 `2024-11-05 / 2025-03-26 / 2025-06-18`（回显客户端版本）；
- 全部响应自带 CORS 头（`Access-Control-Allow-Origin: *` 等），浏览器客户端（如 MCP Inspector）可直接访问。

---

## 工具清单

> 类型列：`查询` = 只读安全；`写入` = 会修改场景/资产/项目，**只读模式下被服务端拒绝**，Agent 调用前建议向用户二次确认。

### MCP 元工具（mcp）

| 工具 | 类型 | 说明 |
| --- | --- | --- |
| `mcp.get_status` | 查询 | 服务运行状态、访问模式、全部工具清单（含读写标注）与端点 |
| `mcp.refresh_tools` | 查询 | 重新扫描程序集并刷新工具注册表（增删扩展后调用） |

### Unity 常规（unity）

| 工具 | 类型 | 说明 |
| --- | --- | --- |
| `unity.get_project_info` | 查询 | 项目基础信息（产品名/Unity 版本/平台/构建场景/资产统计） |
| `unity.get_packages` | 查询 | 已安装 UPM 包清单（含 VRChat 相关包探测） |
| `unity.get_resource_usage` | 查询 | 进程内存/托管内存/场景对象组件统计/各类型资产数量/当前选中 |
| `unity.get_console_logs` | 查询 | 控制台日志（内存环形缓冲 + Editor.log 文件尾部），支持级别/关键字过滤 |
| `unity.get_scene_info` | 查询 | 活动场景信息（名称/路径/对象统计/根对象/组件 Top 统计） |
| `unity.list_gameobjects` | 查询 | 按名称/组件关键字过滤列出场景对象（含非激活） |
| `unity.get_object_info` | 查询 | 对象完整信息（位姿/组件列表/各组件序列化字段） |
| `unity.get_selection` | 查询 | 当前编辑器选中对象 |
| `unity.set_selection` | 写入 | 设置选中（资产路径 / #实例ID / 场景路径） |
| `unity.set_object_property` | 写入 | 通用序列化字段设置（场景对象与预制件资产，自动保存），支持 `parameters.Array.data[i].字段` 路径 |
| `unity.set_transform` | 写入 | 设置对象位姿（位置/欧拉旋转/缩放） |
| `unity.create_gameobject` | 写入 | 创建 GameObject（可指定父级与初始组件） |
| `unity.destroy_object` | 写入 | 销毁场景对象（预制件资产默认拒绝） |
| `unity.create_prefab` | 写入 | 从场景对象保存预制件 |
| `unity.instantiate_prefab` | 写入 | 实例化预制件到场景 |
| `unity.open_scene` | 写入 | 打开场景（可选先保存当前场景） |
| `unity.save_scene` | 写入 | 保存当前场景 |
| `unity.run_menu_item` | 写入 | 执行编辑器菜单项（如 `GameObject/3D Object/Cube`） |
| `unity.list_assets` | 查询 | 搜索列出资产（类型/文件夹/关键字过滤） |
| `unity.get_asset_info` | 查询 | 资产详情（类型/大小/依赖/导入器/预制件摘要） |
| `unity.read_text_asset` | 查询 | 读取项目内文本文件（限 Assets/、Packages/、ProjectSettings/ 下） |
| `unity.create_asset` | 写入 | 创建资产（AnimatorController/Material/PhysicMaterial/AnimationClip/任意 ScriptableObject） |
| `unity.create_script` | 写入 | 创建 C# 脚本文件（MonoBehaviour 模板，可选命名空间） |
| `unity.copy_asset` | 写入 | 复制资产（重名自动加序号） |
| `unity.delete_asset` | 写入 | 删除资产（默认移入回收站） |
| `unity.create_folder` | 写入 | 创建 Assets 下文件夹（逐级） |
| `unity.refresh_assets` | 写入 | 保存并刷新资产数据库 |

### VRChat 专用（vrc）

| 工具 | 类型 | 说明 |
| --- | --- | --- |
| `vrc.get_avatars` | 查询 | 列出场景与项目预制件中的头像（VRCAvatarDescriptor / 旧版描述符） |
| `vrc.get_avatar_info` | 查询 | **头像完整详情**：描述符字段/动画层/菜单树/参数/性能统计/渲染骨骼统计/**贴图尺寸·大小·类型·压缩信息**/MA·VRCFury 等插件组件 —— 供 Agent 输出报告与建议 |
| `vrc.get_performance_stats` | 查询 | 性能统计（面数/骨骼/材质/PhysBone/碰撞体计数与等级；优先 SDK 官方计算，否则按官方阈值估算并标注） |
| `vrc.get_installed_packages` | 查询 | VRChat 相关 SDK/插件版本探测（VRCSDK/MA/VRCFury/Poiyomi/DynamicBone/AAO 等） |
| `vrc.get_component_info` | 查询 | 指定组件（MA/VRCFury/PhysBone 等）完整序列化参数 |
| `vrc.set_component_property` | 写入 | 修改任意组件（MA/VRCFury 等）序列化字段（枚举用名称，资源引用用资产路径） |
| `vrc.backup_avatar` | 写入 | 把场景中唯一激活显示的主头像整体复制为隐藏备份（命名 `原名称(日期时分秒)`，忽略既有隐藏备份；0 个或 2 个及以上激活模型时返回报错） |
| `vrc.list_menus` | 查询 | 列出项目中的菜单（VRCExpressionsMenu）资产 —— 通用菜单 |
| `vrc.get_menu` | 查询 | 读取菜单结构（控件类型/参数/值/图标/子菜单/标签，支持递归）—— 通用菜单 |
| `vrc.create_menu` | 写入 | 新建菜单资产 —— 通用菜单 |
| `vrc.copy_menu` | 写入 | 复制菜单资产 —— 通用菜单 |
| `vrc.set_menu_control` | 写入 | 新增/修改/删除菜单控件（Button/Toggle/SubMenu/TwoAxisPuppet/FourAxisPuppet/RadialPuppet，含 labels 与 subParameters） |
| `vrc.bind_menu` | 写入 | 把菜单/参数资产绑定到头像描述符（场景对象与预制件均支持） |
| `vrc.list_parameters` | 查询 | 列出项目中的参数（VRCExpressionParameters）资产 —— 通用参数 |
| `vrc.get_parameters` | 查询 | 读取参数列表（名称/类型 Int·Float·Bool/默认值/是否保存）—— 通用参数 |
| `vrc.create_parameters` | 写入 | 新建参数资产 —— 通用参数 |
| `vrc.copy_parameters` | 写入 | 复制参数资产 —— 通用参数 |
| `vrc.set_parameter` | 写入 | 新增/修改/删除参数 |
| `vrc.ma_get_parameters` | 查询 | 读取 ModularAvatarParameters 组件全部参数 |
| `vrc.ma_set_parameter` | 写入 | 新增/修改/删除 MA 参数（syncType 按名称设置，非法值会列出该版本可选值） |

> 说明：VRC 的表情、衣柜、饰品切换等**所有菜单/参数都使用同一种资产类型**（`VRCExpressionsMenu` / `VRCExpressionParameters`），
> 因此 `vrc.*_menu` / `vrc.*_parameters` 系列是通用工具，可直接用于衣柜、饰品等菜单/参数。

### 扩展示例（example）

| 工具 | 类型 | 说明 |
| --- | --- | --- |
| `example.hello` | 查询 | 扩展示例（演示自定义工具注册，可删除 `ExampleExtensionTools.cs`） |

### 常用调用示例

```jsonc
// 读取头像报告
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{
  "name":"vrc.get_avatar_info",
  "arguments":{"target":"Assets/MyAvatar.prefab","includeStats":true}}}

// 改 MA 参数默认值（写入，只读模式会被拒绝）
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{
  "name":"vrc.set_component_property",
  "arguments":{"target":"Assets/MyAvatar.prefab","componentType":"ModularAvatarParameters",
               "propertyPath":"parameters.Array.data[0].defaultValue","value":1.0}}}

// 给菜单加一个开关（通用菜单：表情/衣柜/饰品切换均可）
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{
  "name":"vrc.set_menu_control",
  "arguments":{"menuPath":"Assets/Menus/Main.asset","action":"add",
               "control":{"name":"开关","type":"Toggle","parameter":"MyParam"}}}}

// 备份场景中正常显示的主头像（整体复制并隐藏，忽略既有隐藏备份）
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{
  "name":"vrc.backup_avatar",
  "arguments":{}}}

// 排查控制台报错
{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{
  "name":"unity.get_console_logs",
  "arguments":{"level":"Error","maxLines":50}}}
```

---

## 只读 / 读写权限模式

- 服务端在执行 **任何** 写入类工具前检查当前访问模式；只读模式下直接返回 `isError` 结果：

```json
{
  "content": [{"type":"text","text":"当前为【只读】模式，已拒绝写入类工具调用「vrc.set_parameter」。…"}],
  "isError": true,
  "structuredContent": {"error": {"code":"permission_denied","access":"write","mode":"readonly"}}
}
```

- Agent 端建议策略：调用 `tools/list` 或 `mcp.get_status` 获取每个工具的 `_meta.access`；对 `write` 类型工具先向用户确认，且无需在客户端重复实现拦截（服务端已兜底）。

---

## 客户端接入示例

### curl（JSON 模式）

```bash
# 握手
curl -s http://127.0.0.1:8765/mcp -H "Content-Type: application/json" -d \
'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","clientInfo":{"name":"curl","version":"1"}}}'

# 工具清单（注意每个工具 description 前缀的【查询】/【写入】与 _meta.access）
curl -s http://127.0.0.1:8765/mcp -H "Content-Type: application/json" -d \
'{"jsonrpc":"2.0","id":2,"method":"tools/list"}'

# 调用工具
curl -s http://127.0.0.1:8765/mcp -H "Content-Type: application/json" -d \
'{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"mcp.get_status"}}'
```

### curl（SSE 模式）

```bash
# Accept 带 text/event-stream 时响应为 SSE 事件流
curl -sN http://127.0.0.1:8765/mcp -H "Accept: text/event-stream" \
     -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

### MCP Inspector（浏览器）

打开 [MCP Inspector](https://modelcontextprotocol.io/docs/tools/inspector)，Transport 选 **Streamable HTTP**，URL 填 `http://127.0.0.1:8765/mcp`（本服务已内置 CORS 支持）。

### Claude Desktop / 其他仅支持 stdio 的客户端

用社区桥接工具把 HTTP MCP 转成 stdio（桥接工具运行在客户端侧，不影响本插件零依赖）：

```bash
npx mcp-remote http://127.0.0.1:8765/sse
```

或使用自研 Agent 直接以 HTTP 调用（`POST /mcp`，参考上文 JSON-RPC 流程）。

---

## 扩展指南

插件预留了三层扩展点，**无需修改插件源码**即可新增能力：

### 方式一：`[McpTool]` 属性标注（推荐）

在任意引用了 `VrchatProjectMcp.Core` 程序集的代码中定义公开静态方法并标注特性，插件启动或调用 `mcp.refresh_tools` 时自动扫描注册：

```csharp
using VrchatProjectMcp.Core.Json;
using VrchatProjectMcp.Core.Mcp;

public static class MyTools
{
    // access 必须标明：Query（查询）或 Write（写入，只读模式会被服务端拒绝）
    [McpTool("mytools.check_avatar", McpToolAccess.Query, "mytools", "检查头像…")]
    public static object Check([McpParam("头像路径")] string path = null)
    {
        return new JsonObject().Set("ok", true);
    }
}
```

### 方式二：`IMcpToolProvider` 接口

适合动态决定工具集合的场景（如"检测到某插件安装后才注册对应工具"）：

```csharp
public sealed class MyProvider : IMcpToolProvider
{
    public IEnumerable<McpToolDefinition> RegisterTools()
    {
        var def = new McpToolDefinition
        {
            Name = "mytools.dynamic",
            Access = McpToolAccess.Write,
            Category = "mytools",
            Description = "动态注册示例",
        };
        def.Parameters.Add(new McpParamDefinition { Name = "x", JsonType = "string", Required = true });
        def.Handler = args => new JsonObject().Set("done", true);
        yield return def;
    }
}
```

完整可运行示例见 `Editor/Tools/Examples/ExampleExtensionTools.cs`。

### 方式三：运行时注册 / 自定义资源 / 自定义 HTTP 端点

```csharp
// 运行时注册工具
McpToolRegistry.Instance.RegisterTool(myDefinition);

// 注册 MCP 资源（resources/list 可见，Agent 可 resources/read）
McpToolRegistry.Instance.Resources.Add(new McpResourceDefinition
{
    Uri = "mcp://my-report",
    Name = "我的报告",
    ReadHandler = () => new JsonObject().Set("data", 123),
});

// 自定义 HTTP 端点（需服务已启动）
McpServerController.Server?.AddHandler("GET", "/my-endpoint", ctx =>
{
    // ctx.BodyText 读取请求体；用 McpServerController.Server.WriteResponse(...) 写响应
});
```

> 扫描范围：只扫描"引用了 VrchatProjectMcp.Core 程序集"的程序集，不会遍历 Unity 全部类型，开销可控。

---

## 兼容性与已知限制

| 项 | 说明 |
| --- | --- |
| Unity 版本 | 2022.3（.NET Standard 2.1 / C# 9）与 Unity 6（全部代码按 C# 9 语法编写，并在本地用 LangVersion 9.0 编译验证） |
| 平台 | Windows / macOS / Linux 编辑器（HTTP 服务器使用 `TcpListener`，不依赖平台专属 API） |
| 播放模式 | 服务在播放模式同样可用；但播放模式中对场景的**写入会在退出播放模式后丢失**，请谨慎 |
| 编译依赖 | 对 VRCSDK3 / MA / VRCFury 零编译期依赖；未安装时相关工具返回明确错误（不影响插件本身使用） |
| 性能统计 | 优先反射调用 SDK `AvatarPerformanceStats`；SDK 缺失时按官方文档阈值估算，结果中明确标注「估算」 |
| 菜单/参数资产创建与编辑 | 需要项目安装 VRChat SDK3（这些资产类型由 SDK 定义）；SDK2 旧头像仅支持信息读取 |
| 预制件扫描 | `vrc.get_avatars` 的预制件扫描需逐个加载预制件，大项目可能较慢（可用 `limit` 与 `includePrefabAssets=false` 控制） |
| 弹窗类操作 | 工具执行带 120 秒主线程超时；涉及模态弹窗的操作可能超时（插件已避免在工具内弹窗） |
| 长驻资源 | SSE 长连接按需建立；域重载前服务自动停止并清理，防止残留端口占用 |

---

## 安全提示

1. **默认只监听 `127.0.0.1`**：仅本机进程可访问。改为 `0.0.0.0` 会把服务暴露给局域网内所有设备，请务必了解风险；
2. 本插件当前**不内置鉴权**（MCP 社区标准做法是由客户端侧代理统一鉴权）。如暴露到公网，请在反向代理层加认证；
3. 只读模式是最后一道保险，但仍建议 Agent 对写入类操作先获得用户确认；
4. `unity.read_text_asset` 仅允许读取 `Assets/`、`Packages/`、`ProjectSettings/` 下的文件，无法越界读系统文件。

---

## 项目结构

```
vrchat-project-mcp/
├── package.json                        # UPM 包清单（unity ≥ 2022.3，零依赖）
├── README.md                           # 本文档
├── LICENSE                             # MIT
├── Runtime/                            # 纯 C# 协议层（noEngineReferences，无 Unity 依赖）
│   ├── VrchatProjectMcp.Core.asmdef
│   ├── Mcp/
│   │   ├── Json/MiniJson.cs            #   内置 JSON 解析/序列化（零依赖）
│   │   ├── McpTypes.cs                 #   模式枚举/权限接口/资源定义/扩展接口
│   │   ├── McpToolAttribute.cs         #   [McpTool]/[McpParam] 特性（扩展方式二）
│   │   ├── McpToolDefinition.cs        #   工具定义 + inputSchema 生成 + 参数绑定
│   │   ├── McpToolRegistry.cs          #   扫描/注册/权限门控/调用执行
│   │   ├── JsonRpcCore.cs              #   JSON-RPC 2.0 分发（initialize/tools/resources）
│   │   └── IMcpLogger.cs               #   日志接口（宿主实现）
│   └── Net/
│       ├── SimpleHttpServer.cs         #   TcpListener 手写 HTTP/1.1 服务器（SSE/CORS/chunked）
│       └── McpHttpEndpoints.cs         #   /mcp /sse /message /health / 端点
├── Editor/                             # Unity 编辑器层
│   ├── VrchatProjectMcp.Editor.asmdef
│   ├── Core/
│   │   ├── McpMainThreadDispatcher.cs  #   主线程调度（HTTP 线程 → Unity 主线程）
│   │   └── McpServerController.cs      #   生命周期控制/组装/内置资源/菜单项
│   ├── Settings/
│   │   ├── McpSettings.cs              #   配置（EditorPrefs 持久化，按项目隔离）
│   │   └── McpSettingsWindow.cs        #   配置面板（地址/端口/权限/实时日志）
│   ├── Logging/
│   │   ├── McpEditorLogger.cs          #   日志器（窗口富文本 + Unity 控制台）
│   │   └── McpConsoleCapture.cs        #   控制台日志环形缓冲采集
│   └── Tools/
│       ├── ToolHelpers.cs              #   目标解析/序列化读写/预制件编辑等公共辅助
│       ├── McpMetaTools.cs             #   mcp.* 元工具
│       ├── UnityProjectTools.cs        #   unity.* 项目/包/资源/日志
│       ├── UnitySceneTools.cs          #   unity.* 场景/对象/组件/预制件
│       ├── UnityAssetTools.cs          #   unity.* 资产
│       ├── Vrc/
│       │   ├── VrcReflection.cs        #   VRChat SDK 类型反射（无编译期依赖）
│       │   ├── VrcCoreTools.cs         #   vrc.* 头像/性能/插件探测/组件读写
│       │   ├── VrcMenuTools.cs         #   vrc.* 表情菜单 新建/复制/编辑/绑定
│       │   ├── VrcParameterTools.cs    #   vrc.* 表情参数 新建/复制/编辑
│       │   └── VrcMaTools.cs           #   vrc.ma_* MA 参数
│       └── Examples/
│           └── ExampleExtensionTools.cs#   扩展示例（可删除）
└── DevTests~/                          # 开发期冒烟测试（目录名带 ~ 后缀，Unity 不会导入，非包内容）
    └── CoreSanity/                     #   Core 协议层 37 项端到端测试（dotnet 工程）
```

> `DevTests~` 使用 UPM 约定的 `~` 后缀命名，Unity 导入包时会完全忽略该目录；如需本地运行测试：`dotnet run --project DevTests~/CoreSanity/CoreSanity.csproj`。

---

## FAQ

**Q：为什么不用 `HttpListener` / WebSocket？**
Unity 2022/Unity 6 的 .NET Standard 2.1 API 级别下 `HttpListener` 不可用；WebSocket 需要第三方库。`TcpListener` + 手写 HTTP/1.1 是零依赖且跨版本最稳的方案。

**Q：插件会打进游戏包里吗？**
不会。核心逻辑在 Editor 程序集（`includePlatforms: ["Editor"]`），协议层虽在 Runtime 目录但仅被 Editor 引用，构建时不会进入玩家。

**Q：只读模式下为什么还要标注工具类型？**
类型标注服务于 Agent 决策（是否二次确认、是否尝试调用），服务端拦截是兜底保险，两者叠加更安全。

**Q：没装 VRChat SDK 能用吗？**
能。Unity 常规工具全部可用；VRChat 工具中「头像信息/组件读写/插件探测」尽力可用（按类型名反射），「菜单/参数资产创建与编辑」会返回明确提示。

**Q：如何排查 Agent 调用失败？**
看配置面板实时日志框（每次连接与调用都会打印），或让 Agent 调用 `unity.get_console_logs` 读取控制台与 Editor.log。

**Q：端口被占用怎么办？**
配置面板改端口后点「重启」；或填端口 `0` 自动分配（实际端口显示在状态栏）。

---

## 版本记录

- **0.1.0**（初始版本）：HTTP(JSON/SSE) MCP 服务、27 个 Unity 常规工具、19 个 VRChat 专用工具、2 个元工具、1 个扩展示例；只读/读写权限门控；配置面板与实时日志；扩展点；中文注释与文档。

## License

MIT（见 [LICENSE](./LICENSE)）。
