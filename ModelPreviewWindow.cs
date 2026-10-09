using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;

namespace AoTPresetEditor;

public sealed class ModelPreviewWindow : Window
{
    private readonly FormRow form;
    private readonly HelixViewport3D view = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(32, 35, 40)),
        IsInertiaEnabled = false,
        RotateGesture = new MouseGesture(MouseAction.LeftClick),
        ShowViewCube = false,
        ModelUpDirection = new Vector3D(0, 0, 1)
    };
    private readonly TextBlock status = new() { Text = "Loading model...", TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox models = new() { DisplayMemberPath = nameof(ModelPreviewSource.Label) };
    private readonly Button reset = new() { Content = "Reset view", IsEnabled = false };
    private readonly CheckBox geometry = new() { Content = "Show geometry", IsEnabled = false,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        ToolTip = "Show solid surfaces without textures or transparency, including their back faces." };
    private Model3DGroup? model;
    private CancellationTokenSource? loading;

    public ModelPreviewWindow(FormRow form)
    {
        this.form = form;
        var name = form.Name.Length != 0 ? form.Name : form.EditorId.Length != 0 ? form.EditorId : form.Identifier;
        Title = "Model preview - " + name;
        Width = 900; Height = 700; MinWidth = 340; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        EditorControls.FitToOwner(this);
        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        models.ItemsSource = form.Models;
        models.SelectedIndex = 0;
        AutomationProperties.SetName(models, "Model");
        models.ToolTip = "Choose which model to preview.";
        models.Margin = new Thickness(0, 0, 0, 8);
        models.Visibility = form.Models.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        layout.Children.Add(models);
        Grid.SetRow(view, 1); layout.Children.Add(view);
        AutomationProperties.SetName(view, "Model. Drag to rotate and scroll to zoom.");
        var details = new ScrollViewer
        {
            Content = status, MaxHeight = 100, Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(details, 2); layout.Children.Add(details);
        var footer = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(geometry);
        geometry.Checked += (_, _) => ShowModel();
        geometry.Unchecked += (_, _) => ShowModel();
        footer.Children.Add(new TextBlock
        {
            Text = "Drag to rotate · Scroll to zoom", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });
        reset.Click += (_, _) => ResetView();
        footer.Children.Add(reset);
        footer.Children.Add(EditorControls.Button("Close", Close, isCancel: true));
        Grid.SetRow(footer, 3); layout.Children.Add(footer);
        Content = layout;
        Loaded += LoadModel;
        models.SelectionChanged += (_, _) => { if (IsLoaded) LoadModel(this, new RoutedEventArgs()); };
        Closed += (_, _) => { loading?.Cancel(); model = null; view.Children.Clear(); };
    }

    private async void LoadModel(object sender, RoutedEventArgs args)
    {
        Loaded -= LoadModel;
        loading?.Cancel();
        using var request = new CancellationTokenSource();
        loading = request;
        model = null; view.Children.Clear(); reset.IsEnabled = geometry.IsEnabled = false; status.Text = "Loading model...";
        try
        {
            if (models.SelectedItem is not ModelPreviewSource source || form.Assets is null) return;
            var result = await Task.Run(() => ModelPreview.Load(source, form.Assets, request.Token), request.Token);
            if (request.IsCancellationRequested) return;
            model = result.Model; ShowModel();
            status.Text = result.Details;
            reset.IsEnabled = geometry.IsEnabled = true;
            ResetView();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested) status.Text = "Cannot preview this model: " + error.Message;
        }
        finally { if (ReferenceEquals(loading, request)) loading = null; }
    }

    private void ShowModel()
    {
        view.Children.Clear();
        if (model is null) return;
        view.Children.Add(new DefaultLights());
        var shown = model;
        if (geometry.IsChecked == true)
        {
            shown = new Model3DGroup();
            var material = new DiffuseMaterial(Brushes.LightGray);
            foreach (var part in model.Children.OfType<GeometryModel3D>())
                shown.Children.Add(new GeometryModel3D(part.Geometry, material) { BackMaterial = material, Transform = part.Transform });
            shown.Freeze();
        }
        view.Children.Add(new ModelVisual3D { Content = shown });
    }

    private void ResetView()
    {
        view.UpdateLayout();
        view.Camera = new PerspectiveCamera(new Point3D(100, 100, 75), new Vector3D(-100, -100, -75), new Vector3D(0, 0, 1), 40);
        view.ZoomExtents(0);
    }

    public static bool CanPreview(FormRow? form) => form is { Assets: not null, Models.Count: > 0 };

    public static Button CreateButton(Func<FormRow?> currentForm)
    {
        var button = new Button { Content = "Preview model...", IsEnabled = CanPreview(currentForm()) };
        button.Click += (_, _) =>
        {
            var form = currentForm();
            if (!CanPreview(form)) return;
            new ModelPreviewWindow(form!) { Owner = GetWindow(button) }.ShowDialog();
        };
        return button;
    }
}
