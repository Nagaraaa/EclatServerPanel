using System.IO;
using System.Windows;

namespace EclatServerPanel;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { SelfTests.Run(); await PanelUiTests.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); Shutdown(1); }
            return;
        }
        int checkIndex = Array.IndexOf(e.Args, "--check-connection");
        if (checkIndex >= 0 && checkIndex + 1 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var transport = new SshTransport();
                var settings = ConnectionSettings.Load();
                var status = await transport.SendAsync(settings, "status");
                var backups = await transport.SendAsync(settings, "backups");
                var diagnostics = await transport.SendAsync(settings, "diagnostics");
                bool? downloadVerified = null;
                long? downloadBytes = null;
                if (backups.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                {
                    var download = await transport.SendAsync(settings, "download", items[0].GetProperty("name").GetString());
                    var bytes = SshTransport.VerifyDownload(download);
                    downloadVerified = true;
                    downloadBytes = bytes.LongLength;
                }
                await File.WriteAllTextAsync(e.Args[checkIndex + 1], System.Text.Json.JsonSerializer.Serialize(new { ok = true, status, backups, diagnostics, downloadVerified, downloadBytes }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(e.Args[checkIndex + 1], System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
                Shutdown(1);
            }
            return;
        }
        bool demo = e.Args.Contains("--demo") || e.Args.Contains("--capture-demo") || e.Args.Contains("--capture-demo-pages");
        var window = new MainWindow(demo);
        MainWindow = window;
        window.Show();
        int captureIndex = Array.IndexOf(e.Args, "--capture-demo");
        if (captureIndex < 0) captureIndex = Array.IndexOf(e.Args, "--capture-live");
        if (captureIndex >= 0 && captureIndex + 1 < e.Args.Length)
        {
            window.ContentRendered += async (_, _) =>
            {
                await window.WaitForIdleAsync();
                await window.RefreshAsync();
                await Task.Delay(1400); // let counters, gauges and curves finish
                Capture(window, e.Args[captureIndex + 1]);
                Shutdown();
            };
        }
        // --capture-demo-pages DIR: one PNG per page, after its animations.
        int pagesIndex = Array.IndexOf(e.Args, "--capture-demo-pages");
        if (pagesIndex >= 0 && pagesIndex + 1 < e.Args.Length)
        {
            window.ContentRendered += async (_, _) =>
            {
                var directory = Directory.CreateDirectory(Path.GetFullPath(e.Args[pagesIndex + 1])).FullName;
                await window.WaitForIdleAsync();
                await window.RefreshAsync();
                await window.RefreshAsync();
                foreach (var page in new[] { "Overview", "Sessions", "Logs", "Backups", "Diagnostics", "Connection" })
                {
                    await window.ShowPageForCaptureAsync(page);
                    Capture(window, Path.Combine(directory, page.ToLowerInvariant() + ".png"));
                    if (page is "Sessions" or "Diagnostics")
                    {
                        var toggle = (System.Windows.Controls.Primitives.ToggleButton)window.FindName(page == "Sessions" ? "RawSessionsToggle" : "RawDiagnosticsToggle");
                        toggle.IsChecked = true; await Task.Delay(350);
                        Capture(window, Path.Combine(directory, page.ToLowerInvariant() + "-raw.png"));
                        toggle.IsChecked = false;
                    }

                }
                await window.ShowRestoreDialogForCaptureAsync();
                Capture(window, Path.Combine(directory, "restore-dialog.png"));
                Shutdown();
            };
        }
    }

    static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.GetFullPath(path));
        encoder.Save(stream);
    }
}
