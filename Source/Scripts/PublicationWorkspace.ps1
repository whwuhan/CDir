# Preserve live, potentially unsaved state before the authorized publication restart.
function Assert-PublicationWorkspaceJson {
    param([Parameter(Mandatory)][string]$Json)
    $State = $Json | ConvertFrom-Json
    if ($State.Version -ne 1 -or @($State.Tabs).Count -eq 0 -or $null -eq $State.ActiveTab -or
        $State.ActiveTab -lt 0 -or $State.ActiveTab -ge @($State.Tabs).Count -or
        @($State.Tabs | Where-Object { $null -eq $_ -or $null -eq $_.Settings }).Count -gt 0) {
        throw 'The live workspace snapshot is incomplete; publication must not stop this instance.'
    }
    return $State
}

function Write-PublicationJson {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Json)
    $null = $Json | ConvertFrom-Json
    $Temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $Bytes = [Text.Encoding]::UTF8.GetBytes($Json)
        $Stream = [IO.FileStream]::new($Temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
            [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        try { $Stream.Write($Bytes, 0, $Bytes.Length); $Stream.Flush($true) } finally { $Stream.Dispose() }
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($Temporary, $Path, $Path + '.bak', $true) }
        else { [IO.File]::Move($Temporary, $Path) }
    } finally { if ([IO.File]::Exists($Temporary)) { [IO.File]::Delete($Temporary) } }
}

function Export-PublicationWorkspaces {
    param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][object[]]$Instances,
        [Parameter(Mandatory)][string]$BackupDirectory,
        [string]$ProfileDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CDir'))
    $Executable = [IO.Path]::GetFullPath($Executable)
    if (Test-Path -LiteralPath $BackupDirectory) { throw 'The publication backup directory must be new.' }
    New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
    $Snapshots = @()
    foreach ($Instance in $Instances) {
        $Verified = Get-CimInstance Win32_Process -Filter "ProcessId = $($Instance.ProcessId)"
        if (-not $Verified -or -not $Verified.ExecutablePath -or
            -not [string]::Equals([IO.Path]::GetFullPath($Verified.ExecutablePath), $Executable, [StringComparison]::OrdinalIgnoreCase) -or
            $Verified.CommandLine -match '(?:^|\s)--(?:smoke-test|screenshot)(?:\s|$)') {
            throw 'The publication instance path or user-process identity could not be verified.'
        }
        $Pipe = [IO.Pipes.NamedPipeClientStream]::new('.', ('CDir.Workspace.' + $Verified.ProcessId),
            [IO.Pipes.PipeDirection]::In, [IO.Pipes.PipeOptions]::Asynchronous)
        try {
            $Pipe.Connect(3000)
            $Reader = [IO.StreamReader]::new($Pipe, [Text.Encoding]::UTF8)
            try { $Json = $Reader.ReadToEndAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() }
            finally { $Reader.Dispose() }
        } finally { $Pipe.Dispose() }
        $State = Assert-PublicationWorkspaceJson $Json
        $SnapshotFile = Join-Path $BackupDirectory ($Verified.ProcessId.ToString() + '.workspace.json')
        [IO.File]::WriteAllText($SnapshotFile, $Json, [Text.UTF8Encoding]::new($false))
        $Snapshots += [pscustomobject]@{ ProcessId=$Verified.ProcessId; Executable=$Executable; SnapshotFile=$SnapshotFile
            LastActivatedUtc=([DateTimeOffset]$State.LastActivatedUtc).ToUniversalTime().ToString('O')
            CapturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); Sha256=(Get-FileHash -LiteralPath $SnapshotFile).Hash }
    }
    foreach ($Name in @('Settings.json','Settings.json.bak','Settings.json.workspace.json','Settings.json.workspace.json.bak')) {
        $File = Join-Path $ProfileDirectory $Name
        if (Test-Path -LiteralPath $File) {
            Copy-Item -LiteralPath $File -Destination (Join-Path $BackupDirectory $Name)
            if ((Get-FileHash -LiteralPath $File).Hash -ne (Get-FileHash -LiteralPath (Join-Path $BackupDirectory $Name)).Hash) {
                throw 'The existing user configuration backup could not be verified.'
            }
        }
    }
    $Capture = [pscustomobject]@{ ProfileDirectory=[IO.Path]::GetFullPath($ProfileDirectory); BackupDirectory=$BackupDirectory; Snapshots=$Snapshots }
    $Capture | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $BackupDirectory 'Capture.json') -Encoding utf8
    return $Capture
}

function Restore-PublicationWorkspace {
    param([Parameter(Mandatory)][object]$Capture)
    $Snapshots = @($Capture.Snapshots)
    if ($Snapshots.Count -eq 0) { throw 'No live user workspace was captured.' }
    # Startup already chooses the most recently active window. Keep that choice
    # and preserve all the other snapshots for recovery instead of discarding them.
    $Chosen = $Snapshots | Sort-Object { [DateTimeOffset]$_.LastActivatedUtc } -Descending | Select-Object -First 1
    if ((Get-FileHash -LiteralPath $Chosen.SnapshotFile).Hash -ne $Chosen.Sha256) { throw 'The live snapshot backup changed.' }
    $Json = [IO.File]::ReadAllText($Chosen.SnapshotFile)
    $State = Assert-PublicationWorkspaceJson $Json
    $SettingsJson = $State.Tabs[$State.ActiveTab].Settings | ConvertTo-Json -Depth 100 -Compress
    New-Item -ItemType Directory -Path $Capture.ProfileDirectory -Force | Out-Null
    # Workspace is authoritative for all tabs, so write its complete snapshot first.
    Write-PublicationJson -Path (Join-Path $Capture.ProfileDirectory 'Settings.json.workspace.json') -Json $Json
    Write-PublicationJson -Path (Join-Path $Capture.ProfileDirectory 'Settings.json') -Json $SettingsJson
    $Verified = Assert-PublicationWorkspaceJson ([IO.File]::ReadAllText((Join-Path $Capture.ProfileDirectory 'Settings.json.workspace.json')))
    if (($Verified | ConvertTo-Json -Depth 100 -Compress) -cne ($State | ConvertTo-Json -Depth 100 -Compress)) {
        throw 'The preserved publication workspace could not be verified.'
    }
    return $Chosen
}

function Suspend-PublicationInstances {
    param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$BackupDirectory)
    $Executable = [IO.Path]::GetFullPath($Executable)
    $Instances = @(Get-CimInstance Win32_Process -Filter "Name = 'CDir.exe'" | Where-Object {
        $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $Executable, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($Instances.Count -eq 0) { return $null }
    $Capture = Export-PublicationWorkspaces -Executable $Executable -Instances $Instances -BackupDirectory $BackupDirectory
    foreach ($Instance in $Instances) {
        $Verified = Get-CimInstance Win32_Process -Filter "ProcessId = $($Instance.ProcessId)"
        if (-not $Verified) { continue }
        if (-not $Verified.ExecutablePath -or -not [string]::Equals([IO.Path]::GetFullPath($Verified.ExecutablePath), $Executable, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A publication process path changed; refusing to terminate it.'
        }
        Stop-Process -Id $Verified.ProcessId -Force
        Wait-Process -Id $Verified.ProcessId -Timeout 20 -ErrorAction SilentlyContinue
        if (Get-Process -Id $Verified.ProcessId -ErrorAction SilentlyContinue) { throw 'The published instance did not exit.' }
    }
    $Saved = Restore-PublicationWorkspace -Capture $Capture
    return [pscustomobject]@{ Executable=$Executable; WasRunning=$true; ProcessIds=@($Instances.ProcessId)
        WorkspaceBackup=$BackupDirectory; PreservedWorkspaceProcessId=$Saved.ProcessId }
}
