using System.ComponentModel;
using System.Windows;
using AuroraTranslate.Services;

namespace AuroraTranslate;

public partial class UpdateRequiredWindow : Window
{
    private readonly UpdateService _service;
    private readonly UpdateInfo _info;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _installing;
    public bool UpdateStartedSuccess { get; private set; }

    public UpdateRequiredWindow(UpdateService service, UpdateInfo info)
    {
        InitializeComponent();
        _service = service;
        _info = info;
        CurrentVersionText.Text = UpdateService.CurrentVersion;
        NewVersionText.Text = info.Version;
        RequirementText.Text = "Install this required update to continue. Aurora will restart automatically and keep your settings, themes, and voices.";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes) ? "This release keeps Aurora Translate current." : info.ReleaseNotes;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;
        InstallButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        try
        {
            string path = await _service.DownloadAsync(_info, new Progress<string>(message => StatusText.Text = message), _lifetime.Token);
            StatusText.Text = "Starting the installer. Aurora will close and reopen in a moment…";
            await _service.ApplyAndRestartAsync(path, _lifetime.Token);
            UpdateStartedSuccess = true;
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            InstallButton.Content = "Retry update";
        }
        finally
        {
            _installing = false;
            InstallButton.IsEnabled = true;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _lifetime.Cancel();
        // Closing a mandatory-update gate exits the app; it cannot open the older main window.
        if (!UpdateStartedSuccess) Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
    }
}
