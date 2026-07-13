using Vortice.MediaFoundation;

namespace Klip.Interop.Recording;

/// <summary>
/// Ciclo de vida do runtime do Media Foundation com contagem de referencias:
/// MFStartup na primeira gravacao, MFShutdown quando a ultima libera.
/// Em Windows N/KN sem o Media Feature Pack o startup falha e a mensagem
/// orienta a instalacao (RF-F3.17).
/// </summary>
internal static class MediaFoundationRuntime
{
    private static readonly object Lock = new();
    private static int _refCount;

    /// <exception cref="PlatformNotSupportedException">Media Foundation indisponivel (RF-F3.17).</exception>
    public static void Acquire()
    {
        lock (Lock)
        {
            if (_refCount == 0)
            {
                try
                {
                    // MFSTARTUP_LITE: sem infraestrutura de rede (SinkWriter nao precisa)
                    MediaFactory.MFStartup(true).CheckError();
                }
                catch (Exception ex)
                {
                    throw new PlatformNotSupportedException(
                        "O Media Foundation nao esta disponivel neste Windows (edicao N/KN?). " +
                        "Instale o \"Media Feature Pack\" em Configuracoes > Aplicativos > Recursos opcionais " +
                        "para habilitar a gravacao MP4.",
                        ex);
                }
            }

            _refCount++;
        }
    }

    public static void Release()
    {
        lock (Lock)
        {
            if (_refCount == 0)
                return;
            if (--_refCount == 0)
            {
                try
                {
                    MediaFactory.MFShutdown();
                }
                catch (Exception)
                {
                    // shutdown falho nao pode derrubar o app no teardown
                }
            }
        }
    }
}
