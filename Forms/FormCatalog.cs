using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Assets.DI;
using Mutagen.Bethesda.Assets;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Cache;
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
    ItemCategories Categories = ItemCategories.None, bool IsTrigger = false, bool IsBoundObject = false, FormID? RuntimeId = null)
{
    public IAssetProvider? Assets { get; init; }
    public IReadOnlyList<string> SoundFiles { get; init; } = [];
    public IReadOnlyList<ModelPreviewSource> Models { get; init; } = [];
    public IReadOnlyList<TexturePreviewSource> Textures { get; init; } = [];
    public IEffectShaderGetter? EffectShader { get; init; }
    public IReadOnlyList<FormRow>? FormListMembers { get; init; }
    public IComparable IdentifierSortKey => PresetPresentation.IdentifierSortKey(Identifier);
}

public sealed record ModelTextureReplacement(string Name, int Index, FormKey TextureSet, string? DiffusePath = null);
public sealed record ModelPreviewPart(string Path, IReadOnlyList<ModelTextureReplacement> AlternateTextures, string? DiffusePath = null,
    IReadOnlySet<int>? ExcludedPartitions = null, IReadOnlySet<string>? ExcludedShapes = null, string? AttachmentNode = null);
public sealed record ModelPreviewAppearance(Vector3? SkinTint, Vector3? HairTint, string? SkeletonPath = null);
public sealed record ModelPreviewSource(string Label, IReadOnlyList<ModelPreviewPart> Parts, string Note = "", ModelPreviewAppearance? Appearance = null);
public sealed record TexturePreviewSource(string Label, string Path);

public sealed class FormSelection(IReadOnlyCollection<string>? Types = null, string? Category = null, bool TriggerOnly = false,
    bool BoundObjectOnly = false, IReadOnlyCollection<FormRow>? ExcludedForms = null, bool RequiresEditorId = false,
    bool InventoryOwnerOnly = false)
{
    private readonly ItemCategories category = Enum.TryParse<ItemCategories>(Category, true, out var value) ? value : ItemCategories.None;

    public static FormSelection ForField(PresetField? field, string category, IReadOnlyCollection<FormRow>? sourceForms = null,
        IReadOnlyCollection<FormRow>? excludedForms = null)
    {
        var types = field?.FormTypes;
        var excluded = new HashSet<FormRow>(excludedForms ?? []);
        if (field?.MatchSourceType == true)
            foreach (var form in sourceForms ?? [])
            {
                excluded.Add(form);
                types = types is null ? [form.Type] : types.Where(type => type.Equals(form.Type, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
        return new(types, field?.MatchPresetCategory == true ? category : null,
            field?.TriggerOnly == true, field?.BoundObjectOnly == true, excluded, field?.RequiresEditorId == true,
            field?.InventoryOwnerOnly == true);
    }

    public bool RequiresKnownGroupMember => !string.IsNullOrEmpty(Category);

    public bool Matches(FormRow form) =>
        (Types is null || Types.Contains(form.Type, StringComparer.OrdinalIgnoreCase)) &&
        (string.IsNullOrEmpty(Category) || (form.Categories & category) != 0) &&
        (!TriggerOnly || form.IsTrigger) &&
        (!BoundObjectOnly || form.IsBoundObject) &&
        (!RequiresEditorId || form.EditorId.Length != 0) &&
        (ExcludedForms is null || !ExcludedForms.Contains(form));

    // These magic definitions pass the native bound-object check but are not ordinary inventory or placed objects.
    public bool IsPickerChoice(FormRow form) => Matches(form) &&
        (!(TriggerOnly || BoundObjectOnly) || form.Type is not ("DualCastData" or "ObjectEffect" or "LeveledSpell" or "Spell")) &&
        (!TriggerOnly || form.Type is not ("Grass" or "LeveledNpc")) &&
        (!BoundObjectOnly || !form.Type.Equals("AcousticSpace", StringComparison.OrdinalIgnoreCase)) &&
        (!InventoryOwnerOnly || form.Type is "Npc" or "Container");
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

        ResolveFormListMembers(forms, environment.LinkCache);
        AssignRuntimeIds(forms, environment.LoadOrder.ListedOrder.Select(listing => listing.Mod!));
        var assets = GameAssets.FromLoadOrder(dataFolder, environment.LoadOrder.ListedOrder.Select(listing => listing.ModKey));
        ResolvePreviewAssets(forms, environment.LinkCache, assets);
        return new CatalogResult(forms.Values
            .OrderBy(form => form.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(form => form.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(form => form.EditorId, StringComparer.OrdinalIgnoreCase)
            .ToArray(), plugins, pluginsPath);
    }

    private static void AssignRuntimeIds(Dictionary<FormKey, FormRow> forms, IEnumerable<ISkyrimModGetter> loadOrder)
    {
        var nextIndices = new Dictionary<MasterStyle, uint>();
        var indices = new Dictionary<ModKey, (MasterStyle Style, uint Index)>();
        foreach (var mod in loadOrder)
        {
            var style = mod.MasterStyle;
            var index = nextIndices.GetValueOrDefault(style);
            indices.Add(mod.ModKey, (style, index));
            nextIndices[style] = index + 1;
        }
        foreach (var (key, form) in forms.ToArray())
        {
            var (style, index) = indices[key.ModKey];
            forms[key] = form with { RuntimeId = FormID.Factory(style, index, key.ID) };
        }
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

            forms[record.FormKey] = CreateFormRow(record, winner);
        }
    }

    private static FormRow CreateFormRow(IMajorRecordGetter record, string winner)
    {
        string name = record switch
        {
            INamedGetter named => named.Name ?? "",
            INamedRequiredGetter named => named.Name,
            _ => ""
        };
        string origin = record.FormKey.ModKey.FileName.String;
        var boundObject = IsBoundObject(record);
        return new FormRow(name, record.EditorID ?? "", record.Registration.Name, origin, winner,
            $"0x{record.FormKey.ID:X}~{origin}", GetCategories(record), boundObject || record is ILocationGetter or IPerkGetter or IEffectShaderGetter or IFormListGetter or IKeywordGetter or IMagicEffectGetter, boundObject)
        {
            SoundFiles = record is ISoundDescriptorGetter sound ? sound.SoundFiles.Select(file => file.DataRelativePath.Path).ToArray() : [],
            Textures = GetTextures(record),
            EffectShader = record is IEffectShaderGetter shader ? shader.DeepCopy() : null,
            FormListMembers = record is IFormListGetter ? [] : null
        };
    }

    private static void ResolveFormListMembers(Dictionary<FormKey, FormRow> forms, ILinkCache cache)
    {
        foreach (var (key, form) in forms.Where(pair => pair.Value.FormListMembers is not null).ToArray())
        {
            var members = new List<FormRow>();
            if (Resolve<IFormListGetter>(cache, key) is { } list)
                foreach (var link in list.Items)
                    if (forms.TryGetValue(link.FormKey, out var member)) members.Add(member);
                    else if (Resolve<ISkyrimMajorRecordGetter>(cache, link.FormKey) is { } record)
                        members.Add(CreateFormRow(record, record.FormKey.ModKey.FileName.String));
            forms[key] = form with { FormListMembers = members };
        }
    }

    public static IReadOnlyList<FormRow> ExpandFormList(FormRow form, Func<string, FormRow?> resolve)
    {
        var result = new List<FormRow>();
        AppendFormList(form, resolve, new HashSet<string>(StringComparer.OrdinalIgnoreCase), result);
        return result;
    }

    private static void AppendFormList(FormRow form, Func<string, FormRow?> resolve, HashSet<string> visited, List<FormRow> result)
    {
        if (form.FormListMembers is not { } members) { result.Add(form); return; }
        if (!visited.Add(form.Identifier)) return;
        foreach (var member in members) AppendFormList(resolve(member.Identifier) ?? member, resolve, visited, result);
    }

    private static IReadOnlyList<ModelPreviewSource> GetDirectModels(IMajorRecordGetter record, ILinkCache cache)
    {
        var models = new List<ModelPreviewSource>();
        if (record is IModeledGetter modeled) AddModel(models, "Model", modeled.Model);
        switch (record)
        {
            case IArmorGetter armor:
                AddModel(models, "Male world model", armor.WorldModel?.Male?.Model);
                AddModel(models, "Female world model", armor.WorldModel?.Female?.Model);
                break;
            case IArmorAddonGetter addon:
                AddModel(models, "Male worn model", addon.WorldModel?.Male, SkinDiffuse(addon, false, cache));
                AddModel(models, "Female worn model", addon.WorldModel?.Female, SkinDiffuse(addon, true, cache));
                AddModel(models, "Male first-person model", addon.FirstPersonModel?.Male, SkinDiffuse(addon, false, cache));
                AddModel(models, "Female first-person model", addon.FirstPersonModel?.Female, SkinDiffuse(addon, true, cache));
                break;
            case IWeaponGetter weapon: AddModel(models, "Scope", weapon.ScopeModel); break;
            case IProjectileGetter projectile: AddModelPath(models, "Muzzle flash", projectile.MuzzleFlashModel?.DataRelativePath.Path); break;
            case IFurnitureGetter furniture: AddModelPath(models, "Marker model", furniture.ModelFilename?.DataRelativePath.Path); break;
            case IWeatherGetter weather: AddModel(models, "Aurora", weather.Aurora); break;
            case IWorldspaceGetter worldspace: AddModel(models, "Clouds", worldspace.CloudModel); break;
            case IDebrisGetter debris:
                for (var index = 0; index < debris.Models.Count; index++)
                    AddModelPath(models, $"Debris {index + 1}", debris.Models[index].ModelFilename.DataRelativePath.Path);
                break;
            case IStaticGetter item when item.Lod is { } lod:
                AddModelPath(models, "LOD 0", lod.Level0?.DataRelativePath.Path);
                AddModelPath(models, "LOD 1", lod.Level1?.DataRelativePath.Path);
                AddModelPath(models, "LOD 2", lod.Level2?.DataRelativePath.Path);
                AddModelPath(models, "LOD 3", lod.Level3?.DataRelativePath.Path);
                break;
            case IBodyPartDataGetter body:
                foreach (var part in body.Parts)
                    AddModelPath(models, $"{part.Name} limb replacement", part.LimbReplacementModel);
                break;
            case IRaceGetter race:
                AddModelPath(models, "Male skeleton", race.SkeletalModel?.Male?.File.DataRelativePath.Path);
                AddModelPath(models, "Female skeleton", race.SkeletalModel?.Female?.File.DataRelativePath.Path);
                AddModel(models, "Male head", race.HeadData?.Male?.Model);
                AddModel(models, "Female head", race.HeadData?.Female?.Model);
                break;
        }
        if (record is IHasDestructibleGetter { Destructible: { } destruction })
            for (var index = 0; index < destruction.Stages.Count; index++)
                AddModel(models, $"Destruction stage {destruction.Stages[index].Data?.Index ?? index}", destruction.Stages[index].Model);
        return models;
    }

    private static void AddModel(List<ModelPreviewSource> models, string label, IModelGetter? model, string? diffuse = null, ModelPreviewAppearance? appearance = null)
    {
        if (model?.File.IsNull != false) return;
        models.Add(new(label, [ModelPart(model) with { DiffusePath = diffuse }], Appearance: appearance));
    }

    private static ModelPreviewPart ModelPart(IModelGetter model) => new(model.File.DataRelativePath.Path,
        model.AlternateTextures?.Select(texture => new ModelTextureReplacement(texture.Name, texture.Index, texture.NewTexture.FormKey)).ToArray() ?? []);

    private static void AddModelPath(List<ModelPreviewSource> models, string label, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) models.Add(new(label, [new(path, [])]));
    }

    private static IReadOnlyList<TexturePreviewSource> GetTextures(IMajorRecordGetter record)
    {
        var textures = new List<TexturePreviewSource>();
        void Add(string label, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) textures.Add(new(label, path));
        }
        switch (record)
        {
            case IEffectShaderGetter shader:
                Add("Fill", shader.FillTexture?.DataRelativePath.Path);
                Add("Particles", shader.ParticleShaderTexture?.DataRelativePath.Path);
                Add("Holes", shader.HolesTexture?.DataRelativePath.Path);
                Add("Membrane palette", shader.MembranePaletteTexture?.DataRelativePath.Path);
                Add("Particle palette", shader.ParticlePaletteTexture?.DataRelativePath.Path);
                break;
            case ITextureSetGetter texture:
                Add("Diffuse", texture.Diffuse?.DataRelativePath.Path);
                Add("Normal / gloss", texture.NormalOrGloss?.DataRelativePath.Path);
                Add("Environment mask / subsurface tint", texture.EnvironmentMaskOrSubsurfaceTint?.DataRelativePath.Path);
                Add("Glow / detail", texture.GlowOrDetailMap?.DataRelativePath.Path);
                Add("Height", texture.Height?.DataRelativePath.Path);
                Add("Environment", texture.Environment?.DataRelativePath.Path);
                Add("Multilayer", texture.Multilayer?.DataRelativePath.Path);
                Add("Backlight mask / specular", texture.BacklightMaskOrSpecular?.DataRelativePath.Path);
                break;
        }
        return textures;
    }

    private static void ResolvePreviewAssets(Dictionary<FormKey, FormRow> forms, ILinkCache cache, IAssetProvider assets)
    {
        foreach (var (key, form) in forms.ToArray())
        {
            var models = new List<ModelPreviewSource>();
            var sounds = form.SoundFiles;
            if (cache.TryResolve<ISkyrimMajorRecordGetter>(key, out var record))
            {
                models.AddRange(GetDirectModels(record, cache));
                AddLinkedModels(models, record, cache, assets);
                if (record is ISoundMarkerGetter marker && Resolve<ISoundDescriptorGetter>(cache, marker.SoundDescriptor.FormKey) is { } sound)
                    sounds = sound.SoundFiles.Select(file => file.DataRelativePath.Path).ToArray();
            }
            var resolved = models.Select(source => source with
            {
                Parts = source.Parts.Select(part => part with
                {
                    AlternateTextures = part.AlternateTextures.Select(replacement =>
                        Resolve<ITextureSetGetter>(cache, replacement.TextureSet) is { } texture
                            ? replacement with { DiffusePath = texture.Diffuse?.DataRelativePath.Path ?? "" }
                            : replacement).ToArray()
                }).ToArray()
            }).ToArray();
            forms[key] = form with
            {
                Models = resolved, SoundFiles = sounds,
                Assets = form.Type is "SoundDescriptor" or "SoundMarker" || models.Count != 0 || form.Textures.Count != 0 || form.EffectShader is not null ? assets : null
            };
        }
    }

    private static T? Resolve<T>(ILinkCache cache, FormKey key) where T : class, IMajorRecordGetter =>
        !key.IsNull && cache.TryResolve<T>(key, out var record) && !record.IsDeleted ? record : null;

    private static void AddLinkedModels(List<ModelPreviewSource> models, ISkyrimMajorRecordGetter record, ILinkCache cache, IAssetProvider assets)
    {
        void Add(string label, FormKey key)
        {
            if (Resolve<ISkyrimMajorRecordGetter>(cache, key) is not { } linked) return;
            foreach (var source in GetDirectModels(linked, cache))
                models.Add(source with { Label = source.Label == "Model" ? label : label + ": " + source.Label });
        }
        switch (record)
        {
            case IArmorGetter armor:
                foreach (var link in armor.Armature)
                    if (Resolve<IArmorAddonGetter>(cache, link.FormKey) is { } addon)
                        Add(addon.EditorID ?? "Worn model", addon.FormKey);
                break;
            case IArmorAddonGetter addon: Add("Art object", addon.ArtObject.FormKey); break;
            case IWeaponGetter weapon: Add("First-person model", weapon.FirstPersonModel.FormKey); break;
            case IAmmunitionGetter ammo: Add("Projectile", ammo.Projectile.FormKey); break;
            case ILoadScreenGetter screen: Add("Loading screen model", screen.LoadingScreenNif.FormKey); break;
            case IMagicEffectGetter effect:
                Add("Menu model", effect.MenuDisplayObject.FormKey);
                Add("Casting art", effect.CastingArt.FormKey);
                Add("Hit art", effect.HitEffectArt.FormKey);
                Add("Enchanting art", effect.EnchantArt.FormKey);
                Add("Projectile", effect.Projectile.FormKey);
                Add("Explosion", effect.Explosion.FormKey);
                break;
            case IVisualEffectGetter visual: Add("Effect art", visual.EffectArt.FormKey); break;
            case IDualCastDataGetter dual:
                Add("Hit art", dual.HitEffectArt.FormKey);
                Add("Projectile", dual.Projectile.FormKey);
                Add("Explosion", dual.Explosion.FormKey);
                break;
            case IEffectShaderGetter shader: Add("Addon model", shader.AddonModels.FormKey); break;
            case IHeadPartGetter head:
                foreach (var link in head.ExtraParts) Add("Extra head part", link.FormKey);
                break;
            case INpcGetter npc: AddNpcModels(models, npc, cache, assets); break;
            case IRaceGetter race:
                var skin = Resolve<IArmorGetter>(cache, race.Skin.FormKey);
                foreach (var female in new[] { false, true })
                {
                    var parts = WornModels(skin, race, female, cache).Select(model => model.Part).ToList();
                    if (parts.Count != 0) models.Insert(0, new(female ? "Female body" : "Male body", parts,
                        "Fixed pose. Body-weight morphs, skin tint, clothing, and animation are not shown."));
                }
                break;
        }
    }

    private static void AddNpcModels(List<ModelPreviewSource> models, INpcGetter npc, ILinkCache cache, IAssetProvider assets)
    {
        var traits = ResolveNpcTemplate(npc, NpcConfiguration.TemplateFlag.Traits, cache);
        if (traits is null || Resolve<IRaceGetter>(cache, traits.Race.FormKey) is not { } race) return;
        var female = traits.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        var skin = Resolve<IArmorGetter>(cache, traits.WornArmor.FormKey) ?? Resolve<IArmorGetter>(cache, race.Skin.FormKey);
        var body = WornModels(skin, race, female, cache);
        var parts = body.Select(model => model.Part).ToList();
        var appearance = NpcAppearance(traits, race, female, cache);
        const string poseNote = "Fixed pose. Body-weight morphs and animation are not shown.";
        var facePath = $@"Meshes\Actors\Character\FaceGenData\FaceGeom\{traits.FormKey.ModKey.FileName.String}\{traits.FormKey.ID:X8}.nif";
        var hasFace = assets.Exists(new DataRelativePath(facePath));
        if (hasFace) parts.Add(new(facePath, []));
        if (parts.Count != 0) models.Insert(0, new(hasFace ? "Body and exported head" : "Body", parts, poseNote, appearance));
        if (hasFace) models.Add(new("Exported head", [new(facePath, [])], Appearance: appearance));
        foreach (var link in traits.HeadParts)
            if (Resolve<IHeadPartGetter>(cache, link.FormKey) is { } head)
                AddModel(models, "Head part: " + (head.EditorID ?? head.FormKey.ToString()), head.Model, appearance: appearance);
        var inventory = ResolveNpcTemplate(npc, NpcConfiguration.TemplateFlag.Inventory, cache);
        if (inventory is not null && Resolve<IOutfitGetter>(cache, inventory.DefaultOutfit.FormKey) is { } outfit)
        {
            var armors = new List<IArmorGetter>();
            var deterministic = true;
            foreach (var link in outfit.Items ?? [])
                if (Resolve<IArmorGetter>(cache, link.FormKey) is { } armor)
                {
                    armors.Add(armor);
                    var outfitParts = WornModels(armor, race, female, cache).Select(model => model.Part).ToList();
                    if (outfitParts.Count == 0 || armor.Armature.Any(addon => Resolve<IArmorAddonGetter>(cache, addon.FormKey) is null)) deterministic = false;
                    if (outfitParts.Count != 0) models.Add(new("Outfit: " + (armor.Name?.String ?? armor.EditorID ?? armor.FormKey.ToString()),
                        outfitParts, "This clothing piece is shown by itself. " + poseNote, appearance));
                }
                else deterministic = false;
            var occupied = default(BipedObjectFlag);
            foreach (var armor in armors)
            {
                var slots = armor.BodyTemplate?.FirstPersonFlags ?? default;
                if ((occupied & slots) != 0) deterministic = false;
                occupied |= slots;
            }
            if (deterministic && armors.Count != 0)
            {
                var worn = body.Concat(armors.SelectMany(armor => WornModels(armor, race, female, cache))).ToList();
                var dressed = OutfitParts(worn);
                if (hasFace) dressed.Add(new(facePath, [], ExcludedShapes: CoveredHeadShapes(traits, race, female, occupied, cache)));
                if (dressed.Count != 0) models.Insert(0, new("Default outfit", dressed, poseNote, appearance));
            }
            else if (!deterministic && models.Count != 0)
                models[0] = models[0] with { Note = models[0].Note + " The default outfit contains unresolved choices, missing models, or conflicting equipment; available pieces are shown separately." };
        }
    }

    private static ModelPreviewAppearance NpcAppearance(INpcGetter npc, IRaceGetter race, bool female, ILinkCache cache)
    {
        const float hairColorScale = 128f;
        var skin = npc.TextureLighting;
        var hair = Resolve<IColorRecordGetter>(cache, npc.HairColor.FormKey)?.Color;
        var skeleton = female && race.SkeletalModel?.Female?.File.IsNull == false ? race.SkeletalModel.Female : race.SkeletalModel?.Male;
        return new(skin is { } body ? new Vector3(body.R, body.G, body.B) / byte.MaxValue : null,
            hair is { } color ? new Vector3(color.R, color.G, color.B) / hairColorScale : null, skeleton?.File.DataRelativePath.Path);
    }

    private static HashSet<string> CoveredHeadShapes(INpcGetter npc, IRaceGetter race, bool female, BipedObjectFlag covered, ILinkCache cache)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hideHead = (covered & SlotFlag(race.HeadBipedObject)) != 0;
        var hideHair = (covered & (SlotFlag(race.HairBipedObject) | BipedObjectFlag.LongHair)) != 0;
        if (!hideHead && !hideHair) return hidden;
        var visited = new HashSet<FormKey>();
        void Add(IHeadPartGetter part)
        {
            if (!visited.Add(part.FormKey)) return;
            if (!string.IsNullOrWhiteSpace(part.EditorID)) hidden.Add(part.EditorID);
            foreach (var extra in part.ExtraParts)
                if (Resolve<IHeadPartGetter>(cache, extra.FormKey) is { } child) Add(child);
        }
        var defaults = female ? race.HeadData?.Female : race.HeadData?.Male;
        var keys = (defaults?.HeadParts.Select(part => part.Head.FormKey) ?? []).Concat(npc.HeadParts.Select(part => part.FormKey));
        foreach (var key in keys)
            if (Resolve<IHeadPartGetter>(cache, key) is { } part && (part.Type == HeadPart.TypeEnum.Hair ? hideHair : hideHead)) Add(part);
        return hidden;
    }

    private const int BipedSlotCount = sizeof(uint) * 8;
    private const int SkyrimPartitionOffset = 30;
    private static BipedObjectFlag SlotFlag(BipedObject slot) => (uint)slot < BipedSlotCount ? (BipedObjectFlag)(1u << (int)slot) : default;

    private sealed record WornModel(ModelPreviewPart Part, BipedObjectFlag Slots, byte Priority);

    private static List<ModelPreviewPart> OutfitParts(IReadOnlyList<WornModel> models)
    {
        var winners = new WornModel?[BipedSlotCount];
        foreach (var model in models)
            for (var slot = 0; slot < BipedSlotCount; slot++)
                if ((model.Slots & (BipedObjectFlag)(1u << slot)) != 0 && (winners[slot] is null || winners[slot]!.Priority < model.Priority))
                    winners[slot] = model;
        var parts = new List<ModelPreviewPart>();
        foreach (var model in models)
        {
            if (model.Slots != default && !winners.Any(winner => ReferenceEquals(winner, model))) continue;
            var excluded = new HashSet<int>();
            for (var slot = 0; slot < BipedSlotCount; slot++)
                if (winners[slot] is { } winner && !ReferenceEquals(winner, model)) excluded.Add(slot + SkyrimPartitionOffset);
            parts.Add(model.Part with { ExcludedPartitions = excluded });
        }
        return parts;
    }

    private static INpcGetter? ResolveNpcTemplate(INpcGetter npc, NpcConfiguration.TemplateFlag flag, ILinkCache cache)
    {
        var visited = new HashSet<FormKey>();
        while (npc.Configuration.TemplateFlags.HasFlag(flag))
        {
            if (!visited.Add(npc.FormKey) || Resolve<INpcGetter>(cache, npc.Template.FormKey) is not { } template) return null;
            npc = template;
        }
        return npc;
    }

    private static List<WornModel> WornModels(IArmorGetter? armor, IRaceGetter race, bool female, ILinkCache cache)
    {
        var parts = new List<WornModel>();
        if (armor is null) return parts;
        var shieldNode = race.BipedObjectNames.GetValueOrDefault(BipedObject.Shield);
        var races = new HashSet<FormKey> { race.FormKey };
        while (Resolve<IRaceGetter>(cache, race.ArmorRace.FormKey) is { } inherited && races.Add(inherited.FormKey)) race = inherited;
        foreach (var link in armor.Armature)
        {
            if (Resolve<IArmorAddonGetter>(cache, link.FormKey) is not { } addon ||
                !races.Contains(addon.Race.FormKey) && !addon.AdditionalRaces.Any(link => races.Contains(link.FormKey))) continue;
            var useFemale = female && addon.WorldModel?.Female?.File.IsNull == false;
            var model = useFemale ? addon.WorldModel?.Female : addon.WorldModel?.Male;
            if (model?.File.IsNull != false) continue;
            parts.Add(new(ModelPart(model) with { DiffusePath = SkinDiffuse(addon, useFemale, cache),
                AttachmentNode = addon.BodyTemplate?.FirstPersonFlags == BipedObjectFlag.Shield ? shieldNode : null },
                addon.BodyTemplate?.FirstPersonFlags ?? default, female ? addon.Priority.Female : addon.Priority.Male));
        }
        return parts;
    }

    private static string? SkinDiffuse(IArmorAddonGetter addon, bool female, ILinkCache cache)
    {
        var texture = female ? addon.SkinTexture?.Female : addon.SkinTexture?.Male;
        return texture is null ? null : Resolve<ITextureSetGetter>(cache, texture.FormKey)?.Diffuse?.DataRelativePath.Path;
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
