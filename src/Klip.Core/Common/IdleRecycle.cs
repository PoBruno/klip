using System.Globalization;

namespace Klip.Core.Common;

/// <summary>
/// When to swap the running process for a fresh one, and the arguments the two
/// instances use to hand over. A process that sat untouched for hours (or through a
/// long sleep) has most of its pages out of RAM, and the first hotkey pays to bring
/// them back; a new process starts resident. Only ever proactive and while nothing is
/// going on, never as a reaction to a hang (by then the user is already waiting).
/// </summary>
public static class IdleRecycle
{
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromHours(4);
    public static readonly TimeSpan SleepThreshold = TimeSpan.FromHours(1);

    // a process that just started is as fresh as it gets, and this keeps a bad clock
    // or a resume storm from turning into a restart loop
    public static readonly TimeSpan MinUptime = TimeSpan.FromHours(1);

    public const string RecycledArg = "--recycled";

    /// <summary>
    /// <paramref name="away"/> is the time without any interaction for an idle check,
    /// or how long the machine slept for a resume check.
    /// </summary>
    public static bool ShouldRecycle(bool enabled, bool afterResume, TimeSpan uptime, TimeSpan away, bool busy)
    {
        if (!enabled || busy || uptime < MinUptime)
            return false;
        return away >= (afterResume ? SleepThreshold : IdleThreshold);
    }

    /// <summary>Arguments for the new instance: who to wait for and the last clipboard state seen.</summary>
    public static string[] BuildArgs(int oldPid, uint clipboardSequence, bool verbose)
    {
        var args = new List<string>
        {
            "--minimized",
            RecycledArg,
            oldPid.ToString(CultureInfo.InvariantCulture),
            clipboardSequence.ToString(CultureInfo.InvariantCulture),
        };
        if (verbose)
            args.Add("--verbose");
        return [.. args];
    }

    public static bool TryParseArgs(IReadOnlyList<string> args, out int oldPid, out uint clipboardSequence)
    {
        oldPid = 0;
        clipboardSequence = 0;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] != RecycledArg)
                continue;
            return i + 2 < args.Count
                && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out oldPid)
                && oldPid > 0
                && uint.TryParse(args[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out clipboardSequence);
        }
        return false;
    }
}
