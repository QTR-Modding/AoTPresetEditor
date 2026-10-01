using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace AoTPresetEditor;

public sealed record FormRow(string Name, string EditorId, string Type, string Plugin, string Winner, string Identifier);

public sealed record CatalogResult(IReadOnlyList<FormRow> Forms, IReadOnlyList<string> Plugins, string PluginsPath);

public static class FormCatalog
{
    public static CatalogResult Load(string dataFolder, IProgress<string>? progress = null)
    {
        dataFolder = Path.GetFullPath(dataFolder);
        if (!File.Exists(Path.Combine(dataFolder, "Skyrim.esm")))
            throw new DirectoryNotFoundException("Choose the Skyrim Special Edition Data folder containing Skyrim.esm.");

        string pluginsPath = PluginListings.GetListingsPath(GameRelease.SkyrimSE);
        if (!File.Exists(pluginsPath))
            throw new FileNotFoundException("The Skyrim plugin list was not found. Launch the editor through MO2 to read the selected profile.", pluginsPath);

        progress?.Report("Reading the active plugin list...");
        using var environment = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE)
            .WithTargetDataFolder(dataFolder)
            .TransformLoadOrderListings(listings => listings.Where(listing => listing.Enabled))
            .Build();

        var forms = new Dictionary<FormKey, FormRow>();
        var plugins = new List<string>();
        foreach (var listing in environment.LoadOrder.ListedOrder)
        {
            string plugin = listing.ModKey.FileName.String;
            var mod = listing.Mod ?? throw new FileNotFoundException($"Active plugin '{plugin}' is missing from the selected Data folder.", Path.Combine(dataFolder, plugin));
            plugins.Add(plugin);
            progress?.Report($"Reading {plugin}...");
            AddRecords(mod, plugin, forms);
        }

        return new CatalogResult(forms.Values
            .OrderBy(form => form.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(form => form.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(form => form.EditorId, StringComparer.OrdinalIgnoreCase)
            .ToArray(), plugins, pluginsPath);
    }

    private static void AddRecords(ISkyrimModGetter mod, string winner, Dictionary<FormKey, FormRow> forms)
    {
        foreach (var record in mod.EnumerateMajorRecords())
        {
            if (record is IPlacedGetter or ICellGetter or ILandscapeGetter or INavigationMeshGetter or IDialogResponsesGetter)
                continue;

            if (record.IsDeleted)
            {
                forms.Remove(record.FormKey);
                continue;
            }

            string name = record switch
            {
                INamedGetter named => named.Name ?? "",
                INamedRequiredGetter named => named.Name,
                _ => ""
            };
            string origin = record.FormKey.ModKey.FileName.String;
            forms[record.FormKey] = new FormRow(name, record.EditorID ?? "", record.Registration.Name, origin, winner,
                $"0x{record.FormKey.ID:X}~{origin}");
        }
    }
}
