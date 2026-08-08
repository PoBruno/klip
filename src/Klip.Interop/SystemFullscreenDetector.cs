using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>Politica de interferencia do app com o que o usuario esta fazendo.</summary>
public enum SystemActivityState
{
    Normal,

    /// <summary>Jogo ou app em tela cheia, apresentacao, tela bloqueada: o app deve se auto-suspender.</summary>
    Suspended,
}

/// <summary>
/// Deteccao de tela cheia para auto-suspensao (RF-P1.06): combina o estado de
/// notificacao do shell com uma heuristica geometrica que pega o borderless.
/// <para>
/// O Windows NAO envia notificacao ao entrar/sair de tela cheia, entao o consumidor
/// precisa fazer polling folgado (2-5 s) num timer de background, nunca num
/// DispatcherTimer.
/// </para>
/// <para>Nenhum metodo lanca: qualquer falha e tratada como "estado normal".</para>
/// </summary>
public static class SystemFullscreenDetector
{
    /// <summary>Tolerancia em pixels fisicos: o DWM pode reportar bordas invisiveis.</summary>
    private const int EdgeTolerance = 1;

    // Cache de indisponibilidade do export (shell32 sempre tem, mas o contrato e "nunca lanca").
    private static bool _shellApiMissing;

    /// <summary>Le SHQueryUserNotificationState. Retorna null se a chamada falhar.</summary>
    public static QUERY_USER_NOTIFICATION_STATE? QueryNotificationState()
    {
        if (_shellApiMissing)
            return null;

        try
        {
            // HRESULT negativo = falha; nao muda o comportamento do app
            return NativeMethods.SHQueryUserNotificationState(out var state) < 0 ? null : state;
        }
        catch (EntryPointNotFoundException)
        {
            _shellApiMissing = true;
            return null;
        }
        catch (DllNotFoundException)
        {
            _shellApiMissing = true;
            return null;
        }
    }

    /// <summary>
    /// Heuristica geometrica: a janela em foreground cobre exatamente o retangulo do
    /// monitor. Cobre borderless fullscreen, que QUNS_RUNNING_D3D_FULL_SCREEN nao reporta.
    /// </summary>
    public static bool IsForegroundWindowFullscreen()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == nint.Zero)
                return false;

            // desktop e barras de tarefa cobrem o monitor por definicao
            if (IsShellClass(NativeMethods.GetClassNameSafe(hwnd)))
                return false;

            // janelas do proprio processo (overlay de captura do Klip) nao contam,
            // senao o app se auto-detecta como jogo em tela cheia
            var ownerThread = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (ownerThread == 0 || processId == (uint)Environment.ProcessId)
                return false;

            // geometria SEMPRE em pixels fisicos - GetWindowRect e rcMonitor ja sao fisicos
            if (!NativeMethods.GetWindowRect(hwnd, out var windowRect))
                return false;

            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == nint.Zero)
                return false;

            var monitorInfo = new NativeMethods.MONITORINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
                return false;

            var bounds = monitorInfo.rcMonitor;
            return Math.Abs(windowRect.left - bounds.left) <= EdgeTolerance
                && Math.Abs(windowRect.top - bounds.top) <= EdgeTolerance
                && Math.Abs(windowRect.right - bounds.right) <= EdgeTolerance
                && Math.Abs(windowRect.bottom - bounds.bottom) <= EdgeTolerance;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Combina os dois sinais.</summary>
    public static SystemActivityState Evaluate()
    {
        var state = QueryNotificationState();
        if (state is QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
            or QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
            or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE
            or QUERY_USER_NOTIFICATION_STATE.QUNS_NOT_PRESENT)
        {
            return SystemActivityState.Suspended;
        }

        return IsForegroundWindowFullscreen()
            ? SystemActivityState.Suspended
            : SystemActivityState.Normal;
    }

    private static bool IsShellClass(string className) => className is
        "Progman" or
        "WorkerW" or
        "Shell_TrayWnd" or
        "Shell_SecondaryTrayWnd" or
        "Windows.UI.Core.CoreWindow";
}
