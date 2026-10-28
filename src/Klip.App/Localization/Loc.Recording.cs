namespace Klip.App.Localization;

// strings for video recording page and options
public static partial class Loc
{
    public static string SectionRecordingPage => Get(nameof(SectionRecordingPage));
    public static string RecordingVideoHeader => Get(nameof(RecordingVideoHeader));
    public static string RecordingGifHeader => Get(nameof(RecordingGifHeader));
    public static string Mp4FpsTitle => Get(nameof(Mp4FpsTitle));
    public static string Mp4FpsCaption => Get(nameof(Mp4FpsCaption));
    public static string Mp4FpsValue => Get(nameof(Mp4FpsValue));
    public static string Mp4QualityTitle => Get(nameof(Mp4QualityTitle));
    public static string Mp4QualityCaption => Get(nameof(Mp4QualityCaption));
    public static string Mp4QualitySmall => Get(nameof(Mp4QualitySmall));
    public static string Mp4QualityBalanced => Get(nameof(Mp4QualityBalanced));
    public static string Mp4QualityHigh => Get(nameof(Mp4QualityHigh));
    public static string Mp4ResolutionTitle => Get(nameof(Mp4ResolutionTitle));
    public static string Mp4ResolutionCaption => Get(nameof(Mp4ResolutionCaption));
    public static string Mp4ResolutionNative => Get(nameof(Mp4ResolutionNative));
    public static string Mp4ShowCursorTitle => Get(nameof(Mp4ShowCursorTitle));
    public static string Mp4ShowCursorCaption => Get(nameof(Mp4ShowCursorCaption));
    public static string Mp4SystemAudioCaption => Get(nameof(Mp4SystemAudioCaption));
    public static string Mp4MicrophonesCaption => Get(nameof(Mp4MicrophonesCaption));
    public static string RecordingAdvancedTitle => Get(nameof(RecordingAdvancedTitle));
    public static string RecordingAdvancedCaption => Get(nameof(RecordingAdvancedCaption));
    public static string Mp4CodecTitle => Get(nameof(Mp4CodecTitle));
    public static string Mp4CodecCaption => Get(nameof(Mp4CodecCaption));
    public static string Mp4CodecAuto => Get(nameof(Mp4CodecAuto));
    public static string Mp4CodecSoftwareOnly => Get(nameof(Mp4CodecSoftwareOnly));
    public static string RecordingQualityPresetLabel => Get(nameof(RecordingQualityPresetLabel));
    public static string NotifyRecordingSoftwareEncoder => Get(nameof(NotifyRecordingSoftwareEncoder));
    public static string NotifyRecordingCodecFallback => Get(nameof(NotifyRecordingCodecFallback));
    public static string RecordingDropsWarning => Get(nameof(RecordingDropsWarning));
    public static string RecordingProcessLost => Get(nameof(RecordingProcessLost));

    private static readonly Dictionary<string, string> PtRecording = new()
    {
        [nameof(SectionRecordingPage)] = "Gravação",
        [nameof(RecordingVideoHeader)] = "Vídeo MP4",
        [nameof(RecordingGifHeader)] = "GIF",
        [nameof(Mp4FpsTitle)] = "Quadros por segundo",
        [nameof(Mp4FpsCaption)] = "60 fps deixa movimento e jogos mais fluidos. 30 fps basta para aulas e demonstrações e gera arquivos menores.",
        [nameof(Mp4FpsValue)] = "{0} fps",
        [nameof(Mp4QualityTitle)] = "Qualidade",
        [nameof(Mp4QualityCaption)] = "A taxa de bits é calculada pela resolução, pelo FPS e pelo codec; aqui você só escolhe o equilíbrio entre nitidez e tamanho do arquivo.",
        [nameof(Mp4QualitySmall)] = "Arquivo menor",
        [nameof(Mp4QualityBalanced)] = "Equilibrado",
        [nameof(Mp4QualityHigh)] = "Alta",
        [nameof(Mp4ResolutionTitle)] = "Resolução de saída",
        [nameof(Mp4ResolutionCaption)] = "Reduz o vídeo mantendo a proporção, nunca amplia. Nativa grava no tamanho da área selecionada.",
        [nameof(Mp4ResolutionNative)] = "Nativa",
        [nameof(Mp4ShowCursorTitle)] = "Mostrar cursor",
        [nameof(Mp4ShowCursorCaption)] = "Estado inicial do botão de cursor na barra de gravação. Dá para trocar durante a gravação.",
        [nameof(Mp4SystemAudioCaption)] = "Grava o som que o computador está tocando.",
        [nameof(Mp4MicrophonesCaption)] = "Microfones mixados na trilha de áudio do vídeo.",
        [nameof(RecordingAdvancedTitle)] = "Avançado",
        [nameof(RecordingAdvancedCaption)] = "Codec do vídeo e caminho do ffmpeg.",
        [nameof(Mp4CodecTitle)] = "Codec",
        [nameof(Mp4CodecCaption)] = "Automático usa H.264, que toca em qualquer lugar, e HEVC acima de 4096 px de largura. Só aparecem os codecs com aceleração de hardware neste PC.",
        [nameof(Mp4CodecAuto)] = "Automático",
        [nameof(Mp4CodecSoftwareOnly)] = "Nenhum codificador de vídeo por hardware foi encontrado: a gravação usa o processador, limitada a 30 fps.",
        [nameof(RecordingQualityPresetLabel)] = "Qualidade",
        [nameof(NotifyRecordingSoftwareEncoder)] = "Sem codificador de vídeo por hardware: gravando a {0} fps pelo processador. O vídeo pode perder quadros.",
        [nameof(NotifyRecordingCodecFallback)] = "O codec escolhido não está disponível neste PC; gravando em {0}.",
        [nameof(RecordingDropsWarning)] = "A gravação está perdendo quadros ({0}). Tente 30 fps ou uma resolução menor.",
        [nameof(RecordingProcessLost)] = "O processo de gravação parou inesperadamente. O que já foi gravado foi preservado.",
    };

    private static readonly Dictionary<string, string> EnRecording = new()
    {
        [nameof(SectionRecordingPage)] = "Recording",
        [nameof(RecordingVideoHeader)] = "MP4 video",
        [nameof(RecordingGifHeader)] = "GIF",
        [nameof(Mp4FpsTitle)] = "Frames per second",
        [nameof(Mp4FpsCaption)] = "60 fps makes motion and games smoother. 30 fps is enough for lessons and demos and makes smaller files.",
        [nameof(Mp4FpsValue)] = "{0} fps",
        [nameof(Mp4QualityTitle)] = "Quality",
        [nameof(Mp4QualityCaption)] = "The bitrate comes from the resolution, fps and codec; here you only pick the balance between sharpness and file size.",
        [nameof(Mp4QualitySmall)] = "Smaller file",
        [nameof(Mp4QualityBalanced)] = "Balanced",
        [nameof(Mp4QualityHigh)] = "High",
        [nameof(Mp4ResolutionTitle)] = "Output resolution",
        [nameof(Mp4ResolutionCaption)] = "Shrinks the video keeping the aspect ratio, never enlarges. Native records at the size of the selected area.",
        [nameof(Mp4ResolutionNative)] = "Native",
        [nameof(Mp4ShowCursorTitle)] = "Show cursor",
        [nameof(Mp4ShowCursorCaption)] = "Initial state of the cursor button on the recording bar. You can still switch it while recording.",
        [nameof(Mp4SystemAudioCaption)] = "Records the sound the computer is playing.",
        [nameof(Mp4MicrophonesCaption)] = "Microphones mixed into the video audio track.",
        [nameof(RecordingAdvancedTitle)] = "Advanced",
        [nameof(RecordingAdvancedCaption)] = "Video codec and ffmpeg path.",
        [nameof(Mp4CodecTitle)] = "Codec",
        [nameof(Mp4CodecCaption)] = "Automatic uses H.264, which plays everywhere, and HEVC above 4096 px wide. Only codecs with hardware acceleration on this PC are listed.",
        [nameof(Mp4CodecAuto)] = "Automatic",
        [nameof(Mp4CodecSoftwareOnly)] = "No hardware video encoder was found: recording uses the processor, limited to 30 fps.",
        [nameof(RecordingQualityPresetLabel)] = "Quality",
        [nameof(NotifyRecordingSoftwareEncoder)] = "No hardware video encoder: recording at {0} fps on the processor. The video may drop frames.",
        [nameof(NotifyRecordingCodecFallback)] = "The chosen codec is not available on this PC; recording in {0}.",
        [nameof(RecordingDropsWarning)] = "The recording is dropping frames ({0}). Try 30 fps or a lower resolution.",
        [nameof(RecordingProcessLost)] = "The recording process stopped unexpectedly. What was recorded so far was kept.",
    };
}
