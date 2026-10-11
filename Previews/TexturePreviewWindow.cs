using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AoTPresetEditor;

public sealed class TexturePreviewWindow : Window
{
    private readonly FormRow form;
    private readonly ComboBox textures = new() { DisplayMemberPath = nameof(TexturePreviewSource.Label) };
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock status = new() { Text = "Loading texture...", TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? loading;

    public TexturePreviewWindow(FormRow form)
    {
        this.form = form;
        var name = form.Name.Length != 0 ? form.Name : form.EditorId.Length != 0 ? form.EditorId : form.Identifier;
        Title = "Texture preview - " + name;
        Width = 800; Height = 650; MinWidth = 340; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        EditorControls.FitToOwner(this);
        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        textures.ItemsSource = form.Textures; textures.SelectedIndex = 0;
        textures.Visibility = form.Textures.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        textures.Margin = new Thickness(0, 0, 0, 8);
        AutomationProperties.SetName(textures, "Texture");
        layout.Children.Add(textures);
        var surface = new Border { Background = EditorControls.Checkerboard(), Child = image };
        Grid.SetRow(surface, 1); layout.Children.Add(surface);
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(image, "Source texture");
        var details = new ScrollViewer { Content = status, MaxHeight = 100, Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(details, 2); layout.Children.Add(details);
        var close = EditorControls.Button("Close", Close, isCancel: true);
        close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(close, 3); layout.Children.Add(close);
        Content = layout;
        Loaded += LoadTexture;
        textures.SelectionChanged += (_, _) => { if (IsLoaded) LoadTexture(this, new RoutedEventArgs()); };
        Closed += (_, _) => { loading?.Cancel(); image.Source = null; };
    }

    private async void LoadTexture(object sender, RoutedEventArgs args)
    {
        Loaded -= LoadTexture;
        loading?.Cancel();
        using var request = new CancellationTokenSource();
        loading = request;
        image.Source = null; status.Text = "Loading texture...";
        try
        {
            if (textures.SelectedItem is not TexturePreviewSource source || form.Assets is null) return;
            var bitmap = await Task.Run(() => ModelPreview.LoadTexture(form.Assets, source.Path, request.Token), request.Token);
            if (request.IsCancellationRequested) return;
            image.Source = bitmap;
            status.Text = $"{source.Label} · {bitmap.PixelWidth} × {bitmap.PixelHeight}\n{source.Path}\nSource texture. The in-game shader is not simulated.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested) status.Text = "Cannot preview this texture: " + error.Message;
        }
        finally { if (ReferenceEquals(loading, request)) loading = null; }
    }

    public static bool CanPreview(FormRow? form) => form is { Assets: not null, Textures.Count: > 0 };

    public static Button CreateButton(Func<FormRow?> currentForm)
    {
        var button = new Button { Content = "Preview textures...", IsEnabled = CanPreview(currentForm()) };
        button.Click += (_, _) =>
        {
            var form = currentForm();
            if (!CanPreview(form)) return;
            new TexturePreviewWindow(form!) { Owner = GetWindow(button) }.ShowDialog();
        };
        return button;
    }
}
