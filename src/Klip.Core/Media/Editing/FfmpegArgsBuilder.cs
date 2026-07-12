using System.Globalization;
using System.Text;

namespace Klip.Core.Media.Editing;

/// <summary>
/// RF-F5.11: pure, deterministic translation of a <see cref="MediaEditProject"/>
/// into FFmpeg command-line arguments (the same input always yields the exact
/// same string, so exports are golden-string testable). No process is started
/// here; the export service owns the ffmpeg.exe lifecycle (RF-F5.12/RF-F5.14).
///
/// Filter-graph shape (video): the edited timeline is flattened into ITEMS in
/// timeline order - one trim/setpts chain per segment plus one synthetic black
/// source per gap (RF-F5.20: "color=c=black:s=WxH:r=FPS:d=SEC"), concat
/// (n=K:v=1:a=0), plus one atrim/asetpts chain per NON-MUTED audio track per
/// segment with the track volume applied (omitted when exactly 1.0) and one
/// "anullsrc" silence chain per gap, concat per track, and amix (normalize=0,
/// to honor user volumes) when more than one audible track. All tracks muted
/// (or none): video-only export with "-an" and video-only gap filler.
/// Gap filler dimensions/fps come from the settings and must match the source
/// (see <see cref="VideoExportSettings.Width"/>).
///
/// GIF route (D-F5.4, MP4 source): single-pass palette - the segment chain is
/// followed by fps[,scale] then split/palettegen/paletteuse in one filter
/// graph. Chosen over two-pass (two commands + palette temp file) because it
/// keeps the builder a single pure string and quality is equivalent for
/// screencast content; this is why the palette path parameter was dropped
/// from the draft contract. Gaps become black filler at the target GIF fps.
/// </summary>
public static class FfmpegArgsBuilder
{
    /// <summary>One flattened timeline item: a segment or a gap (RF-F5.20).</summary>
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

        // Video: trim/setpts per segment, black filler per gap (RF-F5.20),
        // concat when more than one item (RF-F5.11)
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
        // silence (anullsrc) so audio and video stay in sync (RF-F5.20)
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
    /// become black filler at the target GIF framerate (RF-F5.20).
    /// </summary>
    public static string BuildGifFromVideoArgs(MediaEditProject project, GifFromVideoSettings settings, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (project.Kind != MediaKind.Video)
            throw new ArgumentException("The MP4-to-GIF route requires a Video project (D-F5.4).", nameof(project));
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
    /// Flattens the edited timeline into items in timeline order: segments
    /// interleaved with the gaps between them (and before the first one).
    /// There is never a trailing gap by construction (RF-F5.17).
    /// </summary>
    private static List<TimelineItem> FlattenTimeline(MediaEditProject project)
    {
        var items = new List<TimelineItem>();
        var pos = TimeSpan.Zero;
        foreach (var seg in project.Segments)
        {
            if (seg.TimelineStart > pos)
                items.Add(new TimelineItem(null, seg.TimelineStart - pos));
            items.Add(new TimelineItem(seg, TimeSpan.Zero));
            pos = seg.TimelineEnd;
        }
        return items;
    }

    // RF-F5.20: synthetic black video for a gap. Size/fps must match the real
    // source (concat requires homogeneous streams).
    private static string BlackSource(int width, int height, int fps, TimeSpan duration)
        => $"color=c=black:s={width.ToString(CultureInfo.InvariantCulture)}x{height.ToString(CultureInfo.InvariantCulture)}:r={fps.ToString(CultureInfo.InvariantCulture)}:d={Sec(duration)}";

    // RF-F5.20: synthetic silence for a gap (48 kHz stereo, the recording
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
