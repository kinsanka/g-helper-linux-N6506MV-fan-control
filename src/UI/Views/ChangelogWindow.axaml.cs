using System.Diagnostics;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI.Views;

// Renders the project CHANGELOG.md inside an Avalonia window. Fetches the
// live copy from GitHub raw on Loaded, falls back to the binary's embedded
// copy if the network fails. Images are fetched async by the renderer.
public partial class ChangelogWindow : Window
{
    private const string ChangelogUrl =
        "https://raw.githubusercontent.com/kinsanka/g-helper-linux-N6506MV-fan-control/main/CHANGELOG.md";
    private const string ChangelogBrowserUrl =
        "https://github.com/kinsanka/g-helper-linux-N6506MV-fan-control/blob/main/CHANGELOG.md";
    private const string EmbeddedResourceName = "GHelper.Linux.CHANGELOG.md";

    private readonly List<Bitmap> _bitmapSink = new();

    public ChangelogWindow()
    {
        InitializeComponent();

        Labels.LanguageChanged += ApplyLabels;
        ApplyLabels();

        Loaded += (_, _) => _ = LoadAsync();
        Closed += (_, _) => DisposeBitmaps();
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("changelog_title");
        labelTitle.Text = Labels.Get("changelog_title");
        buttonOpenBrowser.Content = Labels.Get("changelog_open_browser");
        labelLoading.Text = Labels.Get("changelog_loading");
    }

    private async Task LoadAsync()
    {
        string? markdown = await FetchRemoteAsync();
        string source = "network";
        if (markdown == null)
        {
            markdown = LoadEmbedded();
            source = "embedded";
        }
        if (markdown == null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ShowError(Labels.Get("changelog_load_failed")));
            return;
        }
        Logger.WriteLine($"ChangelogWindow: rendering changelog from {source} ({markdown.Length} chars)");
        var blocks = ChangelogParser.Parse(markdown);
        await Dispatcher.UIThread.InvokeAsync(() => Render(blocks));
    }

    private static async Task<string?> FetchRemoteAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent",
                "G-Helper-Linux/" + AppConfig.AppVersion);
            http.Timeout = TimeSpan.FromSeconds(8);
            return await http.GetStringAsync(ChangelogUrl);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"ChangelogWindow: remote fetch failed: {ex.Message}");
            return null;
        }
    }

    private static string? LoadEmbedded()
    {
        try
        {
            var asm = typeof(ChangelogWindow).Assembly;
            using var stream = asm.GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null)
            {
                Logger.WriteLine($"ChangelogWindow: embedded resource '{EmbeddedResourceName}' not found");
                return null;
            }
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"ChangelogWindow: embedded fallback failed: {ex.Message}");
            return null;
        }
    }

    private void Render(List<ChangelogBlock> blocks)
    {
        panelBody.Children.Clear();
        ChangelogRenderer.Render(blocks, panelBody, _bitmapSink);
    }

    private void ShowError(string message)
    {
        panelBody.Children.Clear();
        panelBody.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.Parse("#FF8080")),
            FontSize = 13,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private void DisposeBitmaps()
    {
        lock (_bitmapSink)
        {
            foreach (var bmp in _bitmapSink)
            {
                try
                { bmp.Dispose(); }
                catch { }
            }
            _bitmapSink.Clear();
        }
    }

    private void ButtonOpenBrowser_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ChangelogBrowserUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"ChangelogWindow: open in browser failed: {ex.Message}");
        }
    }
}
