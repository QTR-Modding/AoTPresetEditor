using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace AoTPresetEditor;

public sealed class YamlPreviewWindow : Window
{
    private const double DefaultTextSize = 14;
    private const double MinimumTextSize = 10;
    private const double MaximumTextSize = 30;
    private readonly TabControl tabs = new();
    private readonly List<CodePage> pages = [];
    private readonly TextBlock zoom = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) };
    private sealed record CodePage(TextEditor Editor, FoldingManager Folding, SearchPanel Search);

    public YamlPreviewWindow(string filename, string source, string expanded)
    {
        Title = filename + " - YAML";
        Width = 1120; Height = 780; MinWidth = 640; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        var layout = new DockPanel { Margin = new Thickness(20) };
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        toolbar.Children.Add(EditorControls.Button("Find (Ctrl+F)", Find));
        toolbar.Children.Add(EditorControls.Button("Copy YAML", CopyYaml));
        toolbar.Children.Add(EditorControls.Button("Collapse sections", () => FoldAll(true)));
        toolbar.Children.Add(EditorControls.Button("Expand sections", () => FoldAll(false)));
        toolbar.Children.Add(zoom);
        toolbar.Children.Add(EditorControls.Button("A-", () => ChangeZoom(-1)));
        toolbar.Children.Add(EditorControls.Button("A+", () => ChangeZoom(1)));
        toolbar.Children.Add(EditorControls.Button("Reset size", () => SetZoom(DefaultTextSize)));
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        AddPage("Preset YAML", "The preset to save, including shared settings and templates. Editor labels are folded at the top; copying includes them.", source, false);
        AddPage("Expanded rules", "Shared settings and template arguments have been applied. This read-only view helps check the resulting rules. Definitions are folded to keep the rules in view.", expanded, true);
        layout.Children.Add(tabs);
        Content = layout;
        Loaded += (_, _) => UpdatePalette();
        PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            ChangeZoom(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        };
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => Find()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        SetZoom(DefaultTextSize);
        UpdatePalette();
    }

    private void AddPage(string title, string explanation, string text, bool expanded)
    {
        var editor = new TextEditor
        {
            Text = text, IsReadOnly = true, ShowLineNumbers = true, FontFamily = new FontFamily("Consolas"),
            FontSize = DefaultTextSize, Padding = new Thickness(8), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        editor.Options.IndentationSize = 2;
        editor.Options.HighlightCurrentLine = true;
        editor.Options.AllowScrollBelowDocument = false;
        editor.SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        editor.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "TextFillColorSecondaryBrush");
        var search = SearchPanel.Install(editor);
        search.SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        search.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        var searchButton = new Style(typeof(Button), TryFindResource(typeof(Button)) as Style);
        searchButton.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        searchButton.Setters.Add(new Setter(MinWidthProperty, 0d));
        search.Resources[typeof(Button)] = searchButton;
        var searchText = new Style(typeof(TextBox), TryFindResource(typeof(TextBox)) as Style);
        searchText.Setters.Add(new Setter(PaddingProperty, new Thickness(4, 0, 4, 0)));
        searchText.Setters.Add(new Setter(FontSizeProperty, 12d));
        search.Resources[typeof(TextBox)] = searchText;
        var folding = FoldingManager.Install(editor.TextArea);
        folding.UpdateFoldings(CreateFoldings(editor.Document, expanded), -1);
        editor.TextArea.TextView.BackgroundRenderers.Add(new IndentationGuides(editor));
        pages.Add(new CodePage(editor, folding, search));
        var panel = new DockPanel();
        var info = EditorControls.Text(explanation);
        info.Margin = new Thickness(4, 12, 4, 12);
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        DockPanel.SetDock(info, Dock.Top);
        panel.Children.Add(info);
        panel.Children.Add(editor);
        tabs.Items.Add(new TabItem { Header = title, Content = panel });
        if (tabs.SelectedIndex < 0) tabs.SelectedIndex = 0;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BackgroundProperty && pages is not null) UpdatePalette();
    }

    private void UpdatePalette()
    {
        if (Background is not SolidColorBrush surface) return;
        const int darkPaletteThreshold = byte.MaxValue * 3 / 2;
        var dark = surface.Color.R + surface.Color.G + surface.Color.B < darkPaletteThreshold;
        var colors = SystemParameters.HighContrast
            ? Enumerable.Repeat(((SolidColorBrush)SystemColors.WindowTextBrush).Color.ToString(), 6).ToArray()
            : dark ? new[] { "#8CCBFF", "#C4A7E7", "#A8C790", "#E8BD8C", "#A6C6A0", "#E2C58D" }
                   : new[] { "#075D91", "#7845A8", "#39712D", "#91490D", "#47713B", "#765700" };
        var definition = CreateHighlighting(colors);
        FoldingElementGenerator.TextBrush = Foreground;
        foreach (var page in pages)
        {
            page.Editor.SyntaxHighlighting = definition;
            page.Search.Resources[SystemColors.WindowBrushKey] = Background;
            page.Search.Resources[SystemColors.WindowTextBrushKey] = Foreground;
            page.Search.Resources[SystemColors.ControlTextBrushKey] = Foreground;
            page.Search.MarkerBrush = new SolidColorBrush(Color.FromArgb(70, 220, 160, 30));
            FoldingMargin.SetFoldingMarkerBackgroundBrush(page.Editor.TextArea, Background);
            FoldingMargin.SetFoldingMarkerBrush(page.Editor.TextArea, Foreground);
            FoldingMargin.SetSelectedFoldingMarkerBackgroundBrush(page.Editor.TextArea, Background);
            FoldingMargin.SetSelectedFoldingMarkerBrush(page.Editor.TextArea, Foreground);
            page.Editor.TextArea.TextView.Redraw();
        }
    }

    private static IHighlightingDefinition CreateHighlighting(string[] colors)
    {
        var xshd = $$"""
            <SyntaxDefinition name="AoT YAML" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Key" foreground="{{colors[0]}}"/>
              <Color name="Reference" foreground="{{colors[1]}}"/>
              <Color name="Comment" foreground="{{colors[2]}}"/>
              <Color name="String" foreground="{{colors[3]}}"/>
              <Color name="Number" foreground="{{colors[4]}}"/>
              <Color name="Call" foreground="{{colors[5]}}"/>
              <RuleSet>
                <Span color="Comment" begin="(^|\s)\#" end="$" />
                <Span color="String" begin="&quot;" end="&quot;"><RuleSet><Span begin="\\." end="" /></RuleSet></Span>
                <Span color="String" begin="'" end="'"><RuleSet><Span begin="''" end="" /></RuleSet></Span>
                <Rule color="Key">[A-Za-z_][A-Za-z0-9_-]*(?=\s*:)</Rule>
                <Rule color="Reference">[&amp;*][A-Za-z0-9_-]+|\$\$?[A-Za-z0-9_-]+|&lt;&lt;(?=\s*:)</Rule>
                <Rule color="Call">\b[A-Za-z_][A-Za-z0-9_-]*(?=\()</Rule>
                <Rule color="Number">(?&lt;![\w])(?:0x[0-9A-Fa-f]+|[0-9A-Fa-f]{7,8}|[-+]?[0-9]+(?:\.[0-9]+)?|true|false|null)(?![\w])</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    internal static IReadOnlyList<NewFolding> CreateFoldings(TextDocument document, bool expanded)
    {
        var foldings = new List<NewFolding>();
        foreach (var line in document.Lines)
        {
            if (document.GetText(line).StartsWith("# AoT Preset Editor: ", StringComparison.Ordinal))
                foldings.Add(new NewFolding(line.Offset, line.EndOffset) { Name = "# Editor labels (expand to view)", DefaultClosed = true });
        }
        var stack = new Stack<ParsingEvent>();
        var parser = new Parser(new StringReader(document.Text));
        try
        {
            while (parser.MoveNext())
            {
                if (parser.Current is MappingStart or SequenceStart) stack.Push(parser.Current);
                else if (parser.Current is MappingEnd or SequenceEnd)
                {
                    var start = stack.Pop();
                    if (stack.Count == 0) continue;
                    var first = document.GetLineByOffset((int)start.Start.Index);
                    // An unanchored block starts on its first child's line; keep its key visible instead.
                    if ((start is SequenceStart || start.Start.Column > 1 && !document.GetText(first).TrimStart().StartsWith('-')) &&
                        first.PreviousLine is { } previous && document.GetText(previous).TrimEnd().EndsWith(':'))
                        first = previous;
                    var end = Math.Min((int)parser.Current.Start.Index, document.TextLength);
                    while (end > first.EndOffset && char.IsWhiteSpace(document.GetCharAt(end - 1))) end--;
                    if (end <= first.EndOffset || document.GetLineByOffset(end).LineNumber <= first.LineNumber) continue;
                    var header = document.GetText(first);
                    var rootDefinition = expanded && !char.IsWhiteSpace(header[0]) && !header.StartsWith('-') &&
                        !header.StartsWith("formsLists:", StringComparison.Ordinal) && !header.StartsWith("ownerLists:", StringComparison.Ordinal);
                    foldings.Add(new NewFolding(first.EndOffset, end) { Name = " ...", DefaultClosed = rootDefinition });
                }
            }
        }
        catch (YamlException) { /* A readable preview remains available while a draft is incomplete. */ }
        return foldings.GroupBy(folding => folding.StartOffset).Select(group => group.MaxBy(folding => folding.EndOffset)!)
            .OrderBy(folding => folding.StartOffset).ToArray();
    }

    private CodePage SelectedPage => pages[Math.Max(tabs.SelectedIndex, 0)];
    private void Find() { SelectedPage.Search.Open(); SelectedPage.Search.Reactivate(); }
    private void CopyYaml()
    {
        try { Clipboard.SetText(SelectedPage.Editor.Text); }
        catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "The clipboard is busy. Try copying again.", "Copy YAML"); }
    }
    private void FoldAll(bool fold) { foreach (var section in SelectedPage.Folding.AllFoldings) section.IsFolded = fold; }
    private void ChangeZoom(int step) => SetZoom(Math.Clamp(SelectedPage.Editor.FontSize + step, MinimumTextSize, MaximumTextSize));
    private void SetZoom(double size)
    {
        foreach (var page in pages) page.Editor.FontSize = size;
        zoom.Text = $"{Math.Round(size / DefaultTextSize * 100)}%";
    }

    private sealed class IndentationGuides(TextEditor editor) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid) return;
            var brush = editor.TryFindResource("TextFillColorTertiaryBrush") as Brush ?? Brushes.Gray;
            var pen = new Pen(brush, 0.5);
            foreach (var line in textView.VisualLines)
            {
                var text = editor.Document.GetText(line.FirstDocumentLine);
                var spaces = text.TakeWhile(character => character == ' ').Count();
                for (var column = editor.Options.IndentationSize; column <= spaces; column += editor.Options.IndentationSize)
                {
                    var x = column * textView.WideSpaceWidth - textView.HorizontalOffset;
                    if (x < 0) continue;
                    var y = line.VisualTop - textView.VerticalOffset;
                    drawingContext.DrawLine(pen, new Point(x, y), new Point(x, y + line.Height));
                }
            }
        }
    }
}
