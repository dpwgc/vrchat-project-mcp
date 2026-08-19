#!/usr/bin/env bash
set -euo pipefail

# VRChat 工程助手（DSH 预设）安装 / 卸载脚本
# 用法：
#   ./install.sh              安装（覆盖）
#   ./install.sh --uninstall  卸载

PRESET_ID="vrchat-project-mode"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="${SCRIPT_DIR}/${PRESET_ID}"
DSH_HOME="${DSH_HOME:-$HOME/.dsh}"
DEST_DIR="${DSH_HOME}/.agent-presets/${PRESET_ID}"

if [[ "${1:-}" == "--uninstall" ]]; then
  if [[ -e "${DEST_DIR}" ]]; then
    rm -rf "${DEST_DIR}"
    echo "已卸载：${DEST_DIR}"
  else
    echo "未安装（目录不存在）：${DEST_DIR}"
  fi
  exit 0
fi

if [[ ! -f "${SRC_DIR}/agent.cordis.yml" ]]; then
  echo "错误：未找到预设目录 ${SRC_DIR}（缺少 agent.cordis.yml）" >&2
  exit 1
fi

mkdir -p "$(dirname "${DEST_DIR}")"
rm -rf "${DEST_DIR}"
cp -R "${SRC_DIR}" "${DEST_DIR}"

echo "已安装预设「${PRESET_ID}」→ ${DEST_DIR}"
echo "重启 DSH（或重新打开预设选择器），在列表中选择「VRChat 工程助手」开新会话。"
