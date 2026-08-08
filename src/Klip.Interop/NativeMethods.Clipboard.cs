using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>P/Invoke do clipboard. Listener moderno + sequence number.</summary>
public static partial class NativeMethods
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    // ----- ids dos formatos padrao (windows.h) -----
    // RF-P2.01: usados com IsClipboardFormatAvailable para sondar o clipboard
    // SEM abrir nada. Formatos sintetizados (CF_TEXT <-> CF_UNICODETEXT,
    // CF_BITMAP <-> CF_DIB <-> CF_DIBV5) tambem sao reportados como disponiveis.
    public const uint CF_TEXT = 1;
    public const uint CF_BITMAP = 2;
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_HDROP = 15;
    public const uint CF_DIBV5 = 17;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(nint hwnd);

    /// <summary>anti-loop - número incrementado a cada mudança do clipboard.</summary>
    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll")]
    public static partial nint GetClipboardOwner();

    /// <summary>
    /// RF-P2.01: sonda barata. Le a lista de formatos que o dono do clipboard
    /// anunciou (mantida pelo proprio win32) - nao abre o clipboard e NAO faz
    /// round-trip COM ao processo de origem, ao contrario de
    /// IDataObject.GetDataPresent. Serve para escolher o que pedir antes de
    /// tocar em GetDataObject.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    /// <summary>
    /// Resolve o id de um formato registrado ("HTML Format", "PNG", ...). Ids de
    /// formato registrado sao estaveis por sessao, entao vale cachear o retorno.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string lpszFormat);

    /// <summary>
    /// Diagnostico: HWND que esta com o clipboard aberto (zero se ninguem).
    /// Usado para registrar QUEM bloqueou quando uma leitura falha.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial nint GetOpenClipboardWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowText(nint hWnd, [Out] char[] lpString, int nMaxCount);

    public static string GetWindowTextSafe(nint hWnd)
    {
        if (hWnd == nint.Zero)
            return "";
        var buffer = new char[512];
        var len = GetWindowText(hWnd, buffer, buffer.Length);
        return len > 0 ? new string(buffer, 0, len) : "";
    }

    // ----- identificacao do processo de origem (RF-P2.02) -----

    /// <summary>
    /// RF-P2.02: direito minimo para perguntar caminho e tempos de um processo.
    /// Diferente de PROCESS_QUERY_INFORMATION (0x0400), este funciona atravessando
    /// niveis de integridade - e o unico que um app de usuario consegue abrir
    /// contra a maioria dos processos de terceiros.
    /// </summary>
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(
        nint hProcess,
        uint dwFlags,
        [Out] char[] lpExeName,
        ref uint lpdwSize);

    /// <summary>
    /// Tempos do processo como FILETIME (100ns desde 1601). So o de criacao
    /// interessa aqui: entra na chave do cache porque o Windows recicla PIDs.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(
        nint hProcess,
        out long lpCreationTime,
        out long lpExitTime,
        out long lpKernelTime,
        out long lpUserTime);

    /// <summary>
    /// RF-P2.02: caminho completo da imagem de um processo ja aberto. Buffer fixo
    /// de MAX_PATH estendido; caminho maior que isso simplesmente falha (retorna
    /// null) em vez de crescer o buffer - nao vale a pena para um nome de exe.
    /// </summary>
    public static string? QueryProcessImagePathSafe(nint hProcess)
    {
        if (hProcess == nint.Zero)
            return null;

        var buffer = new char[520];
        var size = (uint)buffer.Length;
        return QueryFullProcessImageName(hProcess, 0, buffer, ref size) && size > 0
            ? new string(buffer, 0, (int)size)
            : null;
    }
}
