# 编译 OpenCode Relocator
# 只依赖 Windows 自带的 .NET Framework 4 编译器（%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe），
# 不需要装 Visual Studio 或 .NET SDK。
param(
    [switch]$Test,      # 顺便跑回归测试
    [switch]$Icon       # 顺便重新渲染图标（需要本机装了 Chrome）
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "找不到 C# 编译器：$csc" }

# csc 需要带 BOM 的 UTF-8 源文件
foreach ($f in @("OpenCodeRelocator.cs", "bugcheck.cs")) {
    $p = Join-Path $root $f
    $t = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($p, $t, (New-Object System.Text.UTF8Encoding($true)))
}

if ($Icon) {
    & (Join-Path $root "build-icon.ps1")
    & (Join-Path $root "iconbuild.exe") 2>$null
}

$refs = @(
    "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll",
    "/r:System.Windows.Forms.dll", "/r:System.Web.Extensions.dll"
)

Write-Host "==> 编译 OpenCodeRelocator.exe"
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /debug:pdbonly /codepage:65001 `
    /win32manifest:app.manifest /win32icon:OpenCodeRelocator.ico `
    "/resource:OpenCodeRelocator.png,OpenCodeRelocator.png" `
    "/resource:OpenCodeRelocator.ico,OpenCodeRelocator.ico" `
    /out:OpenCodeRelocator.exe $refs OpenCodeRelocator.cs
if ($LASTEXITCODE -ne 0) { throw "编译失败" }
Write-Host ("    完成：{0} 字节" -f (Get-Item .\OpenCodeRelocator.exe).Length)

if ($Test) {
    Write-Host "==> 跑回归测试（bugcheck.cs）"
    & $csc /nologo /target:exe /main:OpenCodeRelocator.BugCheck /out:bugcheck.exe $refs OpenCodeRelocator.cs bugcheck.cs
    if ($LASTEXITCODE -ne 0) { throw "测试编译失败" }
    & .\bugcheck.exe
    $code = $LASTEXITCODE
    Remove-Item .\bugcheck.exe, .\bugcheck.pdb -ErrorAction SilentlyContinue
    if ($code -ne 0) { throw "回归测试未通过" }

    Write-Host "==> 跑沙盒测试（内嵌 harness）"
    & $csc /nologo /target:exe /define:TESTHARNESS /main:OpenCodeRelocator.TestHarness /out:harness.exe $refs OpenCodeRelocator.cs
    & .\harness.exe
    $code = $LASTEXITCODE
    Remove-Item .\harness.exe, .\harness.pdb -ErrorAction SilentlyContinue
    if ($code -ne 0) { throw "沙盒测试未通过" }
}
Write-Host "全部完成。"
