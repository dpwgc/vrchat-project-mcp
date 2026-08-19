# VRChat 工程助手 Agent（DSH Preset）

面向 **VRChat 模型（Avatar）/ Unity 工程开发** 的 DeepSeek Harness（DSH）Agent 预设。它在完整编码 Agent（`standard`）的基础上，新增一个 `@deepseek-ai/dsh-mcp-client` 桥接行，实时接入 Unity 编辑器内的 **VRChat Project MCP** 服务，把服务端约 49 个工具以 `mcp__vrchat__*` 命名空间暴露给模型。

## 文件说明

| 文件 | 作用 |
| --- | --- |
| `agent.cordis.yml` | Agent 平面组合（preset 主体）：人设 + MCP 桥接 + `standard` 全套工具行 |
| `preset.yml` | 预设元数据（在预设选择器中显示的名称与描述） |
| `README.md` | 本文档 |

## 前置条件

1. 已安装并启动 **DeepSeek Harness**（DSH）。
2. 目标 Unity 工程已按 [仓库 README](../../README.md) 安装 **VRChat Project MCP** 插件，并在 Unity 中 `Tools → VRChat Project MCP → 配置面板 → 启动服务器`（默认监听 `127.0.0.1:8765`）。

## 安装

DSH 的本地预设目录为 `${DSH_HOME:-$HOME/.dsh}/.agent-presets/<id>/`。把本目录复制进去即可（`<id>` 使用合法 id：`[a-z0-9][a-z0-9-]*`，不能以连字符开头）：

```bash
cp -R Agent/DSH/vrchat-project-mode ~/.dsh/.agent-presets/vrchat-project-mode
```

> 也可以在 DSH 的预设作者界面（Cordis）用 `copy(from, id, name)` 复制，或直接新建一个预设并把本目录的三个文件放进去。

安装后重启 DSH（或重新打开预设选择器），在预设列表中选择 **VRChat 工程助手** 开新会话。

## 使用

- 模型会看到 `mcp__vrchat__*` 命名空间下的工具，例如：
  - `mcp__vrchat__mcp_get_status` —— 服务状态 / 权限模式 / 工具清单
  - `mcp__vrchat__unity_get_console_logs` —— 控制台日志排查
  - `mcp__vrchat__vrc_get_avatar_info` —— 头像完整报告
  - `mcp__vrchat__vrc_set_component_property` / `mcp__vrchat__vrc_set_parameter` 等 —— 写入类工具
- 人设内已写入权限约定：**写入类工具在调用前会先向用户确认**，只读模式下服务端会直接拒绝。
- 若会话开始时 Unity 尚未启动 MCP 服务器，预设仍能正常开启（`failOnStartupError: false`），工具会在服务上线后自动同步出现；服务离线期间工具调用会失败并提示启动服务器。

## 调整

- 改端口 / 地址：编辑 `agent.cordis.yml` 中 `mcp-vrchat` 行的 `url`。
- 改工具命名空间：编辑该行的 `serverName`（`[A-Za-z0-9_-]{1,32}`，同一进程内需唯一）。
- 工具超时默认 `130000ms`（服务端单工具主线程超时为 120s），可按需调整 `toolCallTimeoutMs`。

## 参考

- MCP 客户端插件：`@deepseek-ai/dsh-mcp-client`（`transport: streamable-http`）
- 服务端协议与工具清单：[README.md](../../README.md)
