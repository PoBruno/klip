namespace Klip.Interop.Recording;

/// <summary>
/// Exclui janelas do proprio app (borda de gravacao, toolbar) do conteudo
/// capturado via SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) (RF-F2.10).
/// So funciona em janelas top-level do proprio processo, builds 19041+.
/// </summary>
public static class WindowCaptureExclusion
{
    /// <summary>
    /// Marca a janela para nao aparecer em capturas/gravacoes.
    /// Retorna false se o build nao suporta (&lt; 19041) ou se a chamada falhar.
    /// </summary>
    public static bool Exclude(nint hwnd)
    {
        if (hwnd == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return false;
        return NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Restaura o comportamento padrao (janela volta a aparecer em capturas).</summary>
    public static bool Restore(nint hwnd)
    {
        if (hwnd == 0)
            return false;
        return NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_NONE);
    }
}
