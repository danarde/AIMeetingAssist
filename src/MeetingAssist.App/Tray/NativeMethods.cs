using System.Runtime.InteropServices;

namespace MeetingAssist.App.Tray;

/// <summary>
/// <c>Bitmap.GetHicon</c> allocates an unmanaged icon handle that nothing else frees. Without
/// this, every state change would leak one GDI handle for the life of the process.
/// </summary>
internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint hIcon);
}
