# PCL 下载功能恢复补丁

为 **Plain Craft Launcher 2.99.0.1** 恢复「原版游戏」下载入口的二进制补丁工具。

官方在 2.99.0.1 里把原版下载功能关掉了，实现方式是改代码，不是删文件。这个工具用 41 个字节把它改回来。

> 仅供学习交流使用。请支持 PCL 原作者。本仓库不包含 PCL 本体的任何文件。

## 原理

2.99.0.1 藏了两层：

| 层 | 位置 | 手法 |
|---|---|---|
| 界面 | `PageDownloadLeft::.ctor` | 「原版游戏」项和「社区资源」分组被设为 `Visibility.Collapsed` |
| 数据 | `PageDownloadInstall::LoaderInit` | 真的 `DlClientListLoader` 换成了一个立刻抛异常的假任务（提示「为遵守相关法律法规……」） |

补丁做两件事：

1. 把两处 `ldc.i4.2`（`0x18`）改成 `ldc.i4.0`（`0x16`），可见性 `Collapsed` → `Visible`；
2. 把 `LoaderInit` 里从 `ldstr "关闭下载"` 开始的 48 字节，替换为 `ldsfld` 真加载器 + `nop` 填充。

第二处是**等长替换**（吃掉 48 字节、写入 48 字节），所以文件大小、后续 IL 偏移、分支目标全部不变。

## 用法

### 图形版（推荐）

双击 `PCL下载补丁.exe` → 选 PCL 所在文件夹 → 点「下一步」。

- 放在 PCL 目录里启动会自动带出路径；也可以把文件夹直接拖进窗口
- 可以选上一层目录（比如 `D:\Games\PCL` 的父目录），会自动往下找两层
- 自带「还原原版」按钮

### 命令行版

把 `patch_download.ps1` 放到 PCL 目录后运行：

```powershell
# 打补丁
.\patch_download.ps1

# 还原
.\patch_download.ps1 -Restore
```

`ApplyPatch.bat` / `Restore.bat` 是它的双击包装。

## 构建

只需要系统自带的 .NET Framework 4.x 编译器，不用装 SDK：

```powershell
.\src\build.ps1
```

产物是 `src\PclPatch.exe`。若 `src\pcl.ico` 存在会一并嵌入为程序图标（仓库不附带，因为这个图标属于 PCL）。

## 自测

测试需要一份**未修改的 2.99.0.1 原版 exe** 作为样本，仓库不附带：

```powershell
.\src\test.ps1    -Sample "D:\path\to\Plain Craft Launcher 2.exe"
.\src\test_ps.ps1 -Sample "D:\path\to\Plain Craft Launcher 2.exe"
```

覆盖：补丁生效、幂等、备份内容、还原、错误路径、坏备份拒绝、只读属性、exe 丢失后从备份恢复。

## 安全设计

补丁要改写用户 6MB 的可执行文件，所以这些保护是必要的：

- **SHA256 白名单**：只接受唯一的原版 2.99.0.1，其他版本一律拒绝，不靠猜
  - 原版：`0db34e19d9eeb6d496d20fabe786ec16bbd36ba6b0ccdc8d29cb7e909c114397`
  - 打完后必须是：`47faa87977bf936444b1318139addcf5924b3be5b22eb2f6df71d3161fd1e04d`
- **三重校验**：选目录时预检（仅用于界面显示）→ 写入前重新读取文件再校验（不信任预检结果）→ 写入后从磁盘读回复核
- **原子写入**：先写 `*.patching` 临时文件并 `Flush(true)` 落盘，复核通过后才用 `File.Replace` 原子替换。任何一步失败，原文件保持不变
- **可信备份**：`.bak` 生成后立即校验是否为原版；已存在的 `.bak` 也要校验，避免把旧版本当成原版保存
- **回滚如实报告**：回滚失败会明确告诉你「自动回滚也失败了」并给出备份路径，不会谎称已恢复
- **还原前校验**：确认备份确实是原版才写回，并复核结果
- **只读属性**：自动临时取消，完成后恢复
- **单实例**：Mutex 防止两个进程同时改写同一个文件

## 注意

- PCL 会自动更新。一旦它把 exe 换成新版本，补丁就失效，需要重新跑一次（版本变了脚本会拒绝，那时需要重新做偏移）
- 补丁只绕过客户端的前端限制，**不改任何网络请求**。如果你的系统代理没开，下载照样会失败，那是环境问题

## 文件结构

```
src/
  PatchGui.cs     C# 单文件程序（补丁逻辑 + WinForms 界面）
  build.ps1       用 csc 构建
  test.ps1        程序自测
  test_ps.ps1     命令行版自测
dist/
  备用-命令行版/
    patch_download.ps1   PowerShell 命令行版
    ApplyPatch.bat       双击打补丁
    Restore.bat          双击还原
```
