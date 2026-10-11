using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AoTPresetEditor;

internal static class PresetOpenDialog
{
    private const uint OpenFoldersButton = 1;
    private const int Success = 0;
    private const int Canceled = unchecked((int)0x800704C7);
    private const uint DefaultResponse = 0;
    private const uint SelectedViewItems = 1;

    public static IReadOnlyList<string>? Show(Window owner, bool includeDrafts)
    {
        owner.Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(owner).EnsureHandle();
        var dialog = (IFileOpenDialog)new NativeFileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | DialogOptions.ForceFileSystem | DialogOptions.AllowMultiSelect |
                DialogOptions.PathMustExist | DialogOptions.FileMustExist | DialogOptions.NoChangeDirectory);
            dialog.SetTitle(includeDrafts ? "Open AoT preset or draft" : "Import behaviors");
            FilterSpec[] filters = includeDrafts
                ? [new("Presets and drafts", "*.yml;*.yaml;*.aotdraft"), new("YAML presets", "*.yml;*.yaml"), new("Editor drafts", "*.aotdraft")]
                : [new("YAML presets", "*.yml;*.yaml")];
            dialog.SetFileTypes((uint)filters.Length, filters);
            dialog.SetFileTypeIndex(1);
            var controls = (IFileDialogCustomize)dialog;
            controls.AddPushButton(OpenFoldersButton, "Open selected folders");
            controls.SetControlState(OpenFoldersButton, ControlState.Visible);
            var events = new DialogEvents(dialog, controls);
            dialog.Advise(events, out var cookie);
            try
            {
                int result;
                ComponentDispatcher.PushModal();
                try { result = dialog.Show(handle); }
                finally { ComponentDispatcher.PopModal(); }
                if (result == Canceled) return null;
                Marshal.ThrowExceptionForHR(result);
                if (events.Folders is not null) return events.Folders;
                dialog.GetResults(out var items);
                try { return ReadPaths(items); }
                finally { Marshal.ReleaseComObject(items); }
            }
            finally { dialog.Unadvise(cookie); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    private static string[] ReadPaths(IShellItemArray items)
    {
        items.GetCount(out var count);
        var paths = new string[count];
        for (uint index = 0; index < count; index++)
        {
            items.GetItemAt(index, out var item);
            try { item.GetDisplayName(ShellDisplayName.FileSystemPath, out paths[index]); }
            finally { Marshal.ReleaseComObject(item); }
        }
        return paths;
    }

    private static string[] SelectedFolders(IFileOpenDialog dialog)
    {
        try
        {
            // GetSelectedItems can omit highlighted folders; read the view selection directly.
            var topLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var browserId = typeof(IShellBrowser).GUID;
            ((IServiceProvider)dialog).QueryService(in topLevelBrowser, in browserId, out var browser);
            try
            {
                browser.QueryActiveShellView(out var view);
                try
                {
                    var arrayId = typeof(IShellItemArray).GUID;
                    var folderView = (IFolderView)view;
                    folderView.ItemCount(SelectedViewItems, out var count);
                    if (count == 0) return [];
                    folderView.Items(SelectedViewItems, in arrayId, out var items);
                    try
                    {
                        var paths = ReadPaths(items);
                        return paths.All(Directory.Exists) ? paths : [];
                    }
                    finally { Marshal.ReleaseComObject(items); }
                }
                finally { Marshal.ReleaseComObject(view); }
            }
            finally { Marshal.ReleaseComObject(browser); }
        }
        catch (COMException) { return []; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DialogEvents(IFileOpenDialog dialog, IFileDialogCustomize controls) : IFileDialogEvents, IFileDialogControlEvents
    {
        public string[]? Folders { get; private set; }

        private void UpdateFolderButton()
        {
            try
            {
                var state = SelectedFolders(dialog).Length > 0
                    ? ControlState.Visible | ControlState.Enabled : ControlState.Visible;
                controls.SetControlState(OpenFoldersButton, state);
            }
            catch (COMException error) { dialog.Close(error.HResult); }
        }

        public void OnButtonClicked(nint customize, uint control)
        {
            if (control != OpenFoldersButton) return;
            try
            {
                var folders = SelectedFolders(dialog);
                if (folders.Length == 0) { UpdateFolderButton(); return; }
                // Close does not populate GetResults; retain the selected paths first.
                Folders = folders;
                dialog.Close(Success);
            }
            catch (COMException error) { dialog.Close(error.HResult); }
        }

        public void OnFileOk(nint sender) { }
        public void OnFolderChanging(nint sender, nint folder) { }
        public void OnFolderChange(nint sender) => UpdateFolderButton();
        public void OnSelectionChange(nint sender) => UpdateFolderButton();
        public void OnShareViolation(nint sender, nint item, out uint response) => response = DefaultResponse;
        public void OnTypeChange(nint sender) => UpdateFolderButton();
        public void OnOverwrite(nint sender, nint item, out uint response) => response = DefaultResponse;
        public void OnItemSelected(nint customize, uint control, uint item) { }
        public void OnCheckButtonToggled(nint customize, uint control, bool isChecked) { }
        public void OnControlActivating(nint customize, uint control) { }
    }

    [Flags]
    private enum DialogOptions : uint
    {
        NoChangeDirectory = 0x8, ForceFileSystem = 0x40, AllowMultiSelect = 0x200,
        PathMustExist = 0x800, FileMustExist = 0x1000
    }

    [Flags]
    private enum ControlState : uint { Enabled = 0x1, Visible = 0x2 }
    private enum ShellDisplayName : uint { FileSystemPath = 0x80058000 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec(string name, string pattern)
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name = name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Pattern = pattern;
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        void QueryService(in Guid service, in Guid iid, out IShellBrowser browser);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out nint window);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
        void InsertMenusSB(nint menu, nint widths);
        void SetMenuSB(nint menu, nint reserved, nint window);
        void RemoveMenusSB(nint menu);
        void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);
        void EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enabled);
        void TranslateAcceleratorSB(nint message, ushort id);
        void BrowseObject(nint item, uint flags);
        void GetViewStateStream(uint mode, out nint stream);
        void GetControlWindow(uint id, out nint window);
        void SendControlMsg(uint id, uint message, nint wParam, nint lParam, out nint result);
        void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object view);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode(out uint mode);
        void SetCurrentViewMode(uint mode);
        void GetFolder(in Guid iid, out nint folder);
        void Item(int index, out nint item);
        void ItemCount(uint flags, out int count);
        void Items(uint flags, in Guid iid, out IShellItemArray items);
    }
    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class NativeFileOpenDialog { }

    // Native vtable order, including inherited methods and unused preceding slots.
    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(nint owner);
        void SetFileTypes(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IFileDialogEvents events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(DialogOptions options);
        void GetOptions(out DialogOptions options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint location);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(in Guid guid);
        void ClearClientData();
        void SetFilter(nint filter);
        void GetResults(out IShellItemArray items);
    }

    [ComImport, Guid("E6FDD21A-163F-4975-9C8C-A69F1BA37034"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogCustomize
    {
        void EnableOpenDropDown(uint control);
        void AddMenu(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void AddPushButton(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void AddComboBox(uint control);
        void AddRadioButtonList(uint control);
        void AddCheckButton(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label, [MarshalAs(UnmanagedType.Bool)] bool isChecked);
        void AddEditBox(uint control, [MarshalAs(UnmanagedType.LPWStr)] string text);
        void AddSeparator(uint control);
        void AddText(uint control, [MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetControlLabel(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetControlState(uint control, out ControlState state);
        void SetControlState(uint control, ControlState state);
    }

    [ComImport, Guid("973510DB-7D7F-452B-8975-74A85828D354"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogEvents
    {
        void OnFileOk(nint dialog);
        void OnFolderChanging(nint dialog, nint folder);
        void OnFolderChange(nint dialog);
        void OnSelectionChange(nint dialog);
        void OnShareViolation(nint dialog, nint item, out uint response);
        void OnTypeChange(nint dialog);
        void OnOverwrite(nint dialog, nint item, out uint response);
    }

    [ComImport, Guid("36116642-D713-4B97-9B83-7484A9D00433"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogControlEvents
    {
        void OnItemSelected(nint customize, uint control, uint item);
        void OnButtonClicked(nint customize, uint control);
        void OnCheckButtonToggled(nint customize, uint control, [MarshalAs(UnmanagedType.Bool)] bool isChecked);
        void OnControlActivating(nint customize, uint control);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(nint context, in Guid handler, in Guid iid, out nint result);
        void GetPropertyStore(uint flags, in Guid iid, out nint store);
        void GetPropertyDescriptionList(nint key, in Guid iid, out nint descriptions);
        void GetAttributes(uint flags, uint mask, out uint attributes);
        void GetCount(out uint count);
        void GetItemAt(uint index, out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, in Guid handler, in Guid iid, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(ShellDisplayName displayName, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    }
}
