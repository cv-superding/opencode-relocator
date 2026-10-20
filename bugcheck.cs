using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace OpenCodeRelocator
{
    // 回归测试：沙盒内跑真实的迁移/回滚/校验逻辑 + 界面行状态复用
    // 编译：csc /target:exe /main:OpenCodeRelocator.BugCheck /out:bugcheck.exe OpenCodeRelocator.cs bugcheck.cs
    internal static class BugCheck
    {
        private static int failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) failures++;
        }

        private static void Log(string message, int level) { }

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
            if (!Directory.Exists(dir)) return -1;
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
        }

        private static AppProfile MakeProfile(string root, string key, string sub)
        {
            return new AppProfile
            {
                Key = key, Name = key, ShortName = key, Folder = key,
                App = Path.Combine(root, sub, "app"),
                Roaming = Path.Combine(root, sub, "roaming"),
                Config = Path.Combine(root, sub, "config"),
                UpdaterCache = Path.Combine(root, sub, "cache"),
                ExeName = key + ".exe",
                JournalFile = Path.Combine(root, "data", "journal-" + key + ".json")
            };
        }

        [STAThread]
        public static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "ocmig-bug-" + Guid.NewGuid().ToString("N"));
            try { RunOps(root); RunUi(root); }
            catch (Exception ex) { Console.WriteLine("EXCEPTION " + ex); failures++; }
            finally { try { if (Directory.Exists(root)) Ops.ForceDeleteDir(root); } catch { } }
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)");
            return failures == 0 ? 0 : 1;
        }

        private static void RunOps(string root)
        {
            Console.WriteLine("--- 迁移与回滚 ---");

            // 目标已存在且非空 -> 拒绝，源保持原样
            {
                var p = MakeProfile(root, "p1", "a");
                MakeTree(p.App, 3, 100);
                string tgt = Path.Combine(root, "t1");
                Directory.CreateDirectory(Path.Combine(tgt, "app"));
                File.WriteAllText(Path.Combine(tgt, "app", "x.txt"), "x");
                bool threw = false;
                try { Ops.MigrateOne(Ops.AllItems(p)[0], tgt, false, Ops.LoadJournal(p), Log, p); }
                catch { threw = true; }
                Check(threw, "目标已存在且非空时拒绝迁移");
                Check(!Ops.IsJunction(p.App) && Count(p.App) == 3, "拒绝后源保持原样");
                Ops.ForceDeleteDir(Path.Combine(tgt, "app"));
            }

            // 源文件被占用 -> 失败且不留目标残骸
            {
                var p = MakeProfile(root, "p2", "b");
                MakeTree(p.App, 3, 100);
                string tgt = Path.Combine(root, "t2");
                bool threw = false;
                using (var fs = new FileStream(Path.Combine(p.App, "sub0", "f0.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    try { Ops.MigrateOne(Ops.AllItems(p)[0], tgt, false, Ops.LoadJournal(p), Log, p); }
                    catch { threw = true; }
                }
                Check(threw, "源文件被占用时迁移失败");
                Check(!Ops.IsJunction(p.App) && Count(p.App) == 3, "失败后源文件完整");
                Check(!Directory.Exists(Path.Combine(tgt, "app")), "失败后目标副本被清理，不留残骸");
            }

            // 源目录里含联接 -> 链接按链接原样复制，数量对得上
            {
                var p = MakeProfile(root, "p3", "c");
                MakeTree(p.App, 2, 100);
                string other = Path.Combine(root, "c", "other");
                MakeTree(other, 4, 100);
                Ops.CreateJunction(Path.Combine(p.App, "link"), other);
                string tgt = Path.Combine(root, "t3");
                bool threw = false;
                try { Ops.MigrateOne(Ops.AllItems(p)[0], tgt, false, Ops.LoadJournal(p), Log, p); }
                catch (Exception ex) { threw = true; Console.WriteLine("        " + ex.Message); }
                Check(!threw, "源目录内含联接时仍能迁移");
                Check(Ops.IsJunction(p.App), "迁移后源路径是联接");
                Check(Ops.IsJunction(Path.Combine(tgt, "app", "link")), "目标里的目录联接被保留，没有被悄悄丢掉");
                TreeInfo a = Ops.GetTreeInfo(p.App), b = Ops.GetTreeInfo(Path.Combine(tgt, "app"));
                Check(a.Links == 1 && b.Links == 1, "源与目标的链接数量一致");
            }

            // 备份目录名撞车 -> 自动换名，迁移照常完成
            {
                var p = MakeProfile(root, "p4", "d");
                MakeTree(p.App, 2, 100);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Directory.CreateDirectory(p.App + ".moved-" + stamp);
                Directory.CreateDirectory(p.App + ".moved-" + stamp + "-1");
                bool threw = false;
                try { Ops.MigrateOne(Ops.AllItems(p)[0], Path.Combine(root, "t4"), false, Ops.LoadJournal(p), Log, p); }
                catch (Exception ex) { threw = true; Console.WriteLine("        " + ex.Message); }
                Check(!threw && Ops.IsJunction(p.App), "备份目录名撞车时自动换名，不会中途失败");
            }

            // 回滚：数据搬回原路径，新位置清空
            {
                var p = MakeProfile(root, "p5", "e");
                MakeTree(p.App, 5, 100);
                string tgt = Path.Combine(root, "t5");
                Ops.MigrateOne(Ops.AllItems(p)[0], tgt, false, Ops.LoadJournal(p), Log, p);
                List<JournalEntry> entries = Ops.LoadJournal(p);
                Check(entries.Count == 1 && entries[0].State == "done", "回滚前有一条 done 记录");
                Check(Ops.RollbackOne(entries[0], Log), "回滚成功");
                Check(!Ops.IsJunction(p.App) && Count(p.App) == 5, "回滚后数据回到原路径");
                Check(!Directory.Exists(Path.Combine(tgt, "app")), "回滚后新位置已删除");
            }

            // 回滚（保留过旧副本）: 取新位置的活数据，旧副本一并清理
            {
                var p = MakeProfile(root, "p6", "f");
                MakeTree(p.App, 3, 100);
                string tgt = Path.Combine(root, "t6");
                Ops.MigrateOne(Ops.AllItems(p)[0], tgt, true, Ops.LoadJournal(p), Log, p);
                List<JournalEntry> entries = Ops.LoadJournal(p);
                string backup = entries[0].Backup;
                Check(!string.IsNullOrEmpty(backup) && Directory.Exists(backup), "保留模式下旧副本存在");
                File.WriteAllText(Path.Combine(tgt, "app", "new.txt"), "new");
                Check(Ops.RollbackOne(entries[0], Log), "保留副本模式下回滚成功");
                Check(File.Exists(Path.Combine(p.App, "new.txt")), "回滚取的是新位置的活数据，不是旧副本");
                Check(!Directory.Exists(Path.Combine(tgt, "app")), "回滚后新位置已删除");
                Check(!Directory.Exists(backup), "回滚后旧副本已清理");
            }

            // 记录文件损坏 / 原子写入
            {
                var p = MakeProfile(root, "p7", "g");
                Directory.CreateDirectory(Path.GetDirectoryName(p.JournalFile));
                File.WriteAllText(p.JournalFile, "{ 这不是 json");
                int count = -1;
                bool threw = false;
                try { count = Ops.LoadJournal(p).Count; } catch { threw = true; }
                Check(!threw && count == 0, "记录文件损坏时不崩溃，按空记录处理");
                Check(!string.IsNullOrEmpty(Ops.LastJournalError), "记录损坏有明确提示");

                var q = MakeProfile(root, "p8", "h");
                MakeTree(q.App, 2, 100);
                Ops.MigrateOne(Ops.AllItems(q)[0], Path.Combine(root, "t8"), false, Ops.LoadJournal(q), Log, q);
                Check(!File.Exists(q.JournalFile + ".tmp"), "记录是原子写入，没有留下临时文件");
                Check(Ops.SameVolume(q.App, Path.Combine(root, "t8")), "同盘判定正确");
                Check(Ops.TargetOccupied(Path.Combine(root, "t8", "app")), "目标里有内容时判定为占用");
            }

            Console.WriteLine("--- 目标目录校验 ---");
            {
                string rootOut, err;
                var sources = new List<string> { @"C:\Users\x\AppData\Local\Programs\@app" };
                Check(Ops.ValidateTargetPath(@"C:\Users\x\AppData\Local\Programs\@app2", sources, out rootOut, out err),
                    "同前缀的兄弟目录不再被误判为包含");
                Check(!Ops.ValidateTargetPath(@"C:\Users\x\AppData\Local\Programs\@app\sub", sources, out rootOut, out err),
                    "源目录内部的子目录被拒绝");
                Check(!Ops.ValidateTargetPath(@"C:\Users\x\AppData\Local\Programs", sources, out rootOut, out err),
                    "源目录的父目录被拒绝");
                Check(!Ops.ValidateTargetPath(@"C:\Users\x\AppData\Local\Programs\@app", sources, out rootOut, out err),
                    "目标与源目录相同被拒绝");
                Check(!Ops.ValidateTargetPath("Z:\\Apps\\Foo", sources, out rootOut, out err),
                    "不存在的目标盘被拒绝");
                Check(!Ops.ValidateTargetPath("relative\\path", sources, out rootOut, out err), "相对路径被拒绝");
                Check(!Ops.ValidateTargetPath("", sources, out rootOut, out err), "空路径被拒绝");
                Check(!Ops.ValidateTargetPath("E:", sources, out rootOut, out err), "只写盘符（E:）被拒绝");
                Check(!Ops.ValidateTargetPath("\\\\localhost\\c$\\Apps", sources, out rootOut, out err),
                    "网络路径被拒绝（junction 只能指向本地盘）");
                Check(Ops.ValidateTargetPath(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Apps\\Something"),
                        sources, out rootOut, out err), "系统盘上的普通目录可以通过校验");
            }

            Console.WriteLine("--- 小工具 ---");
            Check(Ops.SamePath(@"C:\a\b", @"C:\a\b\"), "SamePath 忽略尾斜杠");
            Check(Ops.SamePath(@"C:\a\b", @"c:\A\B"), "SamePath 忽略大小写");
            Check(!Ops.SamePath(@"C:\a\b", @"C:\a\bc"), "SamePath 不误判同前缀目录");
            Check(Ops.FormatSize(0) == "0 B", "FormatSize(0)");
            Check(Ops.FormatSize(1024) == "1 KB", "FormatSize(1024)");
            Check(Ops.FormatSize(1048576) == "1.0 MB", "FormatSize(1MB)");
            Check(Ops.FormatSize(1073741824L) == "1.00 GB", "FormatSize(1GB)");
            Check(Ops.GetFreeSpace("Z:\\nonexistent") == -1, "不存在的盘返回 -1");

            MethodInfo suggest = typeof(MainForm).GetMethod("SuggestTarget", BindingFlags.NonPublic | BindingFlags.Static);
            string suggested = (string)suggest.Invoke(null, new object[] { MakeProfile(root, "probe", "probe") });
            Check(!string.Equals(Path.GetPathRoot(Path.GetFullPath(suggested)),
                    Path.GetPathRoot(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase),
                "默认目标不与源盘相同（" + suggested + "）");
        }

        private static void RunUi(string root)
        {
            Console.WriteLine("--- 界面状态（行复用）---");

            Paths.Data = Path.Combine(root, "ui", "data");
            Paths.App = Path.Combine(root, "ui", "oc-app");
            Paths.Roaming = Path.Combine(root, "ui", "oc-roaming");
            Paths.Config = Path.Combine(root, "ui", "oc-config");
            Paths.UpdaterCache = Path.Combine(root, "ui", "cache");
            Paths.OpenCodeApp = Path.Combine(root, "ui", "oa-app");
            Paths.OpenCodeRoaming = Path.Combine(root, "ui", "oa-roaming");
            Paths.OpenCodeConfig = Path.Combine(root, "ui", "oa-config");
            Paths.OpenCodeShare = Path.Combine(root, "ui", "oa-share");
            Paths.OpenCodeCache = Path.Combine(root, "ui", "oa-cache");

            MakeTree(Paths.App, 2, 100);
            MakeTree(Paths.Roaming, 2, 100);
            MakeTree(Paths.Config, 2, 100);
            MakeTree(Paths.OpenCodeApp, 2, 100);
            MakeTree(Paths.OpenCodeRoaming, 2, 100);
            MakeTree(Paths.OpenCodeConfig, 2, 100);
            MakeTree(Paths.OpenCodeShare, 2, 100);

            // 先把 OpenChamber 迁移掉，制造"已迁移"的界面状态
            var profile = new AppProfile
            {
                Key = "openchamber", Name = "OpenChamber", ShortName = "OpenChamber", Folder = "OpenChamber",
                App = Paths.App, Roaming = Paths.Roaming, Config = Paths.Config,
                UpdaterCache = Paths.UpdaterCache, ExeName = "OpenChamber.exe",
                JournalFile = Path.Combine(Paths.Data, "journal.json")
            };
            Ops.MigrateOne(Ops.AllItems(profile)[0], Path.Combine(root, "ui", "oc-target"), false,
                Ops.LoadJournal(profile), Log, profile);

            MainForm form = new MainForm();
            IntPtr handle = form.Handle;
            FieldInfo rowsField = typeof(MainForm).GetField("rows", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo switchMethod = typeof(MainForm).GetMethod("SwitchProfile", BindingFlags.NonPublic | BindingFlags.Instance);
            var rows = (List<ItemRow>)rowsField.GetValue(form);

            Check(rows.Count == 3, "OpenChamber 是 3 行");
            Check(rows[0].Migrated, "迁移过的行显示为已迁移");
            Check(rows[0].MigratedTo != null && rows[0].MigratedTo.Contains("oc-target"), "已迁移的行显示正确的新位置");
            Check(!rows[0].Checked, "已迁移的项不勾选");
            Check(rows[1].Checked, "未迁移的项默认勾选");

            switchMethod.Invoke(form, new object[] { 1 });
            rows = (List<ItemRow>)rowsField.GetValue(form);
            Check(rows.Count == 4, "切到 OpenCode 是 4 行");
            bool stale = false;
            foreach (ItemRow row in rows)
            {
                if (row.Migrated || row.MigratedTo != null) stale = true;
                if (row.Item.Source.StartsWith(root + @"\ui\oc-", StringComparison.OrdinalIgnoreCase)) stale = true;
                if (row.Info.Bytes != 0) stale = true;
            }
            Check(!stale, "切换后没有残留上一个应用的状态（已迁移/目标路径/数据源/大小）");
            bool allChecked = true;
            foreach (ItemRow row in rows) if (!row.Checked) allChecked = false;
            Check(allChecked, "切换后新应用的可选项默认勾选");
            Check(rows[0].Item.Source == Paths.OpenCodeApp, "切换后行指向 OpenCode 的程序目录");
            Check(rows[3].Item.Source == Paths.OpenCodeShare, "切换后最后一行是 OpenCode 的会话目录");

            switchMethod.Invoke(form, new object[] { 0 });
            rows = (List<ItemRow>)rowsField.GetValue(form);
            Check(rows.Count == 3, "切回 OpenChamber 恢复 3 行");
            Check(rows[0].Migrated, "切回后“已迁移”状态恢复");
            Check(rows[0].MigratedTo != null && rows[0].MigratedTo.Contains("oc-target"), "切回后目标路径恢复");
            Check(!rows[0].Checked, "切回后已迁移的项不勾选");
            Check(rows[1].Checked, "切回后可选项勾上");

            form.Dispose();
        }
    }
}
