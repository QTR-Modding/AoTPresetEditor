using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using Mutagen.Bethesda.Assets;
using Mutagen.Bethesda.Assets.DI;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace AoTPresetEditor;

public sealed class SoundPlayback : IDisposable
{
    private IXAudio2? engine;
    private IXAudio2MasteringVoice? output;
    private IXAudio2SourceVoice? voice;
    private AudioBuffer? buffer;

    public event Action? Ended;
    public event Action<string>? Failed;
    public bool IsPlaying => voice?.State.BuffersQueued > 0;
    public ulong SamplesPlayed => voice?.State.SamplesPlayed ?? 0;

    public SoundPlayback(Stream stream)
    {
        try
        {
            using var sound = new SoundStream(stream);
            if (sound.Format is null || sound.Length == 0) throw new InvalidDataException("The sound sample contains no audio.");
            using var data = sound.ToDataStream();
            buffer = new AudioBuffer(data);
            engine = XAudio2.XAudio2Create();
            output = engine.CreateMasteringVoice();
            voice = engine.CreateSourceVoice(sound.Format, enableCallbackEvents: true);
            voice.StreamEnd += OnEnded;
            voice.VoiceError += OnVoiceError;
            voice.SubmitSourceBuffer(buffer, sound.DecodedPacketsInfo).CheckError();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Play(float volume = 1)
    {
        SetVolume(volume);
        voice!.Start().CheckError();
    }

    public void SetVolume(float volume) => output?.SetVolume(volume);

    private void OnEnded() => Ended?.Invoke();
    private void OnVoiceError(IXAudio2SourceVoice.VoiceErrorArgs error) => Failed?.Invoke(error.Result.ToString());

    public void Dispose()
    {
        if (voice is not null)
        {
            voice.StreamEnd -= OnEnded;
            voice.VoiceError -= OnVoiceError;
            voice.DestroyVoice();
            voice.Dispose();
            voice = null;
        }
        buffer?.Dispose(); buffer = null;
        output?.DestroyVoice(); output?.Dispose(); output = null;
        engine?.Dispose(); engine = null;
    }
}

public sealed class SoundPreview : UserControl
{
    private readonly Slider volume = new() { Minimum = 0, Maximum = 1, Value = 1, SmallChange = 0.01, LargeChange = 0.1,
        Width = 110, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
    private readonly ComboBox samples = new() { MinWidth = 160, MaxWidth = 460, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button play = new() { Content = "Play", ToolTip = "Play the selected sound sample", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button stop = new() { Content = "Stop", IsEnabled = false };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private IAssetProvider? assets;
    private CancellationTokenSource? loading;
    private SoundPlayback? playback;

    public SoundPreview()
    {
        var buttons = new WrapPanel();
        buttons.Children.Add(samples); buttons.Children.Add(play); buttons.Children.Add(stop);
        var volumeControls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        volumeControls.Children.Add(new TextBlock { Text = "Volume", VerticalAlignment = VerticalAlignment.Center });
        volumeControls.Children.Add(volume);
        var percentage = new TextBlock { MinWidth = 38, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        percentage.SetBinding(TextBlock.TextProperty, new Binding(nameof(Slider.Value)) { Source = volume, StringFormat = "{0:P0}" });
        volumeControls.Children.Add(percentage); buttons.Children.Add(volumeControls);
        AutomationProperties.SetName(volume, "Preview volume");
        if (Application.Current is App app)
            volume.SetBinding(Slider.ValueProperty, new Binding(nameof(App.PreviewVolume)) { Source = app, Mode = BindingMode.TwoWay });
        volume.ValueChanged += (_, args) => playback?.SetVolume((float)args.NewValue);
        var panel = new StackPanel(); panel.Children.Add(buttons); panel.Children.Add(status); Content = panel;
        play.Click += async (_, _) => await PlaySelected();
        stop.Click += (_, _) => Stop();
        samples.SelectionChanged += (_, _) => { Stop(); status.Text = ""; };
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); };
    }

    public void SetSamples(IAssetProvider? provider, IReadOnlyList<string> paths)
    {
        Stop();
        assets = provider;
        var choices = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        samples.ItemsSource = choices;
        samples.SelectedIndex = choices.Length == 0 ? -1 : 0;
        samples.Visibility = choices.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        play.ToolTip = choices.Length == 1 ? $"Play {choices[0]}" : "Play the selected sound sample";
        play.IsEnabled = assets is not null && choices.Length != 0;
        status.Text = choices.Length == 0 ? "This sound has no audio samples." : provider is null ? "Load game data to preview this sound." : "";
    }

    public void Stop()
    {
        loading?.Cancel();
        loading = null;
        playback?.Dispose(); playback = null;
        stop.IsEnabled = false;
        play.IsEnabled = assets is not null && samples.SelectedItem is string;
        if (status.Text == "Loading sound...") status.Text = "";
    }

    private async Task PlaySelected()
    {
        if (assets is null || samples.SelectedItem is not string path) return;
        Stop();
        var source = assets;
        using var request = new CancellationTokenSource();
        loading = request;
        play.IsEnabled = false; stop.IsEnabled = true; status.Text = "Loading sound...";
        SoundPlayback? loaded = null;
        try
        {
            loaded = await Task.Run(() =>
            {
                request.Token.ThrowIfCancellationRequested();
                if (!source.TryGetStream(new DataRelativePath(path), out var stream))
                    throw new FileNotFoundException($"Sound sample not found: {path}. Check that its mod is enabled in MO2.");
                using (stream)
                {
                    request.Token.ThrowIfCancellationRequested();
                    return new SoundPlayback(stream);
                }
            });
            if (request.IsCancellationRequested) { loaded.Dispose(); return; }
            playback = loaded;
            loaded.Ended += () => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(playback, loaded)) Stop();
            });
            loaded.Failed += message => Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(playback, loaded)) return;
                Stop();
                status.Text = $"Cannot decode {path}: {message}";
            });
            loaded.Play((float)volume.Value);
            status.Text = "";
        }
        catch (OperationCanceledException) { loaded?.Dispose(); }
        catch (Exception error)
        {
            loaded?.Dispose();
            if (!request.IsCancellationRequested)
            {
                Stop();
                status.Text = error is FileNotFoundException ? error.Message : $"Cannot preview {path}: {error.Message}";
            }
        }
        finally { if (ReferenceEquals(loading, request)) loading = null; }
    }
}
