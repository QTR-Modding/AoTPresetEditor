using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
        var main = new MainWindow(null);
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
        }
    }

    private static void CheckEmptyPreset()
    {
        var main = new MainWindow(null);
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
        finally { main.Close(); }
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
        var option = Descendants<Button>(collection).Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == "Options for Duration (game hours)");
        option.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Vary per item"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Measure(client, size);
        var back = Field<Button>(editor, "backToSettings");
        Require(tabs.SelectedIndex == 1 && Field<DockPanel>(editor, "variationNavigation").Visibility == Visibility.Visible,
            "Varying a field from a scrolled page must open per-item editing with a return action.");
        CheckInside(back, viewport, "The return action must remain inside the editor viewport after changing views.");
        var variationScroll = (ScrollViewer)((TabItem)tabs.Items[1]).Content;
        var compactSize = new Size(1050, 450);
        Measure(client, compactSize);
        Require(variationScroll.ScrollableHeight > 0,
            "The navigation regression fixture must overflow the per-item page before checking its return action.");
        variationScroll.ScrollToBottom(); Measure(client, compactSize);
        Require(variationScroll.VerticalOffset > 0,
            "The per-item navigation check must scroll the content before testing the fixed return action.");
        CheckInside(back, viewport, "Scrolling per-item values must not move the return action out of view.");
        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Measure(client, size);
        Require(tabs.SelectedIndex == 0 && Field<DockPanel>(editor, "variationNavigation").Visibility == Visibility.Collapsed,
            "The return action must restore settings instead of leaving an empty or detached view.");
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
