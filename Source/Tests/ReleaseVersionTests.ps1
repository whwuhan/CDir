$ErrorActionPreference = 'Stop'
if ($env:CDIR_QUIET_RUNNER -ne '1') { throw 'Run release checks through Source/Scripts/Test.ps1 -ScriptGroup release.' }
. (Join-Path $PSScriptRoot '..\Scripts\ReleaseVersion.ps1')
$Fixture = Join-Path ([IO.Path]::GetTempPath()) ('CDirRelease-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Fixture | Out-Null
$VersionFile = Join-Path $Fixture 'Version.props'
$CodeFile = Join-Path $Fixture 'Example.cs'
[IO.File]::WriteAllText($VersionFile, '<Project><PropertyGroup><Version>0.0.0</Version><LastPublishedSourceHash></LastPublishedSourceHash></PropertyGroup></Project>')
[IO.File]::WriteAllText($CodeFile, 'first source')
$Checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('Release version test failed: ' + $Name) }
    $script:Checks++
    Write-Host ('PASS: ' + $Name)
}
function Rejects([scriptblock]$Action) {
    try { & $Action | Out-Null; return $false } catch { return $true }
}
$InitialBytes = [IO.File]::ReadAllText($VersionFile)
$Initial = Get-ReleasePlan -SourceRoot $Fixture
Check ($Initial.Version -eq '0.0.0') 'The first release establishes v0.0.0.'
Check ([IO.File]::ReadAllText($VersionFile) -ceq $InitialBytes) 'Planning or testing never changes the saved version.'
Complete-ReleaseVersion -Plan $Initial
Check ((Get-ReleasePlan -SourceRoot $Fixture).Version -eq '0.0.0') 'Repackaging unchanged source keeps the same version.'
[IO.File]::WriteAllText($CodeFile, 'modified source')
$Modified = Get-ReleasePlan -SourceRoot $Fixture
Check ($Modified.Version -eq '0.0.1' -and $Modified.SourceHash -ne $Initial.SourceHash) 'A source change increments only the patch candidate.'
Check ((Get-ReleasePlan -SourceRoot $Fixture).Version -eq '0.0.1') 'Retrying an uncommitted release does not skip patch numbers.'
Complete-ReleaseVersion -Plan $Modified
Check (([xml][IO.File]::ReadAllText($VersionFile)).Project.PropertyGroup.Version -eq '0.0.1') 'A completed release persists its exact candidate.'
$Manual = Get-ReleasePlan -SourceRoot $Fixture -MajorVersion 3
Check ($Manual.Version -eq '3.0.0') 'Changing the major version resets minor and patch to zero.'
Complete-ReleaseVersion -Plan $Manual
$Reduced = Get-ReleasePlan -SourceRoot $Fixture -MajorVersion 1
Check ($Reduced.Version -eq '1.0.0') 'Lowering the major version also resets minor and patch to zero.'
Complete-ReleaseVersion -Plan $Reduced
[IO.File]::WriteAllText($CodeFile, 'changed source during a major release')
$MajorWithFlags = Get-ReleasePlan -SourceRoot $Fixture -MajorVersion 4 -FatalBugFix -FeatureRelease
Check ($MajorWithFlags.Version -eq '4.0.0') 'An explicit major change takes precedence over automatic minor and patch increments.'
Check ((Get-ReleasePlan -SourceRoot $Fixture -MajorVersion 4).Version -eq '4.0.0') 'Retrying a major release does not consume a patch number.'
$ManualDocument = [xml][IO.File]::ReadAllText($VersionFile)
$ManualDocument.Project.PropertyGroup.Version = '5.7.9'
$ManualDocument.Save($VersionFile)
Check ((Get-ReleasePlan -SourceRoot $Fixture).Version -eq '5.0.0') 'Editing the major component in Version.props resets both other components.'
# Use an independent saved baseline for the following fatal-fix progression checks.
[IO.File]::WriteAllText($VersionFile, '<Project><PropertyGroup><Version>1.0.1</Version><LastPublishedVersion>1.0.1</LastPublishedVersion><LastPublishedSourceHash>' + $Reduced.SourceHash + '</LastPublishedSourceHash></PropertyGroup></Project>')
[IO.File]::WriteAllText($CodeFile, 'fix a fatal crash or freeze')
$Fatal = Get-ReleasePlan -SourceRoot $Fixture -FatalBugFix
Check ($Fatal.Version -eq '1.1.2') 'A fatal bug fix increments minor and patch once without resetting patch or changing major.'
Check (([xml][IO.File]::ReadAllText($VersionFile)).Project.PropertyGroup.Version -eq '1.0.1') 'Planning a fatal fix consumes neither minor nor patch.'
Complete-ReleaseVersion -Plan $Fatal
Check ((Get-ReleasePlan -SourceRoot $Fixture -FatalBugFix).Version -eq '1.1.2') 'Repackaging the same fatal fix cannot increment minor twice.'
[IO.File]::WriteAllText($CodeFile, 'a later source change')
$Later = Get-ReleasePlan -SourceRoot $Fixture
Check ($Later.Version -eq '1.1.3') 'Ordinary changes retain major and minor while incrementing patch.'
$BeforeFailure = [IO.File]::ReadAllText($VersionFile)
[IO.File]::WriteAllText($CodeFile, 'changed during publication')
Check (Rejects { Complete-ReleaseVersion -Plan $Later }) 'Source changes during publication prevent committing an incorrect release.'
Check ([IO.File]::ReadAllText($VersionFile) -ceq $BeforeFailure) 'An unsuccessful completion leaves version state untouched.'
$Fresh = Get-ReleasePlan -SourceRoot $Fixture
[IO.File]::WriteAllText($VersionFile, $BeforeFailure.Replace('1.1.2', '2.1.2'))
$ManualEdit = [IO.File]::ReadAllText($VersionFile)
Check (Rejects { Complete-ReleaseVersion -Plan $Fresh }) 'A manual version edit during publication is never overwritten.'
Check ([IO.File]::ReadAllText($VersionFile) -ceq $ManualEdit) 'Rejected completion preserves the manual edit.'
Check (Rejects { Get-ReleasePlan -SourceRoot $Fixture -MajorVersion -1 }) 'Negative version components are rejected.'
[IO.File]::WriteAllText($VersionFile, '<Project><PropertyGroup><Version>1.2</Version></PropertyGroup></Project>')
Check (Rejects { Get-ReleasePlan -SourceRoot $Fixture }) 'Malformed versions are rejected before publication.'
[IO.File]::WriteAllText($VersionFile, '<Project><PropertyGroup><Version>1.2.65534</Version><LastPublishedSourceHash>' + $Initial.SourceHash + '</LastPublishedSourceHash></PropertyGroup></Project>')
Check (Rejects { Get-ReleasePlan -SourceRoot $Fixture }) 'Patch overflow fails without advancing major or minor versions.'
[IO.File]::WriteAllText($VersionFile, '<Project><PropertyGroup><Version>1.65534.2</Version><LastPublishedSourceHash>' + $Initial.SourceHash + '</LastPublishedSourceHash></PropertyGroup></Project>')
Check (Rejects { Get-ReleasePlan -SourceRoot $Fixture -FatalBugFix }) 'Minor overflow cannot automatically advance the user-controlled major version.'
Check (Rejects { Get-ReleasePlan -SourceRoot $Fixture -FeatureRelease }) 'A requested feature release cannot overflow minor or advance major automatically.'
Check ((Get-ReleasePlan -SourceRoot $Fixture).Version -eq '1.65534.3') 'The minor limit does not prevent ordinary patch releases.'

# A feature release is opt-in, shares transactional commit behavior with fatal
# fixes, and must never reset patch or consume a second minor for the same code.
$FeatureBaseHash = Get-ReleaseSourceHash -SourceRoot $Fixture
[IO.File]::WriteAllText($VersionFile, '<Project><PropertyGroup><Version>1.2.3</Version><LastPublishedSourceHash>' + $FeatureBaseHash + '</LastPublishedSourceHash></PropertyGroup></Project>')
Check ((Get-ReleasePlan -SourceRoot $Fixture -FeatureRelease).Version -eq '1.2.3') 'Repackaging unchanged source does not consume minor or patch for a feature release.'
[IO.File]::WriteAllText($CodeFile, 'a major new feature')
$FeatureOriginal = [IO.File]::ReadAllText($VersionFile)
Check ((Get-ReleasePlan -SourceRoot $Fixture).Version -eq '1.2.4') 'A feature does not automatically change minor without an explicit request.'
$Feature = Get-ReleasePlan -SourceRoot $Fixture -FeatureRelease
Check ($Feature.Version -eq '1.3.4') 'An explicitly requested major feature increments minor and patch once without resetting patch.'
Check ((Get-ReleasePlan -SourceRoot $Fixture -FeatureRelease).Version -eq '1.3.4' -and [IO.File]::ReadAllText($VersionFile) -ceq $FeatureOriginal) 'Feature release testing and retries do not consume version numbers.'
Check ((Get-ReleasePlan -SourceRoot $Fixture -FeatureRelease -FatalBugFix).Version -eq '1.3.4') 'A release with a major feature and fatal fix increments minor only once.'
$FeatureMajor = Get-ReleasePlan -SourceRoot $Fixture -FeatureRelease -MajorVersion 5
Check ($FeatureMajor.Version -eq '5.0.0') 'An explicit major change overrides feature increments and resets minor and patch.'
Complete-ReleaseVersion -Plan $Feature
Check ((Get-ReleasePlan -SourceRoot $Fixture -FeatureRelease -FatalBugFix).Version -eq '1.3.4') 'A completed feature release cannot increment minor or patch again for unchanged source.'
[IO.File]::WriteAllText($CodeFile, 'a later ordinary change')
Check ((Get-ReleasePlan -SourceRoot $Fixture).Version -eq '1.3.5') 'A later ordinary source change increments patch and retains the feature minor.'
# Exercise the real packaging worker inside an owned, isolated project layout.
$PackageRoot = Join-Path $Fixture 'Packaging'
$PackageScripts = Join-Path $PackageRoot 'Source\Scripts'
$PackagePublish = Join-Path $PackageRoot 'Publish\CDirWinX64'
New-Item -ItemType Directory -Path $PackageScripts, $PackagePublish -Force | Out-Null
$Worker = Join-Path $PackageScripts 'Package.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\Scripts\Package.ps1') -Destination $Worker
[IO.File]::WriteAllText((Join-Path $PackagePublish 'CDir.exe'), 'Owned packaging fixture; never executed.')
$Manifest = Join-Path $PackagePublish 'Release.json'
[IO.File]::WriteAllText($Manifest, '{"Version":"2.4.6","SourceHash":"owned fixture"}')
[IO.File]::WriteAllText((Join-Path $PackageRoot 'Publish\CDirWinX64.7z'), 'Owned legacy package.')
& $Worker | Out-Host
$FirstArchive = Join-Path $PackageRoot 'Artifacts\CDir_WinX64_v2.4.6.zip'
Check (Test-Path -LiteralPath $FirstArchive) 'Packaging uses the exact CDir_WinX64_vmajor.minor.patch.zip filename.'
$Signature = [IO.File]::ReadAllBytes($FirstArchive)
Check ($Signature[0] -eq 0x50 -and $Signature[1] -eq 0x4B) 'The release archive contains real ZIP data.'
$Zip = [IO.Compression.ZipFile]::OpenRead($FirstArchive)
try {
    $Reader = [IO.StreamReader]::new($Zip.GetEntry('CDir_WinX64_v2.4.6/Release.json').Open())
    try { $Packed = $Reader.ReadToEnd() | ConvertFrom-Json } finally { $Reader.Dispose() }
    Check ($Packed.Version -eq '2.4.6' -and $null -ne $Zip.GetEntry('CDir_WinX64_v2.4.6/CDir.exe') -and
        @($Zip.Entries | Where-Object { -not $_.FullName.StartsWith('CDir_WinX64_v2.4.6/') }).Count -eq 0) 'The ZIP contains exactly one versioned top-level folder with its matching manifest and executable.'
} finally { $Zip.Dispose() }
$Extracted = Join-Path $PackageRoot 'Extracted'
Expand-Archive -LiteralPath $FirstArchive -DestinationPath $Extracted
Check ((Get-ChildItem -LiteralPath $Extracted).Count -eq 1 -and
    [IO.File]::Exists((Join-Path $Extracted 'CDir_WinX64_v2.4.6\CDir.exe'))) 'Extracting the ZIP creates the folder named after the versioned archive.'
Check ((Get-FileHash -LiteralPath $FirstArchive -Algorithm SHA256).Hash -eq
    (Get-FileHash -LiteralPath (Join-Path $PackageRoot 'Publish\CDir_WinX64_v2.4.6.zip') -Algorithm SHA256).Hash) 'Publish receives an identical current version ZIP.'
Check (-not [IO.File]::Exists((Join-Path $PackageRoot 'Publish\CDirWinX64.7z')) -and
    @(Get-ChildItem -LiteralPath (Join-Path $PackageRoot 'Trash') -Recurse -Filter 'CDirWinX64.7z').Count -eq 1) 'The obsolete fixed-name 7z is preserved in Trash instead of remaining in Publish.'
$FirstArchiveHash = (Get-FileHash -LiteralPath $FirstArchive -Algorithm SHA256).Hash
[IO.File]::WriteAllText($Manifest, '{"Version":"2.4.7","SourceHash":"later owned fixture"}')
& $Worker | Out-Host
Check ((Test-Path -LiteralPath (Join-Path $PackageRoot 'Artifacts\CDir_WinX64_v2.4.7.zip')) -and
    (Get-FileHash -LiteralPath $FirstArchive -Algorithm SHA256).Hash -eq $FirstArchiveHash) 'A different release produces its own ZIP without overwriting the prior version.'
foreach ($Invalid in @('1.2', '01.2.3', '1.2.3.4', '1.2.3/other', '65535.0.1')) {
    [IO.File]::WriteAllText($Manifest, (@{ Version = $Invalid } | ConvertTo-Json -Compress))
    Check (Rejects { & $Worker }) ('Invalid published versions cannot become archive filenames: ' + $Invalid)
}
Move-Item -LiteralPath $Manifest -Destination ($Manifest + '.saved')
Check (Rejects { & $Worker }) 'Packaging rejects a missing release manifest instead of guessing a filename.'
. (Join-Path $PSScriptRoot '../Scripts/PublicationWorkspace.ps1')
$Profile = Join-Path $Fixture 'PublicationProfile'
$Snapshots = Join-Path $Fixture 'PublicationSnapshots'
New-Item -ItemType Directory -Path $Profile, $Snapshots | Out-Null
$OldSettings = '{"CustomCategories":[{"Id":"kept","Name":"Saved","Folders":[{"FullPath":"C:\\Saved"}]}]}'
$OldState = @{ Version=1; LastActivatedUtc='2026-11-06T01:00:00Z'; ActiveTab=0
    Tabs=@(@{ Alias='Existing'; Settings=($OldSettings | ConvertFrom-Json) }) } | ConvertTo-Json -Depth 20 -Compress
$NewState = @{ Version=1; LastActivatedUtc='2026-11-06T02:00:00Z'; ActiveTab=1
    Tabs=@(@{ Alias='Existing'; Settings=($OldSettings | ConvertFrom-Json) },
        @{ Alias='Unsaved second tab'; Settings=@{ CustomCategories=@(@{Id='new'; Name='Latest folders'
            Folders=@(@{FullPath='C:\Unsaved Folder 中文'})}) } }) } | ConvertTo-Json -Depth 20 -Compress
[IO.File]::WriteAllText((Join-Path $Profile 'Settings.json'), $OldSettings)
[IO.File]::WriteAllText((Join-Path $Profile 'Settings.json.workspace.json'), $OldState)
$Records = @()
foreach ($Item in @(@{Id=1;Json=$OldState;Activated='2026-11-06T01:00:00Z'}, @{Id=2;Json=$NewState;Activated='2026-11-06T02:00:00Z'})) {
    $SnapshotFile = Join-Path $Snapshots ($Item.Id.ToString() + '.workspace.json')
    [IO.File]::WriteAllText($SnapshotFile, $Item.Json)
    $Records += [pscustomobject]@{ ProcessId=$Item.Id; SnapshotFile=$SnapshotFile; LastActivatedUtc=$Item.Activated
        Sha256=(Get-FileHash -LiteralPath $SnapshotFile).Hash }
}
$Capture = [pscustomobject]@{ProfileDirectory=$Profile; Snapshots=$Records}
$Chosen = Restore-PublicationWorkspace $Capture
$SavedState = Get-Content (Join-Path $Profile 'Settings.json.workspace.json') -Raw | ConvertFrom-Json
$SavedSettings = Get-Content (Join-Path $Profile 'Settings.json') -Raw | ConvertFrom-Json
Check ($Chosen.ProcessId -eq 2 -and $SavedState.Tabs.Count -eq 2 -and $SavedState.ActiveTab -eq 1 -and
    $SavedState.Tabs[1].Alias -eq 'Unsaved second tab') 'Publication preserves the most recently active live workspace, including every unsaved tab.'
Check ($SavedSettings.CustomCategories[0].Folders[0].FullPath -ceq 'C:\Unsaved Folder 中文' -and
    $SavedState.Tabs[0].Settings.CustomCategories[0].Folders[0].FullPath -ceq 'C:\Saved') 'Publication keeps unsaved folder entries and other tabs while saving active-tab preferences.'
Check ([IO.File]::ReadAllText((Join-Path $Profile 'Settings.json.bak')) -ceq $OldSettings -and
    [IO.File]::ReadAllText((Join-Path $Profile 'Settings.json.workspace.json.bak')) -ceq $OldState) 'Publication retains the prior complete settings and workspace as verified backups.'
Check (@($Records | Where-Object { (Get-FileHash -LiteralPath $_.SnapshotFile).Hash -ne $_.Sha256 }).Count -eq 0) 'Every instance snapshot remains intact for recovery after selecting the active window.'
$PreservedHash = (Get-FileHash (Join-Path $Profile 'Settings.json.workspace.json')).Hash
foreach ($Invalid in @('null', '{"Version":1,"ActiveTab":0,"Tabs":[]}', '{"Version":1,"ActiveTab":2,"Tabs":[{"Settings":{}}]}', '{"Version":1,"ActiveTab":0,"Tabs":[{}]}')) {
    Check (Rejects { Assert-PublicationWorkspaceJson $Invalid }) 'Incomplete live snapshots stop publication before an instance can be terminated.'
}
$Records[1].Sha256 = 'changed'
Check (Rejects { Restore-PublicationWorkspace $Capture }) 'A damaged live snapshot cannot overwrite user state.'
Check ((Get-FileHash (Join-Path $Profile 'Settings.json.workspace.json')).Hash -eq $PreservedHash) 'Rejected restoration leaves the preserved workspace unchanged.'
$WrongInstance = @{ Executable=(Join-Path $Fixture 'Unrelated.exe'); Instances=@([pscustomobject]@{ProcessId=$PID})
    BackupDirectory=(Join-Path $Fixture 'RejectedPublication'); ProfileDirectory=$Profile }
Check (Rejects { Export-PublicationWorkspaces @WrongInstance }) 'Publication rejects processes whose executable paths do not match before requesting live state.'
Write-Host ('Release checks: ' + $Checks + ' passed.')
