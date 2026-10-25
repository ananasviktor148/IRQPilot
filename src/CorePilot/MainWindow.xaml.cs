using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CorePilot.Interop;
using CorePilot.ViewModels;

namespace CorePilot;

public partial class MainWindow : Window, IDialogService
{
    private TaskCompletionSource<object?>? _dialogTcs;

    public MainWindow()
    {
        InitializeComponent();
        Dialogs = this;
        StateChanged += (_, _) =>
        {
            // With custom chrome, a maximized window hangs over the invisible
            // resize border; pad the content back on screen.
            RootGrid.Margin = WindowState == WindowState.Maximized
                ? new Thickness(8)
                : new Thickness(0);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        if (NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_CURRENT, ref enabled, sizeof(int)) != 0)
        {
            NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref enabled, sizeof(int));
        }
    }

    internal IDialogService Dialogs { get; }

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            var headerPosition = e.GetPosition(HeaderBar);
            var screenPosition = HeaderBar.PointToScreen(headerPosition);
            if (PresentationSource.FromVisual(HeaderBar)?.CompositionTarget is { } target)
                screenPosition = target.TransformFromDevice.Transform(screenPosition);

            var widthRatio = HeaderBar.ActualWidth > 0
                ? headerPosition.X / HeaderBar.ActualWidth
                : 0.5;
            WindowState = WindowState.Normal;
            Left = screenPosition.X - RestoreBounds.Width * widthRatio;
            Top = screenPosition.Y - headerPosition.Y;
        }

        DragMove();
        e.Handled = true;
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    public Task<bool> ConfirmAsync(string title, string message) => ShowDialog<bool>(title, message,
        ("Cancel", null), ("Confirm", true));

    public Task AlertAsync(string title, string message) => ShowDialog<object?>(title, message,
        ("OK", null));

    public Task<ApplyChoice> ConfirmApplyAsync(string deviceName, string changeSummary, bool canRestart)
    {
        var message = $"Write new interrupt affinity settings to:\n\n{deviceName}\n\n{changeSummary}";
        return canRestart
            ? ShowDialog<ApplyChoice>(title: "Apply affinity", message,
                ("Cancel", ApplyChoice.Cancel), ("Write only", ApplyChoice.WriteOnly),
                ("Write + restart device", ApplyChoice.WriteAndRestart))
            : ShowDialog<ApplyChoice>(title: "Apply affinity",
                message + "\n\n(Boot-critical device: restart is blocked, change applies on next reboot.)",
                ("Cancel", ApplyChoice.Cancel), ("Write only", ApplyChoice.WriteOnly));
    }

    private Task<T> ShowDialog<T>(string title, string message, params (string Label, object? Result)[] buttons)
    {
        if (_dialogTcs is not null)
        {
            // Never stack dialogs: replace the pending one.
            _dialogTcs.TrySetResult(null);
        }

        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogButtons.Children.Clear();

        var tcs = new TaskCompletionSource<object?>();
        _dialogTcs = tcs;

        foreach (var (label, result) in buttons)
        {
            var button = new Button
            {
                Content = label.ToUpperInvariant(),
                Margin = new Thickness(10, 0, 0, 0),
                MinWidth = 90,
            };
            if (result is not null && !Equals(result, buttons[0].Result))
                button.Style = (Style)FindResource("AccentButton");
            var captured = result;
            button.Click += (_, _) =>
            {
                DialogOverlay.Visibility = Visibility.Collapsed;
                _dialogTcs = null;
                tcs.TrySetResult(captured);
            };
            DialogButtons.Children.Add(button);
        }

        DialogOverlay.Visibility = Visibility.Visible;
        return tcs.Task.ContinueWith(t =>
        {
            var value = t.IsCompletedSuccessfully ? t.Result : null;
            return value is T typed ? typed : default!;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
