# Builds installer\out\CampLedgerSetup-<version>.exe.
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-Version 0.3.1]
# Needs: .NET 8 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup), the launcher ASI
# (launcher\build\CampLedgerLauncher.asi; rebuilt here when zig is on PATH) and the Ultimate ASI Loader
# (launcher\build\install\winmm.dll, see docs/architecture.md; checked against its SHA-256 below).
param([string]$Version = "0.3.1")
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot "out"
$app = Join-Path $out "app"
$loaderSha = "57ec56390c92a18b82f58855e63c93985e254d1e29d5f75cbc28dc08f96c8a12"   # winmm.dll from Ultimate ASI Loader v10.0.2 x64

if (Test-Path $app) { Remove-Item -Recurse -Force $app }

# 1. The app, self-contained so users need no .NET install.
dotnet publish "$root\app\CampLedger.App" -c Release -r win-x64 --self-contained true -p:Version=$Version -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Get-ChildItem $app -Filter *.pdb | Remove-Item

# 2. Launcher files, copied into the app; "start with the game" places them from there.
$asi = Join-Path $root "launcher\build\CampLedgerLauncher.asi"
$zig = Get-Command zig -ErrorAction SilentlyContinue
if ($zig) {
    Push-Location (Join-Path $root "launcher")
    try {
        New-Item -ItemType Directory -Force build | Out-Null
        & $zig.Source cc -shared -O2 -target x86_64-windows-gnu CampLedgerLauncher.c -o build/CampLedgerLauncher.asi -lkernel32 -luser32
        if ($LASTEXITCODE -ne 0) { throw "zig build of the launcher failed" }
    } finally { Pop-Location }
}
if (-not (Test-Path $asi)) { throw "missing $asi (install zig, or build it as in launcher\README.md)" }

$loader = Join-Path $root "launcher\build\install\winmm.dll"
if (-not (Test-Path $loader)) { throw "missing $loader (Ultimate ASI Loader x64 dinput8.dll renamed to winmm.dll)" }
$got = (Get-FileHash $loader -Algorithm SHA256).Hash.ToLowerInvariant()
if ($got -ne $loaderSha) { throw "winmm.dll SHA-256 is $got, expected $loaderSha" }

$dest = New-Item -ItemType Directory -Force (Join-Path $app "launcher")
Copy-Item $asi, $loader $dest

# 3. The installer.
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot "CampLedger.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Join-Path $out "CampLedgerSetup-$Version.exe"
"{0}  {1:N1} MB  SHA-256 {2}" -f $setup, ((Get-Item $setup).Length / 1MB), (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
