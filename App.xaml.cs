using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AuroraTranslate.Services;

namespace AuroraTranslate;

public partial class App : Application
{
    public const string OfficialRepository = "rarmaximus181-cmd/Aurora-Translate-AI";
    private UpdateService? _updates;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show("Aurora could not complete that action. Please try again.\n\n" + args.Exception.Message,
                "Aurora Translate", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        if (e.Args.Contains("--self-test"))
        {
            var pathIndex = Array.IndexOf(e.Args, "--report");
            var reportPath = pathIndex >= 0 && pathIndex + 1 < e.Args.Length ? e.Args[pathIndex + 1] : Path.Combine(AppContext.BaseDirectory, "self-test.json");
            try { var success = await SelfTestRunner.RunAsync(reportPath); Shutdown(success ? 0 : 1); }
            catch (Exception ex) { File.WriteAllText(reportPath, ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--voice-check"))
        {
            var reportIndex = Array.IndexOf(e.Args, "--report");
            var reportPath = reportIndex >= 0 && reportIndex + 1 < e.Args.Length ? e.Args[reportIndex + 1] : Path.Combine(AppContext.BaseDirectory, "voice-check.json");
            var checks = new List<object>();
            try
            {
                using var library = new Services.VoiceLibraryService();
                var voices = library.GetInstalledVoices();
                if (voices.Count == 0) throw new InvalidOperationException("No extra voice is installed.");
                foreach (var voice in voices)
                {
                    var wave = await library.SynthesizeToFileAsync("Hello! This is your Aurora voice.", voice);
                    try { checks.Add(new { voice.Name, voice.Language, waveBytes = new FileInfo(wave).Length }); }
                    finally { File.Delete(wave); }
                }
                File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(new { passed = true, checks })); Shutdown();
            }
            catch (Exception ex) { File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(new { passed = false, error = ex.Message })); Shutdown(1); }
            return;
        }
        var diagnostic = e.Args.Contains("--screenshot") || e.Args.Contains("--ui-test") || e.Args.Contains("--startup-preview");
        StartupWindow? startup = null;
        UpdateCheckResult? updateCheck = null;
        _updates = new UpdateService();
        if (!diagnostic)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            using var startupCancellation = new CancellationTokenSource();
            startup = new StartupWindow();
            MainWindow = startup;
            startup.Closed += (_, _) => { if (startup.UserClosed) startupCancellation.Cancel(); };
            startup.Show();
            startup.UpdateStatus("Preparing your workspace and checking updates…");
            try
            {
                var check = _updates.CheckAsync(OfficialRepository, startupCancellation.Token);
                await startup.PlayAsync(AppStore.LoadSettings().AnimateTheme, startupCancellation.Token);
                updateCheck = await check;
            }
            catch (OperationCanceledException) { Shutdown(); return; }
            if (startup.UserClosed) { Shutdown(); return; }
            if (updateCheck.Status == UpdateStatus.Required && updateCheck.Info is not null)
            {
                var gate = new UpdateRequiredWindow(_updates, updateCheck.Info);
                MainWindow = gate;
                startup.Complete();
                gate.ShowDialog();
                Shutdown(); return;
            }
            startup.UpdateStatus("Opening Aurora…");
        }
        if (e.Args.Contains("--startup-preview"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            startup = new StartupWindow(); MainWindow = startup; startup.Show();
            var play = startup.PlayAsync(true);
            await Task.Delay(850);
            var captureIndex = Array.IndexOf(e.Args, "--screenshot");
            if (captureIndex >= 0 && captureIndex + 1 < e.Args.Length) CaptureWindow(startup, e.Args[captureIndex + 1]);
            await play; startup.Complete(); Shutdown(); return;
        }
        var window = new MainWindow();
        window.CheckUpdatesRequested = async () =>
        {
            window.SetUpdateStatus("Checking GitHub for a newer release…");
            var result = await _updates.CheckAsync(OfficialRepository);
            window.SetUpdateStatus(result.Message);
            if (result.Status is UpdateStatus.Required or UpdateStatus.Available && result.Info is not null)
            {
                var gate = new UpdateRequiredWindow(_updates, result.Info) { Owner = window };
                gate.ShowDialog();
            }
        };
        if (updateCheck is not null) window.SetUpdateStatus(updateCheck.Message);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var pageIndex = Array.IndexOf(e.Args, "--page");
        if (pageIndex >= 0 && pageIndex + 1 < e.Args.Length) window.ShowPage(e.Args[pageIndex + 1]);
        var themeIndex = Array.IndexOf(e.Args, "--theme");
        if (themeIndex >= 0 && themeIndex + 1 < e.Args.Length) window.SetPreviewTheme(e.Args[themeIndex + 1]);
        window.Show();
        startup?.Complete();
        var sectionIndex = Array.IndexOf(e.Args, "--section");
        if (sectionIndex >= 0 && sectionIndex + 1 < e.Args.Length) window.ScrollSettingsTo(e.Args[sectionIndex + 1]);
        var uiTest = e.Args.Contains("--ui-test");
        var uiSuccess = true;
        if (uiTest)
        {
            var reportIndex = Array.IndexOf(e.Args, "--report");
            var reportPath = reportIndex >= 0 && reportIndex + 1 < e.Args.Length ? e.Args[reportIndex + 1] : Path.Combine(AppContext.BaseDirectory, "ui-test.json");
            try { uiSuccess = await UiSmokeRunner.RunAsync(window, reportPath); }
            catch (Exception ex) { File.WriteAllText(reportPath, ex.ToString()); uiSuccess = false; }
        }
        var shotIndex = Array.IndexOf(e.Args, "--screenshot");
        if (shotIndex >= 0 && shotIndex + 1 < e.Args.Length)
        {
            await Task.Delay(1400);
            await Dispatcher.InvokeAsync(() =>
            {
                CaptureWindow(window, e.Args[shotIndex + 1]);
            }, DispatcherPriority.ApplicationIdle);
            Shutdown(uiSuccess ? 0 : 1);
        }
        else if (uiTest) Shutdown(uiSuccess ? 0 : 1);
    }

    private static void CaptureWindow(Window window, string path)
    {
        var surface = window.Content as FrameworkElement;
        if (surface is null) return;
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updates?.Dispose();
        base.OnExit(e);
    }
}
