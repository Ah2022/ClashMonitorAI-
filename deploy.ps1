# ============================================================================
#  ClashResolve AI v4.0 - Professional Revit Addin Deployment Script
#  deploy.ps1
#
#  STRATEGY: Whitelist only.
#    Copies ONLY DLLs that Revit 2024 cannot find on its own.
#    Skips anything in: .NET 4.8 GAC, Revit install folder,
#    or any Microsoft.Extensions.* / System.* that Autodesk ships.
#
#  USAGE:
#    .\deploy.ps1                        # defaults to Revit 2024, Release
#    .\deploy.ps1 -Config Debug          # deploy Debug build
#    .\deploy.ps1 -RevitVersion 2025     # target Revit 2025
#    .\deploy.ps1 -DryRun                # preview without copying
# ============================================================================

param(
    [string] $RevitVersion = "2024",
    [string] $Config       = "Release",
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Paths
$BuildOutput = Join-Path $PSScriptRoot "bin\$Config"
$AddinDest   = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion\ClashResolveAI"
$RevitDir    = "C:\Program Files\Autodesk\Revit $RevitVersion"

# Colours helper
function Write-OK    { param($m) Write-Host "  [OK]   $m" -ForegroundColor Green    }
function Write-SKIP  { param($m) Write-Host "  [SKIP] $m" -ForegroundColor DarkGray }
function Write-WARN  { param($m) Write-Host "  [WARN] $m" -ForegroundColor Yellow   }
function Write-ERR   { param($m) Write-Host "  [ERR]  $m" -ForegroundColor Red      }
function Write-HEAD  { param($m) Write-Host "`n$m" -ForegroundColor Cyan            }

# WHITELIST
# Only these DLLs are safe to deploy.
# Any DLL not on this list is NEVER copied, regardless of what the build
# output folder contains.
#
# Rule for being on this list:
#   1. Not present in C:\Program Files\Autodesk\Revit 2024\
#   2. Not in the .NET 4.8 GAC  (check: gacutil /l <name>)
#   3. Not a Microsoft.Extensions.* assembly (Autodesk ships these)
#   4. Not a System.* BCL assembly (shipped with .NET 4.8)

$Whitelist = @(

    # Your addin DLL (always required)
    [PSCustomObject]@{
        File     = "ClashResolveAI.dll"
        Reason   = "Addin assembly"
        Required = $true
    }

    # SQLite (Revit has no SQLite at all)
    [PSCustomObject]@{
        File     = "System.Data.SQLite.dll"
        Reason   = "SQLite managed wrapper - not in Revit"
        Required = $true
    }

    # ClosedXML + its own dependencies (Revit has no Excel writer)
    [PSCustomObject]@{
        File     = "ClosedXML.dll"
        Reason   = "Excel report generation - not in Revit"
        Required = $true
    }
    [PSCustomObject]@{
        File     = "ExcelNumberFormat.dll"
        Reason   = "ClosedXML dependency - not in Revit"
        Required = $true
    }

    # DocumentFormat.OpenXml (Word reports)
    # Revit 2024 does NOT ship DocumentFormat.OpenXml.
    # Revit 2025+ DOES - handled by version check below.
    [PSCustomObject]@{
        File     = "DocumentFormat.OpenXml.dll"
        Reason   = "Word report generation - not in Revit 2024"
        Required = $true
    }

    # OpenAI client
    [PSCustomObject]@{
        File     = "OpenAI_API.dll"
        Reason   = "OpenAI client - not in Revit"
        Required = $false    # non-critical: addin works without AI if missing
    }

    # FastMember (ClosedXML optional dependency)
    [PSCustomObject]@{
        File     = "FastMember.dll"
        Reason   = "ClosedXML optional dependency"
        Required = $false
    }
)

# BLACKLIST - safety net
# Even if somehow in the build output, these are NEVER copied.
# Copying any of these will break Revit or cause an addin load failure.

$Blacklist = @(
    # .NET 4.8 GAC
    "System.dll"
    "System.Core.dll"
    "System.Xml.dll"
    "System.Data.dll"
    "System.IO.dll"
    "System.IO.Compression.dll"
    "System.IO.Compression.FileSystem.dll"
    "System.Net.Http.dll"
    "System.Runtime.dll"
    "System.Runtime.Extensions.dll"
    "System.Collections.dll"
    "System.Linq.dll"
    "System.Threading.dll"
    "System.Threading.Tasks.dll"
    "System.Threading.Tasks.Extensions.dll"
    "System.Memory.dll"
    "System.Buffers.dll"
    "System.Numerics.dll"
    "System.Numerics.Vectors.dll"
    "System.Reflection.dll"
    "System.Text.RegularExpressions.dll"
    "System.Diagnostics.dll"
    "System.Security.dll"
    "System.ServiceModel.dll"
    "System.Windows.Forms.dll"
    "PresentationCore.dll"
    "PresentationFramework.dll"
    "WindowsBase.dll"

    # Newtonsoft.Json - Revit ships its own (v13.0.x).
    # Your code targets 13.0.3 which is binary-compatible with Revit's copy.
    # Deploying a second copy causes assembly identity conflicts between addins.
    "Newtonsoft.Json.dll"

    # Microsoft.Extensions.* - Autodesk ships ALL of these inside Revit 2024.
    # Copying yours alongside Revit's causes TypeLoadException on startup.
    "Microsoft.Extensions.Options.dll"
    "Microsoft.Extensions.Primitives.dll"
    "Microsoft.Extensions.Logging.dll"
    "Microsoft.Extensions.Logging.Abstractions.dll"
    "Microsoft.Extensions.Http.dll"
    "Microsoft.Extensions.DependencyInjection.dll"
    "Microsoft.Extensions.DependencyInjection.Abstractions.dll"
    "Microsoft.Extensions.Configuration.dll"
    "Microsoft.Extensions.Configuration.Abstractions.dll"
    "Microsoft.Extensions.Hosting.dll"

    # Revit API - must NEVER be in the addin folder
    "RevitAPI.dll"
    "RevitAPIUI.dll"
    "RevitAddinUtility.dll"
    "AdWindows.dll"
    "UIFramework.dll"
    "UIFrameworkServices.dll"
    "RibbonLib.dll"
)

# SPECIAL CASE: DocumentFormat.OpenXml in Revit 2025+
# Revit 2025 bundles it. Deploying ours would conflict.

if ([int]$RevitVersion -ge 2025) {
    $revitHasOpenXml = Test-Path (Join-Path $RevitDir "DocumentFormat.OpenXml.dll")
    if ($revitHasOpenXml) {
        Write-WARN "Revit $RevitVersion ships DocumentFormat.OpenXml - removing from whitelist."
        $Whitelist = $Whitelist | Where-Object { $_.File -ne "DocumentFormat.OpenXml.dll" }
        $Blacklist += "DocumentFormat.OpenXml.dll"
    }
}

# PRE-FLIGHT CHECKS

Write-HEAD "ClashResolve AI v4.0 - Deploy to Revit $RevitVersion ($Config)"
if ($DryRun) { Write-Host "  *** DRY RUN - no files will be copied ***" -ForegroundColor Magenta }

# Build output must exist
if (-not (Test-Path $BuildOutput)) {
    Write-ERR "Build output not found: $BuildOutput"
    Write-ERR "Run: dotnet build -c $Config"
    exit 1
}

# Main DLL must exist
$mainDll = Join-Path $BuildOutput "ClashResolveAI.dll"
if (-not (Test-Path $mainDll)) {
    Write-ERR "ClashResolveAI.dll not found in $BuildOutput"
    Write-ERR "Run: dotnet build -c $Config"
    exit 1
}

# Warn if Revit dir not found (non-fatal - might be building on CI)
if (-not (Test-Path $RevitDir)) {
    Write-WARN "Revit $RevitVersion not found at: $RevitDir"
    Write-WARN "Blacklist cross-check against Revit install folder skipped."
}

Write-HEAD "Build output : $BuildOutput"
Write-HEAD "Deploy target: $AddinDest"

# CREATE DESTINATION FOLDER

if (-not $DryRun) {
    New-Item -ItemType Directory -Force -Path $AddinDest | Out-Null
    New-Item -ItemType Directory -Force -Path "$AddinDest\x64" | Out-Null
    New-Item -ItemType Directory -Force -Path "$AddinDest\Resources" | Out-Null
}

Write-HEAD "Directories created successfully."

# SAFETY CHECK: scan build output and warn about any non-whitelisted DLL
# that is ALSO not on the blacklist (unknown DLL - needs manual review)

Write-HEAD "Scanning build output for unexpected DLLs..."

$allBuiltDlls   = Get-ChildItem $BuildOutput -Filter "*.dll" -File
$whitelistNames = $Whitelist | Select-Object -ExpandProperty File
$blacklistSet   = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
$Blacklist | ForEach-Object { $blacklistSet.Add($_) | Out-Null }

foreach ($dll in $allBuiltDlls) {
    $name = $dll.Name
    $inWhite = $whitelistNames -contains $name
    $inBlack = $blacklistSet.Contains($name)

    if ($inBlack)  { Write-SKIP "$name  < blacklisted (Revit / GAC owns this)" }
    elseif ($inWhite) { Write-OK   "$name  < whitelisted" }
    else {
        # Check if Revit ships it
        $revitPath = Join-Path $RevitDir $name
        if (Test-Path $revitPath) {
            Write-WARN "$name  < FOUND in Revit folder - will NOT copy (would conflict)"
        } else {
            Write-WARN "$name  < not on whitelist or blacklist - skipping (review manually)"
        }
    }
}

# COPY WHITELISTED DLLS

Write-HEAD "Copying whitelisted DLLs..."

$copied  = 0
$skipped = 0
$missing = 0

foreach ($entry in $Whitelist) {
    $src  = Join-Path $BuildOutput $entry.File
    $dest = Join-Path $AddinDest   $entry.File

    # Extra safety: confirm this name is not in Revit's own folder
    $revitConflict = Test-Path (Join-Path $RevitDir $entry.File)
    if ($revitConflict) {
        Write-WARN "$($entry.File)  - EXISTS in Revit $RevitVersion folder. Skipping to prevent conflict."
        $skipped++
        continue
    }

    if (Test-Path $src) {
        if (-not $DryRun) {
            Copy-Item $src $dest -Force
        }
        Write-OK "$($entry.File)"
        $copied++
    }
    elseif ($entry.Required) {
        Write-WARN "$($entry.File) - NOT FOUND (required). Check build output."
        $missing++
    }
    else {
        Write-SKIP "$($entry.File) - not found (optional, skipping)"
        $skipped++
    }
}

# SQLITE NATIVE INTEROP - must live in x64\ subfolder
# SQLite.Interop.dll is a C++ DLL that SQLite's managed wrapper pinvokes.
# It MUST be in an x64\ subfolder relative to the addin or Revit's exe.
# The managed System.Data.SQLite.dll will look for it in:
#   1. <AddinDir>\x64\SQLite.Interop.dll   < where we put it
#   2. <RevitExeDir>\x64\SQLite.Interop.dll

Write-HEAD "Deploying SQLite native interop (x64)..."

$interopSrc  = Join-Path $BuildOutput "x64\SQLite.Interop.dll"
$interopDest = Join-Path $AddinDest   "x64\SQLite.Interop.dll"

if (Test-Path $interopSrc) {
    if (-not $DryRun) { Copy-Item $interopSrc $interopDest -Force }
    Write-OK "x64\SQLite.Interop.dll"
    $copied++
} else {
    # Try alternate NuGet cache location
    $nugetInterop = Get-ChildItem "$env:USERPROFILE\.nuget\packages\system.data.sqlite.core" `
        -Recurse -Filter "SQLite.Interop.dll" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\x64\*" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($nugetInterop) {
        if (-not $DryRun) { Copy-Item $nugetInterop.FullName $interopDest -Force }
        Write-OK "x64\SQLite.Interop.dll  (from NuGet cache: $($nugetInterop.FullName))"
        $copied++
    } else {
        Write-WARN "x64\SQLite.Interop.dll NOT FOUND - SQLite database will fail at runtime."
        Write-WARN "Expected at: $interopSrc"
        Write-WARN "Or in NuGet: %USERPROFILE%\.nuget\packages\system.data.sqlite.core\<ver>\build\net451\x64\"
        $missing++
    }
}

# ADDIN MANIFEST

Write-HEAD "Deploying addin manifest..."

$addinSrc  = Join-Path $PSScriptRoot "ClashResolveAI.addin"
# The .addin file must go in the Addins\2024\ root, NOT in the subfolder
$addinRoot = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion"
$addinManifestDest = Join-Path $addinRoot "ClashResolveAI.addin"

if (Test-Path $addinSrc) {
    # Update the Assembly path inside the .addin file to point to the subfolder
    $addinContent = Get-Content $addinSrc -Raw
    $expectedPath = "$AddinDest\ClashResolveAI.dll"

    # Replace any existing Assembly path with the correct absolute path
    $updatedContent = $addinContent -replace `
        '<Assembly>.*?</Assembly>', `
        "<Assembly>$expectedPath</Assembly>"

    if (-not $DryRun) {
        Set-Content -Path $addinManifestDest -Value $updatedContent -Encoding UTF8
    }
    Write-OK "ClashResolveAI.addin  ->  $addinRoot"
} else {
    Write-ERR "ClashResolveAI.addin not found at: $addinSrc"
    $missing++
}

# RESOURCES (icons)

Write-HEAD "Deploying resources..."

$resourcesSrc = Join-Path $PSScriptRoot "Resources"
if (Test-Path $resourcesSrc) {
    $icons = Get-ChildItem $resourcesSrc -Filter "*.png"
    foreach ($icon in $icons) {
        $dst = Join-Path "$AddinDest\Resources" $icon.Name
        if (-not $DryRun) { Copy-Item $icon.FullName $dst -Force }
        Write-OK "Resources\$($icon.Name)"
    }
    Write-OK "$($icons.Count) icon(s) copied."
} else {
    Write-WARN "Resources folder not found - ribbon icons will be missing."
}

# RULE SET JSON FILES (copy to AppData so users can edit them)

Write-HEAD "Deploying rule sets..."

$rulesAppData = "$env:APPDATA\ClashResolveAI\Rules"
if (-not $DryRun) { New-Item -ItemType Directory -Force -Path $rulesAppData | Out-Null }

$rulesSrc = Join-Path $PSScriptRoot "src\Rules"
$jsonFiles = Get-ChildItem $rulesSrc -Filter "*.json" -ErrorAction SilentlyContinue
foreach ($json in $jsonFiles) {
    $dst = Join-Path $rulesAppData $json.Name
    # Only copy if not already there - don't overwrite user-edited rules
    if (-not (Test-Path $dst)) {
        if (-not $DryRun) { Copy-Item $json.FullName $dst -Force }
        Write-OK "Rules\$($json.Name)  ->  $rulesAppData"
    } else {
        Write-SKIP "Rules\$($json.Name)  (already exists - preserving user edits)"
    }
}

# VERIFY: confirm deployed DLLs don't shadow anything in Revit's folder

Write-HEAD "Post-deploy conflict verification..."

$conflicts = 0
if (-not $DryRun -and (Test-Path $RevitDir)) {
    $deployedDlls = Get-ChildItem $AddinDest -Filter "*.dll" -File
    foreach ($dll in $deployedDlls) {
        $revitDllPath = Join-Path $RevitDir $dll.Name
        if (Test-Path $revitDllPath) {
            Write-ERR "CONFLICT: $($dll.Name) exists in both addin folder AND Revit folder!"
            Write-ERR "          Addin:  $($dll.FullName)"
            Write-ERR "          Revit:  $revitDllPath"
            Write-ERR "          ACTION: Delete the addin copy immediately."
            $conflicts++
        }
    }
    if ($conflicts -eq 0) { Write-OK "No conflicts detected with Revit $RevitVersion installation." }
}

# SUMMARY

Write-Host ""
Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "  DEPLOY SUMMARY" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "  Target   : $AddinDest"
Write-Host "  Addin file : $((Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion\ClashResolveAI.addin"))"
Write-Host "  Copied   : $copied file(s)"   -ForegroundColor Green
if ($skipped -gt 0) { Write-Host "  Skipped  : $skipped file(s)"  -ForegroundColor DarkGray }
if ($missing -gt 0) { Write-Host "  Missing  : $missing file(s)"  -ForegroundColor Yellow   }
if ($conflicts -gt 0){Write-Host "  CONFLICTS: $conflicts - FIX BEFORE STARTING REVIT!" -ForegroundColor Red }
if ($DryRun)        { Write-Host "  *** DRY RUN - nothing was actually written ***" -ForegroundColor Magenta }
Write-Host "====================================================" -ForegroundColor Cyan

if ($conflicts -gt 0) {
    Write-Host "`nDeployment has CONFLICTS. Do not start Revit until resolved." -ForegroundColor Red
    exit 2
}
if ($missing -gt 0) {
    Write-Host "`nDeployment completed with warnings. Check missing files above." -ForegroundColor Yellow
    exit 1
}

Write-Host "`nDeployment successful. Safe to start Revit $RevitVersion." -ForegroundColor Green
exit 0
