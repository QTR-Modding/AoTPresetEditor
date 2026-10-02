using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AoTPresetEditor;

public sealed class ValueEditorWindow : Window
{
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? previewCancellation;
    private readonly string? blockId;
    private readonly PresetPresentation presentation;
    public ValueEditorControl Editor { get; }
    public PresetValue Value => Editor.Value;
    public PresetDocument Document => Editor.Document;
    public string BlockName => Editor.BlockName;

    public ValueEditorWindow(PresetDocument document, IReadOnlyList<FormRow> forms, PresetValue value,
        string context, string title, bool parametersAllowed = false, PresetField? field = null, string? blockName = null,
        IReadOnlyList<string>? issues = null, IReadOnlyList<FormGroupDefinition>? groups = null)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = blockName is null ? title : blockName + " - " + title;
        MinWidth = 620; MinHeight = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        EditorControls.FitToOwner(this);
        Editor = new ValueEditorControl(document, forms, value, context, title, parametersAllowed, field, blockName, issues, groups);
        blockId = document.Blocks.FirstOrDefault(block => ReferenceEquals(block.Value, value))?.Id;
        presentation = new PresetPresentation(Editor.Document, forms, groups);
        previewTimer.Tick += async (_, _) => { previewTimer.Stop(); await RefreshPreview(); };
        Editor.Changed += (_, _) =>
        {
            previewCancellation?.Cancel();
            if (blockId is null) return;
            previewTimer.Stop(); previewTimer.Start();
        };
        Loaded += async (_, _) => await RefreshPreview();
        Closed += (_, _) => { previewTimer.Stop(); previewCancellation?.Cancel(); };
        var layout = new DockPanel { Margin = new Thickness(14) }; var footer = new StackPanel();
        var error = EditorControls.Text(""); error.Visibility = Visibility.Collapsed; footer.Children.Add(error);
        var apply = EditorControls.Button("Apply", () =>
        {
            Editor.Capture();
            if (blockName is not null && BlockName.Length == 0)
            { error.Text = "Give this block a name."; error.Visibility = Visibility.Visible; Editor.FocusIssue(error.Text); return; }
            DialogResult = true;
        });
        apply.SetResourceReference(StyleProperty, "PrimaryButton");
        apply.IsDefault = true;
        var cancel = EditorControls.Button("Cancel", () => DialogResult = false); cancel.IsCancel = true;
        void UpdateApply()
        {
            var missingName = blockName is not null && BlockName.Length == 0;
            apply.Visibility = missingName ? Visibility.Collapsed : Visibility.Visible;
            error.Text = missingName ? "Give this block a name." : "";
            error.Visibility = missingName ? Visibility.Visible : Visibility.Collapsed;
        }
        Editor.Changed += (_, _) => UpdateApply(); UpdateApply();
        EditorControls.Footer(footer, apply, cancel);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = Editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = layout;
    }

    private async Task RefreshPreview()
    {
        if (blockId is null) return;
        previewCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        previewCancellation = cancellation;
        try
        {
            Editor.Capture();
            var snapshot = Editor.Document.Clone();
            var before = snapshot.Clone();
            var block = snapshot.Blocks.First(candidate => candidate.Id == blockId);
            block.Value = Editor.Value.Clone();
            snapshot.RebindTemplateCalls(before);
            var validation = await PresetValidation.Check(snapshot, cancellation.Token, presentation);
            cancellation.Token.ThrowIfCancellationRequested();
            presentation.Update(snapshot, validation.Expanded);
            Editor.UpdatePresentation(presentation, presentation.EffectiveValue(block));
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!cancellation.IsCancellationRequested) Editor.UpdatePresentation(presentation);
        }
        finally { if (ReferenceEquals(previewCancellation, cancellation)) previewCancellation = null; }
    }
    public static PresetValue NewValue(string context, PresetField? field = null) => ValueEditorControl.NewValue(context, field);
    public static string Summary(PresetValue value, PresetDocument document) => ValueEditorControl.Summary(value, document);
}
