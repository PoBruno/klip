using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Klip.Core.Recording;
using Klip.Interop.Recording;

namespace Klip.App.Services;

/// <summary>
/// <c>Klip.exe --recorder --pipe name --parent pid</c>: the mp4 recorder in its own process.
/// No wpf, no settings, no database, no mutex. D3D, Media Foundation and the driver dlls
/// load here and die with the process, so the tray app goes back to its idle footprint
/// after a recording, and a driver crash costs the recording, not the clipboard history.
/// Talks to <see cref="RemoteMp4Recorder"/> over a named pipe (one json per line).
/// </summary>
internal static class RecorderHost
{
    // pipe never connected (parent gone, blocked): give up
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    // parent gone mid recording: finish the file, but never hang around forever
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan StatsInterval = TimeSpan.FromMilliseconds(250);

    public static int Run(string[] args)
    {
        if (!RecorderProtocol.TryParseArguments(args, out var pipeName, out var parentPid))
            return 2;

        try
        {
            // everything on the pool (mta), the main thread only waits
            return Task.Run(() => RunAsync(pipeName, parentPid)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static async Task<int> RunAsync(string pipeName, int parentPid)
    {
        using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connectCts = new CancellationTokenSource(ConnectTimeout))
        {
            try
            {
                await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return 3;
            }
        }

        await using var session = new Session(pipe, parentPid);
        return await session.RunAsync().ConfigureAwait(false);
    }

    private sealed class Session : IAsyncDisposable
    {
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        private readonly NamedPipeClientStream _pipe;
        private readonly int _parentPid;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Channel<string> _outbox = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true });

        // prepare/start/pause/resume/stop run one at a time, the toggles do not wait
        private readonly SemaphoreSlim _lifecycle = new(1, 1);
        private Mp4Recorder? _recorder;

        public Session(NamedPipeClientStream pipe, int parentPid)
        {
            _pipe = pipe;
            _parentPid = parentPid;
        }

        public async Task<int> RunAsync()
        {
            var writerTask = Task.Run(WriteLoopAsync);
            var listener = new PipeTraceListener(line => Post(new RecorderMessage { Kind = RecorderMessageKind.Log, Text = line }));
            Trace.Listeners.Add(listener);

            Post(new RecorderMessage { Kind = RecorderMessageKind.Hello, ProcessId = Environment.ProcessId });
            Log("prioridade: " + RecorderPriority.RaiseCurrentProcess());

            WatchParent();
            var statsTask = Task.Run(StatsLoopAsync);

            try
            {
                await ReadLoopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("leitura do pipe terminou: " + ex.Message);
            }

            // exit asked, pipe closed or parent dead: a running recording still gets finalized
            _lifetime.Cancel();
            var finish = FinishRecordingAsync();
            if (await Task.WhenAny(finish, Task.Delay(FinishTimeout)).ConfigureAwait(false) != finish)
                Environment.Exit(4);

            Trace.Listeners.Remove(listener);
            _outbox.Writer.TryComplete();
            await Task.WhenAny(writerTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            await statsTask.ConfigureAwait(false);
            return 0;
        }

        private async Task ReadLoopAsync()
        {
            using var reader = new StreamReader(_pipe, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                if (line is null)
                    return; // parent closed the pipe
                var message = RecorderProtocol.TryParse(line);
                if (message is null)
                    continue;
                if (message.Kind == RecorderMessageKind.Exit)
                {
                    Reply(message.Id, ok: true);
                    return;
                }

                Dispatch(message);
            }
        }

        private void Dispatch(RecorderMessage message)
        {
            switch (message.Kind)
            {
                case RecorderMessageKind.SetMicrophoneMuted:
                    EnsureRecorder().SetMicrophoneMuted(message.Flag);
                    Reply(message.Id, ok: true);
                    break;
                case RecorderMessageKind.SetSystemAudioMuted:
                    EnsureRecorder().SetSystemAudioMuted(message.Flag);
                    Reply(message.Id, ok: true);
                    break;
                case RecorderMessageKind.SetCursor:
                    EnsureRecorder().SetCursorCaptureEnabled(message.Flag);
                    Reply(message.Id, ok: true);
                    break;
                case RecorderMessageKind.Prepare or RecorderMessageKind.Start or RecorderMessageKind.Pause
                    or RecorderMessageKind.Resume or RecorderMessageKind.Stop:
                    _ = RunLifecycleAsync(message);
                    break;
            }
        }

        private async Task RunLifecycleAsync(RecorderMessage message)
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                var recorder = EnsureRecorder();
                var reply = new RecorderMessage { Kind = RecorderMessageKind.Reply, Id = message.Id, Ok = true };
                switch (message.Kind)
                {
                    case RecorderMessageKind.Prepare:
                        await recorder.PrepareAsync(RequireOptions(message)).ConfigureAwait(false);
                        reply.Plan = recorder.ActivePlan;
                        break;
                    case RecorderMessageKind.Start:
                        await recorder.StartAsync(RequireOptions(message)).ConfigureAwait(false);
                        reply.Plan = recorder.ActivePlan;
                        break;
                    case RecorderMessageKind.Pause:
                        await recorder.PauseAsync().ConfigureAwait(false);
                        break;
                    case RecorderMessageKind.Resume:
                        await recorder.ResumeAsync().ConfigureAwait(false);
                        break;
                    case RecorderMessageKind.Stop:
                        reply.Result = await recorder.StopAsync().ConfigureAwait(false);
                        break;
                }

                Post(reply);
            }
            catch (Exception ex)
            {
                Log($"{message.Kind} falhou: {ex}");
                Post(new RecorderMessage
                {
                    Kind = RecorderMessageKind.Reply,
                    Id = message.Id,
                    Ok = false,
                    Text = ex.Message,
                });
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        private static Mp4RecordingOptions RequireOptions(RecorderMessage message) =>
            message.Options ?? throw new InvalidOperationException("Opcoes de gravacao ausentes.");

        private Mp4Recorder EnsureRecorder()
        {
            if (_recorder is not null)
                return _recorder;

            var recorder = new Mp4Recorder { HighPriority = true };
            recorder.Warning += text => Post(new RecorderMessage { Kind = RecorderMessageKind.Warning, Text = text });
            recorder.Failed += failure => Post(new RecorderMessage
            {
                Kind = RecorderMessageKind.Failed,
                Text = failure.Message,
            });
            recorder.AutoStopped += result => Post(new RecorderMessage
            {
                Kind = RecorderMessageKind.AutoStopped,
                Result = result,
            });
            _recorder = recorder;
            return recorder;
        }

        private async Task FinishRecordingAsync()
        {
            // waits for a stop already running, then tears down (aborting if nobody stopped)
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_recorder is { } recorder)
                {
                    if (recorder.IsRecording)
                    {
                        Log("pipe fechado com gravacao ativa: finalizando o arquivo");
                        try
                        {
                            await recorder.StopAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log("stop final falhou: " + ex.Message);
                        }
                    }

                    await recorder.DisposeAsync().ConfigureAwait(false);
                    _recorder = null;
                }
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        private void WatchParent()
        {
            if (_parentPid <= 0)
                return;
            Process parent;
            try
            {
                parent = Process.GetProcessById(_parentPid);
            }
            catch (ArgumentException)
            {
                _lifetime.Cancel(); // already gone
                return;
            }

            // the pipe breaks too when the parent dies; this covers a parent that hangs
            // with the pipe still open in some zombie state
            _ = Task.Run(async () =>
            {
                using (parent)
                {
                    try
                    {
                        await parent.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
                        Log("processo principal terminou");
                        _lifetime.Cancel();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            });
        }

        private async Task StatsLoopAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    await Task.Delay(StatsInterval, _lifetime.Token).ConfigureAwait(false);
                    if (_recorder is not { IsRecording: true } recorder)
                        continue;
                    Post(new RecorderMessage
                    {
                        Kind = RecorderMessageKind.Stats,
                        Plan = recorder.ActivePlan,
                        Stats = new RecorderStats(
                            recorder.Elapsed,
                            recorder.DroppedFrameRatio,
                            recorder.IsRecording,
                            recorder.IsPaused),
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                using var writer = new StreamWriter(_pipe, Utf8, bufferSize: 4096, leaveOpen: true) { NewLine = "\n" };
                var reader = _outbox.Reader;
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    // drain what is queued, then one flush for the whole batch
                    while (reader.TryRead(out var line))
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
                // parent went away; the read side notices too
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Post(RecorderMessage message) =>
            _outbox.Writer.TryWrite(RecorderProtocol.Serialize(message));

        private void Reply(int id, bool ok) =>
            Post(new RecorderMessage { Kind = RecorderMessageKind.Reply, Id = id, Ok = ok });

        private void Log(string text) =>
            Post(new RecorderMessage { Kind = RecorderMessageKind.Log, Text = text });

        public async ValueTask DisposeAsync()
        {
            if (_recorder is { } recorder)
            {
                try
                {
                    await recorder.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            _lifetime.Dispose();
            _lifecycle.Dispose();
        }
    }

    /// <summary>Trace lines of the recorder go to the app log through the pipe.</summary>
    private sealed class PipeTraceListener(Action<string> sink) : TraceListener
    {
        private readonly StringBuilder _line = new();

        public override void Write(string? message)
        {
            lock (_line)
                _line.Append(message);
        }

        public override void WriteLine(string? message)
        {
            string text;
            lock (_line)
            {
                _line.Append(message);
                text = _line.ToString();
                _line.Clear();
            }
            sink(text);
        }
    }
}
