# Shared release planning: reading a candidate never consumes a version number.
function Get-ReleaseSourceHash {
    param([Parameter(Mandatory)][string]$SourceRoot)
    $Root = [IO.Path]::GetFullPath($SourceRoot)
    $Paths = [string[]]@(Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
        ForEach-Object { [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/') } |
        Where-Object { $_ -ne 'Version.props' -and $_ -notmatch '(^|/)(bin|obj|\.git)/' })
    [Array]::Sort($Paths, [StringComparer]::Ordinal)
    $Digest = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($Relative in $Paths) {
            $FileHash = (Get-FileHash -LiteralPath (Join-Path $Root $Relative) -Algorithm SHA256).Hash
            $Digest.AppendData([Text.Encoding]::UTF8.GetBytes($Relative + ':' + $FileHash + "`n"))
        }
        return [Convert]::ToHexString($Digest.GetHashAndReset())
    } finally { $Digest.Dispose() }
}

function Get-ReleasePlan {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Nullable[int]]$MajorVersion,
        [switch]$FatalBugFix,
        [switch]$FeatureRelease
    )
    $VersionFile = Join-Path $SourceRoot 'Version.props'
    $Original = [IO.File]::ReadAllText($VersionFile)
    $Document = [xml]$Original
    $VersionText = [string]$Document.Project.PropertyGroup.Version
    if ($VersionText -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'Version.props must contain a three-part numeric version, such as 0.0.0.'
    }
    $Parts = @($VersionText.Split('.') | ForEach-Object {
        $Part = 0
        if (-not [int]::TryParse($_, [ref]$Part) -or $Part -gt 65534) { throw 'Version components must be between 0 and 65534.' }
        $Part
    })
    if ($null -ne $MajorVersion -and ($MajorVersion -lt 0 -or $MajorVersion -gt 65534)) { throw 'Version components must be between 0 and 65534.' }
    $PreviousVersion = [string]$Document.Project.PropertyGroup.LastPublishedVersion
    if (-not $PreviousVersion) {
        $PublishedManifest = Join-Path (Split-Path $SourceRoot -Parent) 'Publish/CDirWinX64/Release.json'
        if (Test-Path -LiteralPath $PublishedManifest) {
            $PreviousVersion = [string](Get-Content -LiteralPath $PublishedManifest -Raw | ConvertFrom-Json).Version
        }
    }
    if ($PreviousVersion -and $PreviousVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'The last published version must use major.minor.patch format.'
    }
    $PreviousMajor = if ($PreviousVersion) { [int]$PreviousVersion.Split('.')[0] } else { $Parts[0] }
    if ($null -ne $MajorVersion) { $Parts[0] = [int]$MajorVersion }
    $MajorChanged = $Parts[0] -ne $PreviousMajor
    if ($MajorChanged) { $Parts[1] = 0; $Parts[2] = 0 }
    $Hash = Get-ReleaseSourceHash -SourceRoot $SourceRoot
    $PreviousHash = [string]$Document.Project.PropertyGroup.LastPublishedSourceHash
    if ($PreviousHash -and $PreviousHash -cnotmatch '^[A-F0-9]{64}$') { throw 'The last published source hash is invalid.' }
    if (-not $MajorChanged -and $PreviousHash -and $PreviousHash -ne $Hash) {
        if ($Parts[2] -ge 65534) { throw 'The patch version reached its limit; update Version.props before releasing.' }
        # FeatureRelease requires an explicit user request for a major feature.
        # Combining it with a fatal fix still increments minor only once.
        if ($FatalBugFix -or $FeatureRelease) {
            if ($Parts[1] -ge 65534) { throw 'The minor version reached its limit; only the user may change the major version.' }
            $Parts[1]++
        }
        $Parts[2]++
    }
    [pscustomobject]@{
        Version = $Parts -join '.'
        SourceHash = $Hash
        SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
        VersionFile = $VersionFile
        Original = $Original
    }
}

function Complete-ReleaseVersion {
    param([Parameter(Mandatory)]$Plan)
    if ([IO.File]::ReadAllText($Plan.VersionFile) -cne $Plan.Original) {
        throw 'Version.props changed during publication; the version was not advanced.'
    }
    if ((Get-ReleaseSourceHash -SourceRoot $Plan.SourceRoot) -ne $Plan.SourceHash) {
        throw 'Source files changed during publication; the version was not advanced.'
    }
    $Document = [xml]::new()
    $Document.PreserveWhitespace = $true
    $Document.LoadXml($Plan.Original)
    $Document.Project.PropertyGroup.Version = $Plan.Version
    $Document.Project.PropertyGroup.LastPublishedSourceHash = $Plan.SourceHash
    $PublishedVersionNode = $Document.Project.PropertyGroup.SelectSingleNode('LastPublishedVersion')
    if ($null -eq $PublishedVersionNode) {
        $PublishedVersionNode = $Document.CreateElement('LastPublishedVersion')
        [void]$Document.Project.PropertyGroup.AppendChild($PublishedVersionNode)
    }
    $PublishedVersionNode.InnerText = $Plan.Version
    $TemporaryFile = $Plan.VersionFile + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $Document.Save($TemporaryFile)
        [IO.File]::Move($TemporaryFile, $Plan.VersionFile, $true)
    } finally {
        if ([IO.File]::Exists($TemporaryFile)) { [IO.File]::Delete($TemporaryFile) }
    }
}
