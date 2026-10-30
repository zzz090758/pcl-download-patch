#requires -Version 5.0
# 用系统自带的 C# 编译器构建 PclPatch.exe（无需安装 Visual Studio / SDK）
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$src  = Join-Path $root 'PatchGui.cs'
$out  = Join-Path $root 'PclPatch.exe'
$ico  = Join-Path $root 'pcl.ico'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    Write-Error '找不到 csc.exe（需要 .NET Framework 4.x）'
    exit 1
}

if (Test-Path $out) { Remove-Item $out -Force }

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/codepage:65001'
    '/r:System.dll'
    '/r:System.Windows.Forms.dll'
    '/r:System.Drawing.dll'
    "/out:$out"
)

# 图标是可选的：仓库不附带 PCL 的图标资源
if (Test-Path $ico) { $cscArgs += "/win32icon:$ico" }
$cscArgs += $src

& $csc @cscArgs

if ($LASTEXITCODE -ne 0) { Write-Output "BUILD FAILED ($LASTEXITCODE)"; exit 1 }
if (-not (Test-Path $out)) { Write-Output 'NO OUTPUT'; exit 1 }
Write-Output ("BUILD OK  " + $out + "  " + (Get-Item $out).Length + " bytes")
