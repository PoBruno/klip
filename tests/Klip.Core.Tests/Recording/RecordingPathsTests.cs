using Klip.Core.Recording;

namespace Klip.Core.Tests.Recording;

/// <summary>RF-F3.06: nomeacao e resolucao da pasta de gravacoes.</summary>
public class RecordingPathsTests
{
    [Fact]
    public void Resolve_UsesConfiguredFolderWhenSet() =>
        Assert.Equal(@"D:\Clips", RecordingPaths.Resolve(@"D:\Clips"));

    [Fact]
    public void Resolve_DefaultsToVideosSubfolder()
    {
        var result = RecordingPaths.Resolve(null);
        Assert.EndsWith("Gravacoes de Tela", result);
    }

    [Fact]
    public void BuildOutputPath_FollowsNamingPattern()
    {
        var folder = Path.Combine(Path.GetTempPath(), "klip-tests-" + Guid.NewGuid().ToString("N"));
        var ts = new DateTime(2026, 7, 11, 14, 30, 25);

        var path = RecordingPaths.BuildOutputPath(folder, ts, "gif");

        Assert.Equal(Path.Combine(folder, "Gravacao 2026-07-11 143025.gif"), path);
    }

    [Fact]
    public void BuildOutputPath_AppendsSuffixOnCollision()
    {
        var folder = Path.Combine(Path.GetTempPath(), "klip-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var ts = new DateTime(2026, 7, 11, 14, 30, 25);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "Gravacao 2026-07-11 143025.mp4"), []);

            var path = RecordingPaths.BuildOutputPath(folder, ts, "mp4");

            Assert.Equal(Path.Combine(folder, "Gravacao 2026-07-11 143025 (2).mp4"), path);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
