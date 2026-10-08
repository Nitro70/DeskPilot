using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using DeskPilot.Services;
using DeskPilot.ViewModels;
using DeskPilot.Views;

namespace DeskPilot;

/// <summary>
/// The chat window. Behaviour lives in <see cref="MainViewModel"/>; this class only handles
/// view concerns: scrolling, keyboard, window placement and opening other windows.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel? _vm;
    private ScrollViewer? _logScroll;
    private bool _stickToBottom = true;
    private bool _minimizedForTurn;
    private WindowState _stateBeforeTurn = WindowState.Normal;
    private bool _settingsOpen;
    private DispatcherTimer? _noteTimer;

    /// <summary>Design-time / bare construction: no view model attached.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        _vm = viewModel;
        DataContext = viewModel;
        ApplySavedPlacement(viewModel.Store.Current.Ui);

        viewModel.TurnStarted += OnTurnStarted;
        viewModel.TurnEnded += OnTurnEnded;
        viewModel.SettingsRequested += () => OpenSettings();
        viewModel.ImageRequested += OpenImage;
        viewModel.CopyRequested += CopyToClipboard;
        viewModel.FocusInputRequested += FocusInput;

        Loaded += OnLoaded;
        NativeUi.Prepare(this);
    }

    public MainViewModel? ViewModel => _vm;

    /// <summary>Set by the app when it really exits; otherwise closing goes to the tray or asks the app to exit.</summary>
    public bool AllowClose { get; set; }

    /// <summary>The user closed the window and the app should exit.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>The window was hidden to the tray instead of closing.</summary>
    public event EventHandler? HiddenToTray;

    // ---------------------------------------------------------------- lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LogList.ApplyTemplate();
        _logScroll = LogList.Template.FindName("PART_LogScroll", LogList) as ScrollViewer;
        if (_logScroll != null) _logScroll.ScrollChanged += OnLogScrollChanged;
        FocusInput();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (AllowClose || _vm == null)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_vm.Store.Current.Ui.CloseToTray)
        {
            SaveBounds();
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
            return;
        }
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Restores, shows and focuses the window.</summary>
    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = _stateBeforeTurn == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        // Bring it in front even when another app currently has the focus.
        Topmost = true;
        Topmost = false;
        FocusInput();
    }

    public void FocusInput()
    {
        if (!IsLoaded) return;
        InputBox.Focus();
        Keyboard.Focus(InputBox);
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    // ---------------------------------------------------------------- placement

    /// <summary>Applies the saved size and position when they still fit on the current screens.</summary>
    public void ApplySavedPlacement(UiSettings ui)
    {
        Width = Math.Max(MinWidth, double.IsFinite(ui.WindowWidth) && ui.WindowWidth > 0 ? ui.WindowWidth : Width);
        Height = Math.Max(MinHeight, double.IsFinite(ui.WindowHeight) && ui.WindowHeight > 0 ? ui.WindowHeight : Height);
        if (ui.WindowLeft is { } left && ui.WindowTop is { } top && IsOnScreen(left, top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    public static bool IsOnScreen(double left, double top, double width, double height)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top)) return false;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var title = new Rect(left, top, Math.Max(width, 100), 40);
        var visible = Rect.Intersect(screen, title);
        return !visible.IsEmpty && visible.Width >= 80 && visible.Height >= 20;
    }

    /// <summary>Stores the window's normal (restored) size and position in the settings.</summary>
    public void SaveBounds()
    {
        if (_vm == null) return;
        var r = WindowState == WindowState.Normal && IsVisible ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (r.IsEmpty || !double.IsFinite(r.Width) || !double.IsFinite(r.Left) || r.Width < 200 || r.Height < 150) return;

        var ui = _vm.Store.Current.Ui;
        if (Math.Abs(ui.WindowWidth - r.Width) < 1 && Math.Abs(ui.WindowHeight - r.Height) < 1 &&
            ui.WindowLeft is { } l && Math.Abs(l - r.Left) < 1 && ui.WindowTop is { } t && Math.Abs(t - r.Top) < 1) return;
        try
        {
            _vm.Store.Update(s =>
            {
                s.Ui.WindowWidth = Math.Round(r.Width);
                s.Ui.WindowHeight = Math.Round(r.Height);
                s.Ui.WindowLeft = Math.Round(r.Left);
                s.Ui.WindowTop = Math.Round(r.Top);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Window position not saved: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- turns

    private void OnTurnStarted()
    {
        _stickToBottom = true;
        _logScroll?.ScrollToEnd();
        UpdateJumpButton();

        if (_vm?.Store.Current.Ui.MinimizeWhileWorking == true && IsVisible && WindowState != WindowState.Minimized)
        {
            _stateBeforeTurn = WindowState;
            _minimizedForTurn = true;
            WindowState = WindowState.Minimized;
        }
    }

    private void OnTurnEnded()
    {
        if (!_minimizedForTurn) return;
        _minimizedForTurn = false;
        // Only undo our own minimize: if the user restored or hid the window meanwhile, leave it alone.
        if (IsVisible && WindowState == WindowState.Minimized)
        {
            WindowState = _stateBeforeTurn;
            Activate();
            FocusInput();
        }
    }

    // ---------------------------------------------------------------- dialogs

    /// <summary>Opens the settings dialog (modal). Returns true when the user saved.</summary>
    public bool OpenSettings()
    {
        if (_vm == null || _settingsOpen) return false;
        _settingsOpen = true;
        try
        {
            var dialog = new SettingsWindow(_vm.Store, _vm.Session, _vm.Catalog, _vm.Detector);
            if (IsVisible) dialog.Owner = this;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            NativeUi.Prepare(dialog);
            bool saved = dialog.ShowDialog() == true;
            if (saved) _vm.RefreshFromSettings();
            return saved;
        }
        catch (Exception ex)
        {
            Log.Error("Settings window failed", ex);
            _vm.Conversation.AddStatus($"The settings window could not open: {ex.Message}", Core.Abstractions.StatusLevel.Error);
            return false;
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    private void OpenImage(ToolStepItem step)
    {
        var bytes = step.ImageBytes;
        if (bytes == null) return;
        var image = ToolStepItem.DecodeImage(bytes, 0);
        if (image == null)
        {
            ShowNote("That screenshot could not be decoded", isError: true);
            return;
        }
        var viewer = new ImageViewerWindow(image, $"{step.Summary}  ·  {step.TimeText}");
        if (IsVisible) viewer.Owner = this;
        else viewer.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        NativeUi.Prepare(viewer);
        viewer.Show();
    }

    private void CopyToClipboard(string text)
    {
        // The clipboard can be locked by another process for a moment; retry briefly.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                ShowNote("Conversation copied to the clipboard");
                return;
            }
            catch (COMException) when (attempt < 2)
            {
                Thread.Sleep(60);
            }
            catch (COMException ex)
            {
                Log.Warn($"Copy log failed: {ex.Message}");
            }
        }
        ShowNote("The clipboard is busy, try again", isError: true);
    }

    private void ShowNote(string text, bool isError = false)
    {
        TransientNote.Text = text;
        TransientNote.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "WarningBrush" : "SuccessBrush");
        TransientNote.Visibility = Visibility.Visible;
        _noteTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _noteTimer.Stop();
        _noteTimer.Tick -= HideNote;
        _noteTimer.Tick += HideNote;
        _noteTimer.Start();
    }

    private void HideNote(object? sender, EventArgs e)
    {
        _noteTimer?.Stop();
        TransientNote.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- input

    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return; // Shift+Enter inserts a new line
        e.Handled = true;
        if (_vm == null || _vm.IsBusy) return;
        if (_vm.SendOrStopCommand.CanExecute(null)) _vm.SendOrStopCommand.Execute(null);
    }

    private void OnModelBoxKeyDown(object sender, KeyEventArgs e)
    {
        // With the list open, Enter picks the highlighted model; let the combo box handle that.
        if (e.Key == Key.Enter && !ModelBox.IsDropDownOpen)
        {
            ModelBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            FocusInput();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- scrolling

    private void OnLogPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_logScroll == null) return;
        // Reply boxes would swallow the wheel; scroll the whole log instead.
        if (e.Delta > 0) _stickToBottom = false;
        _logScroll.ScrollToVerticalOffset(_logScroll.VerticalOffset - e.Delta * 0.6);
        e.Handled = true;
        UpdateJumpButton();
    }

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = _logScroll;
        if (sv == null) return;
        if (e.VerticalChange < 0 && e.ExtentHeightChange >= 0) _stickToBottom = false;
        if (sv.VerticalOffset >= sv.ScrollableHeight - 8) _stickToBottom = true;
        if (_stickToBottom && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)) sv.ScrollToEnd();
        UpdateJumpButton();
    }

    private void UpdateJumpButton()
    {
        bool show = !_stickToBottom && _logScroll is { ScrollableHeight: > 0 };
        JumpToLatest.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnJumpToLatest(object sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        _logScroll?.ScrollToEnd();
        UpdateJumpButton();
        FocusInput();
    }
}
