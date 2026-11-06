using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace CDir.Services;

// CFSTR_SHELLIDLIST contains a CIDA: count, offsets, an absolute parent PIDL,
// and relative child PIDLs. Keep the namespace identity; recycled names and
// backing paths are not interchangeable with ordinary filesystem paths.
internal static class ShellDropData
{
    internal const string Format = "Shell IDList Array";
    private const int MaximumBytes = 64 * 1024 * 1024;
    internal static void ReportCompleted(IDataObject Data, bool Copy)
    {
        SetEffect("Performed DropEffect", Copy ? 1 : 0);
        SetEffect("Logical Performed DropEffect", Copy ? 1 : 2);
        void SetEffect(string Name, int Effect)
        {
            if (Data is not System.Runtime.InteropServices.ComTypes.IDataObject Native)
            {
                Data.SetData(Name, new MemoryStream(BitConverter.GetBytes(Effect)), false);
                return;
            }
            // WPF's managed SetData can keep changes in a wrapper. Call the
            // COM interface so Explorer's original object receives the result.
            var Format = new System.Runtime.InteropServices.ComTypes.FORMATETC
            {
                cfFormat = unchecked((short)DataFormats.GetDataFormat(Name).Id),
                dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = -1, tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
            };
            var Memory = GlobalAlloc(0x42, 4);
            if (Memory == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                var Pointer = GlobalLock(Memory);
                if (Pointer == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                try { Marshal.WriteInt32(Pointer, Effect); }
                finally { GlobalUnlock(Memory); }
                var Medium = new System.Runtime.InteropServices.ComTypes.STGMEDIUM
                {
                    tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
                    unionmember = Memory
                };
                Native.SetData(ref Format, ref Medium, true);
                Memory = IntPtr.Zero; // The native data object now owns the HGLOBAL.
            }
            finally { if (Memory != IntPtr.Zero) GlobalFree(Memory); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint Flags, nuint Bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr Memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr Memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr Memory);
    internal static bool IsFromRecycleBin(byte[][] Ids)
    {
        if (Ids.Length == 0) return false;
        var Root = ShellLocations.RecycleBinIdList();
        try
        {
            foreach (var Bytes in Ids)
            {
                var Item = Marshal.AllocCoTaskMem(Bytes.Length);
                try
                {
                    Marshal.Copy(Bytes, 0, Item, Bytes.Length);
                    if (!ILIsParent(Root, Item, false)) return false;
                }
                finally { Marshal.FreeCoTaskMem(Item); }
            }
            return true;
        }
        finally { Marshal.FreeCoTaskMem(Root); }
    }

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ILIsParent(IntPtr Parent, IntPtr Child, [MarshalAs(UnmanagedType.Bool)] bool Immediate);
    internal static bool IsPresent(IDataObject Data)
    {
        try { return Data.GetDataPresent(Format, false); }
        catch (COMException) { return false; }
    }

    internal static byte[][]? Read(IDataObject Data)
    {
        if (!IsPresent(Data)) return null;
        var Value = Data.GetData(Format, false);
        if (Value is byte[] Bytes) return Parse(Bytes);
        if (Value is Stream Stream && Stream.CanSeek && Stream.Length <= MaximumBytes)
        {
            var Position = Stream.Position;
            try
            {
                Stream.Position = 0;
                var Buffer = new byte[checked((int)Stream.Length)];
                Stream.ReadExactly(Buffer);
                return Parse(Buffer);
            }
            finally { Stream.Position = Position; }
        }
        throw new InvalidDataException("Invalid Shell drag data.");
    }

    internal static byte[][] Parse(byte[] Data)
    {
        if (Data.Length < 12 || Data.Length > MaximumBytes) throw new InvalidDataException("Invalid Shell drag data size.");
        var Count = BinaryPrimitives.ReadUInt32LittleEndian(Data);
        var Header = 4L + (Count + 1L) * 4;
        if (Count == 0 || Count > 100000 || Header > Data.Length) throw new InvalidDataException("Invalid Shell drag item count.");
        byte[] ReadId(int Index)
        {
            var Offset = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(4 + Index * 4));
            if (Offset < Header || Offset > Data.Length - 2) throw new InvalidDataException("Invalid Shell drag item offset.");
            var Length = IdLength(Data.AsSpan((int)Offset));
            return Data.AsSpan((int)Offset, Length).ToArray();
        }
        var Parent = ReadId(0);
        var Result = new List<byte[]>();
        var Seen = new HashSet<string>(StringComparer.Ordinal);
        long TotalBytes = 0;
        for (var Index = 1; Index <= Count; Index++)
        {
            var Child = ReadId(Index);
            if (Child.Length == 2) throw new InvalidDataException("Empty Shell drag child identity.");
            TotalBytes += Parent.Length - 2L + Child.Length;
            if (TotalBytes > MaximumBytes) throw new InvalidDataException("Shell drag identities are too large.");
            var Absolute = new byte[Parent.Length - 2 + Child.Length];
            Parent.AsSpan(0, Parent.Length - 2).CopyTo(Absolute);
            Child.CopyTo(Absolute, Parent.Length - 2);
            if (Seen.Add(Convert.ToHexString(Absolute))) Result.Add(Absolute);
        }
        return Result.ToArray();
    }

    internal static int IdLength(ReadOnlySpan<byte> Data)
    {
        var Offset = 0;
        while (Offset <= Data.Length - 2)
        {
            var Size = BinaryPrimitives.ReadUInt16LittleEndian(Data[Offset..]);
            if (Size == 0) return Offset + 2;
            if (Size < 2 || Size > Data.Length - Offset) break;
            Offset += Size;
        }
        throw new InvalidDataException("Invalid Shell drag item identity.");
    }

    // A desktop parent allows absolute identities to be exported unchanged.
    internal static byte[] Create(byte[][] AbsoluteIds, byte[]? Parent = null)
    {
        if (AbsoluteIds.Length == 0) throw new InvalidDataException("Empty Shell drag selection.");
        Parent ??= [0, 0];
        if (IdLength(Parent) != Parent.Length) throw new InvalidDataException("Invalid Shell drag parent.");
        var Prefix = Parent.Length - 2;
        var Children = AbsoluteIds.Select(Id =>
        {
            if (!Id.AsSpan().StartsWith(Parent.AsSpan(0, Prefix))) throw new InvalidDataException("Shell drag parent does not match its child.");
            return Id[Prefix..];
        }).ToArray();
        var Header = checked(4 + (AbsoluteIds.Length + 1) * 4);
        var Size = checked(Header + Parent.Length + Children.Sum(Id => Id.Length));
        if (Size > MaximumBytes) throw new InvalidDataException("Shell drag selection is too large.");
        var Data = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(Data, (uint)AbsoluteIds.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(4), (uint)Header);
        Parent.CopyTo(Data, Header);
        var Offset = Header + Parent.Length;
        for (var Index = 0; Index < Children.Length; Index++)
        {
            var Id = Children[Index];
            if (IdLength(Id) != Id.Length || Id.Length == 2) throw new InvalidDataException("Invalid Shell drag identity.");
            BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(8 + Index * 4), (uint)Offset);
            Id.CopyTo(Data, Offset);
            Offset += Id.Length;
        }
        return Data;
    }
}
