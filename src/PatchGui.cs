using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;

[assembly: AssemblyTitle("PCL 下载功能恢复补丁")]
[assembly: AssemblyDescription("为 Plain Craft Launcher 2.99.0.1 恢复原版游戏下载")]
[assembly: AssemblyProduct("PCL Download Patch")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace PclPatch
{
    internal enum PatchState
    {
        None,       // 尚未选择
        NotFound,   // 没找到 exe
        Original,   // 原版 2.99.0.1，可修补
        Patched,    // 已打过补丁
        Mismatch,   // 找到了但版本不对 / 文件已损坏
        Running,    // 原版，但 PCL 正在运行
        NoAccess    // 读不了：被占用或没有权限
    }

    internal static class Patcher
    {
        public const string ExeName    = "Plain Craft Launcher 2.exe";
        public const string ShaOrig    = "0db34e19d9eeb6d496d20fabe786ec16bbd36ba6b0ccdc8d29cb7e909c114397";
        public const string ShaPatched = "47faa87977bf936444b1318139addcf5924b3be5b22eb2f6df71d3161fd1e04d";

        // PageDownloadLeft::.ctor —— 里面两处 ldc.i4.2(0x18) 把「原版游戏」藏了起来
        private const string HexCtor =
            "02 6F ?? ?? ?? ?? 18 6F ?? ?? ?? ?? 02 6F ?? ?? ?? ?? 18 6F ?? ?? ?? ?? " +
            "02 18 7D ?? ?? ?? ?? 02 6F ?? ?? ?? ?? 17 6F ?? ?? ?? ?? 2A";

        // PageDownloadInstall::LoaderInit —— 伪造 "关闭下载" 任务替换真正的 DlClientListLoader
        private const string HexLoader =
            "02 28 ?? ?? ?? ?? 02 02 6F ?? ?? ?? ?? 02 6F ?? ?? ?? ?? 02 6F ?? ?? ?? ?? 14 " +
            "72 ?? ?? ?? ?? 7E ?? ?? ?? ?? 2C ?? 7E ?? ?? ?? ?? 2B ?? 7E ?? ?? ?? ?? FE " +
            "06 ?? ?? ?? ?? 73 ?? ?? ?? ?? 25 80 ?? ?? ?? ?? 14 18 73 ?? ?? ?? ?? 02 FE " +
            "06 ?? ?? ?? ?? 73 ?? ?? ?? ?? 14 17 28 ?? ?? ?? ?? 2A";

        private static readonly byte[] CtorPat, CtorMask, LoadPat, LoadMask;

        // DlClientListLoader 字段 token：0x0400040F
        private static readonly byte[] RealLoaderRef = new byte[] { 0x7E, 0x0F, 0x04, 0x00, 0x04 };

        static Patcher()
        {
            Build(HexCtor,   out CtorPat, out CtorMask);
            Build(HexLoader, out LoadPat, out LoadMask);
        }

        private static void Build(string hex, out byte[] pat, out byte[] mask)
        {
            string[] parts = hex.Split(new char[] { ' ', '\r', '\n', '\t' },
                                       StringSplitOptions.RemoveEmptyEntries);
            pat  = new byte[parts.Length];
            mask = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "??") { pat[i] = 0; mask[i] = 0; }
                else { pat[i] = Convert.ToByte(parts[i], 16); mask[i] = 1; }
            }
        }

        public static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>在字节流里找唯一匹配，返回起始偏移；未找到 -1；多处命中 -2</summary>
        private static int Find(byte[] data, byte[] pat, byte[] mask)
        {
            int n = data.Length, m = pat.Length, found = -1;
            for (int i = 0; i <= n - m; i++)
            {
                if (mask[0] != 0 && data[i] != pat[0]) continue;
                bool ok = true;
                for (int j = 1; j < m; j++)
                {
                    if (mask[j] != 0 && data[i + j] != pat[j]) { ok = false; break; }
                }
                if (ok)
                {
                    if (found >= 0) return -2;
                    found = i;
                }
            }
            return found;
        }

        /// <summary>在目录（及其两层子目录）里找指定文件名</summary>
        public static string FindFile(string dir, string name)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

            string p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;

            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    p = Path.Combine(sub, name);
                    if (File.Exists(p)) return p;
                }
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    foreach (string sub2 in Directory.GetDirectories(sub))
                    {
                        p = Path.Combine(sub2, name);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>在目录（及其两层子目录）里找 PCL 主程序</summary>
        public static string FindExe(string dir) { return FindFile(dir, ExeName); }

        public static bool IsPclRunning()
        {
            try
            {
                foreach (Process pr in Process.GetProcessesByName(
                             Path.GetFileNameWithoutExtension(ExeName)))
                {
                    using (pr)
                    {
                        if (!pr.HasExited) return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>读取并判断目标 exe 当前状态</summary>
        public static PatchState Detect(string exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return PatchState.NotFound;

            string sha;
            try { sha = Sha256(File.ReadAllBytes(exePath)); }
            catch (UnauthorizedAccessException) { return PatchState.NoAccess; }
            catch (IOException)                 { return PatchState.NoAccess; }
            catch (Exception)                   { return PatchState.Mismatch; }

            if (sha == ShaPatched) return PatchState.Patched;
            if (sha != ShaOrig)    return PatchState.Mismatch;
            return IsPclRunning() ? PatchState.Running : PatchState.Original;
        }

        public static string BakPath(string exePath) { return exePath + ".bak"; }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        /// <summary>
        /// 确保 .bak 是一份可信的原版副本。返回 null 表示可用。
        /// 已有备份会先校验内容，避免拿旧版本的备份当过用。
        /// </summary>
        private static string EnsureBackup(string exePath, string bak)
        {
            if (File.Exists(bak))
            {
                try
                {
                    if (Sha256(File.ReadAllBytes(bak)) == ShaOrig) return null;
                }
                catch (Exception ex) { return "读取已有备份失败：" + ex.Message; }

                return "同目录已有备份（" + Path.GetFileName(bak) + "），但它不是 2.99.0.1 原版。\n" +
                       "为避免还原时写回错误内容，已中止。请先删除这个备份文件再试。";
            }

            try
            {
                File.Copy(exePath, bak);
                if (Sha256(File.ReadAllBytes(bak)) != ShaOrig)
                {
                    TryDelete(bak);
                    return "备份校验失败，已中止（未改动 PCL）。请检查磁盘空间或安全软件。";
                }
            }
            catch (Exception ex) { return "备份失败：" + ex.Message; }

            return null;
        }

        /// <summary>回滚。回滚失败如实报告，不谎称已恢复。</summary>
        private static string Rollback(string exePath, string bak, string reason)
        {
            if (!File.Exists(bak))
                return reason + "，且找不到备份文件，无法自动回滚。\n请重新下载官方 PCL 安装包。";

            try
            {
                File.Copy(bak, exePath, true);
                if (Sha256(File.ReadAllBytes(exePath)) != ShaOrig)
                    throw new IOException("回滚后内容不符");
            }
            catch (Exception ex)
            {
                return reason + "，自动回滚也失败了：" + ex.Message +
                       "\n请手动把\n" + bak + "\n改名为 " + Path.GetFileName(exePath) + "。";
            }
            return reason + "，已自动回滚为原版。";
        }

        /// <summary>
        /// 先写临时文件并强制落盘，复核通过后再原子替换目标。
        /// 任何一步失败，原文件保持原样；替换后复核不符才回滚。
        /// </summary>
        private static string WriteAtomically(string exePath, byte[] data, string expectSha, string bak)
        {
            string tmp = exePath + ".patching";
            bool hadReadOnly = false;

            try
            {
                FileInfo fi = new FileInfo(exePath);
                if (fi.Exists && fi.IsReadOnly)
                {
                    fi.IsReadOnly = false;
                    hadReadOnly = true;
                }

                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);                  // 真正落盘，不留在缓存里
                }

                if (Sha256(File.ReadAllBytes(tmp)) != expectSha)
                {
                    TryDelete(tmp);
                    return "写入校验失败，原文件未被改动。请确认 PCL 版本为 2.99.0.1。";
                }

                if (!File.Exists(exePath))
                {
                    File.Move(tmp, exePath);
                }
                else
                {
                    try { File.Replace(tmp, exePath, null); }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(tmp, exePath, true);
                        TryDelete(tmp);
                    }
                }
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                return "写入失败：" + ex.Message + "\n原文件未被改动。";
            }
            finally
            {
                if (hadReadOnly)
                {
                    try { new FileInfo(exePath).IsReadOnly = true; }
                    catch (Exception) { }
                }
            }

            string after;
            try { after = Sha256(File.ReadAllBytes(exePath)); }
            catch (Exception ex) { return Rollback(exePath, bak, "写入后无法读回文件（" + ex.Message + "）"); }

            if (after != expectSha) return Rollback(exePath, bak, "写入后复核不符");
            return null;
        }

        /// <summary>执行修补。成功返回 null，失败返回错误说明</summary>
        public static string Apply(string exePath)
        {
            byte[] data;
            try { data = File.ReadAllBytes(exePath); }
            catch (Exception ex) { return "读取文件失败：" + ex.Message; }

            if (Sha256(data) != ShaOrig)
                return "文件版本不匹配，这不是未修改的 PCL 2.99.0.1。";
            if (IsPclRunning())
                return "PCL 正在运行，请先退出 PCL 再修补。";

            int h1 = Find(data, CtorPat, CtorMask);
            if (h1 == -2) return "定位失败：下载页左栏代码命中多处，已中止（未改动文件）。";
            if (h1 < 0)   return "定位失败：没有找到下载页左栏代码（模式 1）。";

            int h2 = Find(data, LoadPat, LoadMask);
            if (h2 == -2) return "定位失败：下载页初始化代码命中多处，已中止（未改动文件）。";
            if (h2 < 0)   return "定位失败：没有找到下载页初始化代码（模式 2）。";

            // 补丁 1：两处 Visibility = Collapsed(2) 改成 Visible(0)
            foreach (int rel in new int[] { 6, 18 })
            {
                if (data[h1 + rel] != 0x18)
                    return "补丁 1 校验失败：目标字节不是预期值（未改动文件）。";
                data[h1 + rel] = 0x16;      // ldc.i4.0
            }

            // 补丁 2：从 ldstr "关闭下载" 处起 48 字节，换成真正的 DlClientListLoader
            if (data[h2 + 0x1A] != 0x72)
                return "补丁 2 校验失败：目标位置不是 ldstr（未改动文件）。";

            byte[] rep = new byte[48];
            Array.Copy(RealLoaderRef, 0, rep, 0, RealLoaderRef.Length);
            Array.Copy(rep, 0, data, h2 + 0x1A, rep.Length);

            string bak = BakPath(exePath);
            string err = EnsureBackup(exePath, bak);
            if (err != null) return err;

            return WriteAtomically(exePath, data, ShaPatched, bak);
        }

        /// <summary>用备份还原。成功返回 null</summary>
        public static string Restore(string exePath)
        {
            string bak = BakPath(exePath);
            if (!File.Exists(bak)) return "找不到备份文件（" + Path.GetFileName(bak) + "）。";

            byte[] bakData;
            try { bakData = File.ReadAllBytes(bak); }
            catch (Exception ex) { return "读取备份失败：" + ex.Message; }

            if (Sha256(bakData) != ShaOrig)
                return "备份文件不是 2.99.0.1 原版，已中止（未改动任何文件）。";

            if (IsPclRunning()) return "PCL 正在运行，请先退出 PCL 再还原。";

            return WriteAtomically(exePath, bakData, ShaOrig, bak);
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly Color Accent  = Color.FromArgb(45, 127, 249);
        private static readonly Color OkGreen = Color.FromArgb(22, 140, 74);
        private static readonly Color Warn    = Color.FromArgb(196, 106, 0);
        private static readonly Color Bad     = Color.FromArgb(196, 43, 43);
        private static readonly Color Gray    = Color.FromArgb(110, 110, 110);

        private readonly TextBox _txtPath   = new TextBox();
        private readonly Button  _btnBrowse = new Button();
        private readonly Button  _btnNext   = new Button();
        private readonly Button  _btnRestore= new Button();
        private readonly Panel   _panel     = new Panel();
        private readonly Label   _lblStatus = new Label();

        private string     _exePath = null;
        private PatchState _state   = PatchState.None;
        private readonly float _scale;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int index);

        private static float GetDpiScale()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return 1f;
            int dpi = GetDeviceCaps(dc, 88);     // LOGPIXELSX
            ReleaseDC(IntPtr.Zero, dc);
            if (dpi <= 0) return 1f;
            return dpi / 96f;
        }

        /// <summary>按系统 DPI 缩放尺寸（字体本身已是物理单位，不再缩放）</summary>
        private int S(int v) { return (int)Math.Round(v * _scale); }

        public MainForm()
        {
            _scale = GetDpiScale();
            AutoScaleMode = AutoScaleMode.None;

            Text            = "PCL 2.99.0.1 下载功能恢复补丁";
            ClientSize      = new Size(S(560), S(356));
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            Font            = new Font("Microsoft YaHei UI", 9F);
            BackColor       = Color.White;
            AllowDrop       = true;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }

            BuildUi();

            DragEnter += OnDragEnter;
            DragDrop  += OnDragDrop;
        }

        private void BuildUi()
        {
            Label title = new Label();
            title.Text      = "PCL 2.99.0.1 · 下载功能恢复补丁";
            title.Font      = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(28, 28, 28);
            title.AutoSize  = true;
            title.Location  = new Point(S(28), S(22));
            Controls.Add(title);

            Label sub = new Label();
            sub.Text      = "为 2.99.0.1 恢复「原版游戏」的下载入口，只改一个 exe，自动备份";
            sub.ForeColor = Gray;
            sub.AutoSize  = true;
            sub.Location  = new Point(S(30), S(56));
            Controls.Add(sub);

            Panel line = new Panel();
            line.BackColor = Color.FromArgb(232, 232, 232);
            line.Location  = new Point(S(28), S(86));
            line.Size      = new Size(S(504), Math.Max(1, S(1)));
            Controls.Add(line);

            Label step = new Label();
            step.Text      = "① 选择你的 PCL 文件夹";
            step.Font      = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            step.AutoSize  = true;
            step.Location  = new Point(S(30), S(100));
            Controls.Add(step);

            _txtPath.Location   = new Point(S(30), S(124));
            _txtPath.Size       = new Size(S(380), S(26));
            _txtPath.ReadOnly   = true;
            _txtPath.BackColor  = Color.FromArgb(248, 248, 248);
            _txtPath.BorderStyle= BorderStyle.FixedSingle;
            _txtPath.Text       = "";
            Controls.Add(_txtPath);

            _btnBrowse.Text      = "浏览...";
            _btnBrowse.Location  = new Point(S(418), S(123));
            _btnBrowse.Size      = new Size(S(112), S(28));
            _btnBrowse.FlatStyle = FlatStyle.System;
            _btnBrowse.Click    += OnBrowse;
            Controls.Add(_btnBrowse);

            _panel.Location    = new Point(S(30), S(166));
            _panel.Size        = new Size(S(500), S(64));
            _panel.BackColor   = Color.FromArgb(245, 246, 248);
            _panel.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_panel);

            _lblStatus.AutoSize  = false;
            _lblStatus.Location  = new Point(S(10), S(8));
            _lblStatus.Size      = new Size(S(474), S(46));
            _lblStatus.ForeColor = Gray;
            _lblStatus.Text      = "还没有选择文件夹。点「浏览...」挑一个，或者把文件夹直接拖进来。";
            _panel.Controls.Add(_lblStatus);

            Label hint = new Label();
            hint.Text      = "选择包含「Plain Craft Launcher 2.exe」的那个文件夹（选上一层也能自动找到）。";
            hint.ForeColor = Gray;
            hint.Font      = new Font("Microsoft YaHei UI", 8F);
            hint.AutoSize  = true;
            hint.Location  = new Point(S(30), S(242));
            Controls.Add(hint);

            _btnRestore.Text      = "还原原版";
            _btnRestore.Location  = new Point(S(234), S(288));
            _btnRestore.Size      = new Size(S(106), S(38));
            _btnRestore.Enabled   = false;
            _btnRestore.Click    += OnRestore;
            Controls.Add(_btnRestore);

            _btnNext.Text        = "下一步  →";
            _btnNext.Location    = new Point(S(350), S(288));
            _btnNext.Size        = new Size(S(180), S(38));
            _btnNext.FlatStyle   = FlatStyle.Flat;
            _btnNext.FlatAppearance.BorderSize = 0;
            _btnNext.BackColor   = Color.FromArgb(206, 206, 206);
            _btnNext.ForeColor   = Color.White;
            _btnNext.Font        = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            _btnNext.Enabled     = false;
            _btnNext.Click      += OnNext;
            Controls.Add(_btnNext);

            Button close = new Button();
            close.Text      = "退出";
            close.Location  = new Point(S(30), S(288));
            close.Size      = new Size(S(106), S(38));
            close.Click    += delegate { Close(); };
            Controls.Add(close);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 如果本程序就放在 PCL 目录里，启动时自动带出路径
            string self = Path.GetDirectoryName(Application.ExecutablePath);
            SetFolder(self, false);
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] items = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (items == null || items.Length == 0) return;
            string p = items[0];
            if (File.Exists(p)) p = Path.GetDirectoryName(p);
            SetFolder(p, true);
        }

        private string _lastFolder = "";

        private void OnBrowse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "请选择 PCL 所在的文件夹（里面应有 Plain Craft Launcher 2.exe）";
                dlg.ShowNewFolderButton = false;
                if (!string.IsNullOrEmpty(_lastFolder) && Directory.Exists(_lastFolder))
                    dlg.SelectedPath = _lastFolder;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                SetFolder(dlg.SelectedPath, true);
            }
        }

        private void SetFolder(string folder, bool interactive)
        {
            if (string.IsNullOrEmpty(folder))
            {
                Reset();
                return;
            }
            _lastFolder = folder;
            string exe = Patcher.FindExe(folder);

            if (exe == null)
            {
                // 主程序可能被删/被安全软件隔离，但备份还在 —— 这仍是可救的场景
                string bak = Patcher.FindFile(folder, Patcher.ExeName + ".bak");
                if (bak != null)
                {
                    _exePath = bak.Substring(0, bak.Length - 4);
                    _state   = PatchState.Mismatch;
                    _txtPath.Text = _exePath + "（当前不存在）";
                    ShowStatus(Bad, "✗  找不到 PCL 主程序，但发现了备份文件。\n点「还原原版」可以把备份写回去。");
                    Sync();
                    return;
                }

                _exePath = null;
                _state   = PatchState.NotFound;
                _txtPath.Text = folder;
                ShowStatus(Bad, "✗  这个文件夹里没有找到「" + Patcher.ExeName + "」。\n请选择 PCL 所在的文件夹。");
                Sync();
                return;
            }

            _exePath = exe;
            _state   = Patcher.Detect(exe);
            _txtPath.Text = exe;
            _txtPath.SelectionStart = 0;
            _txtPath.SelectionLength = 0;

            switch (_state)
            {
                case PatchState.Original:
                    ShowStatus(OkGreen, "✓  已找到原版 2.99.0.1，可以修补。\n点「下一步」开始。");
                    break;
                case PatchState.Patched:
                    ShowStatus(Warn, HasBackup()
                        ? "✓  这个程序已经打过补丁了，不用重复修补。\n（想恢复的话，点左侧「还原原版」）"
                        : "✓  这个程序已经打过补丁了，不用重复修补。\n没有找到备份，如需原版请重新下载官方安装包。");
                    break;
                case PatchState.Running:
                    ShowStatus(Warn, "⚠  PCL 正在运行中，请先完全退出 PCL。\n退出后重新选择文件夹即可。");
                    break;
                case PatchState.NoAccess:
                    ShowStatus(Bad, "✗  无法读取或写入这个文件（可能被占用、只读，或没有权限）。\n" +
                                     (HasBackup() ? "如果文件已经损坏，可点「还原原版」从备份恢复。"
                                                  : "请确认 PCL 已完全退出，或尝试以管理员身份运行本工具。"));
                    break;
                default:
                    ShowStatus(Bad, HasBackup()
                        ? "✗  版本不匹配：这个 exe 不是 2.99.0.1 原版。\n如果文件已经损坏，可点「还原原版」从备份恢复。"
                        : "✗  版本不匹配：这个 exe 不是 2.99.0.1 原版。\n补丁只支持 2.99.0.1。");
                    break;
            }
            Sync();
        }

        private bool HasBackup()
        {
            return _exePath != null && File.Exists(Patcher.BakPath(_exePath));
        }

        private void Reset()
        {
            _exePath = null;
            _state   = PatchState.None;
            _txtPath.Text = "";
            ShowStatus(Gray, "还没有选择文件夹。点「浏览...」挑一个，或者把文件夹直接拖进来。");
            Sync();
        }

        private static int Tint(int v) { return v + (255 - v) * 16 / 20; }

        private void ShowStatus(Color c, string text)
        {
            _panel.BackColor     = Color.FromArgb(Tint(c.R), Tint(c.G), Tint(c.B));
            _lblStatus.ForeColor = c;
            _lblStatus.Text      = text;
        }

        private void Sync()
        {
            bool canPatch = _state == PatchState.Original;
            _btnNext.Enabled   = canPatch;
            _btnNext.BackColor = canPatch ? Accent : Color.FromArgb(206, 206, 206);

            // 只要存在可信备份，就允许还原 —— 包括文件已损坏/不匹配的情形，
            // 这是用户自救的唯一通道，不能因为状态“不对”而关掉。
            _btnRestore.Enabled = HasBackup();
        }

        private void OnNext(object sender, EventArgs e)
        {
            if (_state != PatchState.Original || _exePath == null) return;

            string err;
            try { err = Patcher.Apply(_exePath); }
            catch (Exception ex) { err = "意外错误：" + ex.Message + "\n原文件未被改动。"; }

            if (err != null)
            {
                MessageBox.Show(this, err, "修补失败",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetFolder(Path.GetDirectoryName(_exePath), false);
                return;
            }

            _state = PatchState.Patched;
            Sync();
            ShowStatus(OkGreen, "✓  修补完成！\n打开 PCL → 下载 → 「原版游戏」就能正常用了。");

            DialogResult r = MessageBox.Show(this,
                "修补完成！\n\n重新打开 PCL，点「下载」→ 左边就会出现「原版游戏」，\n" +
                "进入后选版本就能正常下载了。\n\n现在启动 PCL 吗？",
                "完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (r == DialogResult.Yes)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(_exePath);
                    psi.WorkingDirectory = Path.GetDirectoryName(_exePath);
                    Process.Start(psi);
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "启动失败：" + ex.Message);
                }
            }
        }

        private void OnRestore(object sender, EventArgs e)
        {
            if (_exePath == null) return;
            if (MessageBox.Show(this, "确定要把 PCL 还原成未修改的原版吗？\n（下载功能会重新被关闭）",
                                "还原确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                != DialogResult.Yes) return;

            string err;
            try { err = Patcher.Restore(_exePath); }
            catch (Exception ex) { err = "意外错误：" + ex.Message; }

            if (err != null)
            {
                MessageBox.Show(this, err, "还原失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            SetFolder(Path.GetDirectoryName(_exePath), false);
            MessageBox.Show(this, "已还原为原始版本。", "完成",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            // 静默模式，供自动化测试 / 批处理调用
            if (args.Length >= 2 && args[0] == "-auto")
            {
                string folder = args[1];
                string exe = Patcher.FindExe(folder);
                if (exe == null) return 2;
                PatchState st = Patcher.Detect(exe);
                if (st == PatchState.Patched) return 0;
                if (st != PatchState.Original) return 3;
                return Patcher.Apply(exe) == null ? 0 : 4;
            }
            if (args.Length >= 2 && args[0] == "-restore")
            {
                // 主程序可能已不存在（被隔离/误删），只要备份在就允许还原
                string exe = Patcher.FindExe(args[1]);
                if (exe == null)
                {
                    string bak = Patcher.FindFile(args[1], Patcher.ExeName + ".bak");
                    if (bak == null) return 2;
                    exe = bak.Substring(0, bak.Length - 4);
                }
                return Patcher.Restore(exe) == null ? 0 : 4;
            }

            try { SetProcessDPIAware(); }
            catch (Exception) { }

            // 单实例：避免两个进程同时改写同一个 exe
            bool first;
            using (new System.Threading.Mutex(true, "PclPatch.SingleInstance", out first))
            {
                if (!first)
                {
                    MessageBox.Show("补丁程序已经在运行了。", "提示",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 兜底：任何未捕获异常都给出可操作的提示，而不是静默崩溃
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs te)
                {
                    MessageBox.Show(
                        "程序遇到意外错误：\n" + te.Exception.Message +
                        "\n\n如果 PCL 打不开，请用同目录的\n" + Patcher.ExeName + ".bak\n覆盖回去。",
                        "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };

                Application.Run(new MainForm());
            }
            return 0;
        }
    }
}
