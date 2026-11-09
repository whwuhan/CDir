[CmdletBinding()]
param(
    [ValidateSet('filesystem', 'search', 'language', 'address', 'version-control', 'all')]
    [string[]]$Group = @('filesystem', 'language', 'address'),
    [ValidateSet('transfer', 'selection', 'lifecycle', 'tabs', 'panes', 'navigation', 'sidebar', 'address', 'operations', 'columns', 'fonts', 'search', 'language', 'shortcuts', 'shell-icons', 'version-control', 'terminal', 'all')]
    [string[]]$UIGroup = @(),
    [ValidateSet('release')][string[]]$ScriptGroup = @(),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(0, 100)][int]$CpuPercent = 0,
    [ValidateRange(10, 3600)][int]$TimeoutSeconds = 300,
    [switch]$SkipBuild,
    [switch]$PublishConfiguration,
    [ValidateRange(0, 65534)][Nullable[int]]$MajorVersion,
    [switch]$FatalBugFix,
    [switch]$FeatureRelease,
    [switch]$Published
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QuietTools.ps1')
$SourceRoot = Split-Path $PSScriptRoot -Parent
$ProjectRoot = Split-Path $SourceRoot -Parent
$RunTimer = [Diagnostics.Stopwatch]::StartNew()
if (($PSBoundParameters.ContainsKey('UIGroup') -or $PSBoundParameters.ContainsKey('ScriptGroup')) -and -not $PSBoundParameters.ContainsKey('Group')) { $Group = @() }
$Group = @($Group | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
$UIGroup = @($UIGroup | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
if ($Group.Count -eq 0 -and $UIGroup.Count -eq 0 -and $ScriptGroup.Count -eq 0) { throw 'Select at least one background, UI or script group.' }
if ($Published -and ($Group.Count -gt 0 -or $UIGroup.Count -eq 0)) { throw '-Published requires UI groups only.' }
if ($PublishConfiguration -and ($UIGroup.Count -eq 0 -or $Published -or $SkipBuild)) {
    throw '-PublishConfiguration requires UI groups and a fresh build; do not combine it with -Published or -SkipBuild.'
}
if ($FatalBugFix -and -not $PublishConfiguration) { throw '-FatalBugFix requires -PublishConfiguration to test the release candidate.' }
if ($FeatureRelease -and -not $PublishConfiguration) { throw '-FeatureRelease requires -PublishConfiguration to test the release candidate.' }
if ($null -ne $MajorVersion -and -not $PublishConfiguration) { throw '-MajorVersion requires -PublishConfiguration to test the release candidate.' }
$AvailableGroups = @('filesystem', 'search', 'language', 'address', 'version-control')
$AvailableUIGroups = @('transfer', 'selection', 'lifecycle', 'tabs', 'panes', 'navigation', 'sidebar', 'address', 'operations', 'columns', 'fonts', 'search', 'language', 'shortcuts', 'shell-icons', 'version-control', 'terminal')
if ($Group -contains 'all') { $Group = $AvailableGroups }
# Keep a fresh window and owned app scope per group, including all, while building the project once.
if ($UIGroup -contains 'all') { $UIGroup = $AvailableUIGroups }
$SelectedUIGroups = $UIGroup
Write-Host ('Selected background groups: ' + ($Group -join ', ') + '; UI groups: ' + ($UIGroup -join ', '))
Write-Host ('Selected script groups: ' + ($ScriptGroup -join ', ') + '; not selected script groups: ' + (@('release') | Where-Object { $_ -notin $ScriptGroup }))
Write-Host ('Not selected background groups: ' + (($AvailableGroups | Where-Object { $_ -notin $Group }) -join ', ') +
    '; UI groups: ' + (($AvailableUIGroups | Where-Object { $_ -notin $SelectedUIGroups }) -join ', '))
$RunnerPath = Initialize-QuietRunner
$DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
$RunId = [Guid]::NewGuid().ToString('N')
$RunParent = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CDir\TestRuns'
# Packaged desktop hosts can redirect new AppData folders. Native Git and the
# managed test code must receive the same physical owned root for path checks.
New-Item -ItemType Directory -Path $RunParent -Force | Out-Null
$RunParent = Resolve-QuietDirectory $RunParent
$RunRoot = Join-Path $RunParent $RunId
$TempRoot = Join-Path $RunRoot 'Temp'
$OutputRoot = Join-Path $ProjectRoot ('Artifacts\TestRuns\' + $RunId)
New-Item -ItemType Directory -Path $TempRoot, $OutputRoot -Force | Out-Null
$Marker = Join-Path $RunRoot '.CDirTestRunOwner'
[IO.File]::WriteAllText($Marker, $RunId)
$BuildFlags = @('--nologo', '--disable-build-servers', '-nr:false', '-p:UseSharedCompilation=false')
$Completed = $false
$CompletedUI = @()
$SkippedUI = @()
try {
    foreach ($ScriptName in ($ScriptGroup | Select-Object -Unique)) {
        Invoke-QuietProcess -RunnerPath $RunnerPath -Executable (Get-Process -Id $PID).Path -WorkingDirectory $ProjectRoot -CpuPercent $CpuPercent -TimeoutSeconds $TimeoutSeconds -TempRoot $TempRoot `
            -Arguments @('-NoProfile', '-NonInteractive', '-File', (Join-Path $SourceRoot 'Tests\ReleaseVersionTests.ps1'))
    }
    if ($Group.Count -gt 0) {
        if (-not $SkipBuild) {
            Invoke-QuietProcess -RunnerPath $RunnerPath -Executable $DotnetPath -WorkingDirectory $ProjectRoot -CpuPercent $CpuPercent -TimeoutSeconds $TimeoutSeconds -TempRoot $TempRoot `
                -Arguments @('build', (Join-Path $SourceRoot 'Tests\CDirTests.csproj'), '-c', $Configuration) -AdditionalArguments $BuildFlags
        }
        $TestExe = Join-Path $ProjectRoot "Build\Bin\CDirTests\$Configuration\net10.0-windows\CDirTests.exe"
        Invoke-QuietProcess -RunnerPath $RunnerPath -Executable $TestExe -WorkingDirectory $ProjectRoot -CpuPercent $CpuPercent -TimeoutSeconds $TimeoutSeconds -TempRoot $TempRoot `
            -Arguments @('--group', ($Group -join ','))
    }
    if ($UIGroup.Count -gt 0) {
        if (-not $SkipBuild -and -not $Published) {
            $UIBuildArguments = @('build', (Join-Path $SourceRoot 'CDir.csproj'), '-c', $Configuration)
            if ($PublishConfiguration) {
                . (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
                $ReleasePlan = Get-ReleasePlan -SourceRoot $SourceRoot -MajorVersion $MajorVersion -FatalBugFix:$FatalBugFix -FeatureRelease:$FeatureRelease
                Write-Host ('Testing release candidate: v' + $ReleasePlan.Version + ' (version will not be advanced).')
                $UIBuildArguments += @('--no-incremental', '-r', 'win-x64', '--self-contained', 'true',
                    '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false',
                    ('-p:Version=' + $ReleasePlan.Version), '-p:IncludeSourceRevisionInInformationalVersion=false')
            }
            Invoke-QuietProcess -RunnerPath $RunnerPath -Executable $DotnetPath -WorkingDirectory $ProjectRoot -CpuPercent $CpuPercent -TimeoutSeconds $TimeoutSeconds -TempRoot $TempRoot `
                -Arguments $UIBuildArguments -AdditionalArguments $BuildFlags
        }
        $AppExe = if ($Published) { Join-Path $ProjectRoot 'Publish\CDirWinX64\CDir.exe' }
            elseif ($PublishConfiguration) { Join-Path $ProjectRoot "Build\Bin\CDir\$Configuration\net10.0-windows\win-x64\CDir.exe" }
            else { Join-Path $ProjectRoot "Build\Bin\CDir\$Configuration\net10.0-windows\CDir.exe" }
        if ($Published) { Write-Host ('Testing existing published application: ' + $AppExe) }
        foreach ($UIName in $UIGroup) {
            $PreviewPath = Join-Path $OutputRoot ($UIName + '.png')
            Write-Host "UI group: $UIName (current desktop, software rendering)"
            Invoke-QuietProcess -RunnerPath $RunnerPath -Executable $AppExe -WorkingDirectory $ProjectRoot -CpuPercent $CpuPercent -TimeoutSeconds $TimeoutSeconds -TempRoot $TempRoot `
                -Arguments @('--smoke-test', '--smoke-group', $UIName, '--screenshot', $PreviewPath)
            $Report = Get-Content -LiteralPath ($PreviewPath + '.json') -Raw | ConvertFrom-Json
            if ([string]$Report.requested_group -ne $UIName -or $null -eq $Report.passed -or @($Report.passed).Count -eq 0) { throw 'UI report is missing or does not match the requested group.' }
            $ActualGroups = @($Report.groups | Where-Object { $_ })
            $ActualSkipped = @($Report.skipped_groups | Where-Object { $_ })
            if ([string]$Report.desktop -ne 'current') { throw 'UI report does not match the current-desktop test mode.' }
            $ExpectedGroups = if ($UIName -eq 'all') { $AvailableUIGroups } else { @($UIName) }
            foreach ($ExpectedGroup in $ExpectedGroups) {
                if ($ActualGroups -notcontains $ExpectedGroup -and $ActualSkipped -notcontains $ExpectedGroup) { throw ('UI report omitted group: ' + $ExpectedGroup) }
            }
            $CompletedUI += $ActualGroups
            $SkippedUI += $ActualSkipped
            Write-Host ("UI {0}: {1} checks passed; completed: {2}; skipped: {3}." -f $UIName, @($Report.passed).Count, ($ActualGroups -join ', '), ($ActualSkipped -join ', '))
        }
    }
    $Completed = $true
    Write-Host ('Completed script groups: ' + (($ScriptGroup | Select-Object -Unique) -join ', '))
    Write-Host ('Completed background groups: ' + ($Group -join ', ') + '; UI groups: ' + (($CompletedUI | Select-Object -Unique) -join ', ') + '; skipped UI groups: ' + (($SkippedUI | Select-Object -Unique) -join ', '))
    Write-Host ('Reports: ' + $OutputRoot)
} finally {
    # Reclaim only this run's marked private tree, after its owned process tree has exited.
    $Resolved = [IO.Path]::GetFullPath($RunRoot)
    $ExpectedParent = [IO.Path]::GetFullPath($RunParent).TrimEnd('\') + '\'
    if ($Completed -and $Resolved.StartsWith($ExpectedParent, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($Resolved) -eq $RunId -and [IO.File]::Exists($Marker) -and
        [IO.File]::ReadAllText($Marker) -eq $RunId -and ([IO.File]::GetAttributes($Resolved) -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        try {
            function Clear-TestReadOnly([string]$DirectoryPath) {
                foreach ($Entry in [IO.Directory]::EnumerateFileSystemEntries($DirectoryPath)) {
                    $Attributes = [IO.File]::GetAttributes($Entry)
                    if (($Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unexpected test reparse point; preserving data.' }
                    if (($Attributes -band [IO.FileAttributes]::Directory) -ne 0) { Clear-TestReadOnly $Entry }
                    else { [IO.File]::SetAttributes($Entry, ($Attributes -band (-bnot [IO.FileAttributes]::ReadOnly))) }
                }
            }
            Clear-TestReadOnly $Resolved
            [IO.Directory]::Delete($Resolved, $true)
        } catch { Write-Warning "Test data retained: $Resolved ($($_.Exception.Message))" }
    } elseif (-not $Completed) { Write-Warning ('Failed-run data retained: ' + $RunRoot) }
    Write-Host ('Test run elapsed: {0:N2}s (including build and cleanup).' -f $RunTimer.Elapsed.TotalSeconds)
}
