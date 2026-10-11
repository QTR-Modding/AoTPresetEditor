using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Color4 = HelixToolkit.Maths.Color4;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using SharpDX.Direct3D11;
using NiflySharp.Enums;

namespace AoTPresetEditor;

public sealed class ModelPreviewWindow : Window
{
    private const int AnimationFramesPerSecond = 30;
    private const float CameraFitPadding = 1.1f;
    private const int EffectSurfaceDepthBias = -1;
    private readonly FormRow form;
    private readonly FormRow? appliedEffect;
    private readonly Viewport3DX view = new()
    {
        BackgroundColor = Color.FromRgb(32, 35, 40), IsInertiaEnabled = false,
        ShowViewCube = false, ModelUpDirection = new Vector3D(0, 0, 1),
        OITRenderMode = OITRenderType.DepthPeeling
    };
    private readonly TextBlock status = new() { Text = "Loading model...", TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox models = new() { DisplayMemberPath = nameof(ModelPreviewSource.Label) };
    private readonly ComboBox effectModels = new() { DisplayMemberPath = nameof(ModelPreviewSource.Label) };
    private readonly Button reset = new() { Content = "Reset view", IsEnabled = false };
    private readonly CheckBox geometry = new() { Content = "Show geometry", IsEnabled = false,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        ToolTip = "Show solid surfaces without textures or transparency, including their back faces." };
    private readonly Grid animationControls = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ComboBox clips = new() { DisplayMemberPath = nameof(ModelPreviewAnimationClip.Name), MinWidth = 160, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button play = new() { Content = "Play", MinWidth = 65, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Slider position = new() { Minimum = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 100 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1d / AnimationFramesPerSecond) };
    private readonly PlaybackState playback = new();
    private readonly List<ShownMesh> meshes = [];
    private readonly List<ShownParticle> particles = [];
    private DefaultEffectsManager? effects;
    private ModelPreviewResult? model;
    private ShaderDisplay? shader;
    private CancellationTokenSource? loading;

    private sealed record ShownMesh(ModelPreviewMesh Source, MeshGeometryModel3D Visual, PhongMaterial Material);
    private sealed record ShownParticle(ModelPreviewParticleMesh Source, BillboardTextModel3D Visual, ModelPreviewParticleBillboards Geometry);
    private sealed record ShownShaderMesh(EffectShaderOverlay Source, MeshGeometryModel3D Visual);
    private sealed class ShaderDisplay(EffectShaderPreview source)
    {
        public readonly EffectShaderPreview Source = source;
        public readonly List<ShownShaderMesh> Meshes = [];
        public ModelPreviewParticleBillboards? Geometry;
        public BillboardTextModel3D? Particles;
    }
    private sealed class PlaybackState
    {
        public readonly Stopwatch Clock = new();
        public double StartPosition;
        public bool UpdatingSlider;
    }

    public ModelPreviewWindow(FormRow form, FormRow? appliedEffect = null)
    {
        this.form = form;
        this.appliedEffect = appliedEffect;
        var name = form.Name.Length != 0 ? form.Name : form.EditorId.Length != 0 ? form.EditorId : form.Identifier;
        var effectName = appliedEffect is null ? "" : appliedEffect.Name.Length != 0 ? appliedEffect.Name
            : appliedEffect.EditorId.Length != 0 ? appliedEffect.EditorId : appliedEffect.Identifier;
        Title = appliedEffect is null ? "Model preview - " + name : "Effect preview - " + effectName + " on " + name;
        Width = 900; Height = 700; MinWidth = 340; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        EditorControls.FitToOwner(this);
        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        for (var row = 0; row < 3; row++) layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        models.ItemsSource = form.Models; models.SelectedIndex = 0;
        AutomationProperties.SetName(models, "Model");
        models.ToolTip = "Choose which model to preview.";
        models.Margin = new Thickness(0, 0, 0, 8);
        models.Visibility = form.Models.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var selectors = new StackPanel(); selectors.Children.Add(models);
        effectModels.ItemsSource = appliedEffect?.Type == "ArtObject" ? appliedEffect.Models : null;
        effectModels.SelectedIndex = 0; effectModels.Margin = new Thickness(0, 0, 0, 8);
        effectModels.Visibility = appliedEffect?.Type == "ArtObject" && appliedEffect.Models.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(effectModels, "Effect model"); effectModels.ToolTip = "Choose which art-object model to preview on the item.";
        selectors.Children.Add(effectModels); layout.Children.Add(selectors);
        view.InputBindings.Add(new MouseBinding(ViewportCommands.Rotate, new MouseGesture(MouseAction.LeftClick)));
        view.RenderExceptionOccurred += (_, args) => { args.Handled = true; PreviewFailed(args.Exception); };
        view.CameraChanged += (_, _) => UpdateShader(playback.StartPosition + playback.Clock.Elapsed.TotalSeconds);
        Grid.SetRow(view, 1); layout.Children.Add(view);
        AutomationProperties.SetName(view, "Model. Drag to rotate and scroll to zoom.");
        BuildAnimationControls();
        Grid.SetRow(animationControls, 2); layout.Children.Add(animationControls);
        var details = new ScrollViewer
        {
            Content = status, MaxHeight = 100, Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(details, 3); layout.Children.Add(details);
        var footer = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(geometry);
        geometry.Checked += (_, _) => ShowGeometry(); geometry.Unchecked += (_, _) => ShowGeometry();
        footer.Children.Add(new TextBlock
        {
            Text = "Drag to rotate · Scroll to zoom", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });
        reset.Click += (_, _) => ResetView(); footer.Children.Add(reset);
        footer.Children.Add(EditorControls.Button("Close", Close, isCancel: true));
        Grid.SetRow(footer, 4); layout.Children.Add(footer);
        Content = layout;
        Loaded += LoadModel;
        models.SelectionChanged += (_, _) => { if (IsLoaded) LoadModel(this, new RoutedEventArgs()); };
        effectModels.SelectionChanged += (_, _) => { if (IsLoaded) LoadModel(this, new RoutedEventArgs()); };
        Closed += (_, _) => { loading?.Cancel(); StopPlayback(); model = null; shader = null; meshes.Clear(); particles.Clear(); view.Dispose(); effects?.Dispose(); };
    }

    private void BuildAnimationControls()
    {
        animationControls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        animationControls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        animationControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        animationControls.ColumnDefinitions.Add(new ColumnDefinition());
        animationControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        clips.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetColumnSpan(clips, 3);
        animationControls.Children.Add(clips);
        Grid.SetRow(play, 1); animationControls.Children.Add(play);
        Grid.SetRow(position, 1); Grid.SetColumn(position, 1); animationControls.Children.Add(position);
        Grid.SetRow(time, 1); Grid.SetColumn(time, 2); animationControls.Children.Add(time);
        AutomationProperties.SetName(clips, "Animation"); AutomationProperties.SetName(position, "Animation position in seconds");
        play.Click += (_, _) =>
        {
            if (playback.Clock.IsRunning) StopPlayback();
            else StartPlayback();
        };
        clips.SelectionChanged += (_, _) =>
        {
            StopPlayback();
            if (clips.SelectedItem is not ModelPreviewAnimationClip clip) return;
            playback.StartPosition = 0;
            position.Maximum = clip.Duration; position.Value = 0; play.IsEnabled = clip.Duration > 0;
            ApplyAnimation(0);
        };
        position.ValueChanged += (_, _) =>
        {
            if (playback.UpdatingSlider) return;
            playback.StartPosition = position.Value;
            if (playback.Clock.IsRunning) playback.Clock.Restart();
            ApplyAnimation(position.Value);
        };
        timer.Tick += (_, _) =>
        {
            var seconds = playback.StartPosition + playback.Clock.Elapsed.TotalSeconds;
            var shown = seconds;
            if (clips.SelectedItem is not ModelPreviewAnimationClip clip) return;
            if (clip.Cycle == CycleType.CYCLE_CLAMP && seconds >= position.Maximum)
            {
                seconds = shown = position.Maximum; StopPlayback(); playback.StartPosition = seconds;
            }
            else if (position.Maximum > 0)
            {
                shown %= clip.Cycle == CycleType.CYCLE_REVERSE ? position.Maximum * 2 : position.Maximum;
                if (shown > position.Maximum) shown = position.Maximum * 2 - shown;
            }
            playback.UpdatingSlider = true;
            position.Value = shown;
            playback.UpdatingSlider = false;
            ApplyAnimation(seconds);
        };
    }

    private void StartPlayback()
    {
        if (!play.IsEnabled || clips.SelectedItem is not ModelPreviewAnimationClip) return;
        if (clips.SelectedItem is ModelPreviewAnimationClip { Cycle: CycleType.CYCLE_CLAMP } && playback.StartPosition >= position.Maximum) playback.StartPosition = 0;
        playback.Clock.Restart(); timer.Start(); play.Content = "Pause";
    }

    private async void LoadModel(object sender, RoutedEventArgs args)
    {
        Loaded -= LoadModel; loading?.Cancel(); StopPlayback();
        using var request = new CancellationTokenSource(); loading = request;
        model = null; shader = null; meshes.Clear(); particles.Clear(); view.Items.Clear(); animationControls.Visibility = Visibility.Collapsed;
        clips.ItemsSource = null; reset.IsEnabled = geometry.IsEnabled = false; status.Text = "Loading model...";
        try
        {
            if (models.SelectedItem is not ModelPreviewSource source || form.Assets is null) return;
            var artSource = effectModels.SelectedItem as ModelPreviewSource;
            var result = await Task.Run(() => appliedEffect?.Type == "ArtObject"
                ? ModelPreview.Load(source, form.Assets, artSource
                    ?? throw new InvalidDataException("The art object has no model."), appliedEffect.Assets
                    ?? throw new InvalidDataException("The art object's assets are unavailable."), request.Token)
                : ModelPreview.Load(source, form.Assets, request.Token), request.Token);
            var shaderPreview = appliedEffect?.Type == "EffectShader" ? await Task.Run(() => EffectShaderPreview.Load(
                appliedEffect.EffectShader ?? throw new InvalidDataException("The effect shader is unavailable."), result,
                appliedEffect.Assets ?? throw new InvalidDataException("The shader's assets are unavailable."), request.Token), request.Token) : null;
            if (request.IsCancellationRequested) return;
            effects ??= ModelPreviewParticleShader.CreateEffectsManager(); view.EffectsManager = effects;
            model = result; ShowModel(); status.Text = result.Details;
            if (shaderPreview is not null) { ShowShader(shaderPreview); status.Text += "\n" + shaderPreview.Details; }
            reset.IsEnabled = geometry.IsEnabled = true;
            var animations = shaderPreview is null ? result.Clips : [new ModelPreviewAnimationClip("Effect", shaderPreview.Duration, shaderPreview.Cycle)];
            if (animations.Count > 0)
            {
                animationControls.Visibility = Visibility.Visible;
                clips.Visibility = animations.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                clips.ItemsSource = animations;
                var looping = animations.ToList().FindIndex(clip => clip.Cycle == CycleType.CYCLE_LOOP);
                clips.SelectedIndex = appliedEffect is not null || form.Type == "ArtObject" ? Math.Max(0, looping) : 0;
            }
            ResetView();
            if (appliedEffect is not null || form.Type == "ArtObject") StartPlayback();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!request.IsCancellationRequested) PreviewFailed(error); }
        finally { if (ReferenceEquals(loading, request)) loading = null; }
    }

    private void ShowShader(EffectShaderPreview preview)
    {
        shader = new ShaderDisplay(preview);
        foreach (var overlay in preview.Overlays)
        {
            var target = meshes[overlay.TargetMeshIndex];
            var visual = new MeshGeometryModel3D
            {
                Geometry = overlay.Geometry, Material = new HelixToolkit.Wpf.SharpDX.DiffuseMaterial(overlay.Material), IsTransparent = true,
                CullMode = target.Source.DoubleSided ? CullMode.None : CullMode.Back, DepthBias = EffectSurfaceDepthBias,
                Visibility = target.Visual.Visibility
            };
            shader.Meshes.Add(new(overlay, visual)); view.Items.Add(visual);
        }
        if (preview.ParticleTexture is { } texture)
        {
            shader.Geometry = new ModelPreviewParticleBillboards(texture);
            shader.Particles = new BillboardTextModel3D { Geometry = shader.Geometry, FixedSize = false, IsTransparent = true };
            view.Items.Add(shader.Particles);
        }
        ShowGeometry();
    }

    private void UpdateShader(double seconds)
    {
        if (shader is null || view.Camera is null) return;
        shader.Source.Evaluate(seconds, view.Camera.Position.ToVector3());
        foreach (var overlay in shader.Meshes)
        {
            var target = meshes[overlay.Source.TargetMeshIndex].Visual;
            overlay.Visual.Transform = target.Transform;
            overlay.Visual.Visibility = geometry.IsChecked == true ? Visibility.Hidden : target.Visibility;
        }
        shader.Geometry?.Update(shader.Source.EvaluateParticles(seconds));
    }

    private void PreviewFailed(Exception error)
    {
        StopPlayback(); reset.IsEnabled = geometry.IsEnabled = false; animationControls.Visibility = Visibility.Collapsed;
        status.Text = "Cannot preview this model: " + error.Message;
    }

    private void ShowModel()
    {
        if (model is null) return;
        view.Items.Add(new AmbientLight3D { Color = Color.FromRgb(85, 85, 85) });
        view.Items.Add(new DirectionalLight3D { Color = Colors.White, Direction = new Vector3D(-1, -1, -2) });
        view.Items.Add(new DirectionalLight3D { Color = Color.FromRgb(100, 100, 100), Direction = new Vector3D(1, 1, 0) });
        if (model.EnvironmentMap is { } cube) view.Items.Add(new EnvironmentMap3D { Texture = cube, SkipRendering = true });
        foreach (var mesh in model.Meshes)
        {
            var material = new PhongMaterial(mesh.Material);
            var visual = new MeshGeometryModel3D
            {
                Geometry = mesh.Geometry, Material = material, IsTransparent = mesh.Transparent,
                CullMode = mesh.DoubleSided ? CullMode.None : CullMode.Back,
                Visibility = mesh.Visible ? Visibility.Visible : Visibility.Hidden
            };
            meshes.Add(new(mesh, visual, material)); view.Items.Add(visual);
        }
        foreach (var source in model.Particles ?? [])
        {
            var billboards = new ModelPreviewParticleBillboards(source.Texture, source.PaletteColor,
                source.PaletteAlpha, source.TextureClampMode);
            var visual = new BillboardTextModel3D { Geometry = billboards, FixedSize = false, IsTransparent = true };
            particles.Add(new(source, visual, billboards)); view.Items.Add(visual);
        }
        ShowGeometry();
    }

    private void ShowGeometry()
    {
        var plain = geometry.IsChecked == true;
        var solid = new PhongMaterial { DiffuseColor = new Color4(.8f, .8f, .8f, 1), SpecularColor = new Color4(0, 0, 0, 1) };
        foreach (var mesh in meshes)
        {
            mesh.Visual.Material = plain ? solid : mesh.Material;
            mesh.Visual.IsTransparent = !plain && (mesh.Source.Transparent || mesh.Material.DiffuseColor.Alpha < 1);
            mesh.Visual.CullMode = plain || mesh.Source.DoubleSided ? CullMode.None : CullMode.Back;
        }
        foreach (var particle in particles) particle.Visual.Visibility = plain ? Visibility.Hidden : Visibility.Visible;
        if (shader is not null)
        {
            foreach (var overlay in shader.Meshes) overlay.Visual.Visibility = plain ? Visibility.Hidden : meshes[overlay.Source.TargetMeshIndex].Visual.Visibility;
            if (shader.Particles is not null) shader.Particles.Visibility = plain ? Visibility.Hidden : Visibility.Visible;
        }
    }

    private void ApplyAnimation(double seconds)
    {
        if (clips.SelectedItem is not ModelPreviewAnimationClip clip) return;
        if (shader is null && model?.Animation is { } animation)
        {
            var index = animation.Clips.ToList().FindIndex(candidate => candidate.Name == clip.Name);
            ApplyMeshAnimation(animation, index >= 0 ? index : null, seconds);
        }
        foreach (var particle in particles) particle.Geometry.Update(particle.Source.System.Evaluate(clip.Name, seconds));
        UpdateShader(seconds);
        time.Text = $"{position.Value:0.0} / {position.Maximum:0.0} s";
    }

    private void ApplyMeshAnimation(ModelPreviewAnimation animation, int? clipIndex, double seconds)
    {
        foreach (var frame in animation.Evaluate(clipIndex, seconds))
        {
            var mesh = meshes[frame.MeshIndex];
            var transform = frame.Transform.ToMatrix3D();
            if (mesh.Visual.Transform.Value != transform) mesh.Visual.Transform = new MatrixTransform3D(transform);
            mesh.Visual.Visibility = frame.Visible ? Visibility.Visible : Visibility.Hidden;
            if (frame.Alpha is { } alpha)
            {
                var tint = mesh.Source.Material.DiffuseColor; tint.Alpha = Math.Clamp(alpha, 0, 1);
                if (mesh.Material.DiffuseColor != tint)
                {
                    mesh.Material.DiffuseColor = tint; ModelPreview.UpdateVertexTint(mesh.Source, tint);
                }
                mesh.Visual.IsTransparent = geometry.IsChecked != true && (mesh.Source.Transparent || tint.Alpha < 1);
            }
            if (frame.EmissiveColor is { } color)
            {
                color *= frame.EmissiveMultiplier ?? 1;
                mesh.Material.EmissiveColor = new Color4(color.X, color.Y, color.Z, 1);
            }
            if (frame.UVOffset is { } offset && frame.UVScale is { } scale) mesh.Material.UVTransform = new UVTransform(0, scale, offset);
        }
    }

    private void StopPlayback()
    {
        timer.Stop(); playback.StartPosition += playback.Clock.Elapsed.TotalSeconds; playback.Clock.Reset(); play.Content = "Play";
    }

    private void ResetView()
    {
        if (model is null) return;
        view.UpdateLayout();
        if (view.ActualWidth <= 0 || view.ActualHeight <= 0) return;
        var camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera
        {
            Position = new Point3D(100, 100, 75), LookDirection = new Vector3D(-100, -100, -75),
            UpDirection = new Vector3D(0, 0, 1), FieldOfView = 40
        };
        view.Camera = camera;
        var bounds = PreviewBounds(currentPose: true);
        if (bounds.IsEmpty) bounds = PreviewBounds(currentPose: false);
        var box = bounds.ToBoundingBox();
        var center = box.Center; var extent = (box.Maximum - box.Minimum) * (CameraFitPadding / 2);
        box.Minimum = center - extent; box.Maximum = center + extent;
        camera.Position = center.ToPoint3D() - camera.LookDirection;
        var core = camera.CameraInternal;
        var shift = core.CreateFrustum((float)(view.ActualWidth / view.ActualHeight)).GetZoomToExtentsShiftDistance(box.GetCorners());
        var direction = System.Numerics.Vector3.Normalize(core.LookDirection);
        camera.Position = (core.Position + direction * shift).ToPoint3D();
        camera.LookDirection = (center - camera.Position.ToVector3()).ToVector3D();
        camera.FarPlaneDistance = Math.Max(camera.FarPlaneDistance, camera.LookDirection.Length + extent.Length());
    }

    private Rect3D PreviewBounds(bool currentPose)
    {
        var bounds = Rect3D.Empty;
        foreach (var mesh in meshes)
        {
            if (currentPose && (mesh.Visual.Visibility != Visibility.Visible || geometry.IsChecked != true && mesh.Material.DiffuseColor.Alpha <= 0)) continue;
            var transform = currentPose ? mesh.Visual.Transform.Value : Matrix3D.Identity;
            foreach (var vertex in mesh.Source.Geometry.Positions!) bounds.Union(transform.Transform(new Point3D(vertex.X, vertex.Y, vertex.Z)));
        }
        foreach (var particle in particles)
        {
            if (currentPose)
            {
                if (particle.Visual.Visibility != Visibility.Visible) continue;
                AddBillboardBounds(particle.Geometry, ref bounds);
            }
            if (!currentPose || appliedEffect is not null)
            {
                var point = particle.Source.System.BoundsCenter; var reach = particle.Source.System.BoundsRadius;
                bounds.Union(new Rect3D(point.X - reach, point.Y - reach, point.Z - reach, reach * 2, reach * 2, reach * 2));
            }
        }
        if (shader?.Geometry is { } billboards && shader.Particles?.Visibility == Visibility.Visible)
        {
            AddBillboardBounds(billboards, ref bounds);
            var box = shader.Source.Bounds;
            bounds.Union(new Rect3D(box.Minimum.X, box.Minimum.Y, box.Minimum.Z,
                box.Maximum.X - box.Minimum.X, box.Maximum.Y - box.Minimum.Y, box.Maximum.Z - box.Minimum.Z));
        }
        return bounds;
    }

    private static void AddBillboardBounds(ModelPreviewParticleBillboards geometry, ref Rect3D bounds)
    {
        foreach (var image in geometry.ImageInfos)
        {
            var point = image.Position; var reach = Math.Sqrt(image.Width * image.Width + image.Height * image.Height) / 2;
            bounds.Union(new Rect3D(point.X - reach, point.Y - reach, point.Z - reach, reach * 2, reach * 2, reach * 2));
        }
    }

    public static bool CanPreview(FormRow? form) => form is { Assets: not null, Models.Count: > 0 };

    public static bool CanPreviewEffect(FormRow? effect) => effect is { Assets: not null } &&
        (effect.Type == "ArtObject" && effect.Models.Count > 0 || effect.Type == "EffectShader" && effect.EffectShader is not null);

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
