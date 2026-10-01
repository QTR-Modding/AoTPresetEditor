using System.IO;
using System.Text;

namespace AoTPresetEditor;

public static class PresetExport
{
    private sealed class ExportFile(string path, byte[] content, bool preset)
    {
        public string Path { get; } = path;
        public byte[] Content { get; } = content;
        public bool Preset { get; } = preset;
        public byte[]? Original { get; set; }
        public string? Temporary { get; set; }
        public string? Backup { get; set; }
        public bool Committed { get; set; }
        public bool KeepBackup { get; set; }
    }

    public static void Save(string presetPath, string source, IReadOnlyList<FormGroupDefinition> pendingGroups, string modFolder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modFolder));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Choose an existing destination mod folder.");
        presetPath = Path.GetFullPath(presetPath);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!presetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The preset must be saved inside the selected mod folder.");
        var encoding = new UTF8Encoding(false);
        var files = pendingGroups.Select(group => new ExportFile(FormGroupCatalog.GetExportPath(root, group.Name),
            encoding.GetBytes(FormGroupCatalog.Serialize(group)), false)).ToList();
        files.Add(new(presetPath, encoding.GetBytes(source), true));
        if (files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new InvalidDataException("Two exported files have the same name. Give each form group a different name.");

        foreach (var file in files)
        {
            if (Directory.Exists(file.Path)) throw new IOException($"A folder occupies the output file path: {file.Path}");
            if (!File.Exists(file.Path)) continue;
            file.Original = File.ReadAllBytes(file.Path);
            if (!file.Preset && !file.Original.AsSpan().SequenceEqual(file.Content))
                throw new IOException($"A different form group already exists at {file.Path}. Choose another group name.");
        }
        files.RemoveAll(file => file.Original is not null && file.Original.AsSpan().SequenceEqual(file.Content));
        var createdDirectories = new List<string>();
        try
        {
            foreach (var file in files)
            {
                CreateDirectories(Path.GetDirectoryName(file.Path)!, createdDirectories);
                var token = Guid.NewGuid().ToString("N");
                file.Temporary = file.Path + "." + token + ".aot-new";
                File.WriteAllBytes(file.Temporary, file.Content);
                if (file.Original is null) continue;
                file.Backup = file.Path + "." + token + ".aot-backup";
                File.WriteAllBytes(file.Backup, file.Original);
            }
            foreach (var file in files)
            {
                if (file.Original is not null && !File.ReadAllBytes(file.Path).AsSpan().SequenceEqual(file.Original))
                    throw new IOException($"The file changed while the export was being prepared: {file.Path}");
                File.Move(file.Temporary!, file.Path, file.Original is not null);
                file.Committed = true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var recovery = RollBack(files);
            var message = "Could not save the preset and form groups: " + error.Message;
            if (recovery.Count > 0)
                message += Environment.NewLine + "Some files could not be restored. Recovery details:" + Environment.NewLine + string.Join(Environment.NewLine, recovery);
            throw new IOException(message, error);
        }
        finally
        {
            foreach (var file in files)
            {
                DeleteTemporary(file.Temporary);
                if (!file.KeepBackup) DeleteTemporary(file.Backup);
            }
            foreach (var directory in createdDirectories.AsEnumerable().Reverse())
            {
                try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void CreateDirectories(string directory, List<string> created)
    {
        if (Directory.Exists(directory)) return;
        CreateDirectories(Path.GetDirectoryName(directory)!, created);
        Directory.CreateDirectory(directory);
        created.Add(directory);
    }

    private static List<string> RollBack(IEnumerable<ExportFile> files)
    {
        var recovery = new List<string>();
        foreach (var file in files.Reverse().Where(file => file.Committed))
        {
            try
            {
                if (!File.ReadAllBytes(file.Path).AsSpan().SequenceEqual(file.Content))
                    throw new IOException("The destination changed after it was written.");
                if (file.Original is null) File.Delete(file.Path);
                else File.Move(file.Backup!, file.Path, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                file.KeepBackup = true;
                recovery.Add(file.Backup is null
                    ? $"Created file remains at {file.Path}. It did not exist before this export. {error.Message}"
                    : $"Original file: {file.Path}{Environment.NewLine}Recovery copy: {file.Backup}{Environment.NewLine}{error.Message}");
            }
        }
        return recovery;
    }

    private static void DeleteTemporary(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}


