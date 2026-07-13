namespace Klip.Core.Recording;

/// <summary>Caminhos da gravacao de tela (RF-F3.06).</summary>
public static class RecordingPaths
{
    /// <summary>
    /// Pasta de gravacoes: a configurada ou Videos\Gravacoes de Tela
    /// (paridade com o Snipping Tool, que usa Videos\Screen Recordings).
    /// A pasta e criada sob demanda pelo chamador, nao aqui.
    /// </summary>
    public static string Resolve(string? configuredFolder) =>
        string.IsNullOrWhiteSpace(configuredFolder)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "Gravacoes de Tela")
            : configuredFolder;

    /// <summary>
    /// Nome "Gravacao YYYY-MM-DD HHMMSS.ext" (RF-F3.06), com sufixo " (n)"
    /// quando ja existe arquivo homonimo na pasta.
    /// </summary>
    public static string BuildOutputPath(string folder, DateTime timestamp, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var baseName = $"Gravacao {timestamp:yyyy-MM-dd HHmmss}";
        var path = Path.Combine(folder, $"{baseName}.{extension}");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName} ({i}).{extension}");
        return path;
    }
}
