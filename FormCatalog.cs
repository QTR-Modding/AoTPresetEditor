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

[Flags]
public enum ItemCategories
{
    None = 0,
    FOOD = 1 << 0, INGR = 1 << 1, MEDC = 1 << 2, POSN = 1 << 3, ARMO = 1 << 4,
    WEAP = 1 << 5, SCRL = 1 << 6, BOOK = 1 << 7, SLGM = 1 << 8, MISC = 1 << 9
}

public sealed record FormRow(string Name, string EditorId, string Type, string Plugin, string Winner, string Identifier,
    ItemCategories Categories = ItemCategories.None, bool IsTrigger = false, bool IsBoundObject = false);

public sealed class FormSelection(IReadOnlyCollection<string>? Types = null, string? Category = null, bool TriggerOnly = false,
    bool BoundObjectOnly = false, IReadOnlyCollection<FormRow>? ExcludedForms = null)
{
    private readonly ItemCategories category = Enum.TryParse<ItemCategories>(Category, true, out var value) ? value : ItemCategories.None;

    public bool Matches(FormRow form) =>
        (Types is null || Types.Contains(form.Type, StringComparer.OrdinalIgnoreCase)) &&
        (string.IsNullOrEmpty(Category) || (form.Categories & category) != 0) &&
        (!TriggerOnly || form.IsTrigger) &&
        (!BoundObjectOnly || form.IsBoundObject) &&
        (ExcludedForms is null || !ExcludedForms.Contains(form));
}

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
            var boundObject = IsBoundObject(record);
            forms[record.FormKey] = new FormRow(name, record.EditorID ?? "", record.Registration.Name, origin, winner,
                $"0x{record.FormKey.ID:X}~{origin}", GetCategories(record), boundObject || record is ILocationGetter or IPerkGetter, boundObject);
        }
    }

    private static ItemCategories GetCategories(IMajorRecordGetter record)
    {
        // AoT's category checks are independent: a food ingredient belongs to FOOD and INGR.
        if (record is IIngestibleGetter ingestible)
        {
            var categories = ItemCategories.None;
            if (ingestible.Flags.HasFlag(Ingestible.Flag.FoodItem) && !ingestible.Flags.HasFlag(Ingestible.Flag.Poison))
                categories |= ItemCategories.FOOD;
            if (ingestible.Flags.HasFlag(Ingestible.Flag.Medicine)) categories |= ItemCategories.MEDC;
            if (ingestible.Flags.HasFlag(Ingestible.Flag.Poison)) categories |= ItemCategories.POSN;
            return categories;
        }
        return record switch
        {
            IIngredientGetter ingredient => ItemCategories.INGR |
                (ingredient.Flags.HasFlag(Ingredient.Flag.FoodItem) ? ItemCategories.FOOD : ItemCategories.None),
            IArmorGetter => ItemCategories.ARMO,
            IWeaponGetter => ItemCategories.WEAP,
            IScrollGetter => ItemCategories.SCRL,
            IBookGetter => ItemCategories.BOOK,
            ISoulGemGetter => ItemCategories.SLGM,
            IMiscItemGetter => ItemCategories.MISC,
            _ => ItemCategories.None
        };
    }

    // Matches CommonLib's TESForm::As<TESBoundObject>(); OBND fields alone do not describe this inheritance.
    private static bool IsBoundObject(IMajorRecordGetter record) => record is
        IAcousticSpaceGetter or IActivatorGetter or IAddonNodeGetter or IAlchemicalApparatusGetter or
        IAmmunitionGetter or IArmorGetter or IArtObjectGetter or IBookGetter or IContainerGetter or
        IDoorGetter or IDualCastDataGetter or IExplosionGetter or IFloraGetter or IFurnitureGetter or
        IGrassGetter or IHazardGetter or IIdleMarkerGetter or IIngestibleGetter or IIngredientGetter or
        IKeyGetter or ILeveledItemGetter or ILeveledNpcGetter or ILeveledSpellGetter or ILightGetter or
        IMiscItemGetter or IMoveableStaticGetter or INpcGetter or IObjectEffectGetter or IProjectileGetter or
        IScrollGetter or ISoulGemGetter or ISoundMarkerGetter or ISpellGetter or IStaticGetter or
        ITalkingActivatorGetter or ITextureSetGetter or ITreeGetter or IWeaponGetter;
}
