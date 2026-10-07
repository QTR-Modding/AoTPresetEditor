using System.Diagnostics;
using System.IO;
using System.Text;

namespace AoTPresetEditor;

public enum PresetKind { Default, Custom, Addon, Other }

public sealed record PresetFile(string FullPath, string RelativePath)
{
    public string DisplayName => Path.GetFileNameWithoutExtension(FullPath) + " · " + Category;
    public string Category
    {
        get
        {
            var separator = RelativePath.IndexOf(Path.DirectorySeparatorChar);
            return separator < 0 ? "Root" : RelativePath[..separator];
        }
    }

    public string CategoryPath => RelativePath[(RelativePath.IndexOf(Path.DirectorySeparatorChar) + 1)..];

    public PresetKind Kind
    {
        get
        {
            if (CategoryPath.Equals($"AoT_default{Category}.yml", StringComparison.OrdinalIgnoreCase))
                return PresetKind.Default;
            var separator = CategoryPath.IndexOf(Path.DirectorySeparatorChar);
            if (separator < 0) return PresetKind.Other;
            return CategoryPath[..separator].ToLowerInvariant() switch
            {
                "custom" => PresetKind.Custom,
                "addon" => PresetKind.Addon,
                _ => PresetKind.Other
            };
        }
    }

    public string RulePath => Kind is PresetKind.Custom or PresetKind.Addon
        ? CategoryPath[(CategoryPath.IndexOf(Path.DirectorySeparatorChar) + 1)..]
        : CategoryPath;
}

public static class PresetCatalog
{
    public static IReadOnlyList<PresetFile> Discover(string dataFolder)
    {
        var root = Path.Combine(dataFolder, "SKSE", "Plugins", "AlchemyOfTime");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.yml", SearchOption.AllDirectories)
            .Select(path => new PresetFile(path, Path.GetRelativePath(root, path)))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<PresetFile> DiscoverSelection(IEnumerable<string> paths, IReadOnlyList<string> categories)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<string>();
        foreach (var input in paths)
        {
            var path = Path.GetFullPath(input);
            if (Directory.Exists(path))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true,
                    IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var file in Directory.EnumerateFiles(path, "*", options).Where(IsYaml)
                    .Order(StringComparer.OrdinalIgnoreCase))
                    if (files.Add(file)) selected.Add(file);
            }
            else
            {
                if (!File.Exists(path)) throw new FileNotFoundException("The selected file or folder no longer exists.", path);
                if (!IsYaml(path) && !Path.GetExtension(path).Equals(".aotdraft", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Choose a YAML preset, an editor draft, or a folder containing presets.");
                if (files.Add(path)) selected.Add(path);
            }
        }
        if (selected.Count == 0) throw new InvalidDataException("The selected folders contain no .yml or .yaml presets.");
        if (selected.Count > 1 && selected.Any(path => Path.GetExtension(path).Equals(".aotdraft", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Open one editor draft at a time. To open several presets together, select YAML files or folders.");
        return selected.Select(path => DescribeFile(path, categories)).ToArray();
    }

    private static bool IsYaml(string path) => Path.GetExtension(path).Equals(".yml", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".yaml", StringComparison.OrdinalIgnoreCase);

    private static PresetFile DescribeFile(string path, IReadOnlyList<string> categories)
    {
        var marker = Path.DirectorySeparatorChar + "AlchemyOfTime" + Path.DirectorySeparatorChar;
        var start = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            var knownPath = path[(start + marker.Length)..];
            var separator = knownPath.IndexOf(Path.DirectorySeparatorChar);
            var category = categories.FirstOrDefault(value => separator >= 0 &&
                value.Equals(knownPath[..separator], StringComparison.OrdinalIgnoreCase));
            return new PresetFile(path, category is null ? knownPath : category + knownPath[separator..]);
        }
        var relative = Path.GetFileName(path);
        for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
        {
            var category = categories.FirstOrDefault(value => value.Equals(parent.Name, StringComparison.OrdinalIgnoreCase));
            if (category is not null) return new PresetFile(path, Path.Combine(category, relative));
            relative = Path.Combine(parent.Name, relative);
        }
        var name = Path.GetFileNameWithoutExtension(path);
        var defaultCategory = categories.FirstOrDefault(category => name.Equals("AoT_default" + category, StringComparison.OrdinalIgnoreCase));
        return new PresetFile(path, defaultCategory is null ? Path.GetFileName(path)
            : Path.Combine(defaultCategory, Path.GetFileName(path)));
    }
    public static async Task<string> Expand(string yaml, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "AoTYaml.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var process = Process.Start(start) ?? throw new IOException("Could not start YAML expansion.");
        using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        IOException? inputError = null;
        try { await process.StandardInput.WriteAsync(yaml.AsMemory(), cancellationToken); }
        catch (IOException exception) { inputError = exception; }
        try { process.StandardInput.Close(); }
        catch (IOException exception) { inputError ??= exception; }
        await process.WaitForExitAsync(cancellationToken);
        var diagnostics = await error;
        var expanded = await output;
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidDataException(diagnostics);
        if (inputError is not null) throw inputError;
        return expanded;
    }
}
