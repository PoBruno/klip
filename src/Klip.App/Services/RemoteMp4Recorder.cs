using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Klip.Core.Recording;

namespace Klip.App.Services;

/// <summary>
/// <see cref="IMp4Recorder"/> that runs the recording in a child process
/// (<c>Klip.exe --recorder</c>, see <see cref="RecorderHost"/>). The child starts on the
/// first Prepare/Start. If it cannot start, everything falls back to an in process
/// recorder from <c>localFactory</c> and the log says so. If it dies mid recording the
/// failure goes out through <see cref="Failed"/> and the fMP4 stays on disk.
/// </summary>
public sealed class RemoteMp4Recorder : IMp4Recorder
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(20);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<IMp4Recorder> _localFactory;
    private readonly Action<string> _log;
    private readonly string? _exePath;

    private readonly ConcurrentDictionary<int, TaskCompletionSource<RecorderMessage>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private int _nextId;

    private IMp4Recorder? _local;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private Process? _process;
    private Task? _readTask;
    private bool _connected;
    private volatile bool _dead;
    private volatile bool _disposed;
    private bool _exitSent;

    // what the session looks like from here, kept up to date by replies and stats
    private volatile bool _isRecording;
    private volatile bool _isPaused;
    private bool _sessionStarted;
    private volatile VideoEncodingPlan? _plan;
    private double _dropRatio;
    private Mp4RecordingOptions? _options;

    // elapsed is interpolated between stats lines so the tray timer does not stutter
    private readonly object _clockLock = new();
    private TimeSpan _elapsedBase;
    private long _elapsedStamp;
    private bool _clockRunning;

    private volatile bool _micMuted;
    private volatile bool _systemMuted;
    private volatile bool _cursorOn = true;

    public RemoteMp4Recorder(Func<IMp4Recorder> localFactory, Action<string> log, string? exePath = null)
    {
        _localFactory = localFactory;
        _log = log;
        _exePath = exePath;
    }

    /// <summary>True when the work ended up in the child process (false = in process fallback).</summary>
    public bool IsRemote => _local is null && _connected;

    /// <inheritdoc />
    public event Action<RecordingFailure>? Failed;

    /// <inheritdoc />
    public event Action<Mp4RecordingResult>? AutoStopped;

    /// <inheritdoc />
    public event Action<string>? Warning;

    /// <inheritdoc />
    public bool IsRecording => _local?.IsRecording ?? _isRecording;

    /// <inheritdoc />
    public bool IsPaused => _local?.IsPaused ?? _isPaused;

    /// <inheritdoc />
    public TimeSpan Elapsed
    {
        get
        {
            if (_local is { } local)
                return local.Elapsed;
            lock (_clockLock)
            {
                if (!_clockRunning)
                    return _elapsedBase;
                return _elapsedBase + Stopwatch.GetElapsedTime(_elapsedStamp);
            }
        }
    }

    /// <inheritdoc />
    public VideoEncodingPlan? ActivePlan => _local?.ActivePlan ?? _plan;

    /// <inheritdoc />
    public double DroppedFrameRatio => _local?.DroppedFrameRatio ?? Volatile.Read(ref _dropRatio);

    /// <inheritdoc />
    public bool IsMicrophoneMuted => _local?.IsMicrophoneMuted ?? _micMuted;

    /// <inheritdoc />
    public bool IsSystemAudioMuted => _local?.IsSystemAudioMuted ?? _systemMuted;

    /// <inheritdoc />
    public bool IsCursorCaptureEnabled => _local?.IsCursorCaptureEnabled ?? _cursorOn;

    /// <inheritdoc />
    public void SetMicrophoneMuted(bool muted)
    {
        _micMuted = muted;
        if (_local is { } local)
            local.SetMicrophoneMuted(muted);
        else
            SendToggle(RecorderMessageKind.SetMicrophoneMuted, muted);
    }

    /// <inheritdoc />
    public void SetSystemAudioMuted(bool muted)
    {
        _systemMuted = muted;
        if (_local is { } local)
            local.SetSystemAudioMuted(muted);
        else
            SendToggle(RecorderMessageKind.SetSystemAudioMuted, muted);
    }

    /// <inheritdoc />
    public void SetCursorCaptureEnabled(bool enabled)
    {
        _cursorOn = enabled;
        if (_local is { } local)
            local.SetCursorCaptureEnabled(enabled);
        else
            SendToggle(RecorderMessageKind.SetCursor, enabled);
    }

    /// <inheritdoc />
    public async Task PrepareAsync(Mp4RecordingOptions options, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _options = options;
        _cursorOn = options.CaptureCursor;

        if (_local is null && !await TryConnectAsync(ct).ConfigureAwait(false))
            UseLocalFallback("recorder process did not start");

        if (_local is { } local)
        {
            await local.PrepareAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var reply = await RequestAsync(
            new RecorderMessage { Kind = RecorderMessageKind.Prepare, Options = options },
            CommandTimeout, ct).ConfigureAwait(false);
        _plan = reply.Plan;
        _sessionStarted = true;
        ApplyInitialToggles(options);
    }

    /// <inheritdoc />
    public async Task StartAsync(Mp4RecordingOptions options, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_local is { } early)
        {
            await early.StartAsync(options, ct).ConfigureAwait(false);
            return;
        }

        if (!_sessionStarted)
        {
            _options = options;
            _cursorOn = options.CaptureCursor;
        }

        // prepared child died before the start: nothing was recorded yet, so record here
        if (_dead && _local is null)
            UseLocalFallback("recorder process died before the start");
        else if (!_connected && !await TryConnectAsync(ct).ConfigureAwait(false))
            UseLocalFallback("recorder process did not start");

        if (_local is { } local)
        {
            await local.StartAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var reply = await RequestAsync(
            new RecorderMessage { Kind = RecorderMessageKind.Start, Options = options },
            CommandTimeout, ct).ConfigureAwait(false);
        _plan = reply.Plan ?? _plan;
        if (!_sessionStarted)
            ApplyInitialToggles(options);
        _sessionStarted = true;
        _isPaused = false;
        _isRecording = true;
        SetClock(TimeSpan.Zero, running: true);
    }

    /// <inheritdoc />
    public async Task PauseAsync()
    {
        if (_local is { } local)
        {
            await local.PauseAsync().ConfigureAwait(false);
            return;
        }

        await RequestAsync(new RecorderMessage { Kind = RecorderMessageKind.Pause }, CommandTimeout, default)
            .ConfigureAwait(false);
        _isPaused = true;
        SetClock(Elapsed, running: false);
    }

    /// <inheritdoc />
    public async Task ResumeAsync()
    {
        if (_local is { } local)
        {
            await local.ResumeAsync().ConfigureAwait(false);
            return;
        }

        await RequestAsync(new RecorderMessage { Kind = RecorderMessageKind.Resume }, CommandTimeout, default)
            .ConfigureAwait(false);
        _isPaused = false;
        SetClock(Elapsed, running: true);
    }

    /// <inheritdoc />
    public async Task<Mp4RecordingResult> StopAsync()
    {
        if (_local is { } local)
            return await local.StopAsync().ConfigureAwait(false);

        if (_dead)
            return ResultFromDeadChild();

        try
        {
            var reply = await RequestAsync(
                new RecorderMessage { Kind = RecorderMessageKind.Stop }, StopTimeout, default).ConfigureAwait(false);
            var result = reply.Result ?? throw new InvalidOperationException("O gravador nao devolveu o resultado.");
            _isRecording = false;
            _isPaused = false;
            SetClock(result.Duration, running: false);
            return result;
        }
        catch (IOException) when (_dead)
        {
            return ResultFromDeadChild();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_local is { } local)
        {
            await local.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await ShutdownChildAsync().ConfigureAwait(false);
        _writeGate.Dispose();
        _connectGate.Dispose();
    }

    // ----- child process -----

    private async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connected)
                return true;
            if (_dead)
                return false;

            var pipeName = RecorderProtocol.NewPipeName(Environment.ProcessId);
            var pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _pipe = pipe;

            try
            {
                var (file, arguments) = ResolveLaunch(pipeName);
                var process = Process.Start(new ProcessStartInfo(file, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }) ?? throw new InvalidOperationException("Process.Start returned null");
                _process = process;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectTimeout);
                var exited = process.WaitForExitAsync(timeout.Token);
                var connected = pipe.WaitForConnectionAsync(timeout.Token);
                if (await Task.WhenAny(connected, exited).ConfigureAwait(false) != connected)
                    throw new IOException("recorder process exited before connecting");
                await connected.ConfigureAwait(false);

                _writer = new StreamWriter(pipe, Utf8, 4096, leaveOpen: true) { NewLine = "\n" };
                var hello = new TaskCompletionSource<RecorderMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[0] = hello;
                _readTask = Task.Run(() => ReadLoopAsync(pipe));

                var helloLine = await hello.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                _log($"recorder process up (pid {helloLine.ProcessId})");
                _connected = true;
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await ShutdownChildAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                _log("recorder process failed to start: " + ex.Message);
                await ShutdownChildAsync().ConfigureAwait(false);
                _dead = false;
                return false;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private (string File, string Arguments) ResolveLaunch(string pipeName)
    {
        var arguments = RecorderProtocol.BuildArguments(pipeName, Environment.ProcessId);
        var exe = _exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("no process path");

        // started through "dotnet Klip.dll": the host needs the dll as first argument
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "Klip.dll");
            arguments = $"\"{dll}\" {arguments}";
        }

        return (exe, arguments);
    }

    private void UseLocalFallback(string reason)
    {
        _log($"recording in process instead ({reason})");
        var local = _localFactory();
        local.Warning += text => Warning?.Invoke(text);
        local.Failed += failure => Failed?.Invoke(failure);
        local.AutoStopped += result => AutoStopped?.Invoke(result);
        local.SetMicrophoneMuted(_micMuted);
        local.SetSystemAudioMuted(_systemMuted);
        local.SetCursorCaptureEnabled(_cursorOn);
        _local = local;
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (RecorderProtocol.TryParse(line) is { } message)
                    Handle(message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        OnChildGone();
    }

    private void Handle(RecorderMessage message)
    {
        switch (message.Kind)
        {
            case RecorderMessageKind.Hello:
                if (_pending.TryRemove(0, out var hello))
                    hello.TrySetResult(message);
                break;
            case RecorderMessageKind.Reply:
                if (_pending.TryRemove(message.Id, out var waiter))
                {
                    if (message.Ok)
                        waiter.TrySetResult(message);
                    else
                        waiter.TrySetException(new InvalidOperationException(message.Text ?? "O gravador falhou."));
                }
                break;
            case RecorderMessageKind.Warning:
                if (message.Text is { } warning)
                    Warning?.Invoke(warning);
                break;
            case RecorderMessageKind.Failed:
                _isRecording = false;
                SetClock(Elapsed, running: false);
                Failed?.Invoke(new RecordingFailure(message.Text ?? "A gravacao falhou."));
                break;
            case RecorderMessageKind.AutoStopped:
                if (message.Result is { } result)
                {
                    _isRecording = false;
                    SetClock(result.Duration, running: false);
                    AutoStopped?.Invoke(result);
                }
                break;
            case RecorderMessageKind.Stats:
                ApplyStats(message);
                break;
            case RecorderMessageKind.Log:
                if (message.Text is { } text)
                    _log("recorder: " + text);
                break;
        }
    }

    private void ApplyStats(RecorderMessage message)
    {
        if (message.Plan is { } plan)
            _plan = plan;
        if (message.Stats is not { } stats)
            return;

        Volatile.Write(ref _dropRatio, stats.DroppedFrameRatio);

        // a line produced just before a pause/resume would undo it, so only trust the
        // clock when both sides agree on the paused state
        if (stats.IsRecording && stats.IsPaused == _isPaused && _isRecording)
            SetClock(stats.Elapsed, running: !stats.IsPaused);
    }

    private void OnChildGone()
    {
        var expected = _disposed || _exitSent;
        _dead = true;

        var gone = new IOException("O processo de gravacao terminou.");
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var waiter))
                waiter.TrySetException(gone);
        }

        if (!expected && _isRecording)
        {
            _isRecording = false;
            SetClock(Elapsed, running: false);
            _log("recorder process died while recording");
            Failed?.Invoke(new RecordingFailure("O processo de gravacao terminou inesperadamente."));
        }
    }

    private RecorderMessage Stamp(RecorderMessage message, out TaskCompletionSource<RecorderMessage> waiter)
    {
        message.Id = Interlocked.Increment(ref _nextId);
        waiter = new TaskCompletionSource<RecorderMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[message.Id] = waiter;
        return message;
    }

    private async Task<RecorderMessage> RequestAsync(RecorderMessage message, TimeSpan timeout, CancellationToken ct)
    {
        if (_dead)
            throw new IOException("O processo de gravacao terminou.");

        Stamp(message, out var waiter);
        try
        {
            await WriteAsync(message).ConfigureAwait(false);
            return await waiter.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(message.Id, out _);
        }
    }

    private async Task WriteAsync(RecorderMessage message)
    {
        var writer = _writer ?? throw new IOException("pipe not open");
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(RecorderProtocol.Serialize(message)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void SendToggle(RecorderMessageKind kind, bool flag)
    {
        if (!_connected || _dead || _disposed)
            return; // applied at the start (ApplyInitialToggles)

        // toggles come from the ui thread: never wait on the pipe here
        _ = Task.Run(async () =>
        {
            try
            {
                await WriteAsync(new RecorderMessage { Kind = kind, Flag = flag }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // child gone: OnChildGone reports it
            }
        });
    }

    private void ApplyInitialToggles(Mp4RecordingOptions options)
    {
        if (_micMuted)
            SendToggle(RecorderMessageKind.SetMicrophoneMuted, true);
        if (_systemMuted)
            SendToggle(RecorderMessageKind.SetSystemAudioMuted, true);
        if (_cursorOn != options.CaptureCursor)
            SendToggle(RecorderMessageKind.SetCursor, _cursorOn);
    }

    private void SetClock(TimeSpan elapsed, bool running)
    {
        lock (_clockLock)
        {
            _elapsedBase = elapsed;
            _elapsedStamp = Stopwatch.GetTimestamp();
            _clockRunning = running;
        }
    }

    // the child is gone, but with fMP4 what it wrote is still a playable file
    private Mp4RecordingResult ResultFromDeadChild()
    {
        var path = _options?.OutputPath;
        if (path is null || !File.Exists(path))
            throw new IOException("O processo de gravacao terminou e nao deixou arquivo.");

        var plan = _plan;
        return new Mp4RecordingResult(
            path, Elapsed, plan?.Output.Width ?? 0, plan?.Output.Height ?? 0, new FileInfo(path).Length);
    }

    private async Task ShutdownChildAsync()
    {
        var process = _process;
        var pipe = _pipe;
        _process = null;
        _pipe = null;

        if (process is not null)
        {
            try
            {
                if (_connected && !_dead && _writer is not null)
                {
                    _exitSent = true;
                    await WriteAsync(new RecorderMessage { Kind = RecorderMessageKind.Exit }).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // pipe already broken: the child sees that too and leaves
            }

            try
            {
                using var cts = new CancellationTokenSource(ExitTimeout);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log("recorder process did not exit, killing it");
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }
            }
            catch (Exception)
            {
            }

            process.Dispose();
        }

        try
        {
            pipe?.Dispose();
        }
        catch (Exception)
        {
        }

        if (_readTask is { } read)
        {
            await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            _readTask = null;
        }

        _connected = false;
        _writer = null;
    }
}
