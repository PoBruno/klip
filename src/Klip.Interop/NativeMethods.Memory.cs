using System.Runtime.InteropServices;

namespace Klip.Interop;

public static partial class NativeMethods
{
    // hard floor the trimmer can't go below, and no artificial ceiling above it
    private const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x1;
    private const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref PROCESS_MEMORY_COUNTERS counters, uint cb);

    /// <summary>
    /// Page faults of this process since it started (soft and hard together). The
    /// delta across a window opening says how much of it had to come back into RAM.
    /// </summary>
    public static uint GetPageFaultCount()
    {
        var counters = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
        try
        {
            return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb) ? counters.PageFaultCount : 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSizeEx(nint hProcess, nint dwMinimumWorkingSetSize,
        nint dwMaximumWorkingSetSize, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// Pisa um piso de working set que o trimmer do Windows nao derruba nem sob
    /// pressao externa sustentada (jogo em tela cheia por horas) - sem isso o
    /// primeiro Win+V depois de uma sessao assim paga o fault storm inteiro de volta.
    /// O piso escala com a RAM da maquina e e zero em PCs fracos: continuam so com a
    /// prioridade de memoria baixa (ver <see cref="PowerEfficiency"/>), sem reservar nada.
    /// </summary>
    public static bool PinWorkingSetFloor()
    {
        var floor = WorkingSetFloorBytes();
        if (floor <= 0)
            return false;

        try
        {
            return SetProcessWorkingSetSizeEx(GetCurrentProcess(), (nint)floor, (nint)(floor * 4),
                QUOTA_LIMITS_HARDWS_MIN_ENABLE | QUOTA_LIMITS_HARDWS_MAX_DISABLE);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static long WorkingSetFloorBytes()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        try
        {
            if (!GlobalMemoryStatusEx(ref status))
                return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }

        const long Gb = 1024L * 1024 * 1024;
        return status.ullTotalPhys switch
        {
            < 4 * Gb => 0,              // pouca RAM: nada travado, so a prioridade baixa ja ajuda
            < 8 * Gb => 16 * 1024 * 1024,
            _ => 32 * 1024 * 1024,
        };
    }
}
