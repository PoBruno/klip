using System.Windows;

namespace Klip.App.Media;

/// <summary>
/// RF-F5.02 / D-F5.1: abstracao do player de video do editor de midia. A v1
/// usa MediaElement in-box (seek impreciso, sem frame stepping real); a
/// interface existe para trocar por FFME ou um player MF Source Reader sem
/// tocar na janela.
/// </summary>
public interface IVideoPlayer
{
    /// <summary>Midia aberta: Duration e HasAudio validos a partir daqui.</summary>
    event Action? MediaOpened;

    event Action<Exception>? MediaFailed;

    /// <summary>Elemento visual a inserir na area de preview.</summary>
    FrameworkElement Visual { get; }

    TimeSpan Duration { get; }

    bool HasAudio { get; }

    /// <summary>Largura do video em pixels (valida apos MediaOpened; 0 se desconhecida). Usada no filler preto dos gaps (RF-F5.20).</summary>
    int NaturalVideoWidth { get; }

    /// <summary>Altura do video em pixels (valida apos MediaOpened; 0 se desconhecida). RF-F5.20.</summary>
    int NaturalVideoHeight { get; }

    /// <summary>Posicao no ARQUIVO fonte (o mapeamento editado-fonte e do chamador). O set e um seek IMEDIATO (casos pontuais: fim de scrub, step, retomada de gap).</summary>
    TimeSpan Position { get; set; }

    /// <summary>
    /// Seek COALESCIDO para scrub continuo (drag do playhead): no maximo um
    /// seek real por janela (~90 ms), sempre para a ULTIMA posicao pedida -
    /// as intermediarias sao descartadas. Correcao do seek storm: setar
    /// Position a cada MouseMove enfileirava dezenas de ciclos assincronos de
    /// decode no MediaElement (ScrubbingEnabled), que os processava em serie
    /// como um "play em camera lenta" com o player pausado e a UI travada.
    /// </summary>
    void RequestSeek(TimeSpan position);

    /// <summary>Volume do preview, 0..1.</summary>
    double Volume { get; set; }

    void Open(string path);

    void Play();

    void Pause();

    void Close();
}
