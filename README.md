# AoT Preset Editor

Create and edit Alchemy of Time presets using the forms available in your Mod Organizer 2 profile.

## Build

Install the .NET 10 SDK and Visual Studio with Desktop development with C++ and CMake tools. Run:

```powershell
.\build.ps1
```

The complete app is written to `dist`. Keep that folder together; it includes the .NET runtime and the native YAML helper.

## Run through MO2

1. Add `dist\AoTPresetEditor.exe` as an MO2 executable.
2. Set its arguments to `--data "C:\path\to\Skyrim Special Edition\Data"`.
3. Select your profile in MO2 and run the editor.

MO2 supplies that profile's plugin list and virtual Data folder. Running outside MO2 reads the normal game installation instead.

## Appearance

The editor starts in Dark mode. **Tools > Appearance** offers Dark, Light, or Follow Windows, plus a button accent color. Changes preview immediately; **Save** remembers them, and **Cancel** restores the previous appearance. Preferences are stored in `%LOCALAPPDATA%/AoTPresetEditor/appearance.json`, separately from presets.

## Edit a preset

1. Choose **File > New preset**, an item type, and a preset kind. **Custom** defines stages for selected items, **Addon** adds triggers or filters, and **Default** supplies the fallback rule for an item type. **File > Installed presets** opens the file browser; selecting a file hides the browser to make room for editing.
2. Open **Add block > Add rule** and give it a name. The left list shows rules and their affected items and results. Select one to edit its settings alongside the list. Drag the divider to resize the list, or choose **Focus editor** to hide it. **Show rule list** brings it back. The selected block's actions menu contains delete and reorder commands.
3. Required and configured settings appear first; expand the other sections to see optional settings. **Help** beside a field explains its meaning and units. **Choose forms** searches your profile, limited to forms accepted by that field. Form lists show the selected items; **Edit entry** opens an identifier for manual editing. Stages and transformations show their editable settings immediately when selected. Move transformations up or down to set their priority; the first matching trigger wins.
4. Under **Add block**, **Add shared settings** creates named values that several rules can reuse. **Add template** creates a reusable structure with parameters that vary between uses. Choose the content type, then **One entry** or **A list**. Within a template, **Use parameter** names a field's parameter. Each use provides its own arguments.
5. Inherited fields show their resulting values and source after validation. **Override here** changes a field for this use. Editing the shared definition changes all its uses; the editor lists the affected blocks.
6. Validation updates as you edit. **Review issues** lists problems; selecting one opens its rule and focuses the relevant field. **Save draft** keeps unfinished work and new form groups in an `.aotdraft` file. It stays available in **File** when the preset is valid. Reopen it with **File > Open file or draft** to continue.
7. Once valid, **Save to mod** writes the preset and new groups into the correct `SKSE/Plugins/AlchemyOfTime/<type>/...` path. Choose the mod's top-level folder. Your installed files change only on this action.

**View YAML** opens a read-only code viewer with syntax colors, folding, search, copy, and text zoom. **Preset YAML** shows what will be saved. **Expanded rules** shows the values after applying shared settings and template arguments. The editor's naming metadata is folded at the top of the source; copying includes it.

Existing templates, anchors, and rule order are retained. Saving normalizes YAML formatting and comment placement. The editor stores block names in a leading comment that AoT ignores. Original files are changed only when you explicitly select their mod folder and confirm replacement.

Validation checks AoT field requirements, values, stage numbering, templates, and shared references. Form pickers use the active plugin data; manually entered identifiers still need to exist in your game. A valid structure does not guarantee the intended gameplay behavior.

## Use form groups

1. Open a trigger's **Trigger** field and choose **Choose form group**. Select a group to inspect its members, then **Choose group** to use its name in the preset. The list includes groups from the active MO2 profile.
2. To make one, choose **New group**, enter a name, and use **Add forms** to select its members. **Create group** returns to the list. Choose that group to use it in your current field. Member status identifies unavailable forms and forms that do not apply to that field.
3. **Save to mod** writes new groups to `SKSE/Plugins/AlchemyOfTime/formGroups/<name>.txt` alongside the preset. **Save draft** keeps them for later. Canceling a picker or a separate editing dialog discards changes made within that dialog. Existing group files are read-only in the editor.

Groups are lists of literal form identifiers. AoT accepts them in addon **Applies to**, rule **Evolution filter**, and trigger **Trigger** and **Inventory filter** fields. Custom **Applies to** accepts item identifiers or name words instead. The group chooser preserves unresolved members, including identifiers from optional mods.

## Browse forms and installed presets

1. **Tools > Form browser:** search by name, EditorID, local ID, or plugin. Filter by record type or plugin. Selecting a form shows an AoT identifier such as `0x65C99~Skyrim.esm`, which **Copy identifier** copies. The winning plugin supplies the displayed values; the identifier refers to the plugin that defined the form.
2. **Presets:** filter by type (FOOD, BOOK, etc.) and kind (Default, Custom, Addon), then select an installed `.yml` file. Its named blocks appear in the overview. Expansion uses CLibUtilsQTR 2.6.0.
3. **Game data:** choose or reload the Skyrim Data folder and inspect its enabled plugins in load order.

The form browser indexes plugin records, including locations, keywords, perks, and form lists. It excludes placed references, cells, landscape, navigation meshes, and dialogue responses. Forms created while Skyrim is running are not available. Search results display up to 1,000 matches; narrow the search to see others.

Plugin records are read-only.

## Checks

After building, run `dotnet run --project tests -- dist/AoTYaml.exe`. Add a preset directory as the last argument to check that importing and exporting those files preserves their expanded settings.
