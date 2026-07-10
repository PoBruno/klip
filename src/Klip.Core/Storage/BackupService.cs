using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Klip.Core.Common;

namespace Klip.Core.Storage;

/// <summary>
/// Exports and imports the history: a .zip with the items as JSON
/// (manifest.json) plus the media files they point to. Import upserts and
/// dedupes by hash, so nothing gets duplicated. Pure (no WPF), testable.
/// </summary>
public sealed class BackupService(ClipboardItemRepository repository, MediaStore mediaStore)
{
    private const string ManifestEntry = "manifest.json";
    private const string MediaPrefix = "media/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record ExportResult(int Items, int MediaFiles);
    public sealed record ImportResult(int Imported, int SkippedDuplicates, int MissingMedia);

    /// <summary>Writes a .zip with the whole history to the given path.</summary>
    public ExportResult Export(string zipPath)
    {
        var items = repository.GetAllForExport();
        var mediaCount = 0;

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        // manifest with the items
        var manifest = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
        using (var stream = manifest.Open())
        {
            var dtos = items.Select(BackupItemDto.FromItem).ToList();
            JsonSerializer.Serialize(stream, dtos, JsonOptions);
        }

        // media files pointed to by the items (paths are relative to DataDir)
        var mediaPaths = items
            .SelectMany(i => new[] { i.FilePath, i.ThumbPath })
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct();

        foreach (var rel in mediaPaths)
        {
            var abs = mediaStore.ToAbsolute(rel);
            if (File.Exists(abs))
            {
                zip.CreateEntryFromFile(abs, MediaPrefix + rel.Replace('\\', '/'), CompressionLevel.Optimal);
                mediaCount++;
            }
        }

        return new ExportResult(items.Count, mediaCount);
    }

    /// <summary>Imports a .zip, merging by hash so nothing gets duplicated.</summary>
    public ImportResult Import(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var manifest = zip.GetEntry(ManifestEntry)
            ?? throw new InvalidDataException("Pacote inválido: manifest.json ausente.");

        List<BackupItemDto> dtos;
        using (var stream = manifest.Open())
        {
            dtos = JsonSerializer.Deserialize<List<BackupItemDto>>(stream, JsonOptions) ?? [];
        }

        var imported = 0;
        var skipped = 0;
        var missingMedia = 0;

        foreach (var dto in dtos)
        {
            if (repository.ExistsByHash(dto.ContentHash))
            {
                skipped++;
                continue;
            }

            // pull the media out before inserting, keeping the relative paths
            foreach (var rel in new[] { dto.FilePath, dto.ThumbPath })
            {
                if (rel is null)
                    continue;
                var entry = zip.GetEntry(MediaPrefix + rel.Replace('\\', '/'));
                if (entry is null)
                {
                    if (rel == dto.FilePath)
                        missingMedia++;
                    continue;
                }
                var dest = mediaStore.ToAbsolute(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (!File.Exists(dest))
                    entry.ExtractToFile(dest, overwrite: false);
            }

            repository.Upsert(dto.ToItem());
            imported++;
        }

        return new ImportResult(imported, skipped, missingMedia);
    }

    /// <summary>Serialization DTO; keeps the package format apart from the internal model.</summary>
    private sealed record BackupItemDto
    {
        public required string Type { get; init; }
        public long CreatedAt { get; init; }
        public long LastCopiedAt { get; init; }
        public string? SourceApp { get; init; }
        public string? SourceTitle { get; init; }
        public string Origin { get; init; } = "clipboard";
        public bool Pinned { get; init; }
        public bool Favorite { get; init; }
        public required string ContentHash { get; init; }
        public long ByteSize { get; init; }
        public string? TextContent { get; init; }
        public string? HtmlContent { get; init; }
        public string? RtfContent { get; init; }
        public string? FilePath { get; init; }
        public string? ThumbPath { get; init; }
        public string? FilesJson { get; init; }
        public string? OcrText { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }

        public static BackupItemDto FromItem(ClipboardItem i) => new()
        {
            Type = i.Type.ToString(),
            CreatedAt = i.CreatedAt.ToUnixTimeMilliseconds(),
            LastCopiedAt = i.LastCopiedAt.ToUnixTimeMilliseconds(),
            SourceApp = i.SourceApp,
            SourceTitle = i.SourceTitle,
            Origin = i.Origin.ToString(),
            Pinned = i.Pinned,
            Favorite = i.Favorite,
            ContentHash = i.ContentHash,
            ByteSize = i.ByteSize,
            TextContent = i.TextContent,
            HtmlContent = i.HtmlContent,
            RtfContent = i.RtfContent,
            FilePath = i.FilePath,
            ThumbPath = i.ThumbPath,
            FilesJson = i.FilesJson,
            OcrText = i.OcrText,
            Width = i.Width,
            Height = i.Height,
        };

        public ClipboardItem ToItem() => new()
        {
            Type = Enum.Parse<ClipboardItemType>(Type, ignoreCase: true),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt),
            LastCopiedAt = DateTimeOffset.FromUnixTimeMilliseconds(LastCopiedAt),
            SourceApp = SourceApp,
            SourceTitle = SourceTitle,
            Origin = Enum.Parse<ClipboardItemOrigin>(Origin, ignoreCase: true),
            Pinned = Pinned,
            Favorite = Favorite,
            ContentHash = ContentHash,
            ByteSize = ByteSize,
            TextContent = TextContent,
            HtmlContent = HtmlContent,
            RtfContent = RtfContent,
            FilePath = FilePath,
            ThumbPath = ThumbPath,
            FilesJson = FilesJson,
            OcrText = OcrText,
            Width = Width,
            Height = Height,
        };
    }
}
