using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klip.Core.Recording;

/// <summary>What a line on the recorder pipe means.</summary>
public enum RecorderMessageKind
{
    // child -> parent, first line after connecting
    Hello,

    // parent -> child commands (answered with Reply, same Id)
    Prepare,
    Start,
    Pause,
    Resume,
    Stop,
    SetMicrophoneMuted,
    SetSystemAudioMuted,
    SetCursor,
    Exit,

    // child -> parent
    Reply,
    Warning,
    Failed,
    AutoStopped,
    Stats,
    Log,
}

/// <summary>Live numbers the child pushes a few times per second.</summary>
public sealed record RecorderStats(
    TimeSpan Elapsed,
    double DroppedFrameRatio,
    bool IsRecording,
    bool IsPaused);

/// <summary>
/// One message between the app and the recorder process. Flat on purpose: one json object
/// per line, and each kind only fills the fields it needs.
/// </summary>
public sealed class RecorderMessage
{
    public RecorderMessageKind Kind { get; set; }

    /// <summary>Request id; the reply carries the same one. 0 for events.</summary>
    public int Id { get; set; }

    public Mp4RecordingOptions? Options { get; set; }

    /// <summary>Argument of the toggles (mute, cursor).</summary>
    public bool Flag { get; set; }

    /// <summary>Reply: false = the command threw, message in <see cref="Text"/>.</summary>
    public bool Ok { get; set; }

    /// <summary>Warning, failure, log line or error message.</summary>
    public string? Text { get; set; }

    public VideoEncodingPlan? Plan { get; set; }

    public Mp4RecordingResult? Result { get; set; }

    public RecorderStats? Stats { get; set; }

    /// <summary>Hello: pid of the recorder process.</summary>
    public int ProcessId { get; set; }
}

[JsonSourceGenerationOptions(
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RecorderMessage))]
internal sealed partial class RecorderJsonContext : JsonSerializerContext;

/// <summary>
/// Wire format and command line of the recorder process (<c>Klip.exe --recorder</c>).
/// Newline delimited json over a named pipe: json escapes line breaks inside strings, so a
/// line is always one whole message.
/// </summary>
public static class RecorderProtocol
{
    public const string RecorderSwitch = "--recorder";
    public const string PipeSwitch = "--pipe";
    public const string ParentSwitch = "--parent";

    public static string Serialize(RecorderMessage message) =>
        JsonSerializer.Serialize(message, RecorderJsonContext.Default.RecorderMessage);

    /// <summary>null for blank or broken lines (never throws: a bad line is just skipped).</summary>
    public static RecorderMessage? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            return JsonSerializer.Deserialize(line, RecorderJsonContext.Default.RecorderMessage);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Unique per launch so a stale child can never pick up the next session.</summary>
    public static string NewPipeName(int parentProcessId) =>
        $"Klip.Recorder.{parentProcessId}.{Guid.NewGuid():N}";

    public static string BuildArguments(string pipeName, int parentProcessId) =>
        $"{RecorderSwitch} {PipeSwitch} {pipeName} {ParentSwitch} {parentProcessId}";

    /// <summary>Cheap check for Main, before anything else loads.</summary>
    public static bool IsRecorderInvocation(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] == RecorderSwitch;

    /// <summary>
    /// Reads <c>--recorder --pipe name [--parent pid]</c>. The pipe name only takes the
    /// characters <see cref="NewPipeName"/> produces, anything else is refused.
    /// </summary>
    public static bool TryParseArguments(IReadOnlyList<string> args, out string pipeName, out int parentProcessId)
    {
        pipeName = string.Empty;
        parentProcessId = 0;
        if (!IsRecorderInvocation(args))
            return false;

        for (int i = 1; i < args.Count - 1; i++)
        {
            if (args[i] == PipeSwitch)
                pipeName = args[i + 1];
            else if (args[i] == ParentSwitch && int.TryParse(args[i + 1], out var pid) && pid > 0)
                parentProcessId = pid;
        }

        return IsValidPipeName(pipeName);
    }

    private static bool IsValidPipeName(string name)
    {
        if (name.Length is 0 or > 128)
            return false;
        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
                return false;
        }
        return true;
    }
}
