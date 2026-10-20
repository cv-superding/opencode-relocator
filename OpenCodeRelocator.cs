using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OpenCodeRelocator
{
    // ================================================================ 基础数据

    internal static class Paths
    {
        public static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static string App = Path.Combine(LocalAppData, @"Programs\@openchamberelectron");
        public static string Roaming = Path.Combine(RoamingAppData, "OpenChamber");
        public static string Config = Path.Combine(UserProfile, @".config\openchamber");
        public static string UpdaterCache = Path.Combine(LocalAppData, @"@openchamberelectron-updater");

        // OpenCode 官方桌面端
        public static string OpenCodeApp = Path.Combine(LocalAppData, @"Programs\@opencodedesktop");
        public static string OpenCodeRoaming = Path.Combine(RoamingAppData, "ai.opencode.desktop");
        public static string OpenCodeConfig = Path.Combine(UserProfile, @".config\opencode");
        public static string OpenCodeShare = Path.Combine(UserProfile, @".local\share\opencode");
        public static string OpenCodeCache = Path.Combine(LocalAppData, @"@opencodedesktop-updater");

        public static string Data = Path.Combine(LocalAppData, "OpenCodeRelocator");
        public static string JournalFile { get { return Path.Combine(Data, "journal.json"); } }
    }

    // 一个可迁移的应用：目录集合、进程名、记录文件都各自独立
    internal sealed class AppProfile
    {
        public string Key;
        public string Name;       // 完整名，如 OpenCode 桌面端
        public string ShortName;  // 状态胶囊用
        public string Folder;     // 默认目标文件夹名
        public string App;
        public string Roaming;
        public string Config;
        public string Share;      // 可选：会话/凭据等第二份数据
        public string UpdaterCache;
        public string ExeName;
        public string JournalFile;
    }

    internal sealed class TreeInfo
    {
        public int Files;
        public long Bytes;
        public int Links; // 目录联接/符号链接数量：复制时必须原样保留，校验时要对上
    }

    internal sealed class JournalEntry
    {
        public string Name { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        public string Backup { get; set; }
        public string MovedAt { get; set; }
        public string State { get; set; } // pending=已改名备份但联接未建好，done=已完成
    }

    internal sealed class MigItem
    {
        public string Key;
        public string Name;
        public string Subdir;
        public string Source;
    }

    internal delegate void Logger(string message, int level);

    // ================================================================ 迁移逻辑

    internal static class Ops
    {
        public const int Info = 0;
        public const int Ok = 1;
        public const int Warn = 2;
        public const int Error = 3;

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return string.Format("{0:N2} GB", bytes / 1073741824.0);
            if (bytes >= 1024L * 1024) return string.Format("{0:N1} MB", bytes / 1048576.0);
            if (bytes >= 1024L) return string.Format("{0:N0} KB", bytes / 1024.0);
            return bytes + " B";
        }

        private static string LongPath(string path)
        {
            if (path.StartsWith(@"\\?\")) return path;
            if (path.StartsWith(@"\\")) return @"\\?\UNC\" + path.Substring(2);
            if (path.Length > 3) path = path.TrimEnd('\\');
            if (path.EndsWith(":")) path += "\\";
            return @"\\?\" + path;
        }

        // 目录统计：直接走 Win32，避免 .NET Framework 对 \\?\ 前缀的非法字符检查，
        // 同时跳过重解析点，防止联接被重复统计或形成环。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public uint ftCreationTimeLow;
            public uint ftCreationTimeHigh;
            public uint ftLastAccessTimeLow;
            public uint ftLastAccessTimeHigh;
            public uint ftLastWriteTimeLow;
            public uint ftLastWriteTimeHigh;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string pattern, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr handle, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr handle);

        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

        public static TreeInfo GetTreeInfo(string path)
        {
            var info = new TreeInfo();
            if (!Directory.Exists(path)) return info;

            var pending = new Stack<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push(path);
            seen.Add(path);

            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                WIN32_FIND_DATA data;
                IntPtr handle = FindFirstFileW(LongPath(dir) + @"\*", out data);
                if (handle == new IntPtr(-1)) continue;
                try
                {
                    do
                    {
                        string name = data.cFileName;
                        if (string.IsNullOrEmpty(name) || name == "." || name == "..") continue;

                        if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                        {
                            // 目录联接/符号链接：只计数不进去，避免成环；复制时按链接原样搬走
                            if ((data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) { info.Links++; continue; }
                            string sub = Path.Combine(dir, name);
                            if (seen.Add(sub)) pending.Push(sub);
                        }
                        else
                        {
                            info.Files++;
                            info.Bytes += ((long)data.nFileSizeHigh << 32) + data.nFileSizeLow;
                        }
                    }
                    while (FindNextFileW(handle, out data));
                }
                finally
                {
                    FindClose(handle);
                }
            }
            return info;
        }

        public static long GetFreeSpace(string anyPath)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(anyPath));
                if (string.IsNullOrEmpty(root)) return -1;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch { return -1; }
        }

        public static bool IsJunction(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return false;
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch { return false; }
        }

        public static void OpenFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
                var psi = new ProcessStartInfo("explorer.exe", "\"" + path + "\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr handle, uint code, IntPtr inBuf, int inSize,
            IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static string JunctionTarget(string path)
        {
            const uint GENERIC_READ = 0x80000000;
            const uint SHARE_ALL = 7;
            const uint OPEN_EXISTING = 3;
            const uint BACKUP_SEMANTICS = 0x02000000;
            const uint OPEN_REPARSE_POINT = 0x00200000;
            const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
            const int IO_REPARSE_TAG_MOUNT_POINT = unchecked((int)0xA0000003);

            IntPtr handle = CreateFileW(path, GENERIC_READ, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                BACKUP_SEMANTICS | OPEN_REPARSE_POINT, IntPtr.Zero);
            if (handle == new IntPtr(-1)) return null;

            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(16384);
                int returned;
                if (!DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0, buffer, 16384, out returned, IntPtr.Zero))
                    return null;
                if (Marshal.ReadInt32(buffer, 0) != IO_REPARSE_TAG_MOUNT_POINT) return null;
                int subOffset = Marshal.ReadInt16(buffer, 8);
                int subLength = Marshal.ReadInt16(buffer, 10);
                string name = Marshal.PtrToStringUni(new IntPtr(buffer.ToInt64() + 16 + subOffset), subLength / 2);
                if (name != null && name.StartsWith(@"\??\")) name = name.Substring(4);
                return name;
            }
            catch { return null; }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                CloseHandle(handle);
            }
        }

        public static void CreateJunction(string link, string target)
        {
            Run("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"", "创建 junction 失败");
        }

        public static void RemoveJunction(string link)
        {
            if (!IsJunction(link)) return;
            string output;
            int code = RunProcess("cmd.exe", "/c rmdir \"" + link + "\"", out output);
            if (code != 0 && Directory.Exists(link))
                throw new Exception("删除 junction 失败：" + output.Trim());
        }

        // 读 stdout/stderr 必须同时进行，否则管道写满会互相等死，迁移会永久卡住
        private static int RunProcess(string file, string args, out string output)
        {
            var psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using (Process p = Process.Start(psi))
            {
                var buffer = new StringBuilder();
                var sync = new object();
                p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (sync) buffer.AppendLine(e.Data);
                };
                p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (sync) buffer.AppendLine(e.Data);
                };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                p.WaitForExit(); // 第二次调用确保异步输出已经收完
                lock (sync) output = buffer.ToString();
                return p.ExitCode;
            }
        }

        private static void Run(string file, string args, string errorText)
        {
            string output;
            int code = RunProcess(file, args, out output);
            if (code != 0) throw new Exception(errorText + "：" + output.Trim());
        }

        public static void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            // /SJ /SL：目录联接与符号链接按链接本身复制（不跟随、不丢弃），
            // 和 GetTreeInfo 的统计口径一致；复制后还会比对链接数量，防止悄悄丢东西
            string args = "/E /SJ /SL /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /MT:8 \"" + source + "\" \"" + target + "\"";
            string output;
            int code = RunProcess("robocopy.exe", args, out output);
            if (code >= 8)
            {
                string tail = output.Length > 700 ? output.Substring(output.Length - 700) : output;
                throw new Exception("robocopy 失败 (exit " + code + ")：" + tail.Trim());
            }
        }

        public static void ForceDeleteDir(string dir)
        {
            if (!Directory.Exists(dir)) return;

            try
            {
                Directory.Delete(dir, true);
                return;
            }
            catch { }

            // 长路径或只读文件的兜底：先清属性，再用 rd /s /q
            try
            {
                var pending = new Stack<string>();
                pending.Push(dir);
                while (pending.Count > 0)
                {
                    string current = pending.Pop();
                    WIN32_FIND_DATA data;
                    IntPtr handle = FindFirstFileW(LongPath(current) + @"\*", out data);
                    if (handle == new IntPtr(-1)) continue;
                    try
                    {
                        do
                        {
                            string name = data.cFileName;
                            if (name == "." || name == "..") continue;
                            string full = Path.Combine(current, name);
                            if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                            {
                                if ((data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0) pending.Push(full);
                            }
                            else
                            {
                                try { File.SetAttributes(full, FileAttributes.Normal); }
                                catch { }
                            }
                        }
                        while (FindNextFileW(handle, out data));
                    }
                    finally
                    {
                        FindClose(handle);
                    }
                }
            }
            catch { }

            string rdOutput;
            RunProcess("cmd.exe", "/c rd /s /q \"" + dir + "\"", out rdOutput);

            if (Directory.Exists(dir) && GetTreeInfo(dir).Files > 0)
                throw new Exception("部分文件无法删除：" + dir);
        }

        public static void MoveDir(string source, string target)
        {
            try { Directory.Move(source, target); }
            catch (IOException)
            {
                CopyTree(source, target);
                var a = GetTreeInfo(source);
                var b = GetTreeInfo(target);
                if (a.Files != b.Files || a.Bytes != b.Bytes)
                    throw new Exception("跨盘搬迁校验不一致：" + a.Files + " / " + b.Files);
                ForceDeleteDir(source);
            }
        }

        public static List<AppProfile> Profiles()
        {
            var list = new List<AppProfile>();
            list.Add(new AppProfile
            {
                Key = "openchamber",
                Name = "OpenChamber",
                ShortName = "OpenChamber",
                Folder = "OpenChamber",
                App = Paths.App,
                Roaming = Paths.Roaming,
                Config = Paths.Config,
                UpdaterCache = Paths.UpdaterCache,
                ExeName = "OpenChamber.exe",
                JournalFile = Path.Combine(Paths.Data, "journal.json")
            });
            list.Add(new AppProfile
            {
                Key = "opencode",
                Name = "OpenCode 桌面端",
                ShortName = "OpenCode",
                Folder = "OpenCode",
                App = Paths.OpenCodeApp,
                Roaming = Paths.OpenCodeRoaming,
                Config = Paths.OpenCodeConfig,
                Share = Paths.OpenCodeShare,
                UpdaterCache = Paths.OpenCodeCache,
                ExeName = "OpenCode.exe",
                JournalFile = Path.Combine(Paths.Data, "journal-opencode.json")
            });
            return list;
        }

        private static bool Under(string path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            return path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        // 比 Process.MainModule 快得多，也不会因为权限不足拖慢整轮扫描
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr handle, uint flags, StringBuilder name, ref int size);

        private static string ProcessPath(int pid)
        {
            IntPtr handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return null;
                return buffer.ToString(0, size);
            }
            catch { return null; }
            finally { CloseHandle(handle); }
        }

        // 进程镜像路径可能已经被系统解析成联接的真实目标，两个位置都要认
        private static List<string> AppRoots(AppProfile profile)
        {
            var roots = new List<string>();
            AddRoot(roots, profile.App);
            AddRoot(roots, profile.Roaming);
            AddRoot(roots, profile.Config);
            AddRoot(roots, profile.Share);
            return roots;
        }

        private static void AddRoot(List<string> roots, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            roots.Add(path.TrimEnd('\\'));
            try
            {
                if (IsJunction(path))
                {
                    string target = JunctionTarget(path);
                    if (!string.IsNullOrEmpty(target)) roots.Add(target.TrimEnd('\\'));
                }
            }
            catch { }
        }

        private static bool UnderAny(string file, List<string> roots)
        {
            if (string.IsNullOrEmpty(file)) return false;
            foreach (string root in roots)
                if (Under(file, root)) return true;
            return false;
        }

        public static List<Process> FindAppProcesses(AppProfile profile)
        {
            List<string> roots = AppRoots(profile);

            var result = new List<Process>();
            foreach (Process p in Process.GetProcesses())
            {
                string file = null;
                try { file = ProcessPath(p.Id); }
                catch { }
                // 路径读不到就不当作目标进程，避免误杀同名程序
                bool hit = !string.IsNullOrEmpty(file) && UnderAny(file, roots);
                if (hit) result.Add(p);
                else p.Dispose();
            }
            return result;
        }

        private static List<string> GuiRoots(AppProfile profile)
        {
            var roots = new List<string>();
            AddRoot(roots, profile.App);
            return roots;
        }

        // 只统计图形界面进程：后台服务（如 opencode-cli）不代表应用开着
        public static Dictionary<string, bool> GuiRunningStates(List<AppProfile> list)
        {
            var result = new Dictionary<string, bool>();
            var roots = new Dictionary<string, List<string>>();
            foreach (AppProfile p in list)
            {
                result[p.Key] = false;
                roots[p.Key] = GuiRoots(p);
            }

            foreach (Process proc in Process.GetProcesses())
            {
                try
                {
                    string file = null;
                    try { file = ProcessPath(proc.Id); }
                    catch { }
                    if (string.IsNullOrEmpty(file)) continue;

                    string baseName;
                    try { baseName = Path.GetFileName(file); }
                    catch { continue; }

                    foreach (AppProfile p in list)
                    {
                        if (result[p.Key]) continue;
                        if (!string.Equals(baseName, p.ExeName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (UnderAny(file, roots[p.Key])) result[p.Key] = true;
                    }
                }
                finally
                {
                    // Process 对象会一直占着句柄，及时释放
                    try { proc.Dispose(); } catch { }
                }
            }
            return result;
        }

        public static bool IsGuiRunning(AppProfile profile)
        {
            var list = new List<AppProfile>();
            list.Add(profile);
            bool value;
            return GuiRunningStates(list).TryGetValue(profile.Key, out value) && value;
        }

        public static bool IsAppRunning(AppProfile profile)
        {
            return IsGuiRunning(profile);
        }

        public static void StopApp(AppProfile profile, Logger log)
        {
            List<Process> procs = FindAppProcesses(profile);
            if (procs.Count == 0) { log(profile.ShortName + " 未在运行", Info); return; }

            var pending = new List<int>();
            foreach (Process p in procs)
            {
                int pid = p.Id;
                string name = "?";
                try { name = p.ProcessName; } catch { }
                try { p.Kill(); log("已结束进程 " + name + " (PID " + pid + ")", Info); pending.Add(pid); }
                catch (Exception ex) { log("无法结束 PID " + pid + "：" + ex.Message, Warn); }
                finally { try { p.Dispose(); } catch { } }
            }

            // 只复查刚结束的那几个 PID，不要每 250ms 就全量重扫一遍
            for (int i = 0; i < 24; i++)
            {
                System.Threading.Thread.Sleep(250);
                if (!AnyAlive(pending)) { log(profile.ShortName + " 已关闭", Ok); return; }
            }
            throw new Exception(profile.ShortName + " 进程仍在运行，请手动退出后重试。");
        }

        private static bool AnyAlive(List<int> pids)
        {
            foreach (int pid in pids)
            {
                try
                {
                    using (Process p = Process.GetProcessById(pid)) { if (!p.HasExited) return true; }
                }
                catch { }
            }
            return false;
        }

        public static void StartApp(AppProfile profile)
        {
            string exe = Path.Combine(profile.App, profile.ExeName);
            if (!File.Exists(exe)) throw new Exception("找不到 " + exe);
            Process.Start(exe);
        }

        public static string LastJournalError;

        public static List<JournalEntry> LoadJournal(AppProfile profile)
        {
            LastJournalError = null;
            try
            {
                if (!File.Exists(profile.JournalFile)) return new List<JournalEntry>();
                string json = File.ReadAllText(profile.JournalFile, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return new List<JournalEntry>();
                var list = new JavaScriptSerializer().Deserialize<List<JournalEntry>>(json);
                return list ?? new List<JournalEntry>();
            }
            catch (Exception ex)
            {
                LastJournalError = profile.JournalFile + " 解析失败：" + ex.Message;
                return new List<JournalEntry>();
            }
        }

        public static void SaveJournal(AppProfile profile, List<JournalEntry> entries)
        {
            string dir = Path.GetDirectoryName(profile.JournalFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 先写临时文件再原子替换：中途断电不会留下半截 json
            string tmp = profile.JournalFile + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(entries), new UTF8Encoding(true));
            try
            {
                if (File.Exists(profile.JournalFile)) File.Replace(tmp, profile.JournalFile, null);
                else File.Move(tmp, profile.JournalFile);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                File.WriteAllText(profile.JournalFile, new JavaScriptSerializer().Serialize(entries), new UTF8Encoding(true));
            }
        }

        public static void ClearJournal(AppProfile profile)
        {
            try { if (File.Exists(profile.JournalFile)) File.Delete(profile.JournalFile); }
            catch { }
        }

        public static List<MigItem> AllItems(AppProfile profile)
        {
            var list = new List<MigItem>();
            bool opencode = profile.Key == "opencode";
            list.Add(new MigItem { Key = "app", Name = "程序目录", Subdir = "app", Source = profile.App });
            list.Add(new MigItem { Key = "roaming", Name = "应用数据", Subdir = @"data\roaming", Source = profile.Roaming });
            list.Add(new MigItem
            {
                Key = "config",
                Name = opencode ? "配置" : "配置与会话",
                Subdir = @"data\config",
                Source = profile.Config
            });
            if (!string.IsNullOrEmpty(profile.Share))
            {
                list.Add(new MigItem { Key = "share", Name = "会话与登录", Subdir = @"data\share", Source = profile.Share });
            }
            return list;
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInside(string parent, string child)
        {
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child)) return false;
            parent = parent.TrimEnd('\\');
            return child.TrimEnd('\\').StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
        }

        // 目标目录校验（纯逻辑，不弹窗，方便测试）
        public static bool ValidateTargetPath(string text, List<string> sources, out string root, out string error)
        {
            root = (text ?? "").Trim().TrimEnd('\\');
            error = null;
            if (root.Length == 0) { error = "请先填写目标目录，例如 E:\\Apps\\OpenChamber"; return false; }
            if (root.Length == 2 && root[1] == ':') { error = "请写完整路径，例如 E:\\Apps\\OpenChamber"; return false; }
            if (!Path.IsPathRooted(root)) { error = "目标必须是绝对路径，例如 E:\\Apps\\OpenChamber"; return false; }
            try { root = Path.GetFullPath(root); }
            catch { error = "目标路径无效。"; return false; }

            string driveRoot = Path.GetPathRoot(root);
            try
            {
                var drive = new DriveInfo(driveRoot);
                if (!drive.IsReady) { error = "目标磁盘不可用（没有插盘或未就绪）：" + driveRoot; return false; }
                // 目录联接只能指向本地卷：固定盘或移动盘都行，网络盘/光驱不行
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                {
                    error = "目标必须是本地磁盘（当前为 " + drive.DriveType + "）：" + driveRoot;
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = "无法访问目标磁盘：" + driveRoot + "（" + ex.Message + "）";
                return false;
            }

            // 目录联接要求 NTFS/FAT32 以外的文件系统其实不支持，先查清楚
            string fs = VolumeFileSystem(driveRoot);
            if (!string.IsNullOrEmpty(fs) &&
                !string.Equals(fs, "NTFS", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fs, "ReFS", StringComparison.OrdinalIgnoreCase))
            {
                error = "目标磁盘必须是 NTFS 格式（当前是 " + fs + "），目录联接无法建立：" + driveRoot;
                return false;
            }

            if (sources != null)
            {
                foreach (string source in sources)
                {
                    if (string.IsNullOrEmpty(source)) continue;
                    if (SamePath(source, root))
                    {
                        error = "目标目录和源目录是同一个地方，换一个位置：\r\n" + root;
                        return false;
                    }
                    if (IsInside(source, root) || IsInside(root, source))
                    {
                        error = "目标目录不能与源目录互相包含：\r\n" + root;
                        return false;
                    }
                }
            }
            return true;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformation(string root, StringBuilder volumeName, int volumeNameSize,
            out int volumeSerial, out int maxComponentLength, out int fileSystemFlags,
            StringBuilder fileSystemName, int fileSystemNameSize);

        public static string VolumeFileSystem(string rootPath)
        {
            try
            {
                var name = new StringBuilder(261);
                var fs = new StringBuilder(261);
                int serial, maxLength, flags;
                if (!GetVolumeInformation(rootPath, name, name.Capacity, out serial, out maxLength, out flags, fs, fs.Capacity))
                    return null;
                return fs.ToString();
            }
            catch { return null; }
        }

        // 备份目录名带时间戳，撞名时自动换一个，避免 Directory.Move 直接失败
        private static string UniqueBackupPath(string source)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string candidate = source + ".moved-" + stamp;
            int n = 1;
            while (Directory.Exists(candidate) || File.Exists(candidate))
            {
                if (n > 50)
                {
                    candidate = source + ".moved-" + stamp + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    break;
                }
                candidate = source + ".moved-" + stamp + "-" + n;
                n++;
            }
            return candidate;
        }

        // 「迁移」与「再次迁移」共用：已经迁移过的（源路径是 junction）直接从当前位置搬走，
        // 不需要先回滚，也不会重复占用空间。
        public static void MigrateOne(MigItem item, string targetRoot, bool keepBackup, List<JournalEntry> journal, Logger log, AppProfile profile)
        {
            string source = item.Source;
            string target = Path.Combine(targetRoot, item.Subdir);

            log(item.Name + "  ·  " + target, Info);

            if (!Directory.Exists(source)) throw new Exception("源目录不存在：" + source);

            bool again = IsJunction(source);
            string current = again ? JunctionTarget(source) : source;

            if (again)
            {
                if (string.IsNullOrEmpty(current) || !Directory.Exists(current))
                    throw new Exception("目录联接已失效，找不到当前数据位置；请先用「回滚」还原。");
                if (SamePath(current, target))
                {
                    log("· 位置没有变化，已跳过", Warn);
                    return;
                }
                log("· 已迁移过，从当前位置搬到新位置：" + current, Info);
            }

            if (IsInside(current, target) || IsInside(target, current))
                throw new Exception("新旧位置不能互相包含：\r\n" + current + "\r\n" + target);

            var info = GetTreeInfo(current);
            log(string.Format("· 复制 {0}，共 {1} 个文件{2}", FormatSize(info.Bytes), info.Files,
                info.Links > 0 ? "、" + info.Links + " 个链接" : ""), Info);

            if (TargetOccupied(target))
                throw new Exception("目标目录里已有内容，请换一个空目录：" + target);

            // 同盘迁移时源和目标会同时存在，需要接近两倍空间
            bool sameVolume = SameVolume(current, target);
            long free = GetFreeSpace(target);
            if (free > 0)
            {
                long need = sameVolume ? (long)(info.Bytes * 2.1) : (long)(info.Bytes * 1.1);
                if (free < need)
                    throw new Exception(string.Format("目标磁盘空间不足：需要约 {0}，可用 {1}",
                        FormatSize(sameVolume ? info.Bytes * 2 : info.Bytes), FormatSize(free)));
            }

            string backup = null;
            bool journalPending = false;
            try
            {
                CopyTree(current, target);
                var copied = GetTreeInfo(target);
                if (copied.Files != info.Files || copied.Bytes != info.Bytes || copied.Links != info.Links)
                {
                    throw new Exception(string.Format("复制校验不一致：源 {0} 文件 / {1}{2}，目标 {3} 文件 / {4}{5}（源未改动）",
                        info.Files, FormatSize(info.Bytes), info.Links > 0 ? " / " + info.Links + " 链接" : "",
                        copied.Files, FormatSize(copied.Bytes), copied.Links > 0 ? " / " + copied.Links + " 链接" : ""));
                }
                log(string.Format("· 校验通过：{0} 个文件，{1}", copied.Files, FormatSize(copied.Bytes)), Ok);

                if (again) RemoveJunction(source);
                else
                {
                    backup = UniqueBackupPath(source);
                    // 先落一条 pending 记录：中途断电或被杀，也能从 journal 找到 .moved-* 备份
                    UpsertJournal(profile, journal, new JournalEntry
                    {
                        Name = item.Name,
                        Source = source,
                        Target = target,
                        Backup = backup,
                        State = "pending",
                        MovedAt = DateTime.Now.ToString("o")
                    });
                    journalPending = true;
                    Directory.Move(source, backup);
                }

                try
                {
                    CreateJunction(source, target);
                    if (!IsJunction(source)) throw new Exception("junction 创建后无法识别");
                    var linked = GetTreeInfo(source);
                    if (linked.Files != info.Files)
                        throw new Exception(string.Format("通过 junction 读到的文件数不一致：{0} vs {1}", linked.Files, info.Files));
                    log("· 已建立目录联接并校验", Ok);
                }
                catch (Exception ex)
                {
                    log("· 失败，正在回滚：" + ex.Message, Warn);
                    bool restored = false;
                    try { if (IsJunction(source)) RemoveJunction(source); }
                    catch { }
                    try
                    {
                        if (!Directory.Exists(source))
                        {
                            if (again) { CreateJunction(source, current); restored = true; }
                            else if (backup != null && Directory.Exists(backup)) { Directory.Move(backup, source); restored = true; }
                        }
                        else restored = true;
                    }
                    catch (Exception rex)
                    {
                        log("· 回滚失败，原路径现在不可用：" + rex.Message, Error);
                        log("· 数据仍在 " + (backup ?? current) + "，请手动移回 " + source, Error);
                    }
                    if (!restored) throw new Exception("迁移失败且原路径没能自动恢复：" + ex.Message);
                    throw;
                }

                UpsertJournal(profile, journal, new JournalEntry
                {
                    Name = item.Name,
                    Source = source,
                    Target = target,
                    Backup = backup,
                    State = "done",
                    MovedAt = DateTime.Now.ToString("o")
                });
                journalPending = false;

                string old = again ? current : backup;
                if (keepBackup)
                {
                    log((again ? "· 旧位置保留在 " : "· 旧副本保留在 ") + old, Info);
                }
                else
                {
                    try
                    {
                        ForceDeleteDir(old);
                        log(again ? "· 旧位置的副本已删除，空间已释放" : "· 旧副本已删除，原盘空间已释放", Ok);
                    }
                    catch (Exception ex)
                    {
                        log("· 旧位置未能完全删除（" + ex.Message + "），可手动删除：" + old, Warn);
                    }
                }
            }
            catch
            {
                // 中途失败：源恢复了就清掉目标副本；源没恢复就把副本留着，别两边都丢
                try
                {
                    bool sourceUsesTarget = IsJunction(source) && SamePath(JunctionTarget(source), target);
                    if (!sourceUsesTarget)
                    {
                        if (Directory.Exists(source))
                        {
                            try { ForceDeleteDir(target); } catch { }
                            if (journalPending) RemoveJournal(profile, journal, source);
                        }
                        else
                        {
                            log("· 目标副本保留在 " + target + "（原路径尚未恢复，先别删）", Warn);
                        }
                    }
                }
                catch { }
                throw;
            }
        }

        private static void UpsertJournal(AppProfile profile, List<JournalEntry> journal, JournalEntry entry)
        {
            for (int i = journal.Count - 1; i >= 0; i--)
                if (SamePath(journal[i].Source, entry.Source)) journal.RemoveAt(i);
            journal.Add(entry);
            SaveJournal(profile, journal);
        }

        private static void RemoveJournal(AppProfile profile, List<JournalEntry> journal, string source)
        {
            bool changed = false;
            for (int i = journal.Count - 1; i >= 0; i--)
                if (SamePath(journal[i].Source, source)) { journal.RemoveAt(i); changed = true; }
            if (changed) SaveJournal(profile, journal);
        }

        // 目标目录里已经有内容（哪怕只是空目录）就不要再往里合并
        public static bool TargetOccupied(string dir)
        {
            if (!Directory.Exists(dir)) return false;
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir)) return true;
            }
            catch { }
            return false;
        }

        public static bool SameVolume(string a, string b)
        {
            try
            {
                string ra = Path.GetPathRoot(Path.GetFullPath(a));
                string rb = Path.GetPathRoot(Path.GetFullPath(b));
                return !string.IsNullOrEmpty(ra) && string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // 按记录还原：优先把当前位置的活数据搬回原路径，旧副本只作兜底
        public static bool RollbackOne(JournalEntry entry, Logger log)
        {
            log("还原 " + entry.Name, Info);
            try
            {
                // 先确认有数据可还原，再动联接，避免原路径被拆掉却没东西放回去
                bool haveTarget = !string.IsNullOrEmpty(entry.Target) && Directory.Exists(entry.Target);
                bool haveBackup = !string.IsNullOrEmpty(entry.Backup) && Directory.Exists(entry.Backup);
                if (!haveTarget && !haveBackup)
                {
                    log("· 没有可还原的数据（目标 " + (entry.Target ?? "-") + "，备份 " + (entry.Backup ?? "-") + "），原路径未改动", Warn);
                    return false;
                }

                if (!IsJunction(entry.Source) && Directory.Exists(entry.Source))
                {
                    log("· 原位置已被占用，无法自动还原：" + entry.Source, Warn);
                    return false;
                }

                if (IsJunction(entry.Source))
                {
                    RemoveJunction(entry.Source);
                    log("· 已移除目录联接", Ok);
                }

                bool restored;
                if (haveTarget) restored = RestoreFrom(entry.Target, entry.Source, log);
                else
                {
                    Directory.Move(entry.Backup, entry.Source);
                    log("· 已用备份还原", Ok);
                    restored = true;
                }
                if (!restored) return false;

                // 还原后清掉残留的旧副本，避免新旧两处重复占用空间
                if (haveBackup && Directory.Exists(entry.Backup) && !SamePath(entry.Backup, entry.Source))
                {
                    try { ForceDeleteDir(entry.Backup); log("· 已清理旧副本 " + entry.Backup, Ok); }
                    catch (Exception ex) { log("· 旧副本未能删除（" + ex.Message + "）：" + entry.Backup, Warn); }
                }
                return restored;
            }
            catch (Exception ex)
            {
                log("· 回滚失败：" + ex.Message, Error);
                return false;
            }
        }

        // 跨盘搬回：先把新位置改名暂存，失败还能原样退回，避免两边都是半份
        private static bool RestoreFrom(string target, string source, Logger log)
        {
            string tmp = target + ".restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { Directory.Move(target, tmp); }
            catch (Exception ex) { log("· 无法暂存新位置的数据：" + ex.Message, Error); return false; }

            try
            {
                MoveDir(tmp, source);
                log("· 已把新位置的数据搬回", Ok);
                return true;
            }
            catch (Exception ex)
            {
                log("· 搬回失败：" + ex.Message, Error);
                try { if (Directory.Exists(source) && !IsJunction(source)) ForceDeleteDir(source); } catch { }
                try
                {
                    if (!Directory.Exists(target) && Directory.Exists(tmp))
                    {
                        Directory.Move(tmp, target);
                        log("· 新位置的数据已退回原处", Ok);
                    }
                }
                catch { }
                return false;
            }
        }
    }

    // ================================================================ 主题与绘制

    internal static class Theme
    {
        public static readonly Color Canvas = Color.FromArgb(255, 255, 255);
        public static readonly Color Surface = Color.FromArgb(250, 251, 252);
        public static readonly Color Hover = Color.FromArgb(246, 248, 250);
        public static readonly Color Border = Color.FromArgb(232, 235, 240);
        public static readonly Color BorderStrong = Color.FromArgb(220, 225, 232);
        public static readonly Color Ink = Color.FromArgb(15, 23, 42);
        public static readonly Color Body = Color.FromArgb(71, 85, 105);
        public static readonly Color Muted = Color.FromArgb(138, 148, 166);
        public static readonly Color Faint = Color.FromArgb(166, 175, 189);
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
        public static readonly Color AccentSoft = Color.FromArgb(238, 244, 255);
        public static readonly Color Success = Color.FromArgb(22, 163, 74);
        public static readonly Color Warn = Color.FromArgb(217, 119, 6);
        public static readonly Color Danger = Color.FromArgb(220, 38, 38);

        public static float S = 1f;

        public static int P(float value)
        {
            return (int)Math.Round(value * S);
        }

        public static float PF(float value)
        {
            return value * S;
        }

        private static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();

        // 全局字号系数：125% 缩放下也要看得清，改这里就能整体放大
        public static float FontScale = 1.25f;

        public static Font F(float size, FontStyle style)
        {
            string key = size.ToString("0.0") + "|" + (int)style;
            Font font;
            if (!Fonts.TryGetValue(key, out font))
            {
                font = new Font("Microsoft YaHei UI", size * FontScale, style);
                Fonts[key] = font;
            }
            return font;
        }

        public static Font U(float size, FontStyle style)
        {
            string key = "cons|" + size.ToString("0.0") + "|" + (int)style;
            Font font;
            if (!Fonts.TryGetValue(key, out font))
            {
                font = new Font("Consolas", size * FontScale, style);
                Fonts[key] = font;
            }
            return font;
        }
    }

    internal static class Draw
    {
        public static GraphicsPath Round(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2f;
            if (d <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void Fill(Graphics g, RectangleF rect, float radius, Color color)
        {
            if (color.A == 0) return;
            using (GraphicsPath path = Round(rect, radius))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, path);
        }

        public static void Stroke(Graphics g, RectangleF rect, float radius, Color color, float width)
        {
            if (color.A == 0) return;
            using (GraphicsPath path = Round(rect, radius))
            using (var pen = new Pen(color, width))
                g.DrawPath(pen, path);
        }

        public static void Dot(Graphics g, float x, float y, float size, Color color)
        {
            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, x, y, size, size);
        }

        public static void Text(Graphics g, string text, Font font, Color color, Rectangle rect,
            TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, font, rect, color, flags);
        }

        public const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        public const TextFormatFlags Right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        public const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        public const TextFormatFlags LeftTop = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix;
    }

    // ================================================================ 圆角面板

    internal class Card : Panel
    {
        public int Radius = 12;
        public Color FillColor = Theme.Canvas;
        public Color BorderColor = Theme.Border;
        public bool DrawBorder = true;
        public int[] Separators;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Draw.Fill(g, rect, Theme.PF(Radius), FillColor);
            if (DrawBorder) Draw.Stroke(g, rect, Theme.PF(Radius), BorderColor, 1f);

            if (Separators != null)
            {
                using (var pen = new Pen(Theme.Border))
                {
                    foreach (int y in Separators)
                    {
                        int left = Theme.P(20);
                        g.DrawLine(pen, left, Theme.P(y), Width - left, Theme.P(y));
                    }
                }
            }
        }
    }

    // ================================================================ 按钮

    internal class FlatButton : Control
    {
        public enum Style { Primary, Secondary, Ghost }
        public Style Kind = Style.Secondary;

        private bool hover;
        private bool press;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            // 自己处理点击，关掉框架的 StandardClick，否则一次点击会触发两次 Click
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = false;
#if CLICKDEBUG
            Click += delegate
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ocmig-click.log"), Text + "\r\n"); }
                catch { }
            };
#endif
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; press = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { press = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            press = false;
            Invalidate();
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) OnClick(EventArgs.Empty);
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float radius = Theme.PF(9);

            Color fill;
            Color border = Color.Transparent;
            Color text;

            if (!Enabled)
            {
                if (Kind == Style.Primary)
                {
                    fill = Color.FromArgb(219, 228, 240);
                    text = Color.White;
                }
                else if (Kind == Style.Ghost)
                {
                    fill = Theme.Canvas;
                    border = Theme.Border;
                    text = Theme.Faint;
                }
                else
                {
                    fill = Theme.Surface;
                    border = Theme.Border;
                    text = Theme.Faint;
                }
            }
            else if (Kind == Style.Primary)
            {
                fill = hover ? Theme.AccentHover : Theme.Accent;
                text = Color.White;
            }
            else if (Kind == Style.Ghost)
            {
                fill = hover ? Theme.Hover : Theme.Canvas;
                border = hover ? Theme.BorderStrong : Theme.Border;
                text = hover ? Theme.Ink : Theme.Body;
            }
            else
            {
                fill = hover ? Theme.Surface : Theme.Canvas;
                border = Theme.BorderStrong;
                text = Theme.Ink;
            }

            if (press && Enabled && Kind != Style.Ghost) fill = Theme.Hover;

            Draw.Fill(g, rect, radius, fill);
            if (border.A > 0) Draw.Stroke(g, rect, radius, border, 1f);
            Draw.Text(g, Text, Font, text, new Rectangle(0, 0, Width, Height), Draw.Center);
        }
    }

    internal class WindowButton : Control
    {
        public bool IsClose;
        private bool hover;

        public WindowButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) OnClick(EventArgs.Empty);
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hover)
            {
                Color bg = IsClose ? Color.FromArgb(254, 226, 226) : Theme.Hover;
                Draw.Fill(g, new RectangleF(6, 8, Width - 12, Height - 16), Theme.PF(7), bg);
            }

            using (var pen = new Pen(IsClose && hover ? Theme.Danger : Theme.Body, 1.4f))
            {
                float cx = Width / 2f;
                float cy = Height / 2f;
                float r = Theme.PF(4.5f);
                if (IsClose)
                {
                    g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                    g.DrawLine(pen, cx + r, cy - r, cx - r, cy + r);
                }
                else
                {
                    g.DrawLine(pen, cx - r, cy, cx + r, cy);
                }
            }
        }
    }

    // ================================================================ 勾选项

    internal class CheckOption : Control
    {
        private bool hover;
        private bool isChecked;

        public event EventHandler CheckedChanged;

        public CheckOption()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) Checked = !Checked;
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float box = Theme.PF(18);
            float top = (Height - box) / 2f;
            var rect = new RectangleF(0.5f, top, box, box);

            if (isChecked)
            {
                Draw.Fill(g, rect, Theme.PF(5), Enabled ? Theme.Accent : Theme.Faint);
                using (var pen = new Pen(Color.White, Theme.PF(1.9f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    float x = rect.X;
                    float y = rect.Y;
                    g.DrawLines(pen, new PointF[] {
                        new PointF(x + box * 0.26f, y + box * 0.53f),
                        new PointF(x + box * 0.44f, y + box * 0.71f),
                        new PointF(x + box * 0.76f, y + box * 0.33f)
                    });
                }
            }
            else
            {
                Draw.Fill(g, rect, Theme.PF(5), Enabled ? Color.White : Theme.Surface);
                Draw.Stroke(g, rect, Theme.PF(5), hover ? Theme.BorderStrong : Theme.Border, Theme.PF(1.3f));
            }

            Color text = Enabled ? (hover ? Theme.Ink : Theme.Body) : Theme.Faint;
            var textRect = new Rectangle((int)(box + Theme.PF(10)), 0, Width - (int)box - Theme.P(10), Height);
            Draw.Text(g, Text, Font, text, textRect, Draw.Left);
        }
    }

    // ================================================================ 迁移项行

    internal class ItemRow : Control
    {
        public MigItem Item;
        public TreeInfo Info = new TreeInfo();
        public bool Present;
        public bool Available;
        public bool Migrated;
        public string MigratedTo;
        public bool SizeKnown;

        private bool hover;
        private bool hoverOpen;
        private bool isChecked;

        public event EventHandler CheckedChanged;

        public ItemRow()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; hoverOpen = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool over = Migrated && Present && OpenRect().Contains(e.Location);
            if (over != hoverOpen) { hoverOpen = over; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
            {
                if (Migrated && Present && OpenRect().Contains(e.Location)) Ops.OpenFolder(MigratedTo);
                else if (Available) Checked = !Checked;
            }
            base.OnMouseUp(e);
        }

        internal static int OpenButtonWidth()
        {
            return TextRenderer.MeasureText("打开", Theme.F(8.2f, FontStyle.Regular)).Width + Theme.P(22);
        }

        internal Rectangle OpenRect()
        {
            int sizeWidth = TextRenderer.MeasureText("000.0 MB", Theme.F(9f, FontStyle.Regular)).Width + Theme.P(16);
            int w = OpenButtonWidth();
            return new Rectangle(Width - sizeWidth - w, Theme.P(16), w - Theme.P(8), Theme.P(26));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (hover && Available)
            {
                Draw.Fill(g, new RectangleF(Theme.P(4), Theme.P(3), Width - Theme.P(8), Height - Theme.P(6)),
                    Theme.PF(9), Theme.Hover);
            }

            float box = Theme.PF(18);
            float top = (Height - box) / 2f;
            var boxRect = new RectangleF(Theme.P(20) + 0.5f, top, box, box);

            if (isChecked)
            {
                Draw.Fill(g, boxRect, Theme.PF(5), Present ? Theme.Accent : Theme.Faint);
                using (var pen = new Pen(Color.White, Theme.PF(1.9f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new PointF[] {
                        new PointF(boxRect.X + box * 0.26f, boxRect.Y + box * 0.53f),
                        new PointF(boxRect.X + box * 0.44f, boxRect.Y + box * 0.71f),
                        new PointF(boxRect.X + box * 0.76f, boxRect.Y + box * 0.33f)
                    });
                }
            }
            else
            {
                Draw.Fill(g, boxRect, Theme.PF(5), Present ? Color.White : Theme.Surface);
                Draw.Stroke(g, boxRect, Theme.PF(5), Present && hover ? Theme.BorderStrong : Theme.Border, Theme.PF(1.3f));
            }

            Font nameFont = Theme.F(9.2f, FontStyle.Bold);
            int textLeft = Theme.P(52);
            int sizeWidth = TextRenderer.MeasureText("000.0 MB", Theme.F(9f, FontStyle.Regular)).Width + Theme.P(16);
            int openWidth = Migrated ? OpenButtonWidth() : 0;
            int textWidth = Width - textLeft - sizeWidth - openWidth - Theme.P(18);
            Color nameColor = Present ? Theme.Ink : Theme.Faint;
            Draw.Text(g, Item.Name, nameFont, nameColor,
                new Rectangle(textLeft, Theme.P(11), textWidth, Theme.P(24)), Draw.Left);

            if (Migrated)
            {
                Font chipFont = Theme.F(7.6f, FontStyle.Bold);
                int nameWidth = TextRenderer.MeasureText(Item.Name, nameFont).Width;
                int chipWidth = TextRenderer.MeasureText("已迁移", chipFont).Width + Theme.P(16);
                var chip = new RectangleF(textLeft + nameWidth + Theme.P(8), Theme.P(13), chipWidth, Theme.P(18));
                Draw.Fill(g, chip, chip.Height / 2f, Color.FromArgb(232, 246, 236));
                Draw.Text(g, "已迁移", chipFont, Theme.Success, Rectangle.Round(chip), Draw.Center);
            }

            string sub;
            Color subColor;
            if (!Present)
            {
                sub = "未找到该目录";
                subColor = Theme.Faint;
            }
            else if (Migrated)
            {
                sub = string.IsNullOrEmpty(MigratedTo) ? "当前不在原路径" : "当前位置  " + MigratedTo;
                subColor = Theme.Success;
            }
            else
            {
                sub = Item.Source;
                subColor = Theme.Muted;
            }

            Draw.Text(g, sub, Theme.F(8.2f, FontStyle.Regular), subColor,
                new Rectangle(textLeft, Theme.P(33), textWidth, Theme.P(20)),
                Draw.Left | TextFormatFlags.EndEllipsis);

            if (Migrated)
            {
                Rectangle open = OpenRect();
                Draw.Fill(g, open, Theme.PF(7), hoverOpen ? Theme.Accent : Theme.AccentSoft);
                Draw.Text(g, "打开", Theme.F(8.2f, FontStyle.Regular),
                    hoverOpen ? Color.White : Theme.Accent, open, Draw.Center);
            }

            if (Present)
            {
                string sizeText = SizeKnown ? Ops.FormatSize(Info.Bytes) : "…";
                Draw.Text(g, sizeText, Theme.F(9f, FontStyle.Regular), SizeKnown ? Theme.Body : Theme.Faint,
                    new Rectangle(Width - sizeWidth - Theme.P(12), 0, sizeWidth, Height), Draw.Right);
            }
        }
    }

    // ================================================================ 状态胶囊

    internal class StatusChip : Control
    {
        private int state; // 0 检测中，1 运行中，2 未运行
        private string appName = "OpenChamber";

        public StatusChip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public void SetRunning(bool value)
        {
            int next = value ? 1 : 2;
            if (state == next) return;
            state = next;
            Invalidate();
        }

        public void SetUnknown()
        {
            if (state == 0) return;
            state = 0;
            Invalidate();
        }

        public void SetAppName(string value)
        {
            if (string.IsNullOrEmpty(value) || appName == value) return;
            appName = value;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Draw.Fill(g, rect, Height / 2f, Theme.Surface);
            Draw.Stroke(g, rect, Height / 2f, Theme.Border, 1f);

            float dot = Theme.PF(6);
            Draw.Dot(g, Theme.P(12), (Height - dot) / 2f, dot,
                state == 0 ? Theme.Faint : state == 1 ? Theme.Warn : Theme.Faint);

            Draw.Text(g, appName + (state == 0 ? " 检测中" : state == 1 ? " 运行中" : " 未运行"),
                Theme.F(8.3f, FontStyle.Regular), Theme.Body,
                new Rectangle(Theme.P(24), 0, Width - Theme.P(30), Height), Draw.Left);
        }
    }

    // ================================================================ 左侧应用栏

    internal class SideRail : Control
    {
        public List<AppProfile> Profiles = new List<AppProfile>();
        public event EventHandler Changed;

        private int selected;
        private int hover = -1;
        private bool[] running = new bool[0];
        private bool[] migrated = new bool[0];

        public SideRail()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            // 自己处理点击，避免一次点击触发两次事件
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            BackColor = Theme.Surface;
            TabStop = false;
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value < 0 || value >= Profiles.Count || selected == value) return;
                selected = value;
                Invalidate();
            }
        }

        public void SetStatus(bool[] run, bool[] mig)
        {
            if (Same(running, run) && Same(migrated, mig)) return;
            running = run ?? new bool[0];
            migrated = mig ?? new bool[0];
            Invalidate();
        }

        private static bool Same(bool[] a, bool[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private Rectangle TabRect(int index)
        {
            int top = Theme.P(46);
            int step = Theme.P(74);
            return new Rectangle(Theme.P(16), top + index * step, Width - Theme.P(32), Theme.P(64));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = -1;
            for (int i = 0; i < Profiles.Count; i++)
                if (TabRect(i).Contains(e.Location)) index = i;
            if (index != hover)
            {
                hover = index;
                Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1;
            Cursor = Cursors.Default;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (Enabled && e.Button == MouseButtons.Left)
            {
                for (int i = 0; i < Profiles.Count; i++)
                {
                    if (!TabRect(i).Contains(e.Location)) continue;
                    if (i != selected)
                    {
                        selected = i;
                        Invalidate();
                        if (Changed != null) Changed(this, EventArgs.Empty);
                    }
                    break;
                }
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);

            using (var pen = new Pen(Theme.Border))
                g.DrawLine(pen, Width - 1, 0, Width - 1, Height);

            Draw.Text(g, "应用", Theme.F(8.2f, FontStyle.Bold), Theme.Muted,
                new Rectangle(Theme.P(24), Theme.P(12), Theme.P(120), Theme.P(18)), Draw.Left);

            for (int i = 0; i < Profiles.Count; i++)
            {
                Rectangle r = TabRect(i);
                var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
                bool on = i == selected;

                if (on)
                {
                    Draw.Fill(g, rf, Theme.PF(11), Theme.Canvas);
                    Draw.Stroke(g, rf, Theme.PF(11), Theme.BorderStrong, 1f);
                    Draw.Fill(g, new RectangleF(rf.X + Theme.PF(1), rf.Y + Theme.PF(15),
                        Theme.PF(3), rf.Height - Theme.PF(30)), Theme.PF(1.5f),
                        Enabled ? Theme.Accent : Theme.Faint);
                }
                else if (i == hover && Enabled)
                {
                    Draw.Fill(g, rf, Theme.PF(11), Theme.Hover);
                }

                Color title = !Enabled ? Theme.Faint : on ? Theme.Ink : Theme.Body;
                Draw.Text(g, Profiles[i].Name, Theme.F(9.2f, FontStyle.Bold), title,
                    new Rectangle(r.X + Theme.P(16), r.Y + Theme.P(12), r.Width - Theme.P(26), Theme.P(24)), Draw.Left);

                bool run = i < running.Length && running[i];
                bool mig = i < migrated.Length && migrated[i];
                string note = run ? (mig ? "运行中 · 已迁移" : "运行中") : (mig ? "已迁移" : "未迁移");
                Color dot = !Enabled ? Theme.Faint : run ? Theme.Warn : mig ? Theme.Success : Theme.Faint;

                Rectangle line = new Rectangle(r.X + Theme.P(16), r.Y + Theme.P(39), r.Width - Theme.P(26), Theme.P(20));
                float size = Theme.PF(6);
                Draw.Dot(g, line.X, line.Y + (line.Height - size) / 2f, size, dot);
                Draw.Text(g, note, Theme.F(7.9f, FontStyle.Regular), Enabled ? Theme.Muted : Theme.Faint,
                    new Rectangle(line.X + Theme.P(12), line.Y, line.Width - Theme.P(12), line.Height), Draw.Left);
            }

            int bottom = Height - Theme.P(98);
            Draw.Text(g, "提示", Theme.F(8.2f, FontStyle.Bold), Theme.Muted,
                new Rectangle(Theme.P(24), bottom, Theme.P(120), Theme.P(20)), Draw.Left);

            string[] tips = { "复制校验后才删旧副本", "失败自动回滚，源目录不动", "原路径保留联接，更新照常" };
            for (int i = 0; i < tips.Length; i++)
                Draw.Text(g, "· " + tips[i], Theme.F(7.8f, FontStyle.Regular), Theme.Faint,
                    new Rectangle(Theme.P(24), bottom + Theme.P(24) + i * Theme.P(20), Width - Theme.P(40), Theme.P(18)), Draw.Left);
        }
    }

    // ================================================================ 输入框

    internal class InputField : Control
    {
        public readonly TextBox Box;
        public readonly FlatButton Browse;
        private bool focused;

        public InputField()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Canvas;

            Box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Font = new Font("Microsoft YaHei UI", 9.5f),
                BackColor = Theme.Canvas,
                ForeColor = Theme.Ink
            };
            Box.Enter += delegate { focused = true; Invalidate(); };
            Box.Leave += delegate { focused = false; Invalidate(); };
            Controls.Add(Box);

            Browse = new FlatButton { Text = "浏览", Kind = FlatButton.Style.Ghost, Font = Theme.F(8.6f, FontStyle.Regular) };
            Controls.Add(Browse);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int pad = Theme.P(14);
            int buttonWidth = Theme.P(58);
            Box.Location = new Point(pad, (Height - Box.PreferredHeight) / 2);
            Box.Width = Width - pad - buttonWidth - Theme.P(12);
            Browse.Bounds = new Rectangle(Width - buttonWidth - Theme.P(6), Theme.P(7), buttonWidth, Height - Theme.P(14));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Draw.Fill(g, rect, Theme.PF(10), Theme.Canvas);
            Draw.Stroke(g, rect, Theme.PF(10), focused ? Theme.Accent : Theme.BorderStrong, focused ? 1.4f : 1f);
        }
    }

    // ================================================================ 顶栏进度

    internal class ProgressLine : Control
    {
        private readonly Timer timer;
        private float position;
        private bool running;

        public ProgressLine()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Canvas;
            timer = new Timer();
            timer.Interval = 16;
            timer.Tick += delegate
            {
                position += Theme.PF(7);
                if (position > Width + Theme.P(120)) position = -Theme.P(120);
                Invalidate();
            };
        }

        public void SetRunning(bool value)
        {
            running = value;
            if (value) { position = -Theme.P(120); timer.Start(); }
            else timer.Stop();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!running) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Theme.P(120);
            Draw.Fill(g, new RectangleF(position, 0, w, Height), Height / 2f, Theme.Accent);
        }
    }

    // ================================================================ 主窗口

    internal sealed class MainForm : Form
    {
        private Card itemCard;
        private readonly List<ItemRow> rows = new List<ItemRow>();
        private readonly List<MigItem> items = new List<MigItem>();
        private InputField target;
        private CheckOption optKill;
        private CheckOption optKeep;
        private FlatButton btnPreview;
        private FlatButton btnMigrate;
        private FlatButton btnRollback;
        private FlatButton btnCache;
        private FlatButton btnRefresh;
        private FlatButton btnClearLog;
        private StatusChip chip;
        private ProgressLine progress;
        private RichTextBox log;
        private Label lblPlan;
        private WindowButton btnMin;
        private WindowButton btnClose;
        private SideRail rail;
        private readonly Dictionary<string, TreeInfo> sizes = new Dictionary<string, TreeInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> runningStatus = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> scanning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> scanGen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool[]> savedChecks = new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> warnedBackups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int scanSeq;
        private bool statusScanning;
        private Timer statusTimer;
        private bool[] migratedStates = new bool[0];
        private bool sizeLogged;
        private static Image logo;

        private List<AppProfile> profiles;
        private AppProfile profile;
        private readonly Dictionary<string, string> lastTargets = new Dictionary<string, string>();

        private bool busy;
        private bool loaded;
        private bool refreshing;

        private const int DesignWidth = 1140;
        private const int RailWidth = 216;
        private const int DesignBaseHeight = 812;
        private const int RowHeight = 58;

        // 纵向节奏：绘制和布局都取这些值，两边不会对不上
        private int YBanner { get { return Theme.P(136); } }
        private int YSelectLabel { get { return Theme.P(210); } }
        private int YCard { get { return Theme.P(238); } }
        private int CardHeight { get { return Theme.P(items.Count * RowHeight + 2); } }
        private int YTargetLabel { get { return YCard + CardHeight + Theme.P(12); } }
        private int YTarget { get { return YTargetLabel + Theme.P(24); } }
        private int YPlan { get { return YTarget + Theme.P(48); } }
        private int YOptions { get { return YPlan + Theme.P(26); } }
        private int YButtons { get { return YOptions + Theme.P(32); } }
        private int YLogLabel { get { return YButtons + Theme.P(50); } }
        private int YLogPanel { get { return YLogLabel + Theme.P(26); } }

        private int DesignHeight { get { return DesignBaseHeight; } }

        public MainForm()
        {
            profiles = Ops.Profiles();
            profile = profiles[0];
            items.AddRange(Ops.AllItems(profile));

            Text = "OpenCode Relocator";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Canvas;
            DoubleBuffered = true;
            KeyPreview = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

            BuildUi();
            ApplyScale();
            RefreshState(false);

            logo = LoadImage("OpenCodeRelocator.png");
            try
            {
                using (Stream s = typeof(MainForm).Assembly.GetManifestResourceStream("OpenCodeRelocator.ico"))
                {
                    if (s != null) Icon = new Icon(s, new Size(32, 32));
                }
            }
            catch { }

            Log("把程序与数据搬到其它磁盘，原路径保留目录联接；快捷方式、自动更新、卸载器都无需改动。", Ops.Info);
            Log("当前对象：" + profile.Name + "，迁移前会先关闭它。目标必须是本地磁盘。", Ops.Info);
            LogProfileSummary();

            long free = Ops.GetFreeSpace(target.Box.Text);
            if (free > 0)
            {
                string root = Path.GetPathRoot(Path.GetFullPath(target.Box.Text));
                Log(string.Format("目标盘 {0} 可用 {1}", root, Ops.FormatSize(free)), Ops.Info);
            }

            // 后台服务/界面的开关不会通知我们，定时轻量刷新运行状态
            statusTimer = new Timer();
            statusTimer.Interval = 2500;
            statusTimer.Tick += delegate { if (!busy) StartStatusScan(); };
            statusTimer.Start();
        }

        private void LogProfileSummary()
        {
            foreach (ItemRow row in rows)
            {
                if (!row.Migrated) continue;
                Log(string.Format("{0} 已迁移到 {1}", row.Item.Name, row.MigratedTo), Ops.Ok);
            }
        }

        // ------------------------------------------------------------ DPI

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            try
            {
                int attribute = 33; // DWMWA_WINDOW_CORNER_PREFERENCE
                int preference = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, attribute, ref preference, 4);
            }
            catch { }

            ApplyScale();
        }

        private void ApplyScale()
        {
            float scale = 1f;
            try
            {
                uint dpi = GetDpiForWindow(Handle);
                if (dpi >= 96) scale = dpi / 96f;
            }
            catch
            {
                try { using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f; }
                catch { scale = 1f; }
            }

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            if (DesignHeight * scale > area.Height * 0.96f)
                scale = area.Height * 0.96f / DesignHeight;
            if (DesignWidth * scale > area.Width * 0.96f)
                scale = area.Width * 0.96f / DesignWidth;

            Theme.S = scale;
            ClientSize = new Size(Theme.P(DesignWidth), Theme.P(DesignHeight));

            try
            {
                Rectangle area2 = Screen.FromControl(this).WorkingArea;
                if (Bottom > area2.Bottom) Top = Math.Max(area2.Top, area2.Bottom - Height);
                if (Right > area2.Right) Left = Math.Max(area2.Left, area2.Right - Width);
            }
            catch { }

            LayoutAll();
            Invalidate(true);
        }

        private void LayoutAll()
        {
            int w = ClientSize.Width;
            int left = Theme.P(RailWidth + 32);
            int right = w - Theme.P(32);
            int content = right - left;

            progress.Bounds = new Rectangle(0, Theme.P(48), w, Theme.P(2));
            rail.Bounds = new Rectangle(0, Theme.P(50), Theme.P(RailWidth), ClientSize.Height - Theme.P(50));

            btnClose.Bounds = new Rectangle(w - Theme.P(44), 0, Theme.P(44), Theme.P(48));
            btnMin.Bounds = new Rectangle(w - Theme.P(88), 0, Theme.P(44), Theme.P(48));

            chip.Bounds = new Rectangle(right - ChipWidth(), Theme.P(67), ChipWidth(), Theme.P(26));

            itemCard.Bounds = new Rectangle(left, YCard, content, CardHeight);
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Bounds = new Rectangle(1, Theme.P(1 + i * RowHeight), itemCard.Width - 2, Theme.P(RowHeight));
            }

            target.Bounds = new Rectangle(left, YTarget, content, Theme.P(42));
            lblPlan.Bounds = new Rectangle(left + Theme.P(1), YPlan, content, Theme.P(20));

            optKill.Bounds = new Rectangle(left, YOptions, Theme.P(310), Theme.P(26));
            optKeep.Bounds = new Rectangle(left + Theme.P(330), YOptions, Theme.P(300), Theme.P(26));

            int gap = Theme.P(12);
            int x = left;
            int migrateWidth = TextRenderer.MeasureText("开始迁移", Theme.F(9.2f, FontStyle.Bold)).Width + Theme.P(44);
            int previewWidth = TextRenderer.MeasureText("预览计划", Theme.F(9f, FontStyle.Regular)).Width + Theme.P(40);
            int rollbackWidth = TextRenderer.MeasureText("回滚", Theme.F(9f, FontStyle.Regular)).Width + Theme.P(34);
            btnMigrate.Bounds = new Rectangle(x, YButtons, migrateWidth, Theme.P(42));
            x += migrateWidth + gap;
            btnPreview.Bounds = new Rectangle(x, YButtons, previewWidth, Theme.P(42));
            x += previewWidth + gap;
            btnRollback.Bounds = new Rectangle(x, YButtons, rollbackWidth, Theme.P(42));

            int cacheWidth = TextRenderer.MeasureText("清理升级缓存", Theme.F(8.6f, FontStyle.Regular)).Width + Theme.P(28);
            btnCache.Bounds = new Rectangle(right - cacheWidth, YButtons + Theme.P(8), cacheWidth, Theme.P(30));
            int refreshWidth = TextRenderer.MeasureText("刷新", Theme.F(8.6f, FontStyle.Regular)).Width + Theme.P(28);
            btnRefresh.Bounds = new Rectangle(right - cacheWidth - refreshWidth - Theme.P(10), YButtons + Theme.P(8), refreshWidth, Theme.P(30));

            int clearWidth = TextRenderer.MeasureText("清空", Theme.F(8.4f, FontStyle.Regular)).Width + Theme.P(24);
            btnClearLog.Bounds = new Rectangle(right - clearWidth, YLogLabel - Theme.P(2), clearWidth, Theme.P(28));

            logPanel.Bounds = new Rectangle(left, YLogPanel, content, Theme.P(776) - YLogPanel);
            logPanel.Padding = new Padding(Theme.P(12), Theme.P(8), Theme.P(8), Theme.P(8));
        }

        private Card logPanel;

        private static int ChipWidth()
        {
            int a = TextRenderer.MeasureText("OpenChamber 运行中", Theme.F(8.3f, FontStyle.Regular)).Width;
            int b = TextRenderer.MeasureText("OpenCode 运行中", Theme.F(8.3f, FontStyle.Regular)).Width;
            return Math.Max(a, b) + Theme.P(40);
        }

        // ------------------------------------------------------------ 构建界面

        private void BuildUi()
        {
            progress = new ProgressLine();
            Controls.Add(progress);

            btnClose = new WindowButton { IsClose = true };
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            btnMin = new WindowButton();
            btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };
            Controls.Add(btnMin);

            chip = new StatusChip();
            Controls.Add(chip);

            rail = new SideRail();
            rail.Profiles = profiles;
            rail.Changed += delegate { SwitchProfile(rail.SelectedIndex); };
            Controls.Add(rail);

            itemCard = new Card { Radius = 12, FillColor = Theme.Canvas, BorderColor = Theme.Border };
            Controls.Add(itemCard);

            target = new InputField();
            target.Box.TextChanged += delegate { UpdatePlan(); };
            target.Browse.Click += delegate { Browse(); };
            Controls.Add(target);

            lblPlan = new Label
            {
                Text = "",
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                AutoSize = false,
                Font = Theme.F(8.2f, FontStyle.Regular)
            };
            Controls.Add(lblPlan);

            optKill = new CheckOption
            {
                Text = "迁移前自动结束 " + profile.ShortName,
                Checked = true,
                Font = Theme.F(9f, FontStyle.Regular)
            };
            Controls.Add(optKill);

            optKeep = new CheckOption
            {
                Text = "保留旧副本（占用原盘空间）",
                Font = Theme.F(9f, FontStyle.Regular)
            };
            Controls.Add(optKeep);

            btnMigrate = new FlatButton
            {
                Text = "开始迁移",
                Kind = FlatButton.Style.Primary,
                Font = Theme.F(9.3f, FontStyle.Bold)
            };
            btnMigrate.Click += delegate { StartMigrate(); };
            Controls.Add(btnMigrate);

            btnPreview = new FlatButton
            {
                Text = "预览计划",
                Kind = FlatButton.Style.Secondary,
                Font = Theme.F(9f, FontStyle.Regular)
            };
            btnPreview.Click += delegate { Preview(); };
            Controls.Add(btnPreview);

            btnRollback = new FlatButton
            {
                Text = "回滚",
                Kind = FlatButton.Style.Ghost,
                Font = Theme.F(9f, FontStyle.Regular)
            };
            btnRollback.Click += delegate { RunRollback(); };
            Controls.Add(btnRollback);

            btnCache = new FlatButton
            {
                Text = "清理升级缓存",
                Kind = FlatButton.Style.Ghost,
                Font = Theme.F(8.6f, FontStyle.Regular)
            };
            btnCache.Click += delegate { CleanCache(); };
            Controls.Add(btnCache);

            btnRefresh = new FlatButton
            {
                Text = "刷新",
                Kind = FlatButton.Style.Ghost,
                Font = Theme.F(8.6f, FontStyle.Regular)
            };
            btnRefresh.Click += delegate { RefreshState(true); };
            Controls.Add(btnRefresh);

            btnClearLog = new FlatButton
            {
                Text = "清空",
                Kind = FlatButton.Style.Ghost,
                Font = Theme.F(8.4f, FontStyle.Regular)
            };
            btnClearLog.Click += delegate { log.Clear(); };
            Controls.Add(btnClearLog);

            logPanel = new Card { Radius = 10, FillColor = Theme.Surface, BorderColor = Theme.Border, Padding = new Padding(Theme.P(12), Theme.P(8), Theme.P(8), Theme.P(8)) };
            log = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface,
                ForeColor = Theme.Body,
                Font = Theme.U(8.6f, FontStyle.Regular),
                DetectUrls = false,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            logPanel.Controls.Add(log);
            Controls.Add(logPanel);
            RebuildRows();
            target.Box.Text = SuggestTarget(profile);
        }

        private void RebuildRows()
        {
            // 复用已有行：切换应用时不再销毁/重建窗口句柄（那一步要 50 ms 以上）
            while (rows.Count > items.Count)
            {
                int last = rows.Count - 1;
                itemCard.Controls.Remove(rows[last]);
                rows[last].Dispose();
                rows.RemoveAt(last);
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (i < rows.Count)
                {
                    rows[i].Item = items[i];
                    continue;
                }

                var row = new ItemRow { Item = items[i], Font = Theme.F(9.5f, FontStyle.Regular) };
                row.CheckedChanged += delegate { if (!refreshing) { UpdatePlan(); SaveChecks(); } };
                rows.Add(row);
                itemCard.Controls.Add(row);
            }

            var separators = new List<int>();
            for (int i = 1; i < items.Count; i++) separators.Add(i * RowHeight);
            itemCard.Separators = separators.ToArray();
            LayoutAll();
        }

        private void SwitchProfile(int index)
        {
            if (busy || index < 0 || index >= profiles.Count) return;
            if (profile != null && !string.IsNullOrEmpty(target.Box.Text))
                lastTargets[profile.Key] = target.Box.Text.Trim();

            profile = profiles[index];
            items.Clear();
            items.AddRange(Ops.AllItems(profile));
            RebuildRows();
            optKill.Text = "迁移前自动结束 " + profile.ShortName;
            chip.SetAppName(profile.ShortName);

            target.Box.Text = SuggestTarget(profile);
            loaded = false;
            sizeLogged = false;
            RefreshState(false);

            string saved;
            if (lastTargets.TryGetValue(profile.Key, out saved) && !string.IsNullOrEmpty(saved))
                target.Box.Text = saved;

            log.Clear();
            Log("已切换到 " + profile.Name, Ops.Info);
            ApplySizes();
            TryLogSizes();
            LogProfileSummary();
            Invalidate(true);
        }

        private static Image LoadImage(string name)
        {
            try
            {
                using (Stream s = typeof(MainForm).Assembly.GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    using (var img = Image.FromStream(s)) return new Bitmap(img);
                }
            }
            catch { return null; }
        }

        private static string SuggestTarget(AppProfile profile)
        {
            // 优先挑一块不是源盘的本地盘，否则“搬到其它磁盘”这件事就没意义
            string sourceRoot = "";
            try { sourceRoot = Path.GetPathRoot(Path.GetFullPath(profile.App)); }
            catch { }

            DriveInfo best = null;
            DriveInfo fallback = null;
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                        if (fallback == null || drive.AvailableFreeSpace > fallback.AvailableFreeSpace) fallback = drive;
                        if (string.Equals(drive.RootDirectory.FullName.TrimEnd('\\'), sourceRoot.TrimEnd('\\'),
                                StringComparison.OrdinalIgnoreCase)) continue;
                        if (best == null || drive.AvailableFreeSpace > best.AvailableFreeSpace) best = drive;
                    }
                    catch { }
                }
            }
            catch { }

            DriveInfo pick = best != null ? best : fallback;
            string root = pick != null ? pick.RootDirectory.FullName : @"C:\";
            return Path.Combine(root, @"Apps\" + profile.Folder);
        }

        // ------------------------------------------------------------ 背景绘制

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Canvas);

            int w = ClientSize.Width;
            int left = Theme.P(RailWidth + 32);
            int content = w - Theme.P(32) - left;

            // 标题栏
            var logoRect = new Rectangle(Theme.P(19), Theme.P(14), Theme.P(20), Theme.P(20));
            if (logo != null)
            {
                InterpolationMode oldMode = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(logo, logoRect);
                g.InterpolationMode = oldMode;
            }
            else
            {
                Draw.Fill(g, logoRect, Theme.PF(5), Theme.Accent);
            }
            Draw.Text(g, "OpenCode Relocator", Theme.F(9.3f, FontStyle.Bold), Theme.Ink,
                new Rectangle(Theme.P(47), 0, Theme.P(400), Theme.P(48)), Draw.Left);

            // 标题区
            Draw.Text(g, "迁移 " + profile.Name, Theme.F(15.5f, FontStyle.Bold), Theme.Ink,
                new Rectangle(left, Theme.P(62), content - Theme.P(200), Theme.P(36)), Draw.LeftTop);
            Draw.Text(g, "把程序与数据搬到其它磁盘，原路径保留目录联接；更新、快捷方式、卸载照常工作",
                Theme.F(8.8f, FontStyle.Regular), Theme.Body,
                new Rectangle(left, Theme.P(102), content - Theme.P(20), Theme.P(22)), Draw.LeftTop);

            // 提示条
            var banner = new Rectangle(left, YBanner, content, Theme.P(64));
            Draw.Fill(g, banner, Theme.PF(10), Theme.Surface);
            Draw.Fill(g, new RectangleF(banner.X + Theme.P(16), banner.Y + Theme.P(18), Theme.P(3), Theme.P(28)), Theme.PF(2), Theme.Accent);
            Draw.Text(g, "迁移会先关闭 " + profile.ShortName + "，请在本窗口操作；目标必须是本地磁盘。",
                Theme.F(8.6f, FontStyle.Regular), Theme.Body,
                new Rectangle(banner.X + Theme.P(30), banner.Y + Theme.P(9), banner.Width - Theme.P(44), Theme.P(20)), Draw.Left);
            Draw.Text(g, "已经迁移过的项目也可以再勾选，改到别的位置（会从当前位置搬走，不会重复占用空间）。",
                Theme.F(8.4f, FontStyle.Regular), Theme.Muted,
                new Rectangle(banner.X + Theme.P(30), banner.Y + Theme.P(35), banner.Width - Theme.P(44), Theme.P(20)), Draw.Left);

            // 分组标题
            Draw.Text(g, "选择内容", Theme.F(8.4f, FontStyle.Bold), Theme.Muted,
                new Rectangle(left + Theme.P(2), YSelectLabel, Theme.P(200), Theme.P(22)), Draw.LeftTop);
            Draw.Text(g, "目标目录", Theme.F(8.4f, FontStyle.Bold), Theme.Muted,
                new Rectangle(left + Theme.P(2), YTargetLabel, Theme.P(200), Theme.P(22)), Draw.LeftTop);
            Draw.Text(g, "日志", Theme.F(8.4f, FontStyle.Bold), Theme.Muted,
                new Rectangle(left + Theme.P(2), YLogLabel, Theme.P(100), Theme.P(22)), Draw.LeftTop);

            // 页脚
            Draw.Text(g, "记录文件  " + profile.JournalFile, Theme.F(7.8f, FontStyle.Regular), Theme.Faint,
                new Rectangle(left, Theme.P(782), content, Theme.P(20)), Draw.LeftTop);
        }

        // ------------------------------------------------------------ 状态刷新

        private void RefreshState(bool rescan)
        {
            refreshing = true;
            List<MigItem> all = Ops.AllItems(profile);
            bool[] saved;
            bool hasSaved = savedChecks.TryGetValue(profile.Key, out saved);
            for (int i = 0; i < rows.Count && i < all.Count; i++)
            {
                MigItem item = all[i];
                bool exists = Directory.Exists(item.Source);
                bool junction = exists && Ops.IsJunction(item.Source);

                rows[i].Item = item;
                rows[i].Present = exists;
                rows[i].Available = exists;
                rows[i].Migrated = junction;
                rows[i].MigratedTo = junction ? Ops.JunctionTarget(item.Source) : null;

                TreeInfo info;
                if (sizes.TryGetValue(SizeKey(item.Source), out info))
                {
                    rows[i].Info = info;
                    rows[i].SizeKnown = true;
                }
                else
                {
                    rows[i].Info = new TreeInfo();
                    rows[i].SizeKnown = false;
                }

                if (junction || !exists) rows[i].Checked = false;
                else if (hasSaved && i < saved.Length) rows[i].Checked = saved[i];
                else if (!loaded) rows[i].Checked = true;
            }
            refreshing = false;

            if (!loaded)
            {
                foreach (ItemRow row in rows)
                {
                    if (!row.Migrated) continue;
                    string root = MigratedRoot(row);
                    if (!string.IsNullOrEmpty(root)) { target.Box.Text = root; break; }
                }
            }
            loaded = true;

            migratedStates = new bool[profiles.Count];
            for (int i = 0; i < profiles.Count; i++)
            {
                foreach (MigItem item in Ops.AllItems(profiles[i]))
                    if (Ops.IsJunction(item.Source)) { migratedStates[i] = true; break; }
            }

            bool running;
            if (runningStatus.TryGetValue(profile.Key, out running)) chip.SetRunning(running);
            else chip.SetUnknown();

            List<JournalEntry> journal = Ops.LoadJournal(profile);
            btnRollback.Enabled = !busy && journal.Count > 0;
            if (!string.IsNullOrEmpty(Ops.LastJournalError)) Log(Ops.LastJournalError + "，回滚可能不可用", Ops.Warn);

            foreach (ItemRow row in rows) row.Invalidate();
            rail.SelectedIndex = profiles.IndexOf(profile);
            PushRailStatus();
            UpdatePlan();
            ApplySizes();
            StartSizeScan(rescan);
            StartStatusScan();
            WarnLeftoverBackups();
        }

        // 上次迁移如果中途断电/被杀，备份目录会留在源路径旁边，这里提醒一下
        private void WarnLeftoverBackups()
        {
            foreach (MigItem item in Ops.AllItems(profile))
            {
                string dir = Path.GetDirectoryName(item.Source);
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    foreach (string backup in Directory.EnumerateDirectories(dir, Path.GetFileName(item.Source) + ".moved-*"))
                    {
                        if (!warnedBackups.Add(backup)) continue;
                        Log("发现上次迁移留下的备份目录：" + backup +
                            "（若原路径不可用，请把它移回 " + item.Source + "）", Ops.Warn);
                    }
                }
                catch { }
            }
        }

        // 缓存键带上联接目标：再次迁移换了盘，旧的大小不能继续沿用
        private static string SizeKey(string source)
        {
            string target = null;
            try { if (Ops.IsJunction(source)) target = Ops.JunctionTarget(source); }
            catch { }
            return source + "|" + (target ?? "");
        }

        // 目录统计很重（OpenCode 约 1 GB），全部放到后台，界面先出来；结果按路径缓存复用。
        private void StartSizeScan(bool rescan)
        {
            var need = new List<string>();
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (MigItem item in Ops.AllItems(profile))
            {
                string key = SizeKey(item.Source);
                if (scanning.Contains(key)) continue;
                if (!rescan && sizes.ContainsKey(key)) continue;
                need.Add(item.Source);
                keys[item.Source] = key;
            }
            if (need.Count == 0) return;

            int seq = ++scanSeq;
            foreach (string path in need)
            {
                scanning.Add(keys[path]);
                scanGen[keys[path]] = seq;
            }

            Task.Factory.StartNew(delegate
            {
                var found = new Dictionary<string, TreeInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in need)
                {
                    try { found[path] = Ops.GetTreeInfo(path); }
                    catch { found[path] = new TreeInfo(); }
                }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            foreach (KeyValuePair<string, TreeInfo> pair in found)
                            {
                                string key = SizeKey(pair.Key);
                                // 期间又启动了更新的统计，旧的这份就丢掉
                                int gen;
                                if (scanGen.TryGetValue(key, out gen) && gen != seq) continue;
                                sizes[key] = pair.Value;
                            }
                            ApplySizes();
                            TryLogSizes();
                        }
                        catch { }
                        finally
                        {
                            foreach (string path in need)
                            {
                                string key = keys[path];
                                int gen;
                                if (scanGen.TryGetValue(key, out gen) && gen == seq) scanning.Remove(key);
                            }
                        }
                    }));
                }
                catch
                {
                    // 窗口已经关闭：标记必须清掉，否则这一项永远显示不出大小
                    foreach (string path in need) scanning.Remove(keys[path]);
                }
            });
        }

        private void StartStatusScan()
        {
            if (statusScanning) return;
            statusScanning = true;
            List<AppProfile> list = new List<AppProfile>(profiles);
            Task.Factory.StartNew(delegate
            {
                Dictionary<string, bool> found;
                try { found = Ops.GuiRunningStates(list); }
                catch { found = new Dictionary<string, bool>(); }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        statusScanning = false;
                        try
                        {
                            foreach (KeyValuePair<string, bool> pair in found) runningStatus[pair.Key] = pair.Value;
                            if (busy) return;
                            bool running;
                            if (runningStatus.TryGetValue(profile.Key, out running)) chip.SetRunning(running);
                            PushRailStatus();
                        }
                        catch { }
                    }));
                }
                catch
                {
                    // 窗口正在关闭，标志要复位，否则状态永远停在“检测中”
                    statusScanning = false;
                }
            });
        }

        private void ApplySizes()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                TreeInfo info;
                if (sizes.TryGetValue(SizeKey(rows[i].Item.Source), out info))
                {
                    rows[i].Info = info;
                    rows[i].SizeKnown = true;
                }
                rows[i].Invalidate();
            }
        }

        private void TryLogSizes()
        {
            if (sizeLogged) return;
            foreach (ItemRow row in rows)
                if (!row.SizeKnown) return;
            sizeLogged = true;

            var line = new StringBuilder("当前占用：");
            foreach (ItemRow row in rows)
            {
                if (line.Length > 5) line.Append(" · ");
                line.Append(row.Item.Name).Append(' ').Append(Ops.FormatSize(row.Info.Bytes));
            }
            Log(line.ToString(), Ops.Info);
        }

        private void PushRailStatus()
        {
            var runStates = new bool[profiles.Count];
            for (int i = 0; i < profiles.Count; i++)
            {
                bool state;
                runStates[i] = runningStatus.TryGetValue(profiles[i].Key, out state) && state;
            }
            rail.SetStatus(runStates, migratedStates);
        }

        // 已有缓存就直接用，避免在界面线程重复遍历上 GB 的目录
        private TreeInfo SizeOf(MigItem item)
        {
            TreeInfo info;
            if (sizes.TryGetValue(SizeKey(item.Source), out info)) return info;
            string from = Ops.IsJunction(item.Source) ? Ops.JunctionTarget(item.Source) : item.Source;
            return Ops.GetTreeInfo(from);
        }

        // 只看后台统计的结果：界面线程上绝不再全量扫盘
        private bool TryCachedSize(MigItem item, out TreeInfo info)
        {
            return sizes.TryGetValue(SizeKey(item.Source), out info);
        }

        private void UpdatePlan()
        {
            int count = 0;
            foreach (ItemRow row in rows)
                if (row.Checked) count++;

            string root = target.Box.Text.Trim();
            if (root.Length == 0)
                lblPlan.Text = "尚未选择目标目录";
            else if (count == 0)
                lblPlan.Text = "已选 0 项 · 已迁移的位置见上方列表";
            else
                lblPlan.Text = string.Format("将迁移到 {0}    ·    已选 {1} 项", root, count);
        }

        // 每个应用各自记住勾选状态，来回切换不会把用户的选择冲掉
        private void SaveChecks()
        {
            if (profile == null) return;
            var arr = new bool[rows.Count];
            for (int i = 0; i < rows.Count; i++) arr[i] = rows[i].Checked;
            savedChecks[profile.Key] = arr;
        }

        private static string MigratedRoot(ItemRow row)
        {
            string target = row.MigratedTo;
            if (string.IsNullOrEmpty(target)) return null;
            string suffix = "\\" + row.Item.Subdir.Trim('\\');
            if (target.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return target.Substring(0, target.Length - suffix.Length);
            try { return Path.GetDirectoryName(target.TrimEnd('\\')); }
            catch { return null; }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            btnPreview.Enabled = !value;
            btnMigrate.Enabled = !value;
            btnRollback.Enabled = !value && Ops.LoadJournal(profile).Count > 0;
            btnCache.Enabled = !value;
            target.Enabled = !value;
            optKill.Enabled = !value;
            optKeep.Enabled = !value;
            if (rail != null) rail.Enabled = !value;
            foreach (ItemRow row in rows) row.Enabled = !value;
            progress.SetRunning(value);
        }

        private void Log(string message, int level)
        {
            try
            {
                if (log.InvokeRequired)
                {
                    log.BeginInvoke(new Action<string, int>(Log), message, level);
                    return;
                }
                if (log.TextLength > 120000) log.Clear();
                log.SelectionStart = log.TextLength;
                log.SelectionLength = 0;
                log.SelectionColor = Theme.Faint;
                log.AppendText(DateTime.Now.ToString("HH:mm") + "   ");
                log.SelectionColor = level == Ops.Ok ? Theme.Success
                    : level == Ops.Warn ? Theme.Warn
                    : level == Ops.Error ? Theme.Danger
                    : Theme.Body;
                log.AppendText(message + "\r\n");
                log.ScrollToCaret();
            }
            catch { }
        }

        private void RunWork(string status, Action work)
        {
            if (busy) return;
            SetBusy(true);
            Task.Factory.StartNew(delegate
            {
                try { work(); }
                catch (Exception ex) { Log("错误：" + ex.Message, Ops.Error); }
                finally
                {
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            try
                            {
                                SetBusy(false);
                                RefreshState(true);
                            }
                            catch { }
                        }));
                    }
                    catch { }
                }
            });
        }

        // ------------------------------------------------------------ 交互

        private void Browse()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 OpenChamber 的新位置（建议用空文件夹，例如 E:\\Apps\\OpenChamber）";
                dialog.ShowNewFolderButton = true;
                try { if (Directory.Exists(target.Box.Text)) dialog.SelectedPath = target.Box.Text; }
                catch { }
                if (dialog.ShowDialog(this) == DialogResult.OK) target.Box.Text = dialog.SelectedPath;
            }
        }

        private bool ValidateTarget(out string root)
        {
            var sources = new List<string>();
            foreach (MigItem item in Ops.AllItems(profile)) sources.Add(item.Source);

            string error;
            if (!Ops.ValidateTargetPath(target.Box.Text, sources, out root, out error))
            {
                WarnBox(error);
                return false;
            }
            return true;
        }

        private List<MigItem> CheckedItems()
        {
            var result = new List<MigItem>();
            foreach (ItemRow row in rows)
                if (row.Checked && row.Available) result.Add(row.Item);
            return result;
        }

        private void Preview()
        {
            string root;
            if (!ValidateTarget(out root)) return;
            List<MigItem> itemsToMove = CheckedItems();
            if (itemsToMove.Count == 0) { WarnBox("请至少勾选一项。"); return; }

            log.Clear();
            Log("预览模式，不会改动任何文件", Ops.Info);
            long need = 0;
            foreach (MigItem item in itemsToMove)
            {
                TreeInfo info;
                if (!TryCachedSize(item, out info))
                {
                    Log("· " + item.Name + " 的占用还在后台统计，稍等几秒再预览一次", Ops.Warn);
                    return;
                }
                need += info.Bytes;
                string extra = "";
                if (Ops.IsJunction(item.Source))
                {
                    string current = Ops.JunctionTarget(item.Source);
                    if (Ops.SamePath(current, Path.Combine(root, item.Subdir))) extra = "（位置未变化，会跳过）";
                    else extra = "（再次迁移，从 " + current + " 搬过去）";
                }
                Log(string.Format("{0} · {1} · {2}{3}", item.Name, Ops.FormatSize(info.Bytes), Path.Combine(root, item.Subdir), extra), Ops.Info);
            }
            long free = Ops.GetFreeSpace(root);
            Log(string.Format("合计 {0}，目标盘可用 {1}", Ops.FormatSize(need), free > 0 ? Ops.FormatSize(free) : "未知"), Ops.Info);
            Log("流程：复制 → 校验 → 原目录改名备份 → 建目录联接 → 校验 → 删除备份", Ops.Info);
            if (Ops.FindAppProcesses(profile).Count > 0)
            {
                bool gui = Ops.IsGuiRunning(profile);
                Log(profile.ShortName + (gui ? " 正在运行" : " 的后台服务正在运行（界面未打开）") +
                    "，正式迁移前会要求关闭", Ops.Warn);
            }
        }

        private void StartMigrate()
        {
            string root;
            if (!ValidateTarget(out root)) return;
            List<MigItem> itemsToMove = CheckedItems();
            if (itemsToMove.Count == 0) { WarnBox("请至少勾选一项。"); return; }

            foreach (MigItem item in itemsToMove)
            {
                string dir = Path.Combine(root, item.Subdir);
                if (Ops.IsJunction(item.Source) && Ops.SamePath(Ops.JunctionTarget(item.Source), dir)) continue;
                if (Ops.TargetOccupied(dir))
                {
                    WarnBox("目标目录里已有内容，请换一个目标目录或先清理：\r\n" + dir);
                    return;
                }
            }

            long need = 0;
            foreach (MigItem item in itemsToMove)
            {
                TreeInfo info;
                if (!TryCachedSize(item, out info))
                {
                    WarnBox("还有项目正在后台统计占用，稍等几秒再试。\r\n（统计在后台进行，界面不会被卡住）");
                    return;
                }
                need += info.Bytes;
            }
            long free = Ops.GetFreeSpace(root);
            if (free > 0 && free < (long)(need * 1.1))
            {
                WarnBox(string.Format("目标磁盘空间不足：\r\n需要约 {0}，可用 {1}", Ops.FormatSize(need), Ops.FormatSize(free)));
                return;
            }

            bool wasRunning = Ops.IsAppRunning(profile);
            bool anyRunning = Ops.FindAppProcesses(profile).Count > 0;
            if (anyRunning && !optKill.Checked)
            {
                string what = wasRunning ? " 正在运行" : " 的后台服务正在运行（界面未打开）";
                var ask = MessageBox.Show(this, profile.ShortName + what + "，迁移前必须关闭。\r\n现在结束它并继续？",
                    "需要关闭 " + profile.ShortName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ask != DialogResult.Yes) return;
                Log("已确认：迁移前会先结束 " + profile.ShortName + " 的进程", Ops.Info);
            }

            var summary = new StringBuilder();
            summary.AppendFormat("将迁移 {0} 项，共 {1}\r\n\r\n", itemsToMove.Count, Ops.FormatSize(need));
            foreach (MigItem item in itemsToMove)
            {
                string line = "· " + item.Name;
                if (Ops.IsJunction(item.Source)) line += "（再次迁移）";
                summary.AppendLine(line + "  →  " + Path.Combine(root, item.Subdir));
            }
            summary.AppendLine();
            summary.Append("迁移期间请勿操作 " + profile.ShortName + "。继续？");

            if (MessageBox.Show(this, summary.ToString(), "确认迁移", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question) != DialogResult.OK) return;

            bool keep = optKeep.Checked;
            log.Clear();
            RunWork("迁移", delegate
            {
                Ops.StopApp(profile, Log);
                List<JournalEntry> journal = Ops.LoadJournal(profile);
                int done = 0;
                bool failed = false;
                foreach (MigItem item in itemsToMove)
                {
                    if (failed) break;
                    try { Ops.MigrateOne(item, root, keep, journal, Log, profile); done++; }
                    catch (Exception ex)
                    {
                        Log("失败：" + item.Name + " — " + ex.Message, Ops.Error);
                        failed = true;
                    }
                }

                if (failed)
                {
                    Log(done > 0 ? "部分完成，可用「回滚」还原" : "未完成任何迁移，源目录完好", Ops.Warn);
                    // 失败也要把应用拉起来，别让用户以为它崩了
                    if (wasRunning)
                    {
                        try { Ops.StartApp(profile); Log("已重新启动 " + profile.ShortName, Ops.Ok); }
                        catch (Exception ex) { Log("重新启动失败，请手动打开：" + ex.Message, Ops.Warn); }
                    }
                    return;
                }

                Log("全部迁移完成", Ops.Ok);
                foreach (MigItem item in itemsToMove)
                    Log(string.Format("{0} 现在位于 {1}", item.Name, Path.Combine(root, item.Subdir)), Ops.Ok);
                if (wasRunning)
                {
                    try { Ops.StartApp(profile); Log("已重新启动 " + profile.ShortName, Ops.Ok); }
                    catch (Exception ex) { Log("重新启动失败，请手动打开：" + ex.Message, Ops.Warn); }
                }
            });
        }

        private void RunRollback()
        {
            List<JournalEntry> entries = Ops.LoadJournal(profile);
            if (entries.Count == 0) { WarnBox("没有找到迁移记录，无法自动回滚。"); return; }

            var text = new StringBuilder();
            foreach (JournalEntry entry in entries)
                text.AppendLine("· " + entry.Name + "\r\n     " + entry.Target + "\r\n  →  " + entry.Source);
            text.AppendLine();
            text.Append("将按记录还原，需要先关闭 " + profile.ShortName + "。继续？");

            if (MessageBox.Show(this, text.ToString(), "确认回滚", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning) != DialogResult.OK) return;

            log.Clear();
            bool wasRunning = Ops.IsAppRunning(profile);
            RunWork("回滚", delegate
            {
                Ops.StopApp(profile, Log);
                List<JournalEntry> list = Ops.LoadJournal(profile);
                bool ok = true;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (!Ops.RollbackOne(list[i], Log)) { ok = false; continue; }

                    // 还原成功的一条立刻从记录里去掉，否则下次回滚会一直卡在这一条
                    list.RemoveAt(i);
                    Ops.SaveJournal(profile, list);
                }
                if (list.Count == 0)
                {
                    Ops.ClearJournal(profile);
                    Log("回滚完成", Ops.Ok);
                }
                else Log("部分项目回滚失败，还剩 " + list.Count + " 条记录已保留", Ops.Warn);

                if (ok && wasRunning)
                {
                    try { Ops.StartApp(profile); Log("已重新启动 " + profile.ShortName, Ops.Ok); }
                    catch (Exception ex) { Log("重新启动失败，请手动打开：" + ex.Message, Ops.Warn); }
                }
            });
        }

        private void CleanCache()
        {
            string cache = profile.UpdaterCache;
            if (!Directory.Exists(cache)) { WarnBox(profile.ShortName + " 的升级缓存目录不存在，无需清理。"); return; }
            var info = Ops.GetTreeInfo(cache);
            string text = string.Format("删除升级缓存 {0}？\r\n{1}\r\n\r\n这只是更新安装包的残留，删除不影响使用。",
                Ops.FormatSize(info.Bytes), cache);
            if (MessageBox.Show(this, text, "清理升级缓存", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                Ops.ForceDeleteDir(cache);
                Log("已删除升级缓存，释放 " + Ops.FormatSize(info.Bytes), Ops.Ok);
                RefreshState(false);
            }
            catch (Exception ex) { WarnBox("删除失败：" + ex.Message); }
        }

        private void WarnBox(string text)
        {
            MessageBox.Show(this, text, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ------------------------------------------------------------ 无边框窗口

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x0084) // WM_NCHITTEST
            {
                int x = (short)(m.LParam.ToInt32() & 0xFFFF);
                int y = (short)((m.LParam.ToInt32() >> 16) & 0xFFFF);
                Point p = PointToClient(new Point(x, y));
                if (p.Y >= 0 && p.Y < Theme.P(48) && p.X < ClientSize.Width - Theme.P(88))
                    m.Result = (IntPtr)2; // HTCAPTION
            }
        }
    }

    // ================================================================ 入口

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                CrashLog(e.Exception);
                MessageBox.Show(e.Exception.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                CrashLog(ex);
                MessageBox.Show(ex.ToString(), "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void CrashLog(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "OpenCodeRelocator-error.txt"),
                    DateTime.Now.ToString("o") + "\r\n" + ex + "\r\n\r\n", new UTF8Encoding(true));
            }
            catch { }
        }
    }
}

#if TESTHARNESS
namespace OpenCodeRelocator
{
    // 沙盒验证：不做真实迁移，只在临时目录里跑同一套 Ops 逻辑
    internal static class TestHarness
    {
        private static int failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) failures++;
        }

        private static void Log(string message, int level)
        {
            Console.WriteLine("        [" + level + "] " + message);
        }

        private static void MakeTree(string dir, int files, int size)
        {
            Directory.CreateDirectory(dir);
            for (int i = 0; i < files; i++)
            {
                string sub = Path.Combine(dir, "sub" + (i % 3));
                Directory.CreateDirectory(sub);
                File.WriteAllBytes(Path.Combine(sub, "f" + i + ".bin"), new byte[size]);
            }
        }

        private static int Count(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
        }

        public static void Main()
        {
            Console.WriteLine("--- real environment probe ---");
            foreach (AppProfile rp in Ops.Profiles())
            {
                Console.WriteLine(string.Format("  {0,-12} related={1} guiRunning={2}",
                    rp.Key, Ops.FindAppProcesses(rp).Count, Ops.IsAppRunning(rp)));
                int shown = 0;
                foreach (Process p in Ops.FindAppProcesses(rp))
                {
                    if (shown++ >= 4) break;
                    try { Console.WriteLine("      " + p.ProcessName + "  " + p.MainModule.FileName); }
                    catch { Console.WriteLine("      " + p.ProcessName + "  (path unavailable)"); }
                }
            }

            string root = Path.Combine(Path.GetTempPath(), "ocmig-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Paths.App = Path.Combine(root, "src", "app");
                Paths.Roaming = Path.Combine(root, "src", "roaming");
                Paths.Config = Path.Combine(root, "src", "config");
                Paths.UpdaterCache = Path.Combine(root, "cache");
                Paths.Data = Path.Combine(root, "data");
                Directory.CreateDirectory(Paths.Data);

                var profile = new AppProfile
                {
                    Key = "test",
                    Name = "测试应用",
                    ShortName = "测试",
                    Folder = "Test",
                    App = Paths.App,
                    Roaming = Paths.Roaming,
                    Config = Paths.Config,
                    UpdaterCache = Paths.UpdaterCache,
                    ExeName = "test.exe",
                    JournalFile = Path.Combine(Paths.Data, "journal.json")
                };

                MakeTree(Paths.App, 9, 4096);
                int files = Count(Paths.App);
                Console.WriteLine("sandbox " + root + " files=" + files);

                MigItem app = Ops.AllItems(profile)[0];
                string t1 = Path.Combine(root, "t1");
                string t2 = Path.Combine(root, "t2");
                string t3 = Path.Combine(root, "t3");

                Ops.MigrateOne(app, t1, false, Ops.LoadJournal(profile), Log, profile);
                Check(Ops.IsJunction(Paths.App), "第一次迁移后源路径是 junction");
                Check(Ops.SamePath(Ops.JunctionTarget(Paths.App), Path.Combine(t1, "app")), "junction 指向 t1\\app");
                Check(Count(Paths.App) == files, "通过 junction 读到同样多的文件");
                Check(Directory.GetDirectories(Path.GetDirectoryName(Paths.App), "*.moved-*").Length == 0, "没有遗留 .moved 备份");
                Check(Ops.LoadJournal(profile).Count == 1, "journal 有 1 条");

                Ops.MigrateOne(app, t2, false, Ops.LoadJournal(profile), Log, profile);
                Check(Ops.SamePath(Ops.JunctionTarget(Paths.App), Path.Combine(t2, "app")), "再次迁移后指向 t2\\app");
                Check(!Directory.Exists(Path.Combine(t1, "app")), "旧位置 t1 已删除");
                Check(Count(Paths.App) == files, "再次迁移后文件数不变");
                var j2 = Ops.LoadJournal(profile);
                Check(j2.Count == 1 && Ops.SamePath(j2[0].Target, Path.Combine(t2, "app")), "journal 被更新为 t2");

                Ops.MigrateOne(app, t3, true, Ops.LoadJournal(profile), Log, profile);
                Check(Ops.SamePath(Ops.JunctionTarget(Paths.App), Path.Combine(t3, "app")), "第三次指向 t3\\app");
                Check(Directory.Exists(Path.Combine(t2, "app")), "勾选保留旧副本时 t2 仍存在");
                Check(Count(Paths.App) == files, "保留副本模式文件数不变");

                Ops.MigrateOne(app, t3, false, Ops.LoadJournal(profile), Log, profile);
                Check(Ops.SamePath(Ops.JunctionTarget(Paths.App), Path.Combine(t3, "app")), "同位置迁移被跳过且未损坏");

                Check(File.Exists(Path.Combine(Paths.App, "sub1", "f1.bin")), "通过 junction 能读到具体文件");

                // 两个应用的记录互不影响
                var other = new AppProfile
                {
                    Key = "other",
                    Name = "另一个应用",
                    ShortName = "另一个",
                    Folder = "Other",
                    App = Path.Combine(root, "other", "app"),
                    Roaming = Path.Combine(root, "other", "roaming"),
                    Config = Path.Combine(root, "other", "config"),
                    Share = Path.Combine(root, "other", "share"),
                    UpdaterCache = Path.Combine(root, "other", "cache"),
                    ExeName = "other.exe",
                    JournalFile = Path.Combine(Paths.Data, "journal-other.json")
                };
                MakeTree(other.App, 4, 1024);
                MakeTree(other.Roaming, 2, 512);
                MakeTree(other.Config, 2, 256);
                MakeTree(other.Share, 3, 512);
                List<MigItem> otherItems = Ops.AllItems(other);
                Check(otherItems.Count == 4, "带会话目录的应用有 4 项");
                foreach (MigItem mi in otherItems)
                    Ops.MigrateOne(mi, Path.Combine(root, "t-other"), false, Ops.LoadJournal(other), Log, other);
                Check(Ops.IsJunction(other.App), "第二个应用也能迁移");
                Check(Ops.IsJunction(other.Share), "第二个应用的会话目录也迁移了");
                Check(Ops.LoadJournal(other).Count == 4, "第二个应用有 4 条独立记录");
                Check(Ops.LoadJournal(profile).Count == 1, "第一个应用的记录未被写入其它应用");
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION " + ex);
                failures++;
            }
            finally
            {
                try { if (Ops.IsJunction(Paths.App)) Ops.RemoveJunction(Paths.App); } catch { }
                try { if (Directory.Exists(root)) Ops.ForceDeleteDir(root); } catch { }
            }
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)");
            Environment.Exit(failures == 0 ? 0 : 1);
        }
    }
}
#endif
