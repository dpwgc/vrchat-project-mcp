# VRChat 工程助手（DSH 预设）安装 / 卸载脚本
# 用法：
#   .\install.ps1              安装（覆盖）
#   .\install.ps1 --uninstall  卸载

$ErrorActionPreference = "Stop"

$PresetId = "vrchat-project-mode"
$ScriptDir = $PSScriptRoot
$SrcDir = Join-Path $ScriptDir $PresetId
$DshHome = if ($env:DSH_HOME) { $env:DSH_HOME } else { Join-Path $HOME ".dsh" }
$DestDir = Join-Path $DshHome ".agent-presets\$PresetId"

if ($args.Count -gt 0 -and $args[0] -eq "--uninstall") {
  if (Test-Path $DestDir) {
    Remove-Item -Recurse -Force $DestDir
    Write-Host "已卸载：$DestDir"
  } else {
    Write-Host "未安装（目录不存在）：$DestDir"
  }
  exit 0
}

if (-not (Test-Path (Join-Path $SrcDir "agent.cordis.yml"))) {
  Write-Error "未找到预设目录 $SrcDir（缺少 agent.cordis.yml）"
  exit 1
}

New-Item -ItemType Directory -Force -Path (Split-Path $DestDir) | Out-Null
if (Test-Path $DestDir) {
  Remove-Item -Recurse -Force $DestDir
}
Copy-Item -Recurse -Force $SrcDir $DestDir

Write-Host "已安装预设「$PresetId」→ $DestDir"
Write-Host "重启 DSH（或重新打开预设选择器），在列表中选择「VRChat 工程助手」开新会话。"
