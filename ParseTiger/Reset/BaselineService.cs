using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParseTiger.Execution;

namespace ParseTiger.Reset;

public sealed record ProjectBaselineInfo(
    string ProjectPath,
    string ProjectKind,
    DateTimeOffset CreatedUtc,
    int FileCount);

/// <summary>
/// Persists exact project bytes outside the source tree and reconstructs them
/// as a full-project backup for deterministic restore.
/// </summary>
public sealed class BaselineService
{
    private const string ManifestFileName = "manifest.json";
    private readonly string _baselineRoot;

    public BaselineService(string? storageRoot = null)
    {
        _baselineRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ParseTiger",
            "Baselines");
    }

    public string StorageRoot => _baselineRoot;

    public bool Exists(string projectFolder) =>
        File.Exists(Path.Combine(GetProjectStoragePath(projectFolder), ManifestFileName));

    public ProjectBaselineInfo? GetInfo(string projectFolder)
    {
        string manifestPath = Path.Combine(
            GetProjectStoragePath(Path.GetFullPath(projectFolder)),
            ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        BaselineManifest manifest = JsonSerializer.Deserialize<BaselineManifest>(
            File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("The baseline manifest is invalid.");
        return new ProjectBaselineInfo(
            manifest.ProjectPath,
            manifest.ProjectKind,
            manifest.CreatedUtc,
            manifest.Files.Count);
    }

    public void Save(string projectFolder, string projectKind)
    {
        string projectRoot = Path.GetFullPath(projectFolder);
        Directory.CreateDirectory(_baselineRoot);

        string destination = GetProjectStoragePath(projectRoot);
        string temporary = destination + ".new-" + Guid.NewGuid().ToString("N");
        string previous = destination + ".previous-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(temporary, "files"));

        try
        {
            var manifest = new BaselineManifest
            {
                ProjectPath = projectRoot,
                ProjectKind = projectKind,
                CreatedUtc = DateTimeOffset.UtcNow
            };

            foreach (string source in ProjectFilePolicy.EnumerateFiles(projectRoot))
            {
                string relative = NormalizeRelative(
                    Path.GetRelativePath(projectRoot, source));
                byte[] bytes = File.ReadAllBytes(source);
                string storedPath = ResolveStoredFile(temporary, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(storedPath)!);
                File.WriteAllBytes(storedPath, bytes);
                manifest.Files.Add(new BaselineEntry
                {
                    Path = relative,
                    Length = bytes.LongLength,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
                });
            }

            File.WriteAllText(
                Path.Combine(temporary, ManifestFileName),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions
                {
                    WriteIndented = true
                }),
                new UTF8Encoding(false));

            if (Directory.Exists(destination))
            {
                Directory.Move(destination, previous);
            }

            Directory.Move(temporary, destination);
            if (Directory.Exists(previous))
            {
                Directory.Delete(previous, recursive: true);
            }
        }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(previous))
            {
                Directory.Move(previous, destination);
            }

            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }

            throw;
        }
    }

    public ProjectBackup Load(string projectFolder)
    {
        string projectRoot = Path.GetFullPath(projectFolder);
        string storage = GetProjectStoragePath(projectRoot);
        string manifestPath = Path.Combine(storage, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                "No baseline has been saved for the selected project.");
        }

        BaselineManifest manifest = JsonSerializer.Deserialize<BaselineManifest>(
            File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("The baseline manifest is invalid.");
        if (!Path.GetFullPath(manifest.ProjectPath).Equals(
                projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The saved baseline belongs to a different project.");
        }

        var backup = new ProjectBackup
        {
            ProjectRoot = projectRoot,
            DeleteUnexpectedFilesOnRestore = true
        };
        foreach (BaselineEntry entry in manifest.Files)
        {
            string relative = NormalizeRelative(entry.Path);
            string storedPath = ResolveStoredFile(storage, relative);
            byte[] bytes = File.ReadAllBytes(storedPath);
            if (bytes.LongLength != entry.Length ||
                !Convert.ToHexString(SHA256.HashData(bytes)).Equals(
                    entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The baseline file \"{relative}\" failed integrity validation.");
            }

            string target = PackageExecutor.ResolveInside(projectRoot, relative);
            backup.Files.Add(new FileBackup(target, bytes));
        }

        return backup;
    }

    private string GetProjectStoragePath(string projectFolder)
    {
        string normalized = Path.GetFullPath(projectFolder)
            .TrimEnd(Path.DirectorySeparatorChar)
            .ToUpperInvariant();
        string key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_baselineRoot, key);
    }

    private static string ResolveStoredFile(string storageRoot, string relativePath)
    {
        string filesRoot = Path.GetFullPath(Path.Combine(storageRoot, "files"));
        string fullPath = Path.GetFullPath(Path.Combine(
            filesRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = filesRoot.EndsWith(Path.DirectorySeparatorChar)
            ? filesRoot
            : filesRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The baseline path \"{relativePath}\" is outside its storage folder.");
        }

        return fullPath;
    }

    private static string NormalizeRelative(string relativePath) =>
        relativePath.Replace('\\', '/');

    private sealed class BaselineManifest
    {
        public string ProjectPath { get; set; } = string.Empty;
        public string ProjectKind { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
        public List<BaselineEntry> Files { get; set; } = new();
    }

    private sealed class BaselineEntry
    {
        public string Path { get; set; } = string.Empty;
        public long Length { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}
