using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Klip.App.Media;

/// <summary>
/// IVideoPlayer sobre o MediaElement in-box (RF-F5.02).
/// LIMITACAO v1 (Q-F5.1): o MediaElement nao tem frame stepping nem seek
/// frame-accurate; o "step" do editor faz seek de +/- 1/30 s aproximado.
/// Upgrade futuro: FFME (MS-PL) ou player proprio sobre MF Source Reader -
/// por isso esta classe fica atras da interface.
/// </summary>
public sealed class MediaElementVideoPlayer : IVideoPlayer
{
    /// <summary>
    /// Janela do coalescing de seeks do scrub (anti seek storm): no maximo um
    /// Position real por janela; ~90 ms cobre o custo tipico do ciclo
    /// assincrono de decode/render que cada seek pausado dispara.
    /// </summary>
    private static readonly TimeSpan SeekCoalesceWindow = TimeSpan.FromMilliseconds(90);

    private readonly MediaElement _element;
    private readonly DispatcherTimer _seekTimer;

    // ultima posicao pedida via RequestSeek ainda nao aplicada (as
    // intermediarias do scrub sao sobrescritas aqui e nunca viram seek real)
    private TimeSpan? _pendingSeek;

    // estado logico de playback: o MediaElement pode retomar o play sozinho
    // em alguns caminhos de seek; usamos isto para re-pausar (seek storm)
    private bool _isPlaying;

    public event Action? MediaOpened;
    public event Action<Exception>? MediaFailed;

    public MediaElementVideoPlayer()
    {
        _element = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true, // atualiza o quadro em seeks pausados
            Stretch = System.Windows.Media.Stretch.Uniform,
        };
        _element.MediaOpened += (_, _) => MediaOpened?.Invoke();
        _element.MediaFailed += (_, e) => MediaFailed?.Invoke(
            e.ErrorException ?? new InvalidOperationException("MediaElement falhou sem excecao."));
        _seekTimer = new DispatcherTimer { Interval = SeekCoalesceWindow };
        _seekTimer.Tick += (_, _) => DrainPendingSeek();
    }

    public FrameworkElement Visual => _element;

    public TimeSpan Duration =>
        _element.NaturalDuration.HasTimeSpan ? _element.NaturalDuration.TimeSpan : TimeSpan.Zero;

    public bool HasAudio => _element.HasAudio;

    public int NaturalVideoWidth => _element.NaturalVideoWidth;

    public int NaturalVideoHeight => _element.NaturalVideoHeight;

    public TimeSpan Position
    {
        get => _element.Position;
        set
        {
            // seek imediato invalida o coalescido pendente (que e sempre um
            // pedido mais antigo do scrub - aplicar depois voltaria no tempo)
            CancelPendingSeek();
            ApplySeek(value);
        }
    }

    public double Volume
    {
        get => _element.Volume;
        set => _element.Volume = Math.Clamp(value, 0, 1);
    }

    public void Open(string path)
    {
        _element.Source = new Uri(path);
        // Play+Pause imediato forca o MediaElement a abrir a midia e
        // renderizar o primeiro quadro sem iniciar o playback de fato
        _element.Play();
        _element.Pause();
        _element.Position = TimeSpan.Zero;
    }

    /// <summary>
    /// Seek coalescido do scrub (anti seek storm): o primeiro pedido aplica
    /// na hora (leading edge, o quadro responde ao inicio do drag); os
    /// seguintes dentro da janela so atualizam o alvo e o timer aplica a
    /// ULTIMA posicao no proximo tick (trailing edge). Resultado: no maximo
    /// um seek real a cada ~90 ms, sem fila de decodes no MediaElement.
    /// </summary>
    public void RequestSeek(TimeSpan position)
    {
        if (_seekTimer.IsEnabled)
        {
            _pendingSeek = position; // janela aberta: descarta a intermediaria
            return;
        }
        ApplySeek(position);
        _seekTimer.Start();
    }

    private void DrainPendingSeek()
    {
        if (_pendingSeek is { } pending)
        {
            _pendingSeek = null;
            ApplySeek(pending);
            return; // mantem a janela aberta para o proximo pedido do drag
        }
        _seekTimer.Stop(); // janela ociosa: proximo RequestSeek aplica na hora
    }

    private void ApplySeek(TimeSpan position)
    {
        _element.Position = position;
        // pausado fica pausado: o MediaElement pode retomar o playback apos
        // um seek em alguns caminhos (parte do bug do seek storm - "play em
        // camera lenta" com o editor pausado)
        if (!_isPlaying)
            _element.Pause();
    }

    private void CancelPendingSeek()
    {
        _pendingSeek = null;
        _seekTimer.Stop();
    }

    public void Play()
    {
        // flush do seek pendente ANTES de tocar: um seek coalescido atrasado
        // nao pode sobrescrever a posicao depois do Play
        if (_pendingSeek is { } pending)
            _element.Position = pending;
        CancelPendingSeek();
        _isPlaying = true;
        _element.Play();
    }

    public void Pause()
    {
        _isPlaying = false;
        _element.Pause();
    }

    public void Close()
    {
        CancelPendingSeek();
        _isPlaying = false;
        _element.Stop();
        _element.Close();
        _element.Source = null;
    }
}
