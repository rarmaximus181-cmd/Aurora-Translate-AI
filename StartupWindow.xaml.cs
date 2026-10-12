using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AuroraTranslate;

public partial class StartupWindow : Window
{
    private readonly CancellationTokenSource _closed = new();
    private bool _normalCompletion;
    public bool UserClosed { get; private set; }

    public StartupWindow() => InitializeComponent();

    public async Task PlayAsync(bool animate, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        if (!animate)
        {
            RevealPanel.Opacity = 1;
            RevealMotion.Y = 0;
            SweepMotion.X = 46;
            await Task.Delay(1800, linked.Token);
            return;
        }
        RevealPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(560)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        RevealMotion.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        OuterRingRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(14)) { RepeatBehavior = RepeatBehavior.Forever });
        InnerRingRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, -360, TimeSpan.FromSeconds(19)) { RepeatBehavior = RepeatBehavior.Forever });
        MarkGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 0.8, TimeSpan.FromSeconds(1.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        SweepMotion.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-45, 140, TimeSpan.FromMilliseconds(1400)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        await Task.Delay(1800, linked.Token);
    }

    public void UpdateStatus(string message)
    {
        if (UserClosed || _normalCompletion) return;
        if (Dispatcher.CheckAccess()) StartupStatus.Text = message;
        else Dispatcher.Invoke(() => StartupStatus.Text = message);
    }

    public void Complete()
    {
        _normalCompletion = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.Source is not System.Windows.Controls.Button)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        UserClosed = !_normalCompletion;
        _closed.Cancel();
    }
}
