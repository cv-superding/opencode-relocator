# 贡献指南

感谢你愿意动手！这个项目很小，源码只有一个文件，很好上手。

## 环境要求

**不需要**装 Visual Studio，也**不需要** .NET SDK。Windows 自带 .NET Framework 4 的编译器就够了：

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

代码只用 C# 5 语法（`/langversion` 默认），请不要用 C# 6+ 的特性（字符串内插、表达式体成员、`?.` 等），
因为编译用的是 .NET Framework 4.0 的参考程序集。

## 编译与测试

```powershell
git clone https://github.com/cv-superding/opencode-relocator.git
cd opencode-relocator

.\build.ps1            # 产出 OpenCodeRelocator.exe
.\build.ps1 -Test      # 顺便跑全部回归测试（58 项 + 19 项沙盒测试）
```

`build.ps1` 会自动把源文件转成带 BOM 的 UTF-8（`csc` 对无 BOM 的 UTF-8 中文会乱码），你不用操心。

## 代码结构

| 位置 | 职责 |
|---|---|
| `Ops` 静态类 | 全部迁移逻辑：路径、联接、复制、校验、记录、回滚。**与界面完全解耦** |
| `AppProfile` | 一个应用的描述：程序目录、数据目录、配置目录、进程名、记录文件 |
| `MigItem` | 一个可迁移的目录：显示名、目标子目录、源路径 |
| `ItemRow` / `SideRail` / `StatusChip` / `FlatButton` … | 自绘界面控件（不依赖任何 UI 框架） |
| `MainForm` | 界面装配与交互；耗时操作一律放后台线程 |
| `Theme` / `Draw` | 配色、字号系数（`Theme.FontScale`）、绘制辅助 |

## 加一条回归测试

测试都在 `bugcheck.cs`，在临时目录里跑真实的迁移 / 回滚逻辑，不碰真实环境：

```csharp
var p = MakeProfile(root, "p1", "a");
MakeTree(p.App, 3, 100);
try { Ops.MigrateOne(Ops.AllItems(p)[0], target, false, Ops.LoadJournal(p), Log, p); }
catch { threw = true; }
Check(threw, "目标已存在且非空时拒绝迁移");
```

写测试时注意：

- **绝不**用真实的 `%LOCALAPPDATA%` 路径，把 `Paths.*` 指到临时目录；
- 断言要针对**行为**（源还是不是联接、文件数对不对、记录写没写），不要针对日志文本；
- 涉及文件占用的分支记得 `using` 释放文件句柄，否则 Windows 上删不掉。

## 支持一个新应用

以新增一个假想的 `FooApp` 为例：

1. 在 `Paths` 里加源目录常量；
2. 在 `Ops.Profiles()` 里加一个 `AppProfile`：
   - `Key` 用来区分记录文件，`JournalFile` 用 `journal-<key>.json`（**每个应用一份，互不影响**）
   - `ExeName` 填主程序文件名，用于判断"界面是否在运行"
   - `Share` 留空表示没有"会话与登录"这类第二份数据
3. 在 `Ops.AllItems()` 里加对应的 `MigItem`（`Subdir` 决定目标目录结构）；
4. 界面上加一行提示文案（`Preview` / `Banner` 里的文字）；
5. 在 `bugcheck.cs` 里加一条沙盒测试，验证这个应用的目录能迁、能回滚。

## 提交前

```powershell
.\build.ps1 -Test
```

- 全部测试通过再提 PR；
- 用户可见的文字（界面、README）改动要一起更新；
- 涉及迁移 / 回滚流程的改动，PR 描述里必须写清楚**失败时会怎样、数据会不会丢**。

## 提交信息

用祈使句写清楚做了什么，例如：

```
修复回滚时优先用旧副本导致新数据丢失的问题
后台目录统计加上代次号，避免旧结果覆盖新结果
```

## 报告 Bug

用 `.github/ISSUE_TEMPLATE/bug_report.yml` 那个模板，**把界面日志区的内容一起贴上**——
那几行日志基本能定位到问题在哪一步。

## 涉及数据安全的问题

如果你的改动可能导致**数据丢失或文件被改坏**，请先按 [SECURITY.md](SECURITY.md) 私下报告，
等修复了再公开讨论。
