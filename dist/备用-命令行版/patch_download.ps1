#requires -Version 5.0
<#
  PCL 2.99.0.1 下载功能恢复补丁
  ---------------------------------------------
  原版把两处功能做了隐藏/替换：
    1) PageDownloadLeft::.ctor        —— 左栏「原版游戏」「社区资源」设为 Collapsed
    2) PageDownloadInstall::LoaderInit —— 版本列表 loader 换成 new LoaderTask("关闭下载", 抛异常)
  本补丁仅改 43 个字节，把它们还原：
    1) 两处 ldc.i4.2 -> ldc.i4.0
    2) 假任务构造函数段 -> ldsfld ModDownload::DlClientListLoader
#>

param(
    [switch]$Restore,
    [switch]$Launch,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

$SHA_ORIG = '0db34e19d9eeb6d496d20fabe786ec16bbd36ba6b0ccdc8d29cb7e909c114397'
$SHA_PATCHED = '47faa87977bf936444b1318139addcf5924b3be5b22eb2f6df71d3161fd1e04d'
$EXE_NAME = 'Plain Craft Launcher 2.exe'

function Write-Title($msg) { Write-Host ''; Write-Host "== $msg" -ForegroundColor Cyan }
function Pause-Exit { if (-not $NoPause) { Read-Host '按回车退出' } }
function Write-Ok($msg) { Write-Host "   [OK] $msg" -ForegroundColor Green }
function Write-Err($msg) { Write-Host "   [X]  $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "        $msg" -ForegroundColor Gray }

function Get-Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLower() }

# 回滚。失败时如实报告，不谎称已恢复
function Restore-Backup([string]$exe, [string]$bak, [string]$reason) {
    if (-not (Test-Path -LiteralPath $bak)) {
        Write-Err "$reason，且找不到备份文件，无法自动还原。"
        Write-Info '请重新下载官方 PCL 安装包。'
        return $false
    }
    try {
        Copy-Item -LiteralPath $bak -Destination $exe -Force
        if ((Get-Sha $exe) -ne $SHA_ORIG) { throw '还原后内容不符' }
    } catch {
        Write-Err "$reason，自动还原也失败了：$($_.Exception.Message)"
        Write-Info "请手动把 $bak 改名为 $EXE_NAME。"
        return $false
    }
    Write-Ok '已还原为原版。'
    return $true
}

# 先写临时文件并落盘，复核通过后再替换目标。失败时原文件保持原样
function Write-FileAtomically([string]$exe, [byte[]]$data, [string]$expectSha, [string]$bak) {
    $tmp = "$exe.patching"
    $hadReadOnly = $false

    try {
        $fi = Get-Item -LiteralPath $exe -ErrorAction SilentlyContinue
        if ($fi -and $fi.IsReadOnly) { $fi.IsReadOnly = $false; $hadReadOnly = $true }

        $fs = [System.IO.File]::Create($tmp)
        try {
            $fs.Write($data, 0, $data.Length)
            $fs.Flush($true)                      # 真正落盘，不留在缓存里
        } finally { $fs.Dispose() }

        if ((Get-Sha $tmp) -ne $expectSha) {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            Write-Err '写入校验失败，原文件未被改动。'
            return $false
        }

        if (-not (Test-Path -LiteralPath $exe)) {
            Move-Item -LiteralPath $tmp -Destination $exe
        } else {
            try {
                # 第三个参数不能传 $null（PS 会转成空字符串，报“路径不合法”），
                # 传一个临时备份路径，替换完再删掉。
                $replaced = "$exe.replaced"
                [System.IO.File]::Replace($tmp, $exe, $replaced)
                Remove-Item -LiteralPath $replaced -Force -ErrorAction SilentlyContinue
            } catch [System.PlatformNotSupportedException] {
                Copy-Item -LiteralPath $tmp -Destination $exe -Force
                Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        Write-Err "写入失败：$($_.Exception.Message)"
        if ($_.Exception.Message -match '拒绝访问|denied|Access') {
            Write-Info '若 PCL 装在 Program Files 下，请右键本工具「以管理员身份运行」。'
        }
        Write-Info '原文件未被改动。'
        return $false
    } finally {
        if ($hadReadOnly) { try { (Get-Item -LiteralPath $exe).IsReadOnly = $true } catch { } }
    }

    try { $after = Get-Sha $exe }
    catch {
        [void](Restore-Backup $exe $bak "写入后无法读回文件（$($_.Exception.Message)）")
        return $false
    }

    if ($after -ne $expectSha) {
        [void](Restore-Backup $exe $bak '写入后复核不符')
        return $false
    }
    return $true
}

# ---------- 快速字节搜索（C# 实现，6MB 文件毫秒级） ----------
if (-not ('PclPatch.Scanner' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
public static class Scanner {
    // mask[i] == 0xFF 表示该字节必须相等；0x00 表示通配
    public static int Find(byte[] data, byte[] pat, byte[] mask) {
        int n = data.Length, m = pat.Length;
        if (m > n) return -1;
        int found = -1;
        for (int i = 0; i <= n - m; i++) {
            if (mask[0] != 0 && data[i] != pat[0]) continue;
            bool ok = true;
            for (int j = 1; j < m; j++) {
                if (mask[j] != 0 && data[i + j] != pat[j]) { ok = false; break; }
            }
            if (ok) { if (found >= 0) return -2; found = i; }
        }
        return found;
    }
}
'@
}

function New-Pattern([string]$hex) {
    # hex 形如 "02 6F ?? ?? ?? ?? 18"，?? 为通配
    $parts = $hex.Trim() -split '\s+'
    $pat = [byte[]]::new($parts.Count)
    $mask = [byte[]]::new($parts.Count)
    for ($i = 0; $i -lt $parts.Count; $i++) {
        if ($parts[$i] -eq '??') { $pat[$i] = 0; $mask[$i] = 0 }
        else { $pat[$i] = [Convert]::ToByte($parts[$i], 16); $mask[$i] = 0xFF }
    }
    return $pat, $mask
}

# 左栏取消隐藏：从 InitializeComponent 调用之后开始
$PAT_CTOR = @'
02 6F ?? ?? ?? ?? 18 6F ?? ?? ?? ?? 02 6F ?? ?? ?? ?? 18 6F ?? ?? ?? ??
02 18 7D ?? ?? ?? ?? 02 6F ?? ?? ?? ?? 17 6F ?? ?? ?? ?? 2A
'@

# LoaderInit 整个方法体（94 字节）
$PAT_LOADER = @'
02 28 ?? ?? ?? ?? 02 02 6F ?? ?? ?? ?? 02 6F ?? ?? ?? ?? 02 6F ?? ?? ?? ?? 14
72 ?? ?? ?? ?? 7E ?? ?? ?? ?? 2C ?? 7E ?? ?? ?? ?? 2B ?? 7E ?? ?? ?? ?? FE
06 ?? ?? ?? ?? 73 ?? ?? ?? ?? 25 80 ?? ?? ?? ?? 14 18 73 ?? ?? ?? ?? 02 FE
06 ?? ?? ?? ?? 73 ?? ?? ?? ?? 14 17 28 ?? ?? ?? ?? 2A
'@

# ---------- 定位目标 exe ----------
Write-Title 'PCL 2.99.0.1 下载功能恢复补丁'

$dir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

# 先看脚本所在目录，再看它的父目录（用户可能把工具放在 PCL 目录的子文件夹里）
$candidates = @(
    (Join-Path $dir $EXE_NAME),
    (Join-Path (Split-Path -Parent $dir) $EXE_NAME)
)
$exe = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $exe -and $Restore) {
    # 主程序可能已被删除或被安全软件隔离，只要备份还在就仍能救回来
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath "$c.bak") { $exe = $c; break }
    }
}

if (-not $exe) {
    Write-Err "没有找到 $EXE_NAME"
    Write-Info '请把本工具放到 PCL 所在文件夹（与 exe 同级）后再运行。'
    Write-Host ''
    Pause-Exit
    exit 1
}
Write-Ok "目标程序：$exe"

$bak = "$exe.bak"
$exeExists = Test-Path -LiteralPath $exe
$bytes = if ($exeExists) { [System.IO.File]::ReadAllBytes($exe) } else { $null }
$sha   = if ($exeExists) { Get-Sha $exe } else { '' }

Write-Title '校验版本'
Write-Info "SHA256 = $sha"

# ---------- 还原模式 ----------
if ($Restore) {
    if (-not (Test-Path -LiteralPath $bak)) {
        Write-Err "找不到备份文件 $EXE_NAME.bak，无法还原。"
        Pause-Exit
        exit 1
    }
    if ((Get-Sha $bak) -ne $SHA_ORIG) {
        Write-Err '备份文件不是 2.99.0.1 原版，已中止（未改动任何文件）。'
        Pause-Exit
        exit 1
    }
    if (Get-Process -Name 'Plain Craft Launcher 2' -ErrorAction SilentlyContinue) {
        Write-Err 'PCL 正在运行，请先关闭。'
        Pause-Exit
        exit 1
    }
    $bakBytes = [System.IO.File]::ReadAllBytes($bak)
    if (Write-FileAtomically $exe $bakBytes $SHA_ORIG $bak) {
        Write-Ok "已还原为原始版本：$exe"
    }
    Pause-Exit
    exit 0
}

if ($sha -eq $SHA_PATCHED) {
    Write-Ok '这个程序已经打过补丁了，无需重复操作。'
    if ($Launch) { Start-Process -FilePath $exe }
    Pause-Exit
    exit 0
}

if ($sha -ne $SHA_ORIG) {
    Write-Err '版本不匹配，本补丁只适用于 PCL 正式版 2.99.0.1 (415) 的原始 exe。'
    Write-Info '如果你更新过 PCL，请把新版本的 exe 发给我重新做。'
    Pause-Exit
    exit 1
}
Write-Ok '版本匹配：PCL 正式版 2.99.0.1'

if (Get-Process -Name 'Plain Craft Launcher 2' -ErrorAction SilentlyContinue) {
    Write-Err 'PCL 正在运行，请先完全退出（含托盘）再运行本补丁。'
    Pause-Exit
    exit 1
}

# ---------- 打补丁 ----------
Write-Title '写入补丁'

# 1) 左栏取消隐藏（2 字节）
$p = New-Pattern $PAT_CTOR
$hit = [Scanner]::Find($bytes, $p[0], $p[1])
if ($hit -eq -2) { Write-Err '左栏模式命中多处，已中止。'; Pause-Exit; exit 1 }
if ($hit -lt 0) { Write-Err '左栏模式未命中，exe 可能不是预期版本。'; Pause-Exit; exit 1 }
foreach ($rel in 6, 18) {
    if ($bytes[$hit + $rel] -ne 0x18) { Write-Err '左栏补丁校验失败。'; Pause-Exit; exit 1 }
    $bytes[$hit + $rel] = 0x16
}
Write-Ok ("左栏「原版游戏」+「社区资源」恢复显示  @0x{0:X}" -f ($hit + 6))

# 2) LoaderInit 换回真加载器（48 字节）
$p = New-Pattern $PAT_LOADER
$hit = [Scanner]::Find($bytes, $p[0], $p[1])
if ($hit -eq -2) { Write-Err '加载器模式命中多处，已中止。'; Pause-Exit; exit 1 }
if ($hit -lt 0) { Write-Err '加载器模式未命中，exe 可能不是预期版本。'; Pause-Exit; exit 1 }

if ($bytes[$hit + 0x1A] -ne 0x72) { Write-Err '加载器补丁校验失败：目标位置不是 ldstr。'; Pause-Exit; exit 1 }

$patched = New-Object byte[] 48
$patched[0] = 0x7E                       # ldsfld
$patched[1] = 0x0F; $patched[2] = 0x04
$patched[3] = 0x00; $patched[4] = 0x04   # token 0x0400040F = ModDownload::DlClientListLoader
for ($i = 5; $i -lt 48; $i++) { $patched[$i] = 0x00 }  # nop 填充
[Array]::Copy($patched, 0, $bytes, $hit + 0x1A, 48)
Write-Ok ("版本列表加载器换回 DlClientListLoader  @0x{0:X}" -f ($hit + 0x1A))

# ---------- 备份并写盘 ----------
Write-Title '保存'
if (Test-Path -LiteralPath $bak) {
    if ((Get-Sha $bak) -ne $SHA_ORIG) {
        Write-Err '同目录已有备份，但它不是 2.99.0.1 原版。'
        Write-Info '为避免还原时写回错误内容，已中止。请先删除该备份文件再试。'
        Pause-Exit
        exit 1
    }
    Write-Info '备份已存在且校验通过，保留最早的原始文件。'
} else {
    try {
        Copy-Item -LiteralPath $exe -Destination $bak -Force
        if ((Get-Sha $bak) -ne $SHA_ORIG) {
            Remove-Item -LiteralPath $bak -Force -ErrorAction SilentlyContinue
            throw '备份内容校验失败'
        }
        Write-Ok "已备份原文件 -> $EXE_NAME.bak"
    } catch {
        Write-Err "备份失败：$($_.Exception.Message)"
        Write-Info '未改动任何文件。'
        Pause-Exit
        exit 1
    }
}

if (-not (Write-FileAtomically $exe $bytes $SHA_PATCHED $bak)) {
    Pause-Exit
    exit 1
}
Write-Ok '补丁写入成功，校验通过。'

Write-Host ''
Write-Host '  完成！现在打开 PCL，进入「下载 -> 原版游戏」即可正常下载。' -ForegroundColor Green
Write-Host '  想还原原始版本，用同目录的 Restore.bat。' -ForegroundColor Gray

if ($Launch) { Start-Process -FilePath $exe }
Pause-Exit
