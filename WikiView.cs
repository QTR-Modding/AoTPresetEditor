using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AoTPresetEditor;

public sealed class WikiView : UserControl, IDisposable
{
    public const string WikiAddress = "https://github.com/QTR-Modding/AlchemyOfTime/wiki";
    private readonly Grid page = new();
    private readonly TextBlock message = new() { Text = "Loading wiki...", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    private readonly Button back = new() { Content = "Back", Visibility = Visibility.Collapsed };
    private readonly Button forward = new() { Content = "Forward", Visibility = Visibility.Collapsed };
    private readonly Button reload = new() { Content = "Reload", Visibility = Visibility.Collapsed };
    private readonly Button home = new() { Content = "Wiki home", Visibility = Visibility.Collapsed };
    private WebView2CompositionControl? browser;
    private bool disposed;

    public WikiView()
    {
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        var layout = new DockPanel { Margin = new Thickness(8) };
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var external = new Button { Content = "Open in browser" };
        back.Click += (_, _) => browser?.GoBack();
        forward.Click += (_, _) => browser?.GoForward();
        home.Click += (_, _) => browser?.CoreWebView2?.Navigate(WikiAddress);
        reload.Click += (_, _) => browser?.Reload();
        external.Click += (_, _) => OpenInBrowser(browser?.Source?.AbsoluteUri ?? WikiAddress);
        foreach (var button in new[] { back, forward, home, reload, external }) toolbar.Children.Add(button);
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        page.Children.Add(message);
        layout.Children.Add(page);
        Content = layout;
        Loaded += InitializeBrowser;
    }

    private async void InitializeBrowser(object sender, RoutedEventArgs e)
    {
        Loaded -= InitializeBrowser;
        if (disposed) return;
        browser = new WebView2CompositionControl { AllowExternalDrop = false };
        SetBrowserBackground();
        page.Children.Insert(0, browser);
        try
        {
            var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AoTPresetEditor", "Wiki");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            if (disposed) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (disposed) return;
            var core = browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            // GitHub follows scrollIntoView with scrollBy, which cancels a smooth heading jump.
            await core.AddScriptToExecuteOnDocumentCreatedAsync($$"""
                const wiki = new URL('{{WikiAddress}}');
                if (location.origin === wiki.origin &&
                    (location.pathname === wiki.pathname || location.pathname.startsWith(wiki.pathname + '/'))) {
                    document.addEventListener('DOMContentLoaded', () => {
                        document.documentElement.style.scrollBehavior = 'auto';
                    }, { once: true });
                }
                """);
            if (disposed) return;
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenInBrowser(args.Uri);
            };
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.NavigationStarting += NavigationStarting;
            core.NavigationCompleted += NavigationCompleted;
            core.HistoryChanged += (_, _) => UpdateNavigation();
            ApplyAppearance();
            home.Visibility = reload.Visibility = Visibility.Visible;
            core.Navigate(WikiAddress);
        }
        catch (Exception error)
        {
            if (disposed) return;
            browser.Dispose();
            page.Children.Remove(browser);
            browser = null;
            ShowMessage("The wiki could not open here. Use Open in browser.\n" + error.Message);
        }
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BackgroundProperty) ApplyAppearance();
    }

    internal void ApplyAppearance()
    {
        if (disposed || browser is null) return;
        SetBrowserBackground();
        if (browser.CoreWebView2 is not { } core) return;
        core.Profile.PreferredColorScheme = (Application.Current as App)?.Appearance.Theme switch
        {
            EditorTheme.Dark => CoreWebView2PreferredColorScheme.Dark,
            EditorTheme.Light => CoreWebView2PreferredColorScheme.Light,
            _ => CoreWebView2PreferredColorScheme.Auto
        };
    }

    private void SetBrowserBackground()
    {
        if (browser is null || Background is not SolidColorBrush brush) return;
        var color = brush.Color;
        browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http"))
        {
            e.Cancel = true;
            return;
        }
        message.Visibility = Visibility.Collapsed;
        if (browser is not null) browser.Visibility = Visibility.Visible;
    }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess) message.Visibility = Visibility.Collapsed;
        else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            if (browser is not null) browser.Visibility = Visibility.Hidden;
            ShowMessage("The wiki could not load. Check your connection, then Reload or Open in browser.");
        }
        UpdateNavigation();
    }

    private void UpdateNavigation()
    {
        back.Visibility = browser?.CanGoBack == true ? Visibility.Visible : Visibility.Collapsed;
        forward.Visibility = browser?.CanGoForward == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMessage(string text)
    {
        message.Text = text;
        message.Visibility = Visibility.Visible;
    }

    private void OpenInBrowser(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http")) return;
        try { Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { ShowMessage("Could not open the browser.\n" + error.Message); }
    }

    public void Dispose()
    {
        disposed = true;
        browser?.Dispose();
    }
}
