using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>
/// P/Invoke do arraste/snap/persistencia da toolbar de gravacao (spec M2-02,
/// RF-T2.01..03). Arquivo separado dos demais NativeMethods.* de proposito:
/// evita conflito com trabalho paralelo nos arquivos existentes.
/// </summary>
public static partial class NativeMethods
{
    // RF-T2.01: drag de janela NOACTIVATE sem roubar foco - ReleaseCapture +
    // WM_NCLBUTTONDOWN/HTCAPTION delega o move-loop ao proprio Windows
    public const uint WM_NCLBUTTONDOWN = 0x00A1;
    public const nint HTCAPTION = 2;

    // RF-T2.03: snap magnetico (ajuste do RECT proposto durante o move-loop)
    public const int WM_MOVING = 0x0216;

    // RF-T2.02: fim do move-loop -> persistir GetWindowRect
    public const int WM_EXITSIZEMOVE = 0x0232;

    public const uint SWP_NOSIZE = 0x0001;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>Monitor mais proximo do RECT (validacao da posicao salva, RF-T2.02).</summary>
    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromRect(ref RECT lprc, uint dwFlags);
}
