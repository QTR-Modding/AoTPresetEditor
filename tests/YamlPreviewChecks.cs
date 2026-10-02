using System.Collections;
using System.Reflection;
using System.Windows.Media;
using AoTPresetEditor;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;

internal static class YamlPreviewChecks
{
    public static void Run()
    {
        const string source = "# AoT Preset Editor: [{\"Name\":\"Beef\"}]\nsettings: &settings\n  duration: 0.06\nformsLists:\n- forms: FoodBeef\n  duration: 2\n- forms: FoodVenison\n  duration: 3\n";
        var text = new TextDocument(source);
        var method = typeof(YamlPreviewWindow).GetMethod("CreateFoldings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var folds = (IReadOnlyList<NewFolding>)method.Invoke(null, [text, true])!;
        var metadata = folds.Single(fold => fold.StartOffset == 0);
        Require(metadata.DefaultClosed && text.GetText(metadata.StartOffset, metadata.EndOffset - metadata.StartOffset).Contains("AoT Preset Editor"),
            "Editor metadata should remain available in a closed fold.");
        Require(folds.Any(fold => fold.StartOffset == text.GetLineByNumber(2).EndOffset && fold.DefaultClosed), "Expanded helper settings should start folded.");
        Require(folds.Any(fold => fold.StartOffset == text.GetLineByNumber(4).EndOffset && !fold.DefaultClosed), "The expanded rule list should remain open.");
        Require(folds.Any(fold => fold.StartOffset == text.GetLineByNumber(5).EndOffset && fold.EndOffset <= text.GetLineByNumber(7).Offset),
            "The first rule needs its own fold instead of folding the whole list.");

        var window = new YamlPreviewWindow("sample.yml", source, source);
        try
        {
            var pages = (IEnumerable)typeof(YamlPreviewWindow).GetField("pages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var page = pages.Cast<object>().First();
            var editor = (TextEditor)page.GetType().GetProperty("Editor")!.GetValue(page)!;
            Require(editor.Text == source && editor.IsReadOnly && editor.ShowLineNumbers, "The preview changed the source or lost code-view controls.");
            window.Background = Brushes.Black;
            var dark = Highlight(editor, 3);
            window.Background = Brushes.White;
            var light = Highlight(editor, 3);
            Require(dark != light, "Code colors did not adapt to the background.");
            var manager = (FoldingManager)page.GetType().GetProperty("Folding")!.GetValue(page)!;
            Require(manager.AllFoldings.First().IsFolded, "The source view opened with its metadata exposed.");
            Require(editor.Text == source, "Folding discarded hidden source text.");
        }
        finally { window.Close(); }
    }

    private static Color Highlight(TextEditor editor, int line)
    {
        using var highlighter = new DocumentHighlighter(editor.Document, editor.SyntaxHighlighting);
        return ((SolidColorBrush)highlighter.HighlightLine(line).Sections.First().Color.Foreground.GetBrush(null)).Color;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
