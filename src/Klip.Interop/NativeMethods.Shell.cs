using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>
/// Estado de notificacao do usuario reportado por SHQueryUserNotificationState.
/// Base da auto-suspensao quando ha jogo/apresentacao em tela cheia (RF-P1.06).
/// </summary>
public enum QUERY_USER_NOTIFICATION_STATE
{
    /// <summary>Screensaver, maquina bloqueada ou sessao FUS inativa.</summary>
    QUNS_NOT_PRESENT = 1,

    /// <summary>Aplicacao em tela cheia rodando ou Presentation Settings ligado.</summary>
    QUNS_BUSY = 2,

    /// <summary>D3D fullscreen exclusivo (nao cobre borderless).</summary>
    QUNS_RUNNING_D3D_FULL_SCREEN = 3,

    /// <summary>Usuario bloqueou notificacoes.</summary>
    QUNS_PRESENTATION_MODE = 4,

    /// <summary>Livre.</summary>
    QUNS_ACCEPTS_NOTIFICATIONS = 5,

    /// <summary>Primeira hora apos o logon inicial / upgrade.</summary>
    QUNS_QUIET_TIME = 6,

    /// <summary>App da Store rodando.</summary>
    QUNS_APP = 7,
}

/// <summary>P/Invoke do shell: estado de notificacao e classe de janela.</summary>
public static partial class NativeMethods
{
    /// <summary>
    /// Retorna HRESULT (&lt; 0 indica falha). ATENCAO: o sistema NAO envia notificacao
    /// ao entrar/sair de tela cheia, entao o consumidor e obrigado a fazer polling.
    /// </summary>
    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(nint hWnd, ref char lpClassName, int nMaxCount);

    /// <summary>
    /// Nome da classe da janela. Buffer na pilha (64 chars cobre as classes de shell
    /// e de jogos que interessam); so aloca string quando ha conteudo.
    /// </summary>
    public static string GetClassNameSafe(nint hwnd)
    {
        if (hwnd == nint.Zero)
            return string.Empty;

        Span<char> buffer = stackalloc char[64];
        var length = GetClassName(hwnd, ref MemoryMarshal.GetReference(buffer), buffer.Length);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }
}
