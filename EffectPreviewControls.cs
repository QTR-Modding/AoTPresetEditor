using System.Windows;
using System.Windows.Controls;

namespace AoTPresetEditor;

public sealed class EffectPreviewControls : WrapPanel
{
    private readonly Func<FormRow?> selectedEffect;
    private readonly Func<IReadOnlyList<FormRow>> targets;
    private readonly Func<IReadOnlyList<FormRow>> candidates;
    private readonly Action<FormRow>? selectPreviewItem;
    private readonly Action? beforePreview;
    private readonly bool showWhenEmpty;
    private readonly Button preview;
    private readonly Button choose;
    private FormRow? previewItem;

    public EffectPreviewControls(Func<FormRow?> selectedEffect, Func<IReadOnlyList<FormRow>> targets,
        Func<IReadOnlyList<FormRow>> candidates, Action<FormRow>? selectPreviewItem = null,
        Action? beforePreview = null, bool showWhenEmpty = false)
    {
        this.selectedEffect = selectedEffect; this.targets = targets; this.candidates = candidates;
        this.selectPreviewItem = selectPreviewItem; this.beforePreview = beforePreview; this.showWhenEmpty = showWhenEmpty;
        choose = EditorControls.Button("Choose preview item...", ChooseItem);
        preview = EditorControls.Button("Preview on item...", PreviewEffect);
        ToolTipService.SetShowOnDisabled(preview, true);
        Children.Add(choose); Children.Add(preview);
        Refresh();
    }

    public IReadOnlyList<FormRow> PreviewTargets()
    {
        var current = targets();
        if (current.Count > 0) return current;
        if (previewItem is { } item && candidates().Contains(item) && ModelPreviewWindow.CanPreview(item)) return [item];
        previewItem = null;
        return [];
    }

    public void SelectPreviewItem(FormRow item)
    {
        if (selectPreviewItem is not null) selectPreviewItem(item);
        else previewItem = item;
        Refresh();
    }

    public void Refresh()
    {
        var effect = selectedEffect();
        Visibility = effect?.Type is "ArtObject" or "EffectShader" || effect is null && showWhenEmpty
            ? Visibility.Visible : Visibility.Collapsed;
        var current = PreviewTargets();
        preview.IsEnabled = current.Any(ModelPreviewWindow.CanPreview) && ModelPreviewWindow.CanPreviewEffect(effect);
        preview.ToolTip = current.Count == 0 ? "Select an item to preview this effect."
            : !current.Any(ModelPreviewWindow.CanPreview) ? "The selected item has no model to preview."
            : effect is null ? "Select an effect to preview."
            : effect.Type == "ArtObject" && !ModelPreviewWindow.CanPreview(effect) ? "The selected art object has no model to preview."
            : !ModelPreviewWindow.CanPreviewEffect(effect) ? "The selected effect shader has no preview data."
            : current.Count > 1 ? "Choose which affected item to preview this effect on."
            : "Preview this effect on " + (current[0].Name.Length > 0 ? current[0].Name : current[0].EditorId.Length > 0
                ? current[0].EditorId : current[0].Identifier) + ".";
        choose.Visibility = candidates().Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FormRow? PickItem(IReadOnlyList<FormRow> available)
    {
        var picker = new FormPickerWindow(available, purpose: "Choose an item to preview this effect on") { Owner = Window.GetWindow(this) };
        return picker.ShowDialog() == true ? picker.SelectedForms.FirstOrDefault() : null;
    }

    private void ChooseItem()
    {
        beforePreview?.Invoke();
        if (PickItem(candidates()) is { } item) SelectPreviewItem(item);
    }

    private void PreviewEffect()
    {
        beforePreview?.Invoke();
        var effect = selectedEffect();
        var available = PreviewTargets().Where(ModelPreviewWindow.CanPreview).ToArray();
        if (!ModelPreviewWindow.CanPreviewEffect(effect) || available.Length == 0) return;
        var target = available.Length == 1 ? available[0] : PickItem(available);
        if (target is not null) new ModelPreviewWindow(target, effect) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
