namespace CDir.Models;

// Windows Shell icon zoom steps, sampled through IFolderView2 at each supported
// size. ViewZoomSmokeTests compares these steps with an owned native Shell view.
// The Shell curve is nonlinear and cannot be reproduced by a fixed pixel delta.
internal static class FileViewZoom
{
    private static readonly int[] Smaller =
    [
        16, 16, 16, 17, 18, 19, 20, 21, 22, 23, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50,
        50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68,
        68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86,
        86, 86, 87, 87, 88, 88, 89, 89, 90, 90, 91, 91, 92, 92, 93, 93, 94, 94, 94, 95,
        95, 95, 95, 96, 96, 96, 96, 100, 100, 102, 102, 105, 105, 105, 107, 107, 109, 109, 111, 111,
        113, 113, 113, 113, 115, 115, 117, 117, 119, 119, 119, 122, 122, 124, 124, 126, 126, 128, 128, 130,
        130, 132, 132, 132, 132, 132, 134, 134, 137, 137, 139, 139, 141, 141, 143, 143, 145, 145, 147, 147,
        149, 149, 149, 151, 151, 151, 151, 154, 154, 156, 156, 158, 158, 160, 160, 162, 162, 162, 164, 164,
        166, 166, 169, 169, 171, 171, 171, 171, 173, 173, 175, 175, 177, 177, 177, 179, 179, 181, 181, 183,
        183, 186, 186, 188, 188, 190, 190, 190, 190, 190, 192, 192, 194, 194, 196, 196, 198, 198, 201, 201,
        203, 203, 205, 205, 207, 207, 207, 209, 209, 209, 209, 211, 211, 213, 213, 215, 215, 218, 218, 220,
        220,
    ];
    private static readonly int[] Larger =
    [
        18, 19, 20, 21, 22, 23, 24, 26, 27, 28, 29, 30, 31, 32, 33, 34, 36, 37, 38, 39,
        40, 41, 42, 43, 44, 46, 47, 48, 49, 50, 51, 52, 53, 54, 56, 57, 58, 59, 60, 61,
        62, 63, 64, 66, 67, 68, 69, 70, 71, 72, 73, 74, 76, 77, 78, 79, 80, 81, 82, 83,
        84, 86, 87, 88, 89, 90, 91, 92, 93, 94, 96, 96, 100, 102, 105, 107, 109, 111, 113, 117,
        119, 119, 122, 122, 124, 124, 126, 126, 128, 128, 130, 130, 132, 132, 134, 134, 139, 139, 139, 141,
        141, 143, 143, 145, 145, 147, 147, 149, 149, 151, 151, 154, 154, 154, 156, 156, 160, 160, 162, 162,
        164, 164, 166, 166, 169, 169, 171, 171, 173, 173, 173, 175, 175, 177, 177, 181, 181, 183, 183, 186,
        186, 188, 188, 190, 190, 190, 192, 192, 194, 194, 196, 196, 198, 198, 203, 203, 205, 205, 207, 207,
        209, 209, 209, 211, 211, 213, 213, 215, 215, 218, 218, 220, 220, 224, 224, 226, 226, 226, 228, 228,
        230, 230, 233, 233, 235, 235, 237, 237, 239, 239, 241, 241, 245, 245, 245, 247, 247, 250, 250, 252,
        252, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256,
        256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256, 256,
        256,
    ];

    internal static bool IsIconView(FileViewMode Mode) => Mode is FileViewMode.ExtraLargeIcons
        or FileViewMode.LargeIcons or FileViewMode.MediumIcons or FileViewMode.SmallIcons;

    internal static int DefaultSize(FileViewMode Mode) => Mode switch
    {
        FileViewMode.ExtraLargeIcons => 256, FileViewMode.LargeIcons => 96,
        FileViewMode.MediumIcons or FileViewMode.Tiles or FileViewMode.Content => 48, _ => 16
    };

    internal static (FileViewMode Mode, int Size) Step(FileViewMode Mode, int Size, bool Increase)
    {
        if (!IsIconView(Mode))
        {
            var Next = (Mode, Increase) switch
            {
                (FileViewMode.Content, true) => FileViewMode.Tiles,
                (FileViewMode.Tiles, true) => FileViewMode.Details,
                (FileViewMode.Details, true) => FileViewMode.List,
                (FileViewMode.List, true) => FileViewMode.SmallIcons,
                (FileViewMode.List, false) => FileViewMode.Details,
                (FileViewMode.Details, false) => FileViewMode.Tiles,
                (FileViewMode.Tiles, false) => FileViewMode.Content,
                _ => Mode
            };
            return (Next, DefaultSize(Next));
        }
        Size = Size is >= 16 and <= 256 ? Size : DefaultSize(Mode);
        if (!Increase && Size == 16) return (FileViewMode.List, 16);
        var Pixels = (Increase ? Larger : Smaller)[Size - 16];
        return (Pixels <= 32 ? FileViewMode.SmallIcons : Pixels <= 48 ? FileViewMode.MediumIcons
            : Pixels <= 96 ? FileViewMode.LargeIcons : FileViewMode.ExtraLargeIcons, Pixels);
    }
}
