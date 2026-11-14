using System.Globalization;
using System.Text;

namespace Klip.Core.Media.Editing;

/// <summary>
/// pure, deterministic translation of a <see cref="MediaEditProject"/>
/// into FFmpeg command-line arguments (the same input always yields the exact
/// same string, so exports are golden-string testable). No process is started
/// here; the export service owns the ffmpeg.exe lifecycle.
///
/// Filter-graph shape (video): the edited timeline is flattened into ITEMS in
/// timeline order - one trim/setpts chain per segment plus one synthetic black
/// source per gap ("color=c=black:s=WxH:r=FPS:d=SEC"), concat
/// (n=K:v=1:a=0), plus one atrim/asetpts chain per NON-MUTED audio track per
/// segment with the track volume applied (omitted when exactly 1.0) and one
/// "anullsrc" silence chain per gap, concat per track, and amix (normalize=0,
/// to honor user volumes) when more than one audible track. All tracks muted
/// (or none): video-only export with "-an" and video-only gap filler.
/// Gap filler dimensions/fps come from the settings and must match the source
/// (see <see cref="VideoExportSettings.Width"/>).
///
/// GIF route (MP4 source): single-pass palette - the segment chain is
/// followed by fps[,scale] then split/palettegen/paletteuse in one filter
/// graph. Chosen over two-pass (two commands + palette temp file) because it
/// keeps the builder a single pure string and quality is equivalent for
/// screencast content; this is why the palette path parameter was dropped
/// from the draft contract. Gaps become black filler at the target GIF fps.
/// </summary>
public static class FfmpegArgsBuilder
{
    /// <summary>One flattened timeline item: a segment or a gap.</summary>
    private readonly record struct TimelineItem(TimelineSegment? Segment, TimeSpan GapDuration)
    {
        public bool IsGap => Segment is null;
    }

    /// <summary>Builds the full argument string for the MP4 re-encode export.</summary>
    public static string BuildVideoExportArgs(MediaEditProject project, VideoExportSettings settings, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (project.Kind != MediaKind.Video)
            throw new ArgumentException("Video export requires a Video project.", nameof(project));

        var items = FlattenTimeline(project);
        var chains = new List<string>();

        // Video: trim/setpts per segment, black filler per gap,
        // concat when more than one item
        if (items.Count == 1)
        {
            chains.Add($"[0:v]{TrimChain(items[0].Segment!)}[v]");
        }
        else
        {
            for (var k = 0; k < items.Count; k++)
            {
                chains.Add(items[k].IsGap
                    ? $"{BlackSource(settings.Width, settings.Height, settings.Fps, items[k].GapDuration)}[v{k}]"
                    : $"[0:v]{TrimChain(items[k].Segment!)}[v{k}]");
            }

            var labels = new StringBuilder();
            for (var k = 0; k < items.Count; k++)
                labels.Append($"[v{k}]");
            chains.Add($"{labels}concat=n={items.Count}:v=1:a=0[v]");
        }

        // Audio: only non-muted tracks make it into the graph; gaps become
        // silence (anullsrc) so audio and video stay in sync
        var audible = project.AudioTracks.Where(t => !t.IsMuted).ToList();
        for (var t = 0; t < audible.Count; t++)
        {
            var track = audible[t];
            var volume = track.Volume == 1.0 ? "" : $",volume={Num(track.Volume)}";
            var trackOut = audible.Count == 1 ? "a" : $"ta{t}";

            if (items.Count == 1)
            {
                chains.Add($"[0:a:{track.StreamIndex}]{AtrimChain(items[0].Segment!)}{volume}[{trackOut}]");
            }
            else
            {
                var labels = new StringBuilder();
                for (var k = 0; k < items.Count; k++)
                {
                    chains.Add(items[k].IsGap
                        ? $"{SilenceSource(items[k].GapDuration)}[a{t}s{k}]"
                        : $"[0:a:{track.StreamIndex}]{AtrimChain(items[k].Segment!)}{volume}[a{t}s{k}]");
                    labels.Append($"[a{t}s{k}]");
                }
                chains.Add($"{labels}concat=n={items.Count}:v=0:a=1[{trackOut}]");
            }
        }

        if (audible.Count > 1)
        {
            var labels = new StringBuilder();
            for (var t = 0; t < audible.Count; t++)
                labels.Append($"[ta{t}]");
            // normalize=0: amix must not rescale the user-chosen track volumes
            chains.Add($"{labels}amix=inputs={audible.Count}:duration=longest:normalize=0[a]");
        }

        var args = new StringBuilder();
        args.Append($"-i {Quote(project.SourcePath)}");
        args.Append($" -filter_complex \"{string.Join(";", chains)}\"");
        args.Append(" -map \"[v]\"");
        args.Append(audible.Count > 0 ? " -map \"[a]\"" : " -an");
        args.Append($" -c:v {settings.VideoCodec}");
        if (settings.BitrateKbps is int kbps)
            args.Append($" -b:v {kbps.ToString(CultureInfo.InvariantCulture)}k");
        if (settings.FastStart)
            args.Append(" -movflags +faststart");
        args.Append($" {Quote(outputPath)}");
        return args.ToString();
    }

    /// <summary>
    /// Builds the full argument string for the MP4-to-GIF export (single-pass
    /// palette via split/palettegen/paletteuse - see class remarks). Gaps
    /// become black filler at the target GIF framerate.
    /// </summary>
    public static string BuildGifFromVideoArgs(MediaEditProject project, GifFromVideoSettings settings, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (project.Kind != MediaKind.Video)
            throw new ArgumentException("The MP4-to-GIF route requires a Video project.", nameof(project));
        if (settings.Fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "Fps must be positive.");
        var dither = settings.Dithering switch
        {
            "none" or "bayer" => settings.Dithering,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), $"Unsupported dithering '{settings.Dithering}'."),
        };

        var items = FlattenTimeline(project);
        var scale = settings.ScaleWidth is int w
            ? $",scale={w.ToString(CultureInfo.InvariantCulture)}:-1:flags=lanczos"
            : "";
        var palette = $"fps={settings.Fps.ToString(CultureInfo.InvariantCulture)}{scale},split[a][b];[a]palettegen[p];[b][p]paletteuse=dither={dither}";

        string graph;
        if (items.Count == 1)
        {
            graph = $"[0:v]{TrimChain(items[0].Segment!)},{palette}";
        }
        else
        {
            var chains = new List<string>();
            var labels = new StringBuilder();
            for (var k = 0; k < items.Count; k++)
            {
                chains.Add(items[k].IsGap
                    ? $"{BlackSource(settings.Width, settings.Height, settings.Fps, items[k].GapDuration)}[v{k}]"
                    : $"[0:v]{TrimChain(items[k].Segment!)}[v{k}]");
                labels.Append($"[v{k}]");
            }
            chains.Add($"{labels}concat=n={items.Count}:v=1:a=0[v]");
            chains.Add($"[v]{palette}");
            graph = string.Join(";", chains);
        }

        return $"-i {Quote(project.SourcePath)} -filter_complex \"{graph}\" {Quote(outputPath)}";
    }

    /// <summary>
    /// Flattens track 0's edited timeline into items in timeline order:
    /// segments interleaved with the gaps between them (and before the first
    /// one). There is never a trailing gap by construction.
    /// </summary>
    private static List<TimelineItem> FlattenTimeline(MediaEditProject project) => FlattenTimeline(project.SegmentsOnTrack(0));

    private static List<TimelineItem> FlattenTimeline(IReadOnlyList<TimelineSegment> segments)
    {
        var items = new List<TimelineItem>();
        var pos = TimeSpan.Zero;
        foreach (var seg in segments)
        {
            if (seg.TimelineStart > pos)
                items.Add(new TimelineItem(null, seg.TimelineStart - pos));
            items.Add(new TimelineItem(seg, TimeSpan.Zero));
            pos = seg.TimelineEnd;
        }
        return items;
    }

    /// <summary>
    /// How one extra track composites: opacity of the whole clip (0 invisible,
    /// 100 opaque) and its own audio volume (0 muted, 100 full). Flat for the
    /// whole clip - no fade in/out ramp; cut the clip in pieces for that.
    /// <para>
    /// Use <see cref="Default"/>, never <c>new TrackOverlay()</c>: a record
    /// struct still gets the implicit parameterless constructor every struct
    /// has, which zero-inits every field regardless of the primary
    /// constructor's declared defaults - "new TrackOverlay()" silently gives
    /// Opacity=0/VolumePercent=0 (invisible and muted), not 100/100.
    /// </para>
    /// </summary>
    public readonly record struct TrackOverlay(double Opacity = 100, double VolumePercent = 100)
    {
        public static readonly TrackOverlay Default = new(Opacity: 100, VolumePercent: 100);
    }

    /// <summary>
    /// Builds the export args for a project with more than one video track:
    /// one input per track, each flattened/trimmed/concatenated like
    /// <see cref="BuildVideoExportArgs"/> does for a single track, then
    /// layered back-to-front like real layers - every track is scaled to the
    /// export canvas and stacked FULL FRAME at 0,0, lowest TrackIndex first
    /// (the backdrop), each higher TrackIndex overlaid on top of it in turn
    /// (the timeline control shows the highest index as the TOP lane for
    /// exactly this reason - top of the timeline ends up in front in the
    /// render). <paramref name="overlays"/> gives each extra track's opacity
    /// and its own audio volume; a track with no entry defaults to fully
    /// opaque and audible. No crossfade/blend curve - a hard opacity value for
    /// the whole clip, same as the single-track route handles trims. Falls
    /// back to <see cref="BuildVideoExportArgs"/> when the project only has
    /// one track, so callers can use this unconditionally.
    /// <para>
    /// The track whose segments point at <see cref="MediaEditProject.SourcePath"/>
    /// (the file the project was opened with, wherever the user has since
    /// dragged it) keeps using <see cref="MediaEditProject.AudioTracks"/> for
    /// its own mute/volume, same as the single-track export - that project-level
    /// state belongs to the recording itself, not to a TrackOverlay entry.
    /// </para>
    /// </summary>
    public static string BuildMultiTrackVideoExportArgs(MediaEditProject project, VideoExportSettings settings,
        IReadOnlyDictionary<int, TrackOverlay> overlays, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(overlays);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (project.Kind != MediaKind.Video)
            throw new ArgumentException("Video export requires a Video project.", nameof(project));

        var trackIndices = project.GetTrackIndices();
        if (trackIndices.Count <= 1)
            return BuildVideoExportArgs(project, settings, outputPath);

        var inputs = new StringBuilder();
        var chains = new List<string>();
        var videoLabels = new List<string>();
        var audioLabels = new List<string>();

        for (var i = 0; i < trackIndices.Count; i++)
        {
            var trackIndex = trackIndices[i];
            var segs = project.SegmentsOnTrack(trackIndex);
            inputs.Append($" -i {Quote(segs[0].SourcePath)}");

            var items = FlattenTimeline(segs);
            var vLabel = $"vt{i}";
            AppendChain(chains, items, i, vLabel, video: true, settings);

            var overlay = overlays.TryGetValue(trackIndex, out var o) ? o : TrackOverlay.Default;
            var scaled = $"{vLabel}sc";
            var scaleFilter = $"scale={settings.Width}:{settings.Height}";
            if (overlay.Opacity < 100)
                scaleFilter += $",format=yuva420p,colorchannelmixer=aa={Num(Math.Clamp(overlay.Opacity, 0, 100) / 100.0)}";
            chains.Add($"[{vLabel}]{scaleFilter}[{scaled}]");
            videoLabels.Add(scaled);

            // the track holding the project's own recording keeps the
            // existing AudioTracks mute/volume; every other track mutes via
            // its own TrackOverlay.VolumePercent
            var isProjectRecording = segs[0].SourcePath == project.SourcePath;
            if (isProjectRecording)
            {
                var muted = project.AudioTracks.Count == 0 || project.AudioTracks.All(t => t.IsMuted);
                if (!muted)
                {
                    var aLabel = $"at{i}";
                    AppendChain(chains, items, i, aLabel, video: false, settings);
                    var volume = project.AudioTracks[0].Volume;
                    audioLabels.Add(AppendVolume(chains, aLabel, volume));
                }
            }
            else if (overlay.VolumePercent > 0)
            {
                var aLabel = $"at{i}";
                AppendChain(chains, items, i, aLabel, video: false, settings);
                audioLabels.Add(AppendVolume(chains, aLabel, overlay.VolumePercent / 100.0));
            }
        }

        // composite back to front: videoLabels[0] is the backdrop (bottom UI
        // lane), each later label overlays full-frame on top of it at 0,0
        var compositeLabel = videoLabels[0];
        for (var i = 1; i < videoLabels.Count; i++)
        {
            var nextLabel = i == videoLabels.Count - 1 ? "v" : $"vc{i}";
            chains.Add($"[{compositeLabel}][{videoLabels[i]}]overlay=0:0[{nextLabel}]");
            compositeLabel = nextLabel;
        }

        if (audioLabels.Count > 0)
        {
            var audioList = new StringBuilder();
            foreach (var l in audioLabels)
                audioList.Append($"[{l}]");
            chains.Add(audioLabels.Count == 1
                ? $"{audioList}anull[a]"
                : $"{audioList}amix=inputs={audioLabels.Count}:duration=longest:normalize=0[a]");
        }

        var args = new StringBuilder();
        args.Append(inputs.ToString().TrimStart());
        args.Append($" -filter_complex \"{string.Join(";", chains)}\"");
        args.Append(" -map \"[v]\"");
        args.Append(audioLabels.Count > 0 ? " -map \"[a]\"" : " -an");
        args.Append($" -c:v {settings.VideoCodec}");
        if (settings.BitrateKbps is int kbps)
            args.Append($" -b:v {kbps.ToString(CultureInfo.InvariantCulture)}k");
        if (settings.FastStart)
            args.Append(" -movflags +faststart");
        args.Append($" {Quote(outputPath)}");
        return args.ToString();
    }

    /// <summary>Appends a volume filter after an already-built audio chain when the factor isn't 1.0; returns the label to mix/map.</summary>
    private static string AppendVolume(List<string> chains, string label, double factor)
    {
        if (Math.Abs(factor - 1.0) < 1e-9)
            return label;
        var outLabel = $"{label}v";
        chains.Add($"[{label}]volume={Num(factor)}[{outLabel}]");
        return outLabel;
    }

    private static void AppendChain(List<string> chains, List<TimelineItem> items, int inputIndex, string outLabel,
        bool video, VideoExportSettings settings)
    {
        var sourceRef = video ? $"[{inputIndex}:v]" : $"[{inputIndex}:a:0]";
        if (items.Count == 1)
        {
            var chain = video ? TrimChain(items[0].Segment!) : AtrimChain(items[0].Segment!);
            chains.Add($"{sourceRef}{chain}[{outLabel}]");
            return;
        }

        var labels = new StringBuilder();
        for (var k = 0; k < items.Count; k++)
        {
            var sub = $"{outLabel}s{k}";
            if (items[k].IsGap)
            {
                chains.Add(video
                    ? $"{BlackSource(settings.Width, settings.Height, settings.Fps, items[k].GapDuration)}[{sub}]"
                    : $"{SilenceSource(items[k].GapDuration)}[{sub}]");
            }
            else
            {
                var chain = video ? TrimChain(items[k].Segment!) : AtrimChain(items[k].Segment!);
                chains.Add($"{sourceRef}{chain}[{sub}]");
            }
            labels.Append($"[{sub}]");
        }
        chains.Add(video
            ? $"{labels}concat=n={items.Count}:v=1:a=0[{outLabel}]"
            : $"{labels}concat=n={items.Count}:v=0:a=1[{outLabel}]");
    }

    // synthetic black video for a gap. Size/fps must match the real
    // source (concat requires homogeneous streams).
    private static string BlackSource(int width, int height, int fps, TimeSpan duration)
        => $"color=c=black:s={width.ToString(CultureInfo.InvariantCulture)}x{height.ToString(CultureInfo.InvariantCulture)}:r={fps.ToString(CultureInfo.InvariantCulture)}:d={Sec(duration)}";

    // synthetic silence for a gap (48 kHz stereo, the recording
    // default; atrim bounds the otherwise infinite anullsrc).
    private static string SilenceSource(TimeSpan duration)
        => $"anullsrc=channel_layout=stereo:sample_rate=48000,atrim=0:{Sec(duration)}";

    private static string TrimChain(TimelineSegment seg)
        => $"trim=start={Sec(seg.SourceStart)}:end={Sec(seg.SourceEnd)},setpts=PTS-STARTPTS";

    private static string AtrimChain(TimelineSegment seg)
        => $"atrim=start={Sec(seg.SourceStart)}:end={Sec(seg.SourceEnd)},asetpts=PTS-STARTPTS";

    private static string Sec(TimeSpan t) => Num(t.TotalSeconds);

    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
}
