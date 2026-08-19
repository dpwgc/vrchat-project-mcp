# VRChat 工程助手（DSH Agent Preset）

面向 **VRChat 模型（Avatar）/ Unity 工程开发** 的 [DeepSeek Harness（DSH）](https://github.com/deepseek-ai) Agent 预设。

它在完整编码 Agent（`standard`）的基础上，新增一个 `@deepseek-ai/dsh-mcp-client` 桥接行，实时接入 Unity 编辑器内的 **VRChat Project MCP** 服务，把服务端约 49 个工具以 `mcp__vrchat__*` 命名空间暴露给模型，同时保留完整的编码、文件、检索与后台任务能力。

> 本项目是一个**可独立发布的 DSH 预设仓库**：克隆后运行安装脚本，或手动复制 `vrchat-project-mode/` 目录，即可在 DSH 预设列表中使用。

---

## 目录

1. [项目结构](#项目结构)
2. [前置条件](#前置条件)
3. [安装](#安装)
4. [使用](#使用)
5. [配置](#配置)
6. [工具家族](#工具家族)
7. [权限约定](#权限约定)
8. [常见问题](#常见问题)
9. [License](#license)

---

## 项目结构

```
dsh-vrchat-assistant/            # 本仓库根目录（可独立发布）
├── README.md                    # 本文档
├── LICENSE                      # MIT
├── CHANGELOG.md                 # 版本记录
├── package.json                 # 仓库清单（元数据；本仓库是「预设」而非 Cordis 插件包）
├── install.sh                   # 一键安装 / 卸载（macOS · Linux）
├── install.ps1                  # 一键安装 / 卸载（Windows PowerShell）
├── .gitignore                   # 独立仓库忽略规则
└── vrchat-project-mode/         # 预设本体（安装单元，id = vrchat-project-mode）
    ├── agent.cordis.yml         # Agent 平面组合：人设 + MCP 桥接 + standard 全套工具行
    └── preset.yml               # 预设元数据（选择器中的名称与描述）
```

> **预设 vs. Cordis 插件包**：本仓库是一个 **DSH Agent 预设**——它用 `agent.cordis.yml` 组合现成插件，本身不含可执行代码，因此真正的“安装单元”只有 `vrchat-project-mode/` 下的两个 YAML 文件。你看到的 `package.json` + `lib/index.js` 属于 **Cordis 插件包**（如 `@deepseek-ai/dsh-mcp-client`、`@deepseek-ai/dsh-tool-bash`），它们才是被组合的底层插件。这里的 `package.json` 仅作仓库元数据与安装脚本入口（`npm run install:preset`），不是 Cordis 插件包。

`vrchat-project-mode/` 就是 DSH 的“预设目录”，目录名即预设 id。安装脚本做的事情，就是把它复制到本机的预设根目录。

---

## 前置条件

1. 已安装并启动 **DeepSeek Harness（DSH）**。
2. 目标 Unity 工程已安装 **VRChat Project MCP** 插件（服务端），并在 Unity 中
   `Tools → VRChat Project MCP → 配置面板 → 启动服务器`（默认监听 `127.0.0.1:8765`）。

> 服务端插件另见「VRChat Project MCP」仓库（Unity 侧 UPM 包，零依赖纯 C#）。本仓库只负责把它的 HTTP MCP 服务桥接进 DSH。

---

## 安装

DSH 的本地预设目录为 `${DSH_HOME:-$HOME/.dsh}/.agent-presets/<id>/`（`<id>` 需符合 `[a-z0-9][a-z0-9-]*`，不能以连字符开头）。

### 方式一：安装脚本（推荐）

macOS / Linux：

```bash
./install.sh
```

Windows PowerShell：

```powershell
.\install.ps1
```

脚本会把 `vrchat-project-mode/` 复制到 `${DSH_HOME:-$HOME/.dsh}/.agent-presets/vrchat-project-mode`（已存在则覆盖）。

### 方式二：手动复制

```bash
cp -R vrchat-project-mode ~/.dsh/.agent-presets/vrchat-project-mode
```

### 方式三：DSH 预设作者界面

在 DSH 的预设作者界面（Cordis）用 `copy(from, id, name)` 复制，或新建预设并把 `vrchat-project-mode/` 下的两个文件放进去。

安装后重启 DSH（或重新打开预设选择器），在预设列表中选择 **VRChat 工程助手** 开新会话。

### 卸载

```bash
./install.sh --uninstall   # macOS / Linux
.\install.ps1 --uninstall  # Windows PowerShell
```

---

## 使用

- 模型会看到 `mcp__vrchat__*` 命名空间下的工具，例如：
  - `mcp__vrchat__mcp_get_status` —— 服务状态 / 权限模式 / 工具清单
  - `mcp__vrchat__unity_get_console_logs` —— 控制台日志排查
  - `mcp__vrchat__vrc_get_avatar_info` —— 头像完整报告
  - `mcp__vrchat__vrc_set_component_property` / `mcp__vrchat__vrc_set_parameter` 等 —— 写入类工具
- 人设内已写入权限约定：**写入类工具在调用前会先向用户确认**，只读模式下服务端会直接拒绝。
- 若会话开始时 Unity 尚未启动 MCP 服务器，预设仍能正常开启（`failOnStartupError: false`），工具会在服务上线后自动同步出现；服务离线期间工具调用会失败并提示启动服务器。

---

## 配置

编辑 `vrchat-project-mode/agent.cordis.yml` 中 `mcp-vrchat` 行：

| 配置项 | 说明 |
| --- | --- |
| `url` | MCP 服务地址，默认 `http://127.0.0.1:8765/mcp` |
| `serverName` | 工具命名空间前缀，默认 `vrchat`（`[A-Za-z0-9_-]{1,32}`，同一进程内需唯一） |
| `toolCallTimeoutMs` | 工具超时，默认 `130000`（服务端单工具主线程超时为 120s） |
| `failOnStartupError` | 服务离线时是否阻止会话启动，默认 `false` |
| `reconnect.enabled` | 服务上线后自动重连，默认 `true` |

---

## 工具家族

| 前缀 | 说明 |
| --- | --- |
| `mcp__vrchat__mcp_*` | 服务状态、访问模式、工具清单、工具刷新 |
| `mcp__vrchat__unity_*` | 常规 Unity：项目/包/资源/控制台日志、场景/对象/组件、资产、预制件、选中 |
| `mcp__vrchat__vrc_*` | VRChat 专用：头像报告/性能/已装插件、组件读写、表情菜单与参数、MA 参数 |

完整工具清单与服务端协议见「VRChat Project MCP」服务端仓库的 README。

---

## 权限约定

每个 MCP 工具都标注 `query`（只读）或 `write`（会修改场景/资产/项目，只读模式下被服务端拒绝）。写入类工具（名称含 `_set_` / `_create_` / `_delete_` / `_copy_` / `_bind_` / `_instantiate_` / `_destroy_` / `_open_scene` / `_save_scene` / `_run_menu_item` / `_refresh_assets`）调用前，人设会先向用户确认改动内容；不确定时先调 `mcp__vrchat__mcp_get_status` 读取当前访问模式与工具清单。

---

## 常见问题

**Q：会话里看不到 `mcp__vrchat__*` 工具？**
确认 Unity 中 MCP 服务器已启动（`Tools → VRChat Project MCP → 配置面板 → 启动服务器`），且 `agent.cordis.yml` 中 `url` 的端口与面板一致。服务离线时预设仍能开启，工具会在服务上线后自动出现。

**Q：写入类工具报 `permission_denied`？**
配置面板把操作权限切到了「只读」。改回「读写」，或只使用查询类工具。

**Q：想改端口 / 地址 / 命名空间？**
见 [配置](#配置)，改 `mcp-vrchat` 行的对应字段后重启 DSH 会话。

---

## License

MIT（见 [LICENSE](./LICENSE)）。
