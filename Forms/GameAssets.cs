using System.Diagnostics.CodeAnalysis;
using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Assets;
using Mutagen.Bethesda.Assets.DI;
using Mutagen.Bethesda.Plugins;

namespace AoTPresetEditor;

public sealed class GameAssets : IAssetProvider
{
    private readonly string dataFolder;
    private readonly Lazy<IArchiveReader>[] archives;

    public GameAssets(string dataFolder, IEnumerable<string> archivePathsInLoadOrder)
    {
        this.dataFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder));
        archives = archivePathsInLoadOrder.Reverse().Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new Lazy<IArchiveReader>(() => Archive.CreateReader(GameRelease.SkyrimSE, path))).ToArray();
    }

    public static GameAssets FromLoadOrder(string dataFolder, IEnumerable<ModKey> plugins, IEnumerable<string>? iniArchives = null)
    {
        var available = Directory.EnumerateFiles(dataFolder, "*.bsa").Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var ordered = new List<string>();
        foreach (var name in iniArchives ?? Archive.GetIniListings(GameRelease.SkyrimSE).Select(file => file.String))
        {
            var path = available.FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (path is not null) ordered.Add(path);
        }
        foreach (var plugin in plugins)
        foreach (var suffix in new[] { ".bsa", " - Textures.bsa" })
        {
            var name = plugin.Name + suffix;
            var path = available.FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (path is not null) ordered.Add(path);
        }
        return new GameAssets(dataFolder, ordered);
    }

    public bool Exists(DataRelativePath assetPath) => File.Exists(LoosePath(assetPath)) || FindArchiveFile(assetPath) is not null;

    public bool TryGetStream(DataRelativePath assetPath, [NotNullWhen(true)] out Stream? stream)
    {
        var loose = LoosePath(assetPath);
        if (File.Exists(loose))
        {
            stream = File.OpenRead(loose);
            return true;
        }
        stream = FindArchiveFile(assetPath)?.AsStream();
        return stream is not null;
    }

    public bool TryGetSize(DataRelativePath assetPath, out uint size)
    {
        var loose = LoosePath(assetPath);
        if (File.Exists(loose))
        {
            size = checked((uint)new FileInfo(loose).Length);
            return true;
        }
        var file = FindArchiveFile(assetPath);
        size = file?.Size ?? 0;
        return file is not null;
    }

    private string LoosePath(DataRelativePath path)
    {
        var fullPath = Path.GetFullPath(Path.Combine(dataFolder, path.Path));
        if (!fullPath.StartsWith(dataFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The asset path must be inside the game's Data folder.");
        return fullPath;
    }

    private IArchiveFile? FindArchiveFile(DataRelativePath path)
    {
        var folder = Path.GetDirectoryName(path.Path);
        if (folder is null) return null;
        var name = Path.GetFileName(path.Path);
        foreach (var archive in archives)
            if (archive.Value.TryGetFolder(folder, out var match))
            {
                var file = match.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (file is not null) return file;
            }
        return null;
    }
}
