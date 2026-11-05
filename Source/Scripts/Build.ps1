[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$Package,
    [ValidateRange(0, 65534)][Nullable[int]]$MajorVersion,
    [switch]$FatalBugFix,
    [switch]$FeatureRelease,
    [ValidateRange(0, 100)]
    [int]$CpuPercent = 0
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QuietTools.ps1')
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
$SourceRoot = Split-Path -Path $PSScriptRoot -Parent
$ProjectRoot = Split-Path -Path $SourceRoot -Parent
$ProjectFile = Join-Path $SourceRoot 'CDir.csproj'
$DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
$RunnerPath = Initialize-QuietRunner
$BuildArguments = @('--nologo', '--disable-build-servers', '-nr:false', '-p:UseSharedCompilation=false')
if (-not ($Publish -or $Package) -and ($null -ne $MajorVersion -or $FatalBugFix -or $FeatureRelease)) {
    throw 'Manual version changes require -Publish or -Package.'
}

if ($Publish -or $Package) {
    New-Item -ItemType Directory -Path (Join-Path $ProjectRoot 'Build') -Force | Out-Null
    try {
        $ReleaseLock = [IO.File]::Open((Join-Path $ProjectRoot 'Build\ReleaseVersion.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch { throw 'Another release owns the version lock; wait for it to finish.' }
    try {
    $ReleasePlan = Get-ReleasePlan -SourceRoot $SourceRoot -MajorVersion $MajorVersion -FatalBugFix:$FatalBugFix -FeatureRelease:$FeatureRelease
    $VersionArguments = @(('-p:Version=' + $ReleasePlan.Version), '-p:IncludeSourceRevisionInInformationalVersion=false')
    $PublishDirectory = Join-Path $ProjectRoot 'Publish\CDirWinX64'
    Write-Host ('Publishing and packaging v' + $ReleasePlan.Version + ' with background I/O scheduling. No tests or application windows will start.')
    # Rebuild BAML and its generated connection IDs together. Incremental WPF
    # caches can otherwise pair new resources with stale code when publishing.
    Invoke-QuietProcess -RunnerPath $RunnerPath -Executable $DotnetPath -WorkingDirectory $ProjectRoot -CpuPercent $CpuPercent -Arguments @(
        'build', $ProjectFile, '-c', $Configuration, '--no-incremental', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false'
    ) -AdditionalArguments ($BuildArguments + $VersionArguments)
    Invoke-QuietProcess -RunnerPath $RunnerPath -Executable $DotnetPath -WorkingDirectory $ProjectRoot -CpuPercent $CpuPercent -Arguments @(
        'publish', $ProjectFile, '-c', $Configuration, '--no-build', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $PublishDirectory
    ) -AdditionalArguments ($BuildArguments + $VersionArguments)
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'Docs\使用说明.txt') -Destination (Join-Path $PublishDirectory '使用说明.txt') -Force
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'Docs\UserGuide.txt') -Destination (Join-Path $PublishDirectory 'UserGuide.txt') -Force
    [ordered]@{ Version = $ReleasePlan.Version; SourceHash = $ReleasePlan.SourceHash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PublishDirectory 'Release.json') -Encoding utf8
    Invoke-QuietProcess -RunnerPath $RunnerPath -Executable (Get-Process -Id $PID).Path -WorkingDirectory $ProjectRoot -CpuPercent $CpuPercent `
        -Arguments @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'Package.ps1'))
    # Commit only after compilation, publication and ZIP creation have all succeeded.
    Complete-ReleaseVersion -Plan $ReleasePlan
    Write-Host "Published: $(Join-Path $PublishDirectory 'CDir.exe')"
    Write-Host ('Release version: v' + $ReleasePlan.Version)
    } finally { $ReleaseLock.Dispose() }
} else {
    Write-Host 'Building only, with background I/O scheduling. Tests and packaging are separate commands.'
    Invoke-QuietProcess -RunnerPath $RunnerPath -Executable $DotnetPath -WorkingDirectory $ProjectRoot -CpuPercent $CpuPercent `
        -Arguments @('build', $ProjectFile, '-c', $Configuration) -AdditionalArguments $BuildArguments
}
