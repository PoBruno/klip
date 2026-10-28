using Klip.App.Services;

namespace Klip.App.Diagnostics;

/// <summary>
/// Registra o estado dos hooks de baixo nivel nas transicoes que importam
/// (abrir e fechar o flyout, armar e desarmar a fila de colagem).
///
/// A regra e: com o flyout FECHADO, nenhum hook LL do Klip pode
/// estar instalado. Um hook instalado e chamado pela Raw Input Thread do Windows a cada
/// evento de teclado e mouse do sistema inteiro - 1000 a 8000 vezes por segundo com um
/// mouse gamer - e a RIT bloqueia esperando o retorno. Por isso "hooks=nao" em repouso
/// nao e detalhe: e a diferenca entre o app ser invisivel para um jogo ou travar o CS2.
///
/// Sai em nivel verbose (--verbose) para nao inflar o log em uso normal.
/// </summary>
internal static class StartupLogHookTrace
{
    public static void Trace(string transition)
    {
        if (!StartupLog.VerboseEnabled)
            return;
        try
        {
            StartupLog.WriteVerbose($"{transition}: {HookHealth.FormatSummary()}");
        }
        catch
        {
            // diagnostico nunca pode derrubar o fluxo que esta instrumentando
        }
    }
}
