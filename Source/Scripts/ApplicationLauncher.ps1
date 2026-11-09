# Package the short-lived entry point separately from the WPF application.
function Install-ApplicationLauncher {
    param([Parameter(Mandatory)][string]$ApplicationDirectory, [Parameter(Mandatory)][string]$LauncherDirectory)
    $ApplicationDirectory = [IO.Path]::GetFullPath($ApplicationDirectory)
    $LauncherDirectory = [IO.Path]::GetFullPath($LauncherDirectory)
    Copy-Item -LiteralPath (Join-Path $ApplicationDirectory 'CDir.exe') -Destination (Join-Path $ApplicationDirectory 'CDir.App.exe') -Force
    foreach ($Name in @('CDir.Launcher.dll','CDir.Launcher.deps.json','CDir.Launcher.runtimeconfig.json')) {
        Copy-Item -LiteralPath (Join-Path $LauncherDirectory $Name) -Destination (Join-Path $ApplicationDirectory $Name) -Force
    }
    Copy-Item -LiteralPath (Join-Path $LauncherDirectory 'CDir.Launcher.exe') -Destination (Join-Path $ApplicationDirectory 'CDir.exe') -Force
    $Files = @(Get-ChildItem -LiteralPath $ApplicationDirectory -Recurse -File | Where-Object { $_.Name -ne 'RuntimeManifest.json' } | ForEach-Object {
        if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Runtime manifests cannot contain reparse points.' }
        [ordered]@{ Path=[IO.Path]::GetRelativePath($ApplicationDirectory,$_.FullName).Replace('\','/'); Length=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash }
    } | Sort-Object { $_.Path })
    [ordered]@{ Schema=1; Files=$Files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ApplicationDirectory 'RuntimeManifest.json') -Encoding utf8NoBOM
}
