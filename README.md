# AoT Preset Editor

Read-only prototype for browsing Alchemy of Time presets and Skyrim Special Edition forms through Mod Organizer 2.

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

## Browse

1. **Forms:** search by name, EditorID, local ID, or plugin. Filter by record type or plugin. Selecting a form shows an AoT identifier such as `0x65C99~Skyrim.esm`, which **Copy identifier** copies. The winning plugin supplies the displayed values; the identifier refers to the plugin that defined the form.
2. **Presets:** filter by type (FOOD, BOOK, etc.) and kind (Default, Custom, Addon), then select an installed `.yml` file to see its original text and its expanded templates and merges. Expansion uses CLibUtilsQTR 2.6.0. It does not validate AoT rules, form availability, or gameplay conditions.
3. **Load order:** inspect the enabled plugins the browser read, in load order.

The form browser indexes plugin records, including locations, keywords, perks, and form lists. It excludes placed references, cells, landscape, navigation meshes, and dialogue responses. Forms created while Skyrim is running are not available. Search results display up to 1,000 matches; narrow the search to see others.

The prototype does not edit or save presets or plugin records.
