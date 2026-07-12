using Klip.Core.Media.Editing;

namespace Klip.App.Services;

/// <summary>
/// Estado de uma sessao de edicao de midia: o projeto imutavel corrente mais
/// os ajustes de exportacao GIF (FPS alvo e escala, RF-F5.08) com undo/redo
/// por snapshot (RF-F5.10) - como o projeto e imutavel, cada snapshot e so
/// uma referencia + dois ints.
/// </summary>
public sealed class MediaEditorSession
{
    private readonly record struct Snapshot(MediaEditProject Project, int? GifTargetFps, int GifScalePercent);

    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();

    public MediaEditProject Project { get; private set; }

    /// <summary>FPS alvo da reducao GIF (RF-F5.08); nulo = sem reducao.</summary>
    public int? GifTargetFps { get; private set; }

    /// <summary>Escala aplicada na exportacao GIF (RF-F5.08); 100 = original.</summary>
    public int GifScalePercent { get; private set; } = 100;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Disparado a cada operacao aplicada, undo ou redo.</summary>
    public event Action? Changed;

    public MediaEditorSession(MediaEditProject project)
    {
        Project = project;
    }

    /// <summary>
    /// Aplica um novo projeto (resultado de SplitAt/RemoveSegment/etc.).
    /// Operacoes no-op (mesma instancia) nao geram snapshot.
    /// </summary>
    public void Apply(MediaEditProject newProject)
    {
        ArgumentNullException.ThrowIfNull(newProject);
        if (ReferenceEquals(newProject, Project))
            return;
        PushUndo();
        Project = newProject;
        Changed?.Invoke();
    }

    public void SetGifTargetFps(int? fps)
    {
        if (fps == GifTargetFps)
            return;
        PushUndo();
        GifTargetFps = fps;
        Changed?.Invoke();
    }

    public void SetGifScalePercent(int percent)
    {
        if (percent == GifScalePercent)
            return;
        PushUndo();
        GifScalePercent = percent;
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            return;
        _redo.Push(new Snapshot(Project, GifTargetFps, GifScalePercent));
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            return;
        _undo.Push(new Snapshot(Project, GifTargetFps, GifScalePercent));
        Restore(_redo.Pop());
    }

    private void PushUndo()
    {
        _undo.Push(new Snapshot(Project, GifTargetFps, GifScalePercent));
        _redo.Clear();
    }

    private void Restore(Snapshot snapshot)
    {
        Project = snapshot.Project;
        GifTargetFps = snapshot.GifTargetFps;
        GifScalePercent = snapshot.GifScalePercent;
        Changed?.Invoke();
    }
}
