[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($env:CDIR_QUIET_RUNNER -ne '1') { throw 'Package.ps1 is an internal worker. Use Source/Scripts/Build.ps1 -Package for background I/O scheduling.' }
$SourceRoot = Split-Path $PSScriptRoot -Parent
$ProjectRoot = Split-Path $SourceRoot -Parent
$PublishDirectory = Join-Path $ProjectRoot 'Publish\CDirWinX64'
if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory 'CDir.exe'))) { throw 'Publish the application before packaging.' }
$ReleaseFile = Join-Path $PublishDirectory 'Release.json'
if (-not (Test-Path -LiteralPath $ReleaseFile)) { throw 'The published Release.json is missing; publish the application before packaging.' }
$Release = Get-Content -LiteralPath $ReleaseFile -Raw | ConvertFrom-Json
$Version = [string]$Release.Version
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'The published version must use major.minor.patch format.'
}
foreach ($Component in $Version.Split('.')) {
    $Number = 0
    if (-not [int]::TryParse($Component, [ref]$Number) -or $Number -gt 65534) { throw 'Published version components must be between 0 and 65534.' }
}
$Artifacts = Join-Path $ProjectRoot 'Artifacts'
New-Item -ItemType Directory -Path $Artifacts -Force | Out-Null
$FolderName = 'CDir_WinX64_v' + $Version
$Archive = Join-Path $Artifacts ($FolderName + '.zip')
$Temporary = Join-Path $Artifacts ('.CDirPackage-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $Zip = [IO.Compression.ZipFile]::Open($Temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $null = $Zip.CreateEntry($FolderName + '/')
        $Pending = [Collections.Generic.Stack[string]]::new()
        $Pending.Push($PublishDirectory)
        while ($Pending.Count -gt 0) {
            foreach ($EntryPath in [IO.Directory]::EnumerateFileSystemEntries($Pending.Pop())) {
                $Attributes = [IO.File]::GetAttributes($EntryPath)
                if (($Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Release packages cannot follow reparse points.' }
                $EntryName = $FolderName + '/' + [IO.Path]::GetRelativePath($PublishDirectory, $EntryPath).Replace('\', '/')
                if (($Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                    $null = $Zip.CreateEntry($EntryName + '/')
                    $Pending.Push($EntryPath)
                } else {
                    $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($Zip, $EntryPath, $EntryName, [IO.Compression.CompressionLevel]::Fastest)
                }
            }
        }
    } finally { $Zip.Dispose() }
    [IO.File]::Move($Temporary, $Archive, $true)
} finally { if ([IO.File]::Exists($Temporary)) { [IO.File]::Delete($Temporary) } }
$PublishParent = Split-Path $PublishDirectory -Parent
$PublishedArchive = Join-Path $PublishParent ($FolderName + '.zip')
$MirrorTemporary = Join-Path $PublishParent ('.CDirPackage-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::Copy($Archive, $MirrorTemporary)
    if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $MirrorTemporary -Algorithm SHA256).Hash) {
        throw 'The Publish and Artifacts release archives do not match.'
    }
    [IO.File]::Move($MirrorTemporary, $PublishedArchive, $true)
} finally { if ([IO.File]::Exists($MirrorTemporary)) { [IO.File]::Delete($MirrorTemporary) } }
foreach ($LegacyName in @('CDirWinX64.7z', 'CDirWinX64.zip')) {
    $Legacy = [IO.Path]::GetFullPath((Join-Path $PublishParent $LegacyName))
    if (-not $Legacy.StartsWith([IO.Path]::GetFullPath($PublishParent).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Legacy package path is outside Publish.' }
    if ([IO.File]::Exists($Legacy)) {
        if (([IO.File]::GetAttributes($Legacy) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Refusing to move a legacy package reparse point.' }
        $LegacyTrash = Join-Path $ProjectRoot ('Trash\LegacyPackages-' + [Guid]::NewGuid().ToString('N') + '\Publish')
        New-Item -ItemType Directory -Path $LegacyTrash -Force | Out-Null
        Move-Item -LiteralPath $Legacy -Destination (Join-Path $LegacyTrash $LegacyName)
        if ([IO.File]::Exists($Legacy)) { throw 'The legacy package move did not complete.' }
    }
}
$PublishedAt = [DateTime]::UtcNow
[IO.Directory]::SetLastWriteTimeUtc($PublishDirectory, $PublishedAt)
[IO.Directory]::SetLastWriteTimeUtc($PublishParent, $PublishedAt)
Write-Host ('Archive: ' + $Archive)
Write-Host ('Published archive: ' + $PublishedArchive)
