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

For **Help**, add `msedgewebview2.exe` to MO2's executable blacklist. The default browser also needs an entry for **Open in browser** (`msedge.exe` for Edge). This lets the browsers run without MO2's filesystem hooks, which can prevent them from starting.

## Appearance

The editor starts in Dark mode. **Tools > Appearance** offers Dark, Light, or Follow Windows, plus a button accent color. Changes preview immediately; **Save** remembers them, and **Cancel** restores the previous appearance. Preferences are stored in `%LOCALAPPDATA%/AoTPresetEditor/appearance.json`, separately from presets.

## Edit a preset

1. Choose **New preset** and give it a name, or open an installed preset from the list. **Back to presets** returns to that list and keeps your unfinished edit; **Resume editing** brings it back. Opening a different file asks about unsaved work. **Add behavior** asks for a name and item type. In **Settings**, choose the affected items, then use **Add stages**, **Add transformation**, or **Add time modifier**. These sections can be combined in the same behavior. The editor chooses AoT's file types and paths from the settings you use.
2. The left list shows your behaviors. Select one to edit its settings alongside the list. The compact buttons above it add a behavior (**+**), move the selection up or down, and delete it with the bin icon. Hover over a button for its label. Unavailable actions are disabled; deleting still asks for confirmation. Drag the divider to resize the list, or choose **Focus editor** to hide it. **Show behaviors** brings it back.
3. A newly added section opens for editing; configured sections can be collapsed. **Final item** belongs to **Stages**. The information icon beside a field explains its meaning and units. **Choose forms** searches your profile, limited to forms accepted by that field. Lists show a summary of each entry. **Edit entry** opens its settings; **Hide entry** closes them without losing changes. Move transformations up or down to set their priority; the first matching trigger wins. To define default stages for an item type, use **All food** (or the corresponding item type) in **Applies to**. The editor asks before clearing selected items and adds Stage 0 when there are no stages yet.
4. To reuse a behavior with different values, check **Variable** beside a field and give it a name. Check **Applies to** and **Final item**, for example, to pair each source item with its result. **Variables** shows one column per name and one row per use. **Settings** holds the values shared by every row. For stages, make individual fields variable, such as duration or stage item. Unchecking **Variable** removes its column and clears the field so you can enter a shared value.
5. In **Variables**, select a cell to edit its value below the table. **Add row** adds an empty row. Its arrow offers form selection for each form variable, adding one row per selected form. Select a range with Shift-click or Shift+Up/Down; Delete or Backspace removes the selected rows when the table has focus. Right-click the table to duplicate rows, paste tab-separated cells, or fill selected rows with the current value. Pasting starts at the selected cell; each line fills a row, with tabs separating columns. Click **Settings** to return; your edits are kept.
6. Validation updates as you edit. **Review issues** numbers the problems and shows each location, including its behavior, stage, or Variables row. Drag the panel's top edge to resize it; the mouse wheel moves between issues, and the scrollbar lets you read longer messages. Select an issue to open and focus that setting. File-wide problems have their own entry in the left list, with the file's path and details. **Save draft** keeps unfinished work and new form groups in an `.aotdraft` file. It stays available in **File** when the preset is valid. Reopen it with **File > Open file or draft** to continue.
7. Once valid, **Save to mod** or **Ctrl+S** writes the behavior files and new groups into the correct `SKSE/Plugins/AlchemyOfTime/<type>/...` paths. Opened presets save back to their existing paths. New presets ask for the mod's top-level folder on their first save. **File > Save to mod as...** chooses another destination. A preset can contain several item types and kinds of behavior; its draft keeps them together. **File > Open file or draft** and **Import into this preset** accept multiple YAML files or folders. In the picker, select folders and click **Open selected folders** to include their YAML presets and subfolders. Editor drafts open individually. Imported files keep their basenames and internal rule order.

**View YAML** opens a read-only code viewer with syntax colors, folding, search, copy, and text zoom. **Preset YAML** shows what will be saved. **Expanded rules** shows the values after applying shared settings and template arguments. The editor's naming metadata is folded at the top of the source; copying includes it.

Direct uses of the same rule template appear as rows of one behavior. More complex imported references retain their existing editing controls. The editor simplifies single-item selectors and redundant parameterless templates where comments, references, and rule order can be preserved. Saving normalizes YAML formatting and comment placement. The editor stores names in a leading comment that AoT ignores. Ordinary saves update the opened files. When adding or removing stages changes an output path, saving offers to retain the previous file as an inactive `.aot-backup` copy. Originals outside the chosen folder stay unchanged; the save dialog identifies files that must be disabled separately to avoid loading both versions. **Reset** clears the whole open preset after confirmation; saved files stay on disk. Deleting the last behavior removes an empty unsaved file from the workspace. For a previously saved file, **Save to mod** asks before disabling it and keeps an inactive backup. A file that was already empty when opened remains an issue until you choose **Remove file on save** in its details.

Deleting every Variables row keeps the behavior's settings and variable columns. **Add row** starts filling them again. You can save the empty table in a draft; saving to a mod requires at least one row.

Fields appear when AoT can use them. Stage 0 uses the original item; later FOOD and MISC stages can generate items with property overrides. Explicit replacement items retain their own properties. Saved settings that have no effect appear under **Unused** and stay in the file until you remove them. Final results accept all bound-object types; intermediate stage items must also fit the source item.

Validation checks AoT field requirements, values, stage numbering, templates, shared references, and known form types. It detects incompatible replacements, reuse of a source as a later stage, and duplicate replacements written with different identifiers. Source items need an EditorID, including when selected by numeric ID. Unknown identifiers still need to exist in your game. Test the exported preset in game to check its behavior.

Trigger fields also accept art objects and effect shaders applied to the world item itself or a nearby reference. Changes to these effects are detected on the next scan. Skyrim form lists can supply several triggers; AoT reads only their plugin-defined members when loading settings; members added by scripts are not included. Select a Skyrim form list in the picker and use **View members...** to inspect its members, including nested lists. Trigger validation checks those members, including lists used through form groups, while preserving the authored list identifiers in YAML. These trigger choices require an AoT build containing the corresponding support.

Transformations and time modifiers need stages, which can come from another preset. The editor checks the current workspace and loaded presets and shows a warning when it cannot find or verify those stages. This warning does not prevent saving.

## Preview world effects

1. Under **World effects**, click the color swatch beside **Tint**. Choose a color and opacity, then **Apply**. **Cancel** keeps the original value; **No tint** explicitly removes an inherited tint. The hexadecimal field remains editable.
2. Select a **Sound** and press **Play** to hear its audio sample. **Stop** ends playback. **Volume** adjusts playback immediately and stays shared between previews during the session; zero mutes it. Sounds with several samples have a sample selector. These controls are also available when selecting sounds in a form picker or the form browser. Changing the selection or leaving the preview stops playback.

The tint picker writes quoted `#RRGGBB` or `#RRGGBBAA` values and requires an AoT build with [the tint encoding update](https://github.com/QTR-Modding/AlchemyOfTime/pull/55). Older color values keep their existing meaning. Sound previews play the audio file directly; Skyrim's positional audio and other sound settings are not simulated.

## Preview models and textures

Select a form and choose **Preview model...** to inspect its model without running Skyrim. The action is available in form pickers, fields, lists, and form groups. Drag to rotate, scroll to zoom, and use **Reset view** to fit the model again. When a form has several models, the menu at the top lets you choose between them, including armor variants and separate outfit pieces. **Show geometry** makes transparent surfaces visible without their textures.

The preview uses the active profile's winning models and textures, including supported normal maps, shine, glow, reflections, and vertex colors. NPCs can show their default outfit, body, and exported head in a fixed pose, with skin and hair colors. The preview identifies missing parts and appearance details it cannot reproduce.

Models with supported animation offer **Play/Pause**, a position slider, and a clip selector when several animations are available. These include opening containers and art-object movement, fades, and moving textures. Supported particle emitters can also play, including world-space motion, planar gravity, drag, animated forces, colors, fades, and palettes. Particle physics uses fixed simulation steps and repeatable turbulence for seeking. Lighting and effects can differ from Skyrim; unsupported animation, particle physics, and material features are identified in the preview.

Art objects and effect shaders offer **Preview on item...** in their preset fields. The preview uses the stage item when specified, otherwise an affected item. When several items apply, choose one for the preview. Behaviors covering a whole item type offer **Choose preview item...**; this selection is only for viewing and does not change the preset. The preview button stays disabled until an item with a model is selected.

Applied effects and standalone art objects start playing automatically. Art objects play supported model animation and particles alongside the item. Named attachments use the matching item node, or the item's root when the node is missing, as Skyrim does. Effect shaders show animated surface colors, textures, fades, and particles. These are offline approximations: Skyrim's blending, per-pixel rim lighting, and dissolve effects are not reproduced exactly. The preview reports unsupported features.

**Preview textures...** shows the source images used by effect shaders and texture sets. Cube maps appear as six faces in a cross. These images help identify the selected effect; they do not play the effect as it appears in Skyrim.

## Use form groups

A form group is a named list of forms. For example, select Raw Beef and Venison in **Applies to**, then choose **Create group from list...**. The group editor opens with those items filled in. Name the group `RawMeat` and choose **Create group**. **Applies to** now uses `RawMeat` instead of listing both items. You can change the members before creating the group; canceling keeps the original list.

1. To reuse it, choose **Choose form group...** in another **Applies to**, **Trigger**, or **Containers** field. Select `RawMeat`, inspect its members, then choose **Choose**. Groups from the active MO2 profile and new groups in the open preset are available here.
2. To create a group from scratch, choose **New group...** in that picker, enter a name, and use **Add forms...** to select its members. **Create group** returns to the picker. Select the group and choose **Choose** to use it.
3. **Save to mod** writes new groups to `SKSE/Plugins/AlchemyOfTime/formGroups/<name>.txt` alongside the preset. **Save draft** keeps them for later. Canceling a picker or a separate editing dialog discards changes made within that dialog. Existing group files are read-only in the editor.

Groups contain literal form identifiers, including identifiers from optional mods that are unavailable in the loaded profile. **Create group from list...** is available for lists of individual forms; lists containing other groups, variables, or name selectors retain their existing editing controls.

Adding or removing stages keeps a selected group's name, so later edits to its file still affect the selection when AoT next loads its settings. Form groups in custom `owners` require an AoT build containing [owner form-group support](https://github.com/QTR-Modding/AlchemyOfTime/pull/53).

Removing stages from a behavior that selects items by name words still asks before replacing those words with the currently matching items, because addon selectors do not match item names.

## Browse forms and installed presets

1. **Tools > Form browser:** search by name, EditorID, local ID, or plugin. Filter by record type or plugin. Selecting a form shows an AoT identifier such as `0x65C99~Skyrim.esm`, which **Copy identifier** copies. The winning plugin supplies the displayed values; the identifier refers to the plugin that defined the form.
2. **Presets:** filter by item type and behavior, then select an installed `.yml` file. Its behaviors appear in the overview. Expansion uses CLibUtilsQTR 2.6.0.
3. **Game data:** choose or reload the Skyrim Data folder and inspect its enabled plugins in load order.
4. **Help:** read the live AoT wiki inside the editor. Switching back to **Presets** keeps your unfinished edit. This uses the Microsoft Edge WebView2 Runtime.

The form browser indexes plugin records, including locations, keywords, perks, and form lists. It excludes placed references, cells, landscape, navigation meshes, and dialogue responses. Forms created while Skyrim is running are not available. Search results display up to 1,000 matches; narrow the search to see others.

Plugin records are read-only.

## Checks

After building, run `dotnet run --project tests -- dist/AoTYaml.exe`. Add a preset directory as the last argument to check that importing and exporting those files preserves their expanded settings.

## License

AoT Preset Editor is licensed under the [GNU General Public License, version 3](LICENSE) (GPL-3.0-only). Third-party license texts and notices are included in `licenses`.
