param(
  [string]$Work = "C:\Users\29436\Tools\openchamber-migrate",
  [string]$Browser = "C:\Program Files\Google\Chrome\Application\chrome.exe",
  [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256)
)
$svg = Join-Path $Work "icon.svg"
$out = Join-Path $Work "icon-build"
New-Item -ItemType Directory -Force -Path $out | Out-Null
foreach ($s in $Sizes) {
  $png = Join-Path $out "icon-$s.png"
  Remove-Item $png -ErrorAction SilentlyContinue
  $bargs = @(
    "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--no-default-browser-check",
    "--default-background-color=00000000",
    "--force-device-scale-factor=1",
    ("--window-size=" + $s + "," + $s),
    ("--screenshot=" + $png),
    ([uri]$svg).AbsoluteUri
  )
  & $Browser @bargs 2>$null | Out-Null
  if (Test-Path $png) { "rendered $s -> $png ($((Get-Item $png).Length) bytes)" } else { "FAILED $s" }
}
