using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AoTPresetEditor;

internal static class CreationDialogChecks
{
    public static void Run()
    {
        var workspace = new PresetWorkspace();
        var host = new Window { Content = workspace, Width = 1000, Height = 750, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(host.Dispatcher));
        try
        {
            host.Show();
            Submit(workspace.New, "New preset", "Create", "Enter preset");
            Require(Field<PresetProject>(workspace, "project").Name == "Enter preset", "Enter must create the named preset.");
            Submit(() => Invoke(workspace, "AddBehavior"), "Add behavior", "Create", "Enter behavior");
            var document = Field<PresetDocument>(workspace, "document");
            Require(document.Blocks.Single().Name == "Enter behavior", "Enter must create the named behavior.");
            Invoke(workspace, "ResetEditor");

            var behavior = new BehaviorEditorControl(document, [], document.Blocks.Single().Id);
            host.Content = behavior;
            string? variableName = null;
            Submit(() => variableName = (string?)Invoke(behavior, "ChooseVariableName",
                PresetSchema.GetFields("CustomRule", PresetKind.Custom).Single(field => field.Key == "owners")),
                "Name variable", "Create", "raw");
            Require(variableName == "raw", "Enter must accept the chosen variable name.");

            var value = new ValueEditorControl(document, [], PresetValue.Scalar(""), "Value", "Value", parametersAllowed: true);
            host.Content = value;
            Submit(() => Invoke(value, "ChooseParameter"), "Template parameter", "Use parameter", "duration");
            Require(value.Value.Text == "$duration", "Enter must use the named parameter.");

            value = new ValueEditorControl(document, [], PresetValue.Map(), "Value", "Value");
            host.Content = value;
            Submit(() => Invoke(value, "AddSetting"), "Add setting", "Add", "duration");
            Require(value.Value.Get("duration") is not null, "Enter must add the named setting.");

            var group = new GroupEditorWindow([], []) { Owner = host, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            Submit(() => group.ShowDialog(), "New form group", "Create group", "Meat");
            Require(group.Group?.Name == "Meat", "Enter must create the named form group.");
        }
        finally
        {
            Invoke(workspace, "ResetEditor");
            host.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void Submit(Action open, string title, string action, string text)
    {
        Exception? failure = null;
        var response = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var dialog = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                .OfType<Window>().Single(window => window.Title == title);
            try
            {
                dialog.Opacity = 0; dialog.ShowInTaskbar = false;
                var input = Descendants<TextBox>(dialog).Single();
                input.Text = text;
                var button = Descendants<Button>(dialog).Single(item => Equals(item.Content, action));
                Require(button.IsDefault && button.Visibility == Visibility.Visible, title + " must offer its creation action on Enter.");
                input.Focus(); dialog.UpdateLayout();
                AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog), "\r", false);
                Require(dialog.DialogResult == true, "WPF's Enter access key must submit " + title + ".");
            }
            catch (Exception error) { failure = error; if (dialog.IsVisible) dialog.DialogResult = false; }
        }));
        try { open(); }
        finally { response.Abort(); }
        if (failure is not null) throw failure;
        Require(response.Status == DispatcherOperationStatus.Completed, title + " must open for the default-key test.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static object? Invoke(object owner, string name, params object?[] args) =>
        owner.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
