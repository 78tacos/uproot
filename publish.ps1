<#
.SYNOPSIS
    Builds Uproot release binaries.

.DESCRIPTION
    Produces two flavours, each a folder holding two single-file executables:

      Uproot.exe   the window
      uproot.com   the command line

    Windows file names ignore case, so the command line cannot be uproot.exe beside Uproot.exe.
    It is built as uproot-cli.exe and shipped as uproot.com: cmd and PowerShell both try .com
    before .exe, so typing "uproot" in a shell runs the command line, and double-clicking
    Uproot.exe opens the window. Visual Studio's devenv.com and devenv.exe work the same way.

      self-contained        Runs on any Windows 10 or 11 x64 machine with no .NET installed.

      framework-dependent   Much smaller; needs the .NET 10 Desktop Runtime on the machine.

    Trimming is deliberately not enabled: WPF does not support it, and the reflection the XAML
    loader performs would break at runtime rather than at publish time.

.PARAMETER Flavour
    Which build to produce. Defaults to both.

.PARAMETER Runtime
    Target runtime identifier. Defaults to win-x64.

.PARAMETER OutputRoot
    Where to write the output. Defaults to .\dist

.PARAMETER Zip
    Also package each flavour as Uproot-<version>.<build>-<flavour>-<runtime>.zip in
    OutputRoot. The zip is always built fresh and checked for duplicate entries before this
    reports success.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Flavour self-contained -Zip
#>

[CmdletBinding()]
param(
    [ValidateSet('both', 'self-contained', 'framework-dependent')]
    [string]$Flavour = 'both',

    [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
    [string]$Runtime = 'win-x64',

    [string]$OutputRoot = (Join-Path $PSScriptRoot 'dist'),

    [switch]$Zip
)

$ErrorActionPreference = 'Stop'

# Expand an 8.3 short path (C:\Users\JOSEPH~1.OGL) to its long form: Compress-Archive once
# failed to delete an existing archive through a short path and appended to it instead.
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$OutputRoot = (Get-Item -LiteralPath $OutputRoot).FullName

Add-Type -AssemblyName System.IO.Compression.FileSystem

<#
    Builds the zip with ZipFile.CreateFromDirectory, which refuses to write over an existing
    file rather than appending to it, and then checks the result.
#>
function New-ReleaseZip {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$ZipPath
    )

    if ([System.IO.File]::Exists($ZipPath)) { [System.IO.File]::Delete($ZipPath) }
    if ([System.IO.File]::Exists($ZipPath)) { throw "Could not remove the previous $ZipPath" }

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $SourceDirectory, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName })
        $duplicates = @($names | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
        $expected = @(Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File).Count

        if ($duplicates.Count -gt 0) { throw "Duplicate entries in ${ZipPath}: $($duplicates -join ', ')" }
        if ($names.Count -ne $expected) { throw "$ZipPath has $($names.Count) entries; expected $expected" }
    }
    finally {
        $archive.Dispose()
    }

    Write-Host ("  {0}  {1:N1} MB, {2} files" -f (Split-Path $ZipPath -Leaf), ((Get-Item $ZipPath).Length / 1MB), $names.Count) -ForegroundColor Green
}

$project = Join-Path $PSScriptRoot 'src\Uproot.App\Uproot.App.csproj'
$cliProject = Join-Path $PSScriptRoot 'src\Uproot.Cli\Uproot.Cli.csproj'
foreach ($p in $project, $cliProject) { if (-not (Test-Path $p)) { throw "Could not find $p" } }

function Publish-Flavour {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$SelfContained
    )

    $outDir = Join-Path $OutputRoot "$Name\$Runtime"
    $cliDir = Join-Path $OutputRoot "obj\cli-$Name-$Runtime"
    Write-Host ""
    Write-Host "=== $Name ($Runtime) ===" -ForegroundColor Cyan

    # Clear the contents rather than the directory itself: a shell sitting in that folder holds
    # a handle on it, and failing the whole publish over that is needless.
    foreach ($dir in $outDir, $cliDir) {
        if (Test-Path $dir) { Get-ChildItem $dir -Force | Remove-Item -Recurse -Force -ErrorAction Stop }
        else { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    }

    $common = @(
        '-c', 'Release',
        '-r', $Runtime,
        '--nologo',
        "-p:SelfContained=$($SelfContained.ToString().ToLower())",
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=none',
        '-p:GenerateDocumentationFile=false',
        '-p:SatelliteResourceLanguages=en'
    )

    # Compression is rejected outright for framework-dependent bundles (NETSDK1176), so it is
    # only added for the self-contained build, where it saves roughly a third of the size.
    if ($SelfContained) { $common += '-p:EnableCompressionInSingleFile=true' }

    & dotnet publish $project -o $outDir @common | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Name (exit $LASTEXITCODE)" }

    # Published apart, then copied in under its shipping name.
    & dotnet publish $cliProject -o $cliDir @common | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for the command line (exit $LASTEXITCODE)" }

    $exe = Join-Path $outDir 'Uproot.exe'
    if (-not (Test-Path $exe)) { throw "Expected $exe to exist after publish" }

    $cliBuilt = Join-Path $cliDir 'uproot-cli.exe'
    if (-not (Test-Path $cliBuilt)) { throw "Expected $cliBuilt to exist after publish" }

    $com = Join-Path $outDir 'uproot.com'
    Copy-Item $cliBuilt $com -Force
    Remove-Item $cliDir -Recurse -Force

    # Both must be the same build: a folder holding two is how a stale copy goes unnoticed.
    $appVersion = (Get-Item $exe).VersionInfo
    $cliVersion = (Get-Item $com).VersionInfo
    if ($appVersion.ProductVersion -ne $cliVersion.ProductVersion) {
        throw "Uproot.exe is $($appVersion.ProductVersion) but uproot.com is $($cliVersion.ProductVersion)"
    }

    # The renamed host has to start: run it once.
    $reported = & $com version
    if ($LASTEXITCODE -ne 0 -or "$reported" -notlike 'Uproot *') { throw "uproot.com did not run: $reported" }

    Write-Host ("  Build          {0}   ({1})" -f $appVersion.FileVersion, $appVersion.ProductVersion) -ForegroundColor Green
    if ($appVersion.ProductVersion -like '*.dirty') {
        Write-Host "  Built from uncommitted changes: the build number does not pin down exactly what is in it." -ForegroundColor Yellow
    }

    Write-Host ("  Uproot.exe     {0,6:N1} MB   (the window)" -f ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
    Write-Host ("  uproot.com     {0,6:N1} MB   (the command line)" -f ((Get-Item $com).Length / 1MB)) -ForegroundColor Green

    Get-ChildItem $outDir -Recurse -File |
        Where-Object { $_.Name -notin @('Uproot.exe', 'uproot.com') } |
        ForEach-Object { Write-Host ("  {0,-30} {1,8:N0} bytes" -f $_.Name, $_.Length) -ForegroundColor DarkGray }

    $zipPath = $null
    if ($Zip) {
        $zipPath = Join-Path $OutputRoot "Uproot-$($appVersion.FileVersion)-$Name-$Runtime.zip"
        New-ReleaseZip -SourceDirectory $outDir -ZipPath $zipPath
    }

    [pscustomobject]@{ Flavour = $Name; Build = $appVersion.ProductVersion; Path = $exe; Zip = $zipPath }
}

$results = @()
if ($Flavour -in @('both', 'self-contained')) { $results += Publish-Flavour -Name 'self-contained' -SelfContained $true }
if ($Flavour -in @('both', 'framework-dependent')) { $results += Publish-Flavour -Name 'framework-dependent' -SelfContained $false }

$objDir = Join-Path $OutputRoot 'obj'
if ((Test-Path $objDir) -and -not (Get-ChildItem $objDir -Force)) { Remove-Item $objDir -Force }

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Cyan
$results | Format-Table Flavour, Build, Path, Zip -AutoSize | Out-String | Write-Host

Write-Host "The framework-dependent build needs the .NET 10 Desktop Runtime:" -ForegroundColor DarkGray
Write-Host "  winget install Microsoft.DotNet.DesktopRuntime.10" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Both run from wherever they are unzipped and write nothing until something is removed." -ForegroundColor DarkGray
