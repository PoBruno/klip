using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Klip.Interop;

/// <summary>P/Invoke do motor de gravacao (spec F2).</summary>
public static partial class NativeMethods
{
    /// <summary>
    /// Embrulha um IDXGIDevice num IDirect3DDevice WinRT (IInspectable), o tipo
    /// de device que o Direct3D11CaptureFramePool consome. O ponteiro retornado
    /// tem ref count proprio e deve ser liberado com Marshal.Release apos o FromAbi.
    /// </summary>
    [LibraryImport("d3d11.dll")]
    public static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    /// <summary>
    /// Timer de alta resolucao (1803+): ticks com jitter sub-ms independentes da
    /// resolucao global do sistema (~15,6 ms) e do estado da janela do processo.
    /// Usado pelo loop CFR do FrameCaptureEngine (RF-F2.05) - ver comentario la
    /// sobre por que timeBeginPeriod nao serve.
    /// </summary>
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;

    public const uint TIMER_ALL_ACCESS = 0x001F0003;

    /// <summary>
    /// Cria um waitable timer. Retorna handle invalido (IsInvalid) em falha -
    /// em particular, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION falha em builds
    /// anteriores ao Windows 10 1803 (recriar com dwFlags = 0).
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeWaitHandle CreateWaitableTimerExW(
        nint lpTimerAttributes,
        string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    /// <summary>
    /// Arma o timer: <paramref name="lpDueTime"/> em unidades de 100 ns
    /// (negativo = relativo), <paramref name="lPeriod"/> em ms (periodico).
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(
        SafeWaitHandle hTimer,
        in long lpDueTime,
        int lPeriod,
        nint pfnCompletionRoutine,
        nint lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);
}
