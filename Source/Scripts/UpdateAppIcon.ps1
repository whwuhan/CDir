[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$SourceRoot = Split-Path $PSScriptRoot -Parent
$IconPath = Join-Path $SourceRoot 'Resource\App.ico'
Add-Type -AssemblyName WindowsBase, PresentationCore
if (-not ('CDir.Assets.FolderIcon' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace CDir.Assets {
    public static class FolderIcon {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StockInfo {
            public uint Size;
            public IntPtr Icon;
            public int SystemImageIndex;
            public int IconIndex;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHGetStockIconInfo(uint Id, uint Flags, ref StockInfo Info);
        [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode)]
        public static extern int Extract(string Path, int Index, uint Flags, out IntPtr Large, out IntPtr Small, uint Sizes);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr Icon);
    }
}
'@
}
$Stock = [CDir.Assets.FolderIcon+StockInfo]::new()
$Stock.Size = [Runtime.InteropServices.Marshal]::SizeOf($Stock)
# SHSTOCKICONID.Folder supplies the current Windows icon location; no DLL resource index is hard-coded.
[Runtime.InteropServices.Marshal]::ThrowExceptionForHR([CDir.Assets.FolderIcon]::SHGetStockIconInfo(3, 0, [ref]$Stock))
$Frames = foreach ($Size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $Large = [IntPtr]::Zero
    $Small = [IntPtr]::Zero
    try {
        [Runtime.InteropServices.Marshal]::ThrowExceptionForHR([CDir.Assets.FolderIcon]::Extract(
            $Stock.Path, $Stock.IconIndex, 0, [ref]$Large, [ref]$Small, [uint32]($Size -bor ($Size -shl 16))))
        if ($Large -eq [IntPtr]::Zero) { throw ('Windows did not return a folder icon at size ' + $Size) }
        $Bitmap = [Windows.Interop.Imaging]::CreateBitmapSourceFromHIcon($Large, [Windows.Int32Rect]::Empty, [Windows.Media.Imaging.BitmapSizeOptions]::FromEmptyOptions())
        if ($Bitmap.PixelWidth -ne $Size -or $Bitmap.PixelHeight -ne $Size) { throw 'Windows returned an unexpected icon size.' }
        $Encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $Encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
        $Stream = [IO.MemoryStream]::new()
        try {
            $Encoder.Save($Stream)
            [PSCustomObject]@{ Size = $Size; Bytes = [byte[]]$Stream.ToArray() }
        } finally { $Stream.Dispose() }
    } finally {
        if ($Large -ne [IntPtr]::Zero) { [void][CDir.Assets.FolderIcon]::DestroyIcon($Large) }
        if ($Small -ne [IntPtr]::Zero -and $Small -ne $Large) { [void][CDir.Assets.FolderIcon]::DestroyIcon($Small) }
    }
}
$Output = [IO.MemoryStream]::new()
$Writer = [IO.BinaryWriter]::new($Output)
try {
    $Writer.Write([uint16]0)
    $Writer.Write([uint16]1)
    $Writer.Write([uint16]$Frames.Count)
    $Offset = 6 + 16 * $Frames.Count
    foreach ($Frame in $Frames) {
        $Dimension = if ($Frame.Size -eq 256) { 0 } else { $Frame.Size }
        $Writer.Write([byte]$Dimension)
        $Writer.Write([byte]$Dimension)
        $Writer.Write([byte]0)
        $Writer.Write([byte]0)
        $Writer.Write([uint16]1)
        $Writer.Write([uint16]32)
        $Writer.Write([uint32]$Frame.Bytes.Length)
        $Writer.Write([uint32]$Offset)
        $Offset += $Frame.Bytes.Length
    }
    foreach ($Frame in $Frames) { $Writer.Write([byte[]]$Frame.Bytes) }
    $Writer.Flush()
    [IO.File]::WriteAllBytes($IconPath, $Output.ToArray())
} finally { $Writer.Dispose(); $Output.Dispose() }
Write-Host ('Updated Windows folder icon: ' + $IconPath + '; sizes: ' + (($Frames | ForEach-Object { $_.Size }) -join ', '))
