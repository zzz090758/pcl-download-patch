#requires -Version 5.0
<#
  自测：打补丁 / 幂等 / 备份 / 还原 / 负例
  需要一个未修改的 PCL 2.99.0.1 原版 exe 作为样本（仓库不附带）。
  默认从 .\orig\Plain Craft Launcher 2.exe 取，也可用 -Sample 指定。
#>
param([string]$Sample = '')

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here
$exe  = Join-Path $here 'PclPatch.exe'

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
function Run([string]$arg) {
    (Start-Process -FilePath $exe -ArgumentList $arg -Wait -PassThru -WindowStyle Hidden).ExitCode
}

$tmp = Join-Path $root '_t1'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Path $tmp | Out-Null
# 故意套一层子目录，验证"选上一层也能找到"
$inner = Join-Path $tmp 'PCL 正式版 2.99.0.1'
New-Item -ItemType Directory -Path $inner | Out-Null
Copy-Item $Sample (Join-Path $inner 'Plain Craft Launcher 2.exe')
$target = Join-Path $inner 'Plain Craft Launcher 2.exe'

$fail = 0
function Check([bool]$ok, [string]$msg) {
    if ($ok) { Write-Output "   [OK] $msg" } else { Write-Output "   [FAIL] $msg"; $script:fail++ }
}

Write-Output '1) 选中上层目录，补丁应生效'
$rc = Run "-auto `"$tmp`""
Check ($rc -eq 0 -and (Sha $target) -eq $SHA_PATCHED) "exit=$rc sha=$((Sha $target).Substring(0,12))..."

Write-Output '2) 幂等：再跑一次不应改动文件'
$rc = Run "-auto `"$tmp`""
Check ($rc -eq 0 -and (Sha $target) -eq $SHA_PATCHED) "exit=$rc"

Write-Output '3) 备份内容应为原版'
Check (Test-Path "$target.bak") '备份文件存在'
Check ((Sha "$target.bak") -eq $SHA_ORIG) '备份内容 = 原版'

Write-Output '4) 还原应回到原版'
$rc = Run "-restore `"$tmp`""
Check ($rc -eq 0 -and (Sha $target) -eq $SHA_ORIG) "exit=$rc sha=$((Sha $target).Substring(0,12))..."

Write-Output '5) 指向不存在的目录应报错'
$rc = Run "-auto `"$(Join-Path $root '__nope__')`""
Check ($rc -eq 2) "exit=$rc 期望 2"

Write-Output '6) 已有坏备份时应中止且不改动 exe'
$d = Join-Path $root '_t2'
if (Test-Path $d) { Remove-Item $d -Recurse -Force }
New-Item -ItemType Directory -Path $d | Out-Null
Copy-Item $Sample (Join-Path $d 'Plain Craft Launcher 2.exe')
[System.IO.File]::WriteAllBytes((Join-Path $d 'Plain Craft Launcher 2.exe.bak'), [byte[]](1..64))
$rc = Run "-auto `"$d`""
Check ($rc -eq 4 -and (Sha (Join-Path $d 'Plain Craft Launcher 2.exe')) -eq $SHA_ORIG) "exit=$rc 期望 4"

Write-Output '7) 只读属性下也能修补，并还原属性'
$ro = Join-Path $d 'Plain Craft Launcher 2.exe'
Remove-Item "$ro.bak" -Force
Set-ItemProperty $ro -Name IsReadOnly -Value $true
$rc = Run "-auto `"$d`""
Check ($rc -eq 0 -and (Sha $ro) -eq $SHA_PATCHED -and (Get-Item $ro).IsReadOnly) "exit=$rc 只读=$(Get-Item $ro).IsReadOnly"

Write-Output '8) exe 被删但备份在，应能还原'
Remove-Item $ro -Force
$rc = Run "-restore `"$d`""
Check ($rc -eq 0 -and (Test-Path $ro) -and (Sha $ro) -eq $SHA_ORIG) "exit=$rc"

Remove-Item $tmp, $d -Recurse -Force -ErrorAction SilentlyContinue
Write-Output ''
if ($fail -eq 0) { Write-Output 'ALL TESTS PASSED'; exit 0 }
Write-Output "FAILED: $fail"
exit 1
