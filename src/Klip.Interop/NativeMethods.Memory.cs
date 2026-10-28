using System.Runtime.InteropServices;

namespace Klip.Interop;

public static partial class NativeMethods
{
    // (SIZE_T)-1 for both sizes = drop the pages that aren't locked
    private static readonly nint TrimAll = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSizeEx(nint hProcess, nint dwMinimumWorkingSetSize,
        nint dwMaximumWorkingSetSize, uint flags);

    /// <summary>
    /// One-off working set trim. Only after letting go of something big (frames,
    /// editor), never on a timer: the pages come back as soft faults on next use.
    /// </summary>
    public static bool TrimWorkingSet()
    {
        try
        {
            return SetProcessWorkingSetSizeEx(GetCurrentProcess(), TrimAll, TrimAll, 0);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
