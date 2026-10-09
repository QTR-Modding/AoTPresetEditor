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
    private readonly Button reset = new() { Content = "Reset view", IsEnabled = false };
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
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(view);
        AutomationProperties.SetName(view, "Item model. Drag to rotate and scroll to zoom.");
        var details = new ScrollViewer
        {
            Content = status, MaxHeight = 100, Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(details, 1); layout.Children.Add(details);
        var footer = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(new TextBlock
        {
            Text = "Drag to rotate · Scroll to zoom", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });
        reset.Click += (_, _) => ResetView();
        footer.Children.Add(reset);
        footer.Children.Add(EditorControls.Button("Close", Close, isCancel: true));
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = layout;
        Loaded += LoadModel;
        Closed += (_, _) => { loading?.Cancel(); view.Children.Clear(); };
    }

    private async void LoadModel(object sender, RoutedEventArgs args)
    {
        Loaded -= LoadModel;
        using var request = new CancellationTokenSource();
        loading = request;
        try
        {
            var result = await Task.Run(() => ModelPreview.Load(form, request.Token), request.Token);
            if (request.IsCancellationRequested) return;
            view.Children.Add(new DefaultLights());
            view.Children.Add(new ModelVisual3D { Content = result.Model });
            status.Text = result.Details;
            reset.IsEnabled = true;
            ResetView();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested) status.Text = "Cannot preview this model: " + error.Message;
        }
        finally { loading = null; }
    }

    private void ResetView()
    {
        view.UpdateLayout();
        view.Camera = new PerspectiveCamera(new Point3D(100, -100, 100), new Vector3D(-100, 100, -100), new Vector3D(0, 0, 1), 40);
        view.ZoomExtents(0);
    }

    public static bool CanPreview(FormRow? form) => form is { Assets: not null, ModelPath.Length: > 0 };

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
