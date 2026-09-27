<#
install.ps1 — PowerBITool installer (Windows only).

PowerBITool is a plugin for AgentBridge / AIOrchestrator that drives the LOCAL
Power BI Desktop instance. It works only on Windows because Power BI Desktop is
Windows-only.

What it does, in order:
  1. Finds AgentBridge. If it is not installed, offers to download and install it
     from the official GitHub release (Graphene-Lab/AgentBridge).
  2. Installs the PowerBITool plugin into <AgentBridge>\Tools\PowerBITool\.
  3. Checks for Power BI Desktop. If it is missing, asks whether to install it; on
     "yes" it opens the default browser to the official download page.

Usage:
  powershell -ExecutionPolicy Bypass -File install.ps1
  powershell -ExecutionPolicy Bypass -File install.ps1 -AgentBridgeDir "C:\path\to\AgentBridge"
  powershell -ExecutionPolicy Bypass -File install.ps1 -NoAgentBridgeInstall   # fail if AgentBridge missing
  powershell -ExecutionPolicy Bypass -File install.ps1 -FromRelease            # get the plugin from the GitHub release instead of building it locally

Notes:
  - By default the plugin is built from this repo (needs the .NET 10 SDK and the
    AIOrchestrator sibling project). Use -FromRelease to download the published
    plugin zip instead (no source needed).
  - The AgentBridge download is large (~950 MB). It is verified before extraction.
#>
param(
    [string]$AgentBridgeDir = "",
    [switch]$NoAgentBridgeInstall,
    [switch]$FromRelease
)

$ErrorActionPreference = 'Stop'

$Repo            = "Graphene-Lab/PowerBITool"
$AgentBridgeRepo = "Graphene-Lab/AgentBridge"
$AgentBridgeAsset= "agentbridge-win-x64.tar.gz"
$PbiDownloadUrl  = "https://www.microsoft.com/it-it/download/details.aspx?id=58494"
$PluginName      = "PowerBITool"
$DefaultAbDir    = Join-Path $env:LOCALAPPDATA "Programs\AgentBridge"

function Write-Step($m)  { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)    { Write-Host "    [OK] $m" -ForegroundColor Green }
function Write-Warn2($m) { Write-Host "    [!] $m" -ForegroundColor Yellow }
function Fail($m)        { Write-Host "`nERROR: $m" -ForegroundColor Red; exit 1 }

function Ask-YesNo([string]$prompt) {
    $a = Read-Host "$prompt [y/N]"
    return $a -match '^\s*[yY]'
}

# --- Locate AgentBridge -------------------------------------------------------
function Find-AgentBridgeExe([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir) -or -not (Test-Path $dir)) { return $null }
    $hit = Get-ChildItem -Path $dir -Filter "AgentBridge.exe" -Recurse -ErrorAction SilentlyContinue |
           Select-Object -First 1
    return $hit
}

function Detect-AgentBridge {
    # 1. explicit parameter
    if ($AgentBridgeDir) {
        $exe = Find-AgentBridgeExe $AgentBridgeDir
        if ($exe) { return $exe.DirectoryName }
        Write-Warn2 "No AgentBridge.exe under -AgentBridgeDir '$AgentBridgeDir'."
    }
    # 2. common install locations
    foreach ($cand in @($DefaultAbDir, "C:\AgentBridge", (Join-Path $env:ProgramFiles "AgentBridge"))) {
        $exe = Find-AgentBridgeExe $cand
        if ($exe) { return $exe.DirectoryName }
    }
    # 3. a running AgentBridge process
    $proc = Get-Process -Name "AgentBridge" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) { try { return (Split-Path $proc.Path -Parent) } catch {} }
    # 4. on PATH
    $onPath = (Get-Command AgentBridge.exe -ErrorAction SilentlyContinue)
    if ($onPath) { return (Split-Path $onPath.Source -Parent) }
    return $null
}

function Install-AgentBridge {
    if ($NoAgentBridgeInstall) {
        Fail "AgentBridge was not found and -NoAgentBridgeInstall was set. Install AgentBridge first, or re-run without that switch."
    }
    if (-not (Ask-YesNo "AgentBridge is not installed. Download and install it now from $AgentBridgeRepo? (~950 MB)")) {
        Fail "AgentBridge is required. Install it manually from https://github.com/$AgentBridgeRepo/releases and re-run."
    }
    $target = $DefaultAbDir
    if ($AgentBridgeDir) { $target = $AgentBridgeDir }
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    $work = Join-Path ([System.IO.Path]::GetTempPath()) "pbitool-ab-$([System.IO.Path]::GetRandomFileName())"
    New-Item -ItemType Directory -Path $work | Out-Null
    $archive = Join-Path $work $AgentBridgeAsset

    Write-Host "    Resolving latest $AgentBridgeRepo release..."
    $tag = $null
    try { $tag = gh release view --repo $AgentBridgeRepo --json tagName --jq .tagName } catch {}
    if ([string]::IsNullOrWhiteSpace($tag)) {
        try { $tag = (Invoke-RestMethod "https://api.github.com/repos/$AgentBridgeRepo/releases/latest").tag_name } catch {}
    }
    if ([string]::IsNullOrWhiteSpace($tag)) { Fail "Cannot resolve the latest $AgentBridgeRepo release." }
    Write-Host "    Latest release: $tag"

    Write-Host "    Downloading $AgentBridgeAsset (this is large; please wait)..."
    $ok = $false
    try { gh release download $tag --repo $AgentBridgeRepo -p $AgentBridgeAsset -D $work --clobber; $ok = ($LASTEXITCODE -eq 0) } catch { $ok = $false }
    if (-not $ok) {
        curl.exe -L --fail --retry 5 --retry-delay 5 -C - -o $archive "https://github.com/$AgentBridgeRepo/releases/download/$tag/$AgentBridgeAsset"
        if ($LASTEXITCODE -ne 0) { Fail "AgentBridge download failed. Re-run (the download resumes) or install manually." }
    }
    if (-not (Test-Path $archive)) { Fail "Download produced no file." }

    Write-Host "    Verifying archive integrity..."
    tar -tzf $archive *> $null
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $archive -Force -ErrorAction SilentlyContinue
        Fail "AgentBridge archive is corrupt/truncated — deleted. Re-run the installer."
    }
    Write-Host "    Extracting into $target ..."
    tar -xzf $archive -C $target
    if ($LASTEXITCODE -ne 0) { Fail "AgentBridge extraction failed." }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

    $exe = Find-AgentBridgeExe $target
    if (-not $exe) { Fail "Extracted but no AgentBridge.exe found under $target." }
    return $exe.DirectoryName
}

# --- Locate / build the plugin ----------------------------------------------
function Get-PluginFiles {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    if (-not $FromRelease) {
        $bin = Join-Path $here "bin\Release\net10.0"
        if (-not (Test-Path (Join-Path $bin "$PluginName.dll"))) {
            Write-Host "    Building the plugin (Release)..."
            dotnet build (Join-Path $here "$PluginName.csproj") -c Release --nologo | Out-Null
            if ($LASTEXITCODE -ne 0) { Fail "Plugin build failed. Install the .NET 10 SDK and the AIOrchestrator sibling project, or use -FromRelease." }
        }
        if (-not (Test-Path (Join-Path $bin "$PluginName.dll"))) { Fail "Build finished but $PluginName.dll is missing." }
        return @{ Source = $bin; Kind = "local build" }
    }
    # FromRelease: download the published plugin zip
    $tag = $null
    try { $tag = gh release view --repo $Repo --json tagName --jq .tagName } catch {}
    if ([string]::IsNullOrWhiteSpace($tag)) {
        try { $tag = (Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest").tag_name } catch {}
    }
    if ([string]::IsNullOrWhiteSpace($tag)) { Fail "Cannot resolve the latest $Repo release for -FromRelease." }
    $ver = $tag -replace '^v',''
    $asset = "$PluginName-$ver.zip"
    $work = Join-Path ([System.IO.Path]::GetTempPath()) "pbitool-plug-$([System.IO.Path]::GetRandomFileName())"
    New-Item -ItemType Directory -Path $work | Out-Null
    $zip = Join-Path $work $asset
    try { gh release download $tag --repo $Repo -p $asset -D $work --clobber; if ($LASTEXITCODE -ne 0) { throw } } catch {
        curl.exe -L --fail -o $zip "https://github.com/$Repo/releases/download/$tag/$asset"
        if ($LASTEXITCODE -ne 0) { Fail "Plugin release download failed: $asset" }
    }
    Expand-Archive -Path $zip -DestinationPath $work -Force
    # The zip root is a PowerBITool/ folder; find the folder that holds the dll.
    $dll = Get-ChildItem -Path $work -Filter "$PluginName.dll" -Recurse | Select-Object -First 1
    if (-not $dll) { Fail "Plugin zip did not contain $PluginName.dll." }
    return @{ Source = $dll.DirectoryName; Kind = "release $tag" }
}

# --- Power BI Desktop detection ---------------------------------------------
function Test-PowerBiDesktop {
    if (Get-Process -Name "PBIDesktop" -ErrorAction SilentlyContinue) { return $true }
    foreach ($hive in @("HKLM:\SOFTWARE\Microsoft\Microsoft Power BI Desktop",
                       "HKCU:\SOFTWARE\Microsoft\Microsoft Power BI Desktop",
                       "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Microsoft Power BI Desktop")) {
        if (Test-Path $hive) { return $true }
    }
    foreach ($p in @((Join-Path $env:LOCALAPPDATA "Microsoft\Power BI Desktop Store App\PBIDesktop.exe"),
                    (Join-Path $env:LOCALAPPDATA "Microsoft\Power BI Desktop\PBIDesktop.exe"),
                    (Join-Path ${env:ProgramFiles} "Microsoft Power BI Desktop\PBIDesktop.exe"))) {
        if (Test-Path $p) { return $true }
    }
    return $false
}

# --- Main -------------------------------------------------------------------
Write-Host "PowerBITool installer (Windows)" -ForegroundColor White

Write-Step "Locating AgentBridge"
$abDir = Detect-AgentBridge
if ($abDir) {
    Write-Ok "AgentBridge found at: $abDir"
} else {
    Write-Warn2 "AgentBridge not found."
    $abDir = Install-AgentBridge
    Write-Ok "AgentBridge installed at: $abDir"
}

Write-Step "Installing the PowerBITool plugin"
$plug = Get-PluginFiles
Write-Ok "Plugin source: $($plug.Kind) -> $($plug.Source)"

$toolsDir = Join-Path $abDir "Tools\$PluginName"
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
# Copy the plugin and its own dependencies; never ship the host's AIOrchestrator assemblies.
Get-ChildItem -Path $plug.Source -Recurse -File |
    Where-Object { $_.Name -notlike "AIOrchestrator.*" } |
    ForEach-Object {
        $rel = $_.FullName.Substring($plug.Source.Length).TrimStart('\')
        $dest = Join-Path $toolsDir $rel
        $destDir = Split-Path $dest -Parent
        if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
        Copy-Item $_.FullName $dest -Force
    }
Write-Ok "Plugin installed to: $toolsDir"

Write-Step "Checking Power BI Desktop"
if (Test-PowerBiDesktop) {
    Write-Ok "Power BI Desktop is installed."
} else {
    Write-Warn2 "Power BI Desktop was not detected."
    if (Ask-YesNo "Do you want to install Power BI Desktop now?") {
        Write-Host "    Opening the official download page in your browser..."
        Start-Process $PbiDownloadUrl
        Write-Host "    After installing, open a report in Power BI Desktop, then use the tool through AgentBridge."
    } else {
        Write-Host "    Skipped. You can install Power BI Desktop later from: $PbiDownloadUrl"
    }
}

Write-Host "`nDone. Start AgentBridge, open a report in Power BI Desktop, and ask the agent to work on the model." -ForegroundColor Green
Write-Host "Remember: model changes are live in memory only — press Ctrl+S in Power BI Desktop to save the .pbix." -ForegroundColor Yellow
