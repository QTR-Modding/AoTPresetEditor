using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AoTPresetEditor;

internal static class LayoutChecks
{
    public static void Run()
    {
        CheckEmptyPreset();
        CheckPreset(PresetDocument.Parse("""
            formsLists:
            - forms: FoodBeef
              transformers:
              - FormEditorID: Campfire
                finalFormEditorID: FoodBeefCooked
                duration: 0.06
            """, "FOOD", PresetKind.Addon), "Valid");
        CheckPreset(PresetDocument.Parse("""
            ownerLists:
            - owners: [FoodBeef]
              stages:
              - no: 0
                name: Fresh
                duration: 1
            """, "FOOD", PresetKind.Custom), "Incomplete");
    }

    private static void CheckPreset(PresetDocument document, string scenario)
    {
        var placementPath = Path.Combine(Path.GetTempPath(), "AoT-layout-" + Guid.NewGuid().ToString("N") + ".json");
        var main = new MainWindow(null, placementPath);
        try
        {
            var workspace = Field<PresetWorkspace>(main, "Workspace");
            var rule = document.Blocks.Single();
            var errors = PresetSchema.Validate(rule.Value, rule.Context, document.Category, document.Kind);
            Require(errors.Count == (scenario == "Valid" ? 0 : 1), "The incomplete layout fixture must contain one real missing-field error.");
            var validation = new PresetValidationResult(document.ToYaml(), document.ToYaml(), new() { [rule.Id] = errors }, []);
            var project = new PresetProject { Name = "Cooking over fire" };
            var entry = project.Add(document, project.Name);
            SetField(workspace, "project", project);
            SetField(workspace, "activeEntry", entry);
            SetField(workspace, "document", document);
            SetField(workspace, "filename", "Cooking over fire");
            Field<TextBlock>(workspace, "title").Text = "Cooking over fire";
            Field<TextBlock>(workspace, "state").Text = document.Category + " / " + document.Kind +
                (validation.IsValid ? " · ✓ Ready to save to a mod" : " · 1 details to complete or fix");
            SetValidation(workspace, entry, validation);
            Invoke(workspace, "UpdatePresentation", document.ToYaml());
            Invoke(workspace, "RenderBlocks", rule.Id);
            Invoke(workspace, "SelectBlock");
            Invoke(workspace, "UpdateActions");
            Invoke(workspace, "RenderIssues");
            Field<Action>(workspace, "Opened").Invoke();
            Field<Menu>(main, "ToolsMenu").Visibility = Visibility.Visible;
            var blocks = Field<ListBox>(workspace, "blocks");
            blocks.SelectedItem = null;
            Require(!Descendants<Button>(Field<ContentControl>(workspace, "details")).Any() &&
                Field<DockPanel>(workspace, "blockSidebar").Visibility == Visibility.Visible,
                "Deselecting a behavior must show a selection prompt without a duplicate Add action.");
            blocks.SelectedIndex = 0;

            Require(Field<DockPanel>(main, "PresetBrowser").Visibility == Visibility.Collapsed,
                "Opening a preset should release the installed-browser area for editing.");
            var viewport = Descendants<ScrollViewer>(workspace).Single(view => view.Name == "EditorViewport");
            Require(viewport.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
                "The workspace must constrain the editor without scrolling its navigation out of view.");
            var sidebar = Descendants<DockPanel>(workspace).Single(panel => panel.Name == "BlockSidebar");
            var editor = Field<BehaviorEditorControl>(workspace, "editor");
            var name = Field<TextBox>(editor, "name");
            var client = (FrameworkElement)main.Content;
            var issues = Field<Expander>(workspace, "issues");
            Require(main.MinWidth == 0 && main.MinHeight == 0,
                "The main window must not impose an editor-specific minimum size.");

            foreach (var size in new[] { new Size(1050, 650), new Size(1400, 900) })
            {
                SetValidation(workspace, entry, validation);
                Invoke(workspace, "UpdateActions"); Invoke(workspace, "RenderIssues");
                Field<TextBlock>(workspace, "state").Text = document.Category + " / " + document.Kind +
                    (validation.IsValid ? " · ✓ Ready to save to a mod" : " · 1 details to complete or fix");
                Measure(client, size);
                Require(!issues.IsExpanded && issues.Visibility == Visibility.Collapsed &&
                    Field<Button>(workspace, "reviewIssues").Visibility == (validation.IsValid ? Visibility.Collapsed : Visibility.Visible),
                    "An incomplete preset must expose its issue action without reserving space for the closed issue list.");
                Require(viewport.ActualHeight >= size.Height * 0.70,
                    $"{scenario} at {size}: the editor viewport gets only {viewport.ActualHeight:F0}px of {size.Height:F0}px client height.");
                Require(viewport.ActualWidth >= workspace.ActualWidth * 0.60 && viewport.ActualWidth >= 500,
                    $"At {size}, the default editor width is only {viewport.ActualWidth:F0}px.");
                Require(sidebar.ActualWidth > 0, "The normal layout must keep its rule overview visible.");
                CheckHeaderBounds(client);
                CheckWorkspaceToolbar(workspace, client);
                var editingSize = new Size(viewport.ActualWidth, viewport.ActualHeight);
                if (!validation.IsValid)
                {
                    var review = Field<Button>(workspace, "reviewIssues");
                    review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Measure(client, size);
                    Require(issues.IsExpanded && issues.Visibility == Visibility.Visible &&
                        Field<StackPanel>(workspace, "issueList").Children.Count == 1 && viewport.ActualHeight < editingSize.Height,
                        "Review issues must reveal the actual issue list and allocate its space only while open.");
                    issues.IsExpanded = false;
                    Measure(client, size);
                    Require(issues.Visibility == Visibility.Collapsed && Math.Abs(viewport.ActualHeight - editingSize.Height) < 1,
                        "Collapsing Review issues must return all of its space to the editor.");
                    review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(!issues.IsExpanded && issues.Visibility == Visibility.Collapsed,
                        "The Review issues toolbar action must also close an open issue list.");
                }
                name.Text = "Cooking beef at " + size.Width;
                SetValidation(workspace, entry, validation);
                Invoke(workspace, "UpdateActions"); Invoke(workspace, "RenderIssues");
                Invoke(workspace, "ToggleFocusMode");
                Measure(client, size);
                Require(viewport.ActualWidth >= workspace.ActualWidth * 0.90,
                    $"At {size}, focus mode gives the editor only {viewport.ActualWidth:F0}px of {workspace.ActualWidth:F0}px available width.");
                Require(sidebar.Visibility == Visibility.Collapsed && ReferenceEquals(editor, Field<BehaviorEditorControl>(workspace, "editor")) &&
                    editor.BlockName == "Cooking beef at " + size.Width,
                    "Focus mode must preserve the selected editor instance and the unfinished edit.");
                Console.WriteLine($"LAYOUT {scenario} {size.Width:F0}x{size.Height:F0}: editor {editingSize.Width:F0}x{editingSize.Height:F0}; focus width {viewport.ActualWidth:F0}.");
                Invoke(workspace, "ToggleFocusMode");
                Measure(client, size);
                Require(sidebar.Visibility == Visibility.Visible && ReferenceEquals(editor, Field<BehaviorEditorControl>(workspace, "editor")) &&
                    editor.BlockName == "Cooking beef at " + size.Width,
                    "Leaving focus mode must restore the overview without resetting the active edit.");
            }

            if (scenario == "Valid") CheckVariationNavigation(editor, client, viewport);
            else CheckIssuePanel(workspace, entry, validation, client, viewport);

            var windowViewport = Field<ScrollViewer>(main, "WindowViewport");
            foreach (var size in new[] { new Size(600, 400), new Size(320, 240) })
            {
                Measure(client, size);
                Require(windowViewport.ScrollableWidth > 0 && windowViewport.ScrollableHeight > 0,
                    $"At {size}, content that no longer fits must remain reachable through scrolling.");
                Require(viewport.ActualHeight > 200 && viewport.ActualWidth >= 400,
                    "Shrinking the outer window must preserve a usable editor inside the scrollable content.");
                CheckHeaderBounds(client);
                CheckWorkspaceToolbar(workspace, client);
                windowViewport.ScrollToRightEnd();
                windowViewport.ScrollToBottom();
                Measure(client, size);
                Require(Math.Abs(windowViewport.HorizontalOffset - windowViewport.ScrollableWidth) < 1 &&
                    Math.Abs(windowViewport.VerticalOffset - windowViewport.ScrollableHeight) < 1,
                    "Both far edges of a compact window's content must be reachable.");
            }
            Measure(client, new Size(1400, 900));
            Require(windowViewport.ScrollableWidth == 0 && windowViewport.ScrollableHeight == 0 &&
                ReferenceEquals(editor, Field<BehaviorEditorControl>(workspace, "editor")) && editor.BlockName == "Cooking beef at 1400",
                "Enlarging the window must restore its normal layout without outer scrollbars or losing the edit.");
            if (scenario == "Valid") CheckBrowseNavigation(main, workspace, editor, client);
        }
        finally
        {
            var workspace = Field<PresetWorkspace>(main, "Workspace");
            Invoke(workspace, "ResetEditor");
            SetField(workspace, "dirty", false);
            main.Close();
            if (File.Exists(placementPath)) File.Delete(placementPath);
        }
    }

    private static void CheckEmptyPreset()
    {
        var placementPath = Path.Combine(Path.GetTempPath(), "AoT-layout-" + Guid.NewGuid().ToString("N") + ".json");
        var main = new MainWindow(null, placementPath);
        var workspace = Field<PresetWorkspace>(main, "Workspace");
        try
        {
            Require(workspace.Visibility == Visibility.Collapsed &&
                Field<DockPanel>(main, "PresetBrowser").Visibility == Visibility.Visible &&
                Field<Button>(main, "ResumeEditing").Visibility == Visibility.Collapsed,
                "Startup must show the preset list without an empty editor or a resume action.");
            SetField(workspace, "project", new PresetProject { Name = "Empty preset" });
            Invoke(workspace, "ShowWelcome"); Invoke(workspace, "UpdateActions");
            Field<Action>(workspace, "Opened").Invoke();
            var client = (FrameworkElement)main.Content;
            Measure(client, new Size(1050, 650));
            var details = Field<ContentControl>(workspace, "details");
            Require(Field<DockPanel>(workspace, "blockSidebar").Visibility == Visibility.Collapsed &&
                Descendants<Button>(details).Count(button => Equals(button.Content, "Add behavior...")) == 1 &&
                details.ActualWidth >= workspace.ActualWidth * 0.9,
                "An empty preset must have one visible Add behavior action and no empty sidebar.");
        }
        finally { main.Close(); if (File.Exists(placementPath)) File.Delete(placementPath); }
    }

    private static void CheckIssuePanel(PresetWorkspace workspace, PresetProjectEntry entry,
        PresetValidationResult validation, FrameworkElement client, ScrollViewer editorViewport)
    {
        const int issueCount = 10;
        const int longIssueIndex = 3;
        var originalError = validation.Errors.Single().Value.Single();
        var errors = Enumerable.Range(0, issueCount).Select(index => originalError +
            (index == longIssueIndex ? " " + string.Join(" ", Enumerable.Repeat("Detailed context for this affected setting.", 160))
                : index % 2 == 0 ? " Check the item selected for this behavior before saving it." : "")).ToArray();
        SetValidation(workspace, entry, new(validation.Source, validation.Expanded,
            new() { [entry.Document.Blocks.Single().Id] = errors }, []));
        Invoke(workspace, "RenderIssues");
        var issues = Field<Expander>(workspace, "issues");
        var scroll = Field<ScrollViewer>(workspace, "issueScroll");
        var list = Field<StackPanel>(workspace, "issueList");
        var splitter = Field<GridSplitter>(workspace, "issueSplitter");
        var review = Field<Button>(workspace, "reviewIssues");
        var details = Field<ContentControl>(workspace, "details").Content;
        var size = new Size(1400, 900);
        review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Measure(client, size);
        var entries = list.Children.Cast<FrameworkElement>().ToArray();
        Require(entries.Length == issueCount && Equals(issues.Header, "Review issues (10)") &&
            entries.Select((control, index) => Descendants<TextBlock>(control).Any(text => text.Text.Trim() == (index + 1) + ".")).All(numbered => numbered),
            "Every displayed issue must have a consecutive visible number matching the issue count.");
        Require(splitter.Visibility == Visibility.Visible && scroll.ScrollableHeight > 0 &&
            entries.Select(control => Math.Round(control.ActualHeight)).Distinct().Count() > 1,
            "The issue navigation fixture must contain differently sized entries in a resizable, overflowing panel.");

        double Boundary(int index) => entries[index].TranslatePoint(new Point(), list).Y - entries[index].Margin.Top;
        void Wheel(int direction, bool measure = true)
        {
            var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, direction)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            scroll.RaiseEvent(args);
            if (measure) Measure(client, size);
            Require(args.Handled, "The issues viewport must consume wheel input, including at its ends.");
        }
        scroll.ScrollToTop(); Measure(client, size);
        Wheel(-120);
        Require(Math.Abs(scroll.VerticalOffset - Boundary(1)) < 1,
            "One wheel movement must show the next whole issue rather than an arbitrary pixel or multi-issue distance.");
        Wheel(-120);
        Require(Math.Abs(scroll.VerticalOffset - Boundary(2)) < 1,
            "Repeated wheel movements must advance one issue at a time across different entry heights.");
        Wheel(120);
        Require(Math.Abs(scroll.VerticalOffset - Boundary(1)) < 1,
            "Scrolling upward must return to the previous issue boundary.");
        scroll.ScrollToTop(); Measure(client, size);
        Wheel(-120, false); Wheel(-120, false); Measure(client, size);
        Require(Math.Abs(scroll.VerticalOffset - Boundary(2)) < 1,
            "Two wheel events received before the next layout must advance two issues without losing a step.");
        scroll.ScrollToTop(); Measure(client, size); Wheel(120);
        Require(scroll.VerticalOffset == 0, "Scrolling above the first issue must stop at the beginning.");
        var wheelOffsets = new List<double>();
        for (var index = 0; index < issueCount + 1; ++index)
        {
            Wheel(-120); wheelOffsets.Add(scroll.VerticalOffset);
        }
        Require(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1,
            "Stepping through all issues must reach the last entry and stop at the bottom. " +
            $"Offset={scroll.VerticalOffset:R}; maximum={scroll.ScrollableHeight:R}; viewport={scroll.ViewportHeight:R}; " +
            "boundaries=[" + string.Join(", ", Enumerable.Range(0, issueCount).Select(index => Boundary(index).ToString("R"))) +
            "]; steps=[" + string.Join(", ", wheelOffsets.Select(offset => offset.ToString("R"))) + "].");
        Require(ReferenceEquals(details, Field<ContentControl>(workspace, "details").Content),
            "Reading issues with the wheel must not navigate away from or recreate the current editor.");

        Require(entries[longIssueIndex].ActualHeight > scroll.ViewportHeight,
            "The long-message fixture must exceed the issue viewport height.");
        var insideLongIssue = Boundary(longIssueIndex) + scroll.ViewportHeight / 2;
        scroll.ScrollToVerticalOffset(insideLongIssue); Measure(client, size);
        Require(Math.Abs(scroll.VerticalOffset - insideLongIssue) < 1 && scroll.ScrollableWidth == 0,
            "The scrollbar must retain pixel movement inside a long wrapped issue without horizontal overflow.");
        Wheel(-120);
        Require(Math.Abs(scroll.VerticalOffset - Math.Min(Boundary(longIssueIndex + 1), scroll.ScrollableHeight)) < 1,
            "After reading within a long issue, scrolling down must advance to the next issue.");

        var originalHeight = issues.ActualHeight;
        var editorHeight = editorViewport.ActualHeight;
        const double resizeAmount = 80;
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        splitter.RaiseEvent(new DragDeltaEventArgs(0, -resizeAmount) { RoutedEvent = Thumb.DragDeltaEvent });
        splitter.RaiseEvent(new DragCompletedEventArgs(0, -resizeAmount, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Measure(client, size);
        var resizedHeight = issues.ActualHeight;
        Require(resizedHeight > 150 && Math.Abs(resizedHeight - originalHeight - resizeAmount) < 2 &&
            Math.Abs(editorHeight - editorViewport.ActualHeight - resizeAmount) < 2,
            "Dragging the divider upward must enlarge Review issues and give it the corresponding editor space.");
        issues.IsExpanded = false; Measure(client, size);
        Require(splitter.Visibility == Visibility.Collapsed && Field<RowDefinition>(workspace, "issueRow").ActualHeight == 0 &&
            editorViewport.ActualHeight > editorHeight,
            "Collapsing Review issues must remove its divider and return its full area to the editor.");
        review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Measure(client, size);
        Require(Math.Abs(issues.ActualHeight - resizedHeight) < 2,
            "Reopening Review issues must retain its resized height.");
        Measure(client, new Size(600, 400));
        Require(issues.ActualHeight >= 80 && editorViewport.ActualHeight >= 120 && scroll.ScrollableWidth == 0,
            "A compact window must keep both panels usable and wrap issue text inside their available width.");
        issues.IsExpanded = false;
        SetValidation(workspace, entry, validation); Invoke(workspace, "RenderIssues");
        Measure(client, size);
    }

    private static void CheckBrowseNavigation(MainWindow main, PresetWorkspace workspace, BehaviorEditorControl editor, FrameworkElement client)
    {
        var browser = Field<DockPanel>(main, "PresetBrowser");
        var resume = Field<Button>(main, "ResumeEditing");
        var search = Field<TextBox>(main, "PresetSearch");
        var project = Field<PresetProject>(workspace, "project");
        var snapshot = project.ToDraft();
        var list = Field<ListBox>(main, "PresetList");
        var files = new[] { new PresetFile("C:\\fixtures\\one.yml", "FOOD\\custom\\one.yml") };
        SetField(main, "presets", files);
        Invoke(main, "UpdatePresetFilters");
        search.Text = "missing";
        Require(list.Items.Count == 0 && Field<TextBlock>(main, "PresetEmpty").Visibility == Visibility.Visible,
            "A search with no matches must explain the empty result.");
        search.Text = "one";
        var back = Descendants<Button>(workspace).Single(button => Equals(button.Content, "Back to presets"));
        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Measure(client, new Size(1050, 650));
        Require(browser.Visibility == Visibility.Visible && workspace.Visibility == Visibility.Collapsed &&
            resume.Visibility == Visibility.Visible && search.Text == "one" && list.Items.Count == 1,
            "Back must restore the filtered preset list and provide Resume editing.");
        Require(ReferenceEquals(editor, Field<BehaviorEditorControl>(workspace, "editor")) &&
            project.ToDraft() == snapshot && Field<bool>(workspace, "editorDirty") && Field<bool>(workspace, "dirty"),
            "Browsing must retain the active editor, uncaptured input, and unsaved state.");

        resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Measure(client, new Size(1050, 650));
        Require(browser.Visibility == Visibility.Collapsed && workspace.Visibility == Visibility.Visible &&
            ReferenceEquals(editor, Field<BehaviorEditorControl>(workspace, "editor")) && editor.BlockName == "Cooking beef at 1400",
            "Resume must return to the same unfinished editor without reloading its file.");
    }

    private static void CheckVariationNavigation(BehaviorEditorControl editor, FrameworkElement client, ScrollViewer viewport)
    {
        var tabs = Field<TabControl>(editor, "tabs");
        var common = Field<ValueEditorControl>(editor, "commonEditor");
        var collection = Descendants<ValueEditorControl>(common)
            .Single(control => Field<PresetField?>(control, "field")?.Key == "transformers");
        Field<ListBox>(collection, "entryList").SelectedIndex = 0;
        foreach (var section in Descendants<Expander>(common)) section.IsExpanded = true;
        var commonScroll = (ScrollViewer)((TabItem)tabs.Items[0]).Content;
        var size = new Size(1050, 650);
        Measure(client, size);
        commonScroll.ScrollToBottom(); Measure(client, size);
        Require(commonScroll.VerticalOffset > 0,
            "The navigation regression fixture must start with a scrolled settings page.");
        editor.Capture();
        BehaviorFamily.MakeVariable(editor.Document, editor.RuleId, ["transformers"], "heat_settings");
        Invoke(editor, "Rebuild", false, null, null);
        Measure(client, size);
        Require(tabs.SelectedIndex == 0 && Equals(((TabItem)tabs.Items[1]).Header, "Variables"),
            "Creating a variable from a scrolled Settings page must keep Settings selected and expose Variables.");
        var settingsTab = (TabItem)tabs.Items[0];
        var variablesTab = (TabItem)tabs.Items[1];
        CheckInside(variablesTab, viewport, "The Variables tab must remain inside the editor viewport after creating a variable.");
        tabs.SelectedIndex = 1;
        var table = Field<DataGrid>(editor, "table");
        table.SelectedItem = table.Items[0];
        table.CurrentCell = new DataGridCellInfo(table.Items[0], table.Columns[1]);
        var cell = Field<ValueEditorControl>(editor, "cellEditor");
        Field<ListBox>(cell, "entryList").SelectedIndex = 0;
        Descendants<Button>(cell).First(button => Equals(button.Content, "Edit entry"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        foreach (var section in Descendants<Expander>(cell)) section.IsExpanded = true;
        Measure(client, size);
        CheckInside(settingsTab, viewport, "The Settings tab must offer a visible return path when Variables is selected.");
        var variationScroll = (ScrollViewer)((TabItem)tabs.Items[1]).Content;
        var compactSize = new Size(1050, 450);
        Measure(client, compactSize);
        Require(variationScroll.ScrollableHeight > 0,
            "The navigation regression fixture must overflow the variable collection editor before checking its return path.");
        variationScroll.ScrollToBottom(); Measure(client, compactSize);
        Require(variationScroll.VerticalOffset > 0,
            "The variable navigation check must scroll the content before testing its fixed tab headers.");
        CheckInside(settingsTab, viewport, "Scrolling variable values must not move the Settings tab out of view.");
        tabs.SelectedIndex = 0; Measure(client, size);
        Require(tabs.SelectedIndex == 0 && Field<ValueEditorControl>(editor, "commonEditor").Visibility == Visibility.Visible,
            "Selecting Settings must restore its editor instead of leaving an empty or detached view.");
    }

    private static void CheckInside(FrameworkElement control, FrameworkElement viewport, string message)
    {
        var bounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
        Require(bounds.Top >= 0 && bounds.Bottom <= viewport.ActualHeight &&
            bounds.Left >= 0 && bounds.Right <= viewport.ActualWidth, message);
    }

    private static void SetValidation(PresetWorkspace workspace, PresetProjectEntry entry, PresetValidationResult validation)
    {
        SetField(workspace, "validation", validation);
        Field<Dictionary<string, PresetValidationResult>>(workspace, "validations")[entry.Id] = validation;
    }

    private static void CheckWorkspaceToolbar(PresetWorkspace workspace, FrameworkElement client)
    {
        var toolbar = (DockPanel)Field<Button>(workspace, "focus").Parent;
        var bounds = toolbar.TransformToAncestor(client).TransformBounds(new Rect(toolbar.RenderSize));
        var controls = Descendants<Button>(toolbar).Cast<FrameworkElement>().Concat(toolbar.Children.OfType<Menu>())
            .Where(control => control.Visibility == Visibility.Visible).ToArray();
        var rectangles = controls.Select(control => control.TransformToAncestor(client).TransformBounds(new Rect(control.RenderSize))).ToArray();
        for (var index = 0; index < rectangles.Length; ++index)
        {
            var current = rectangles[index];
            Require(current.Left >= bounds.Left && current.Right <= bounds.Right && current.Top >= bounds.Top && current.Bottom <= bounds.Bottom,
                "A workspace toolbar action must stay inside the available toolbar area.");
            for (var other = index + 1; other < rectangles.Length; ++other)
                Require(current.Right <= rectangles[other].Left || current.Left >= rectangles[other].Right ||
                    current.Bottom <= rectangles[other].Top || current.Top >= rectangles[other].Bottom,
                    "Visible workspace toolbar actions must not overlap.");
        }
    }

    private static void CheckHeaderBounds(FrameworkElement client)
    {
        var overlay = (DockPanel)Descendants<Menu>(client).Single(menu => menu.Name == "ToolsMenu").Parent;
        var toolbarBounds = overlay.TransformToAncestor(client).TransformBounds(new Rect(overlay.RenderSize));
        foreach (var tab in Descendants<TabItem>(client))
        {
            var tabBounds = tab.TransformToAncestor(client).TransformBounds(new Rect(tab.RenderSize));
            Require(tabBounds.Right <= toolbarBounds.Left || tabBounds.Left >= toolbarBounds.Right ||
                tabBounds.Bottom <= toolbarBounds.Top || tabBounds.Top >= toolbarBounds.Bottom,
                "The Appearance/Tools toolbar must not cover a tab header.");
        }
    }

    private static void Measure(FrameworkElement client, Size size)
    {
        client.Measure(size);
        client.Arrange(new Rect(new Point(), size));
        client.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void SetField(object owner, string name, object? value) => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, value);
    private static void Invoke(object owner, string name, params object?[] arguments) => owner.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, arguments);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
