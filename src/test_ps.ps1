#requires -Version 5.0
<#
  PowerShell 命令行版的自测：正向流程 + 3 个负例
  需要一个未修改的 PCL 2.99.0.1 原版 exe 作为样本（仓库不附带）。
#>
param([string]$Sample = '')

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here
$ps1  = Join-Path $root 'dist\备用-命令行版\patch_download.ps1'

if (-not $Sample) { $Sample = Join-Path $root 'orig\Plain Craft Launcher 2.exe' }
if (-not (Test-Path $Sample)) {
    Write-Output "缺少样本文件：$Sample"
    Write-Output '请用 -Sample <未修改的 2.99.0.1 exe 路径> 运行。'
    exit 1
}

$SHA_ORIG    = '0db34e19d9eeb6d496d20fabe786ec16bbd36ba6b0ccdc8d29cb7e909c114397'
$SHA_PATCHED = '47faa87977bf936444b1318139addcf5924b3be5b22eb2f6df71d3161fd1e04d'

if ((Get-FileHash $Sample -Algorithm SHA256).Hash.ToLower() -ne $SHA_ORIG) {
    Write-Output "样本 SHA 不是 2.99.0.1 原版：$Sample"
    exit 1
}

function Sha($f) { (Get-FileHash $f -Algorithm SHA256).Hash.ToLower() }
function New-Case([string]$name) {
    $d = Join-Path $root $name
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory $d | Out-Null
    Copy-Item $Sample (Join-Path $d 'Plain Craft Launcher 2.exe')
    Copy-Item $ps1    (Join-Path $d 'patch_download.ps1')
    return $d
}
function RunPs([string]$d, [string[]]$extra) {
    $a = @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $d 'patch_download.ps1'),'-NoPause') + $extra
    (Start-Process powershell -ArgumentList $a -Wait -PassThru -WindowStyle Hidden).ExitCode
}

$fail = 0
function Check([bool]$ok, [string]$msg) {
    if ($ok) { Write-Output "   [OK] $msg" } else { Write-Output "   [FAIL] $msg"; $script:fail++ }
}

$cases = @('_ps1','_ps2','_ps3','_ps4')

Write-Output '=== PS 版：正向流程 ==='
$d  = New-Case '_ps1'
$rc = RunPs $d @()
Check ($rc -eq 0 -and (Sha "$d\Plain Craft Launcher 2.exe") -eq $SHA_PATCHED) "打补丁 exit=$rc"
Check ((Sha "$d\Plain Craft Launcher 2.exe.bak") -eq $SHA_ORIG) '备份 = 原版'

$rc = RunPs $d @('-Restore')
Check ($rc -eq 0 -and (Sha "$d\Plain Craft Launcher 2.exe") -eq $SHA_ORIG) "还原 exit=$rc"

Write-Output '=== 负例 1：备份被篡改，应拒绝还原且不动 exe ==='
$d = New-Case '_ps2'
$null = RunPs $d @()
[System.IO.File]::WriteAllBytes("$d\Plain Craft Launcher 2.exe.bak", [byte[]](1..64))
$before = Sha "$d\Plain Craft Launcher 2.exe"
$rc = RunPs $d @('-Restore')
Check ($rc -eq 1 -and (Sha "$d\Plain Craft Launcher 2.exe") -eq $before) "exit=$rc 期望 1"

Write-Output '=== 负例 2：无备份时还原应失败且不动文件 ==='
$d = New-Case '_ps3'
$before = Sha "$d\Plain Craft Launcher 2.exe"
$rc = RunPs $d @('-Restore')
Check ($rc -eq 1 -and (Sha "$d\Plain Craft Launcher 2.exe") -eq $before) "exit=$rc 期望 1"

Write-Output '=== 负例 3：exe 被删但备份在，应能还原 ==='
$d = New-Case '_ps4'
$null = RunPs $d @()
Remove-Item "$d\Plain Craft Launcher 2.exe" -Force
$rc = RunPs $d @('-Restore')
$h = if (Test-Path "$d\Plain Craft Launcher 2.exe") { Sha "$d\Plain Craft Launcher 2.exe" } else { 'missing' }
Check ($rc -eq 0 -and $h -eq $SHA_ORIG) "exit=$rc"

Remove-Item ($cases | ForEach-Object { Join-Path $root $_ }) -Recurse -Force -ErrorAction SilentlyContinue
Write-Output ''
if ($fail -eq 0) { Write-Output 'PS TESTS PASSED'; exit 0 }
Write-Output "FAILED: $fail"
exit 1
