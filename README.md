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

## Edit a preset

1. Open **Presets**, choose **New preset**, and select an item type and Default, Custom, or Addon. You can also open an installed preset from the list.
2. Choose **Add rule**, **Add shared settings**, or **Add template** and give the block a name. Shared settings provide reusable values. Templates provide reusable settings with parameters that vary between uses.
3. Open a named block to edit its fields. Check a field to set it; uncheck it to leave it unset. **Edit** opens the field's values or entries. **Choose form** searches your profile's forms.
4. Within a template, **Use parameter** gives a field a parameter name. When using that template, the editor provides a field for each parameter. **Inherited settings** combines reusable settings with fields set on the current block.
5. Apply changes to return to the overview. Incomplete blocks remain editable. A cross marks problems; open the block to see them. A tick means its structure passes validation.
6. Once valid, **View YAML** shows the preset and its expanded rules. **Save to mod** asks for a destination mod folder and writes the correct `SKSE/Plugins/AlchemyOfTime/<type>/...` path inside it. Choose that mod's top-level folder.

Existing templates, anchors, and rule order are retained. Saving normalizes YAML formatting and comment placement. The editor stores block names in a leading comment that AoT ignores. Original files are changed only when you explicitly select their mod folder and confirm replacement.

Validation checks AoT field requirements, values, stage numbering, templates, and shared references. It does not simulate gameplay or verify custom form-group definitions. Form pickers use the active plugin data; manually entered identifiers still need to exist in your game.

## Browse forms and installed presets

1. **Forms:** search by name, EditorID, local ID, or plugin. Filter by record type or plugin. Selecting a form shows an AoT identifier such as `0x65C99~Skyrim.esm`, which **Copy identifier** copies. The winning plugin supplies the displayed values; the identifier refers to the plugin that defined the form.
2. **Presets:** filter by type (FOOD, BOOK, etc.) and kind (Default, Custom, Addon), then select an installed `.yml` file. Its named blocks appear in the overview. Expansion uses CLibUtilsQTR 2.6.0.
3. **Load order:** inspect the enabled plugins the browser read, in load order.

The form browser indexes plugin records, including locations, keywords, perks, and form lists. It excludes placed references, cells, landscape, navigation meshes, and dialogue responses. Forms created while Skyrim is running are not available. Search results display up to 1,000 matches; narrow the search to see others.

Plugin records are read-only.

## Checks

After building, run `dotnet run --project tests -- dist/AoTYaml.exe`. Add a preset directory as the last argument to check that importing and exporting those files preserves their expanded settings.
