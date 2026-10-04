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

1. Choose **New preset** and give it a name, or open an installed preset from the list. **Back to presets** returns to that list and keeps your unfinished edit; **Resume editing** brings it back. Opening a different file asks about unsaved work. **Add behavior** asks which item type it affects and what it should do. **Stages** is selected by default: choose items and define the states they pass through. The other choices add reactions or preservation, or provide fallback aging for that item type. The editor chooses AoT's file types and paths from these choices.
2. The left list shows your behaviors. Select one to edit its settings alongside the list. Drag the divider to resize the list, or choose **Focus editor** to hide it. **Show behaviors** brings it back. The selected behavior's actions menu contains delete and available reorder commands.
3. Required settings appear first; expand the other sections to see optional settings. **Help** beside a field explains its meaning and units. **Choose forms** searches your profile, limited to forms accepted by that field. Lists show a summary of each entry. **Edit entry** opens its settings; **Hide entry** closes them without losing changes. Entire list sections can also be collapsed. Move transformations up or down to set their priority; the first matching trigger wins.
4. To reuse a behavior with different values, check **Variable** beside a field and give it a name. Check **Applies to** and **Final item**, for example, to pair each source item with its result. **Variables** shows one column per name and one row per use. **Settings** holds the values shared by every row. Entire lists can be variables too. Unchecking **Variable** removes its column and clears the field so you can enter a shared value.
5. In **Variables**, select a cell to edit its value below the table. **Add row** adds an empty row. Its arrow offers form selection for each form variable, adding one row per selected form. Right-click the table to duplicate or remove selected rows, paste tab-separated cells, or fill selected rows with the current value. Pasting starts at the selected cell; each line fills a row, with tabs separating columns. Click **Settings** to return; your edits are kept.
6. Validation updates as you edit. **Review issues** lists problems; selecting one opens its rule and focuses the relevant field. **Save draft** keeps unfinished work and new form groups in an `.aotdraft` file. It stays available in **File** when the preset is valid. Reopen it with **File > Open file or draft** to continue.
7. Once valid, **Save to mod** writes the behavior files and new groups into the correct `SKSE/Plugins/AlchemyOfTime/<type>/...` paths. Choose the mod's top-level folder. A preset can contain several item types and kinds of behavior; its draft keeps them together. **File > Import into this preset** adds an existing file to the workspace. Imported files keep their basenames and internal rule order.

**View YAML** opens a read-only code viewer with syntax colors, folding, search, copy, and text zoom. **Preset YAML** shows what will be saved. **Expanded rules** shows the values after applying shared settings and template arguments. The editor's naming metadata is folded at the top of the source; copying includes it.

Direct uses of the same rule template appear as rows of one behavior. More complex imported references retain their existing editing controls. The editor simplifies single-item selectors and redundant parameterless templates where comments, references, and rule order can be preserved. Saving normalizes YAML formatting and comment placement. The editor stores names in a leading comment that AoT ignores. Original files are changed only when you explicitly select their mod folder and confirm replacement. **Reset** clears the whole open preset after confirmation; saved files stay on disk. Removing every behavior from an imported file leaves a validation issue; export does not delete files.

Fields appear when AoT can use them. Stage 0 uses the original item; later FOOD and MISC stages can generate items with property overrides. Explicit replacement items retain their own properties. Saved settings that have no effect appear under **Unused** and stay in the file until you remove them. Final results accept all bound-object types; intermediate stage items must also fit the source item.

Validation checks AoT field requirements, values, stage numbering, templates, shared references, and known form types. It detects incompatible replacements, reuse of a source as a later stage, and duplicate replacements written with different identifiers. Source items need an EditorID, including when selected by numeric ID. Unknown identifiers still need to exist in your game. Test the exported preset in game to check its behavior.

## Use form groups

1. Open a trigger's **Trigger** field and choose **Choose form group**. Select a group to inspect its members, then **Choose group** to use its name in the preset. The list includes groups from the active MO2 profile.
2. To make one, choose **New group**, enter a name, and use **Add forms** to select its members. **Create group** returns to the list. Choose that group to use it in your current field. Member status identifies unavailable forms and forms that do not apply to that field.
3. **Save to mod** writes new groups to `SKSE/Plugins/AlchemyOfTime/formGroups/<name>.txt` alongside the preset. **Save draft** keeps them for later. Canceling a picker or a separate editing dialog discards changes made within that dialog. Existing group files are read-only in the editor.

Groups are lists of literal form identifiers. AoT accepts them in addon **Applies to**, rule **Evolution filter**, and trigger **Trigger** and **Inventory filter** fields. Custom **Applies to** accepts item identifiers or name words instead. The group chooser preserves unresolved members, including identifiers from optional mods.

## Browse forms and installed presets

1. **Tools > Form browser:** search by name, EditorID, local ID, or plugin. Filter by record type or plugin. Selecting a form shows an AoT identifier such as `0x65C99~Skyrim.esm`, which **Copy identifier** copies. The winning plugin supplies the displayed values; the identifier refers to the plugin that defined the form.
2. **Presets:** filter by item type and behavior, then select an installed `.yml` file. Its behaviors appear in the overview. Expansion uses CLibUtilsQTR 2.6.0.
3. **Game data:** choose or reload the Skyrim Data folder and inspect its enabled plugins in load order.

The form browser indexes plugin records, including locations, keywords, perks, and form lists. It excludes placed references, cells, landscape, navigation meshes, and dialogue responses. Forms created while Skyrim is running are not available. Search results display up to 1,000 matches; narrow the search to see others.

Plugin records are read-only.

## Checks

After building, run `dotnet run --project tests -- dist/AoTYaml.exe`. Add a preset directory as the last argument to check that importing and exporting those files preserves their expanded settings.
