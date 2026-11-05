using System.Runtime.InteropServices;

namespace CDir.Services;

/// <summary>Gives the application a stable Shell identity before any windows are created.</summary>
internal static class TaskbarService
{
    internal const string ApplicationId = "CDir.FileManager";

    internal static void InitializeIdentity() => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(ApplicationId));

    internal static string? ReadIdentity()
    {
        var Result = GetCurrentProcessExplicitAppUserModelID(out var Identity);
        try
        {
            Marshal.ThrowExceptionForHR(Result);
            return Marshal.PtrToStringUni(Identity);
        }
        finally { if (Identity != IntPtr.Zero) Marshal.FreeCoTaskMem(Identity); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string AppId);

    [DllImport("shell32.dll")]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr AppId);
}
