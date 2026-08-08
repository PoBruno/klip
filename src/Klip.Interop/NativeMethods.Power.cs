using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>
/// P/Invoke de QoS/energia (RF-P3.03): EcoQoS por processo e por thread,
/// prioridade de memoria e background IO mode.
/// </summary>
public static partial class NativeMethods
{
    // ----- PROCESS_INFORMATION_CLASS -----

    public const int ProcessMemoryPriority = 0;
    public const int ProcessPowerThrottling = 4;

    // ----- THREAD_INFORMATION_CLASS -----

    public const int ThreadMemoryPriority = 0;
    public const int ThreadPowerThrottling = 3;

    /// <summary>
    /// PROCESS_POWER_THROTTLING_STATE e THREAD_POWER_THROTTLING_STATE tem layout
    /// identico, por isso um unico struct atende os dois niveis.
    /// ControlMask = qual mecanismo estou controlando; StateMask = ligado (bit
    /// setado) ou desligado (bit zerado).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint THREAD_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    /// <summary>So existe no nivel de PROCESSO (defesa contra timeBeginPeriod de dependencias).</summary>
    public const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    public const uint MEMORY_PRIORITY_VERY_LOW = 1;
    public const uint MEMORY_PRIORITY_LOW = 2;
    public const uint MEMORY_PRIORITY_MEDIUM = 3;
    public const uint MEMORY_PRIORITY_BELOW_NORMAL = 4;
    public const uint MEMORY_PRIORITY_NORMAL = 5;

    /// <summary>
    /// SetThreadPriority: BACKGROUND_BEGIN/END so valem para a thread ATUAL e
    /// falham com ERROR_THREAD_MODE_ALREADY_BACKGROUND se ela ja estiver no modo.
    /// </summary>
    public const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;

    public const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    [LibraryImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessInformation(
        nint hProcess,
        int ProcessInformationClass,
        ref POWER_THROTTLING_STATE ProcessInformation,
        uint ProcessInformationSize);

    [LibraryImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessInformation(
        nint hProcess,
        int ProcessInformationClass,
        ref MEMORY_PRIORITY_INFORMATION ProcessInformation,
        uint ProcessInformationSize);

    [LibraryImport("kernel32.dll", EntryPoint = "SetThreadInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadInformation(
        nint hThread,
        int ThreadInformationClass,
        ref POWER_THROTTLING_STATE ThreadInformation,
        uint ThreadInformationSize);

    [LibraryImport("kernel32.dll", EntryPoint = "SetThreadInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadInformation(
        nint hThread,
        int ThreadInformationClass,
        ref MEMORY_PRIORITY_INFORMATION ThreadInformation,
        uint ThreadInformationSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadPriority(nint hThread, int nPriority);

    /// <summary>Pseudo-handle do processo atual (-1); nao precisa ser fechado.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    /// <summary>Pseudo-handle da thread atual (-2); resolve sempre para quem chama.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentThread();
}
