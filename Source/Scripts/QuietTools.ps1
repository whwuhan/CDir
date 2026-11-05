# Shared by Build.ps1 and Test.ps1. Children use background I/O and an owned Windows Job.
function Resolve-QuietDirectory {
    param([Parameter(Mandatory)][string]$Path)
    if (-not ('CDir.Testing.PhysicalDirectory' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace CDir.Testing {
    public static class PhysicalDirectory {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string Name, uint Access, uint Share, IntPtr Security, uint Creation, uint Flags, IntPtr Template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle Handle, StringBuilder Path, uint Length, uint Flags);
        public static string Resolve(string Path) {
            using var Handle = CreateFile(Path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (Handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var Buffer = new StringBuilder(32768);
            var Length = GetFinalPathNameByHandle(Handle, Buffer, (uint)Buffer.Capacity, 0);
            if (Length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (Length >= Buffer.Capacity) throw new InvalidOperationException("Resolved test directory path is too long.");
            var Resolved = Buffer.ToString();
            if (Resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + Resolved.Substring(8);
            return Resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? Resolved.Substring(4) : Resolved;
        }
    }
}
'@
    }
    return [CDir.Testing.PhysicalDirectory]::Resolve([IO.Path]::GetFullPath($Path))
}

function ConvertTo-NativeArgument {
    param([AllowEmptyString()][string]$Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $Quoted = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $Quoted = [regex]::Replace($Quoted, '(\\+)$', '$1$1')
    return '"' + $Quoted + '"'
}

function Initialize-QuietRunner {
    $SourceRoot = Split-Path $PSScriptRoot -Parent
    $RepositoryRoot = Split-Path $SourceRoot -Parent
    $RunnerProject = Join-Path $SourceRoot 'Tests\QuietRunner\QuietRunner.csproj'
    $RunnerPath = Join-Path $RepositoryRoot 'Build\Bin\QuietRunner\Release\net10.0-windows\QuietRunner.exe'
    $RunnerAssembly = Join-Path (Split-Path $RunnerPath -Parent) 'QuietRunner.dll'
    $RuntimeConfig = Join-Path (Split-Path $RunnerPath -Parent) 'QuietRunner.runtimeconfig.json'
    $Sources = Get-ChildItem -LiteralPath (Split-Path $RunnerProject -Parent) -File |
        Where-Object { $_.Extension -in '.cs', '.csproj', '.props' }
    $NeedsBuild = -not ((Test-Path -LiteralPath $RunnerPath) -and (Test-Path -LiteralPath $RunnerAssembly) -and (Test-Path -LiteralPath $RuntimeConfig))
    if (-not $NeedsBuild) {
        $BuiltAt = (Get-Item -LiteralPath $RunnerAssembly).LastWriteTimeUtc
        $NeedsBuild = @($Sources | Where-Object { $_.LastWriteTimeUtc -gt $BuiltAt }).Count -gt 0
    }
    if ($NeedsBuild) {
        # Bootstrap only this small launcher, at background priority. No CPU rate/affinity cap.
        $HostProcess = [Diagnostics.Process]::GetCurrentProcess()
        $PreviousPriority = $HostProcess.PriorityClass
        try {
            $HostProcess.PriorityClass = [Diagnostics.ProcessPriorityClass]::Idle
            $Info = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet -ErrorAction Stop).Source)
            $Info.UseShellExecute = $false
            $Info.CreateNoWindow = $true
            $Info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
            $Info.WorkingDirectory = $RepositoryRoot
            $Info.Arguments = (@('build', $RunnerProject, '-c', 'Release', '--nologo', '--disable-build-servers', '-nr:false', '-p:UseSharedCompilation=false') |
                ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
            $Info.EnvironmentVariables['MSBUILDDISABLENODEREUSE'] = '1'
            $Info.EnvironmentVariables['DOTNET_CLI_USE_MSBUILD_SERVER'] = '0'
            $Process = [Diagnostics.Process]::Start($Info)
            try {
                $Deadline = [DateTime]::UtcNow.AddMinutes(2)
                while (-not $Process.WaitForExit(1000)) {
                    if ([DateTime]::UtcNow -gt $Deadline) { throw 'QuietRunner bootstrap timed out.' }
                }
                if ($Process.ExitCode -ne 0) { throw 'QuietRunner bootstrap failed; no unguarded fallback will run.' }
            } finally {
                if (-not $Process.HasExited) {
                    try { $Process.Kill($true); $Process.WaitForExit(3000) | Out-Null }
                    catch {
                        $StopInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path ([Environment]::GetFolderPath('System')) 'taskkill.exe'))
                        $StopInfo.UseShellExecute = $false
                        $StopInfo.CreateNoWindow = $true
                        $StopInfo.Arguments = '/PID ' + $Process.Id + ' /T /F'
                        $StopProcess = [Diagnostics.Process]::Start($StopInfo)
                        $StopProcess.WaitForExit(3000) | Out-Null
                        $StopProcess.Dispose()
                    }
                }
                $Process.Dispose()
            }
        } finally { $HostProcess.PriorityClass = $PreviousPriority; $HostProcess.Dispose() }
    }
    return $RunnerPath
}

function Invoke-QuietProcess {
    param(
        [Parameter(Mandatory)][string]$RunnerPath,
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [string[]]$Arguments = @(),
        [string[]]$AdditionalArguments = @(),
        [ValidateRange(0, 100)][int]$CpuPercent = 0,
        [ValidateRange(1, 3600)][int]$TimeoutSeconds = 300,
        [string]$TempRoot
    )
    $RunnerArguments = @('--exe', $Executable, '--workdir', $WorkingDirectory,
        '--cpu-percent', [string]$CpuPercent, '--timeout-seconds', [string]$TimeoutSeconds)
    if ($TempRoot) { $RunnerArguments += @('--temp-root', $TempRoot) }
    $RunnerArguments += @('--') + $Arguments + $AdditionalArguments
    $Timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $RunnerPath @RunnerArguments
        if ($LASTEXITCODE -ne 0) { throw "Quiet child failed with exit code $LASTEXITCODE. No unguarded retry was attempted." }
    } finally {
        Write-Host ('{0} elapsed: {1:N2}s (including queue wait).' -f [IO.Path]::GetFileName($Executable), $Timer.Elapsed.TotalSeconds)
    }
}
