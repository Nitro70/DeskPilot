using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using DeskPilot.Linux.ViewModels;
using DeskPilot.Linux.Views;

namespace DeskPilot.Linux;

/// <summary>
/// The chat window. Behaviour lives in <see cref="MainViewModel"/>; this class only handles view concerns:
/// scrolling, keyboard, window placement and opening other windows.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel? _vm;
    private bool _stickToBottom = true;
    private bool _minimizedForTurn;
    private WindowState _stateBeforeTurn = WindowState.Normal;
    private bool _settingsOpen;
    private DispatcherTimer? _noteTimer;
    private PixelPoint? _normalPosition;
    private Size? _normalSize;

    /// <summary>Designer / bare construction: no view model attached.</summary>
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
        viewModel.SettingsRequested += () => _ = OpenSettingsAsync();
        viewModel.ImageRequested += OpenImage;
        viewModel.CopyRequested += text => _ = CopyToClipboardAsync(text);
        viewModel.FocusInputRequested += FocusInput;
        viewModel.Items.CollectionChanged += (_, _) => ScheduleScrollToEnd();

        // Tunnel: the text box would otherwise turn Enter into a new line before we see it.
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        ModelBox.AddHandler(KeyDownEvent, OnModelBoxKeyDown, RoutingStrategies.Tunnel);
        LogScroll.ScrollChanged += OnLogScrollChanged;
        PositionChanged += (_, _) => RememberNormalBounds();
        Resized += (_, _) => RememberNormalBounds();
        Opened += (_, _) =>
        {
            RememberNormalBounds();
            FocusInput();
        };
    }

    public MainViewModel? ViewModel => _vm;

    /// <summary>Set by the app when it really exits; otherwise closing goes to the tray or asks the app to exit.</summary>
    public bool AllowClose { get; set; }

    /// <summary>True while the settings dialog is open.</summary>
    public bool IsSettingsOpen => _settingsOpen;

    /// <summary>The user closed the window and the app should exit.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>The window was hidden to the tray instead of closing.</summary>
    public event EventHandler? HiddenToTray;

    // ---------------------------------------------------------------- lifecycle

    protected override void OnClosing(WindowClosingEventArgs e)
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
        if (!IsVisible) return;
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
    }

    // ---------------------------------------------------------------- placement

    /// <summary>Applies the saved size and position when they still fit on the current screens.</summary>
    public void ApplySavedPlacement(UiSettings ui)
    {
        Width = Math.Max(MinWidth, double.IsFinite(ui.WindowWidth) && ui.WindowWidth > 0 ? ui.WindowWidth : Width);
        Height = Math.Max(MinHeight, double.IsFinite(ui.WindowHeight) && ui.WindowHeight > 0 ? ui.WindowHeight : Height);
        if (ui.WindowLeft is { } left && ui.WindowTop is { } top && IsOnScreen(ScreenAreas(), left, top, Width))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(top));
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private IReadOnlyList<PixelRect> ScreenAreas()
    {
        try
        {
            return Screens?.All.Select(s => s.Bounds).ToList() ?? (IReadOnlyList<PixelRect>)Array.Empty<PixelRect>();
        }
        catch (Exception)
        {
            return Array.Empty<PixelRect>();
        }
    }

    /// <summary>True when at least a grabbable part of the title bar area would be on one of the screens (physical pixels).</summary>
    public static bool IsOnScreen(IReadOnlyList<PixelRect> screens, double left, double top, double width)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || screens.Count == 0) return false;
        var title = new PixelRect((int)Math.Round(left), (int)Math.Round(top), (int)Math.Max(width, 100), 40);
        foreach (var screen in screens)
        {
            var visible = screen.Intersect(title);
            if (visible.Width >= 80 && visible.Height >= 20) return true;
        }
        return false;
    }

    private void RememberNormalBounds()
    {
        if (WindowState != WindowState.Normal || !IsVisible) return;
        _normalPosition = Position;
        _normalSize = ClientSize;
    }

    /// <summary>Stores the window's normal (restored) size and position in the settings.</summary>
    public void SaveBounds()
    {
        if (_vm == null) return;
        RememberNormalBounds();
        if (_normalPosition is not { } pos || _normalSize is not { } size) return;
        if (!double.IsFinite(size.Width) || size.Width < 200 || size.Height < 150) return;

        var ui = _vm.Store.Current.Ui;
        if (Math.Abs(ui.WindowWidth - size.Width) < 1 && Math.Abs(ui.WindowHeight - size.Height) < 1 &&
            ui.WindowLeft is { } l && Math.Abs(l - pos.X) < 1 && ui.WindowTop is { } t && Math.Abs(t - pos.Y) < 1) return;
        try
        {
            _vm.Store.Update(s =>
            {
                s.Ui.WindowWidth = Math.Round(size.Width);
                s.Ui.WindowHeight = Math.Round(size.Height);
                s.Ui.WindowLeft = pos.X;
                s.Ui.WindowTop = pos.Y;
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
        LogScroll.ScrollToEnd();
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
    public async Task<bool> OpenSettingsAsync()
    {
        if (_vm == null || _settingsOpen) return false;
        _settingsOpen = true;
        try
        {
            if (!IsVisible) ShowAndActivate();
            var dialog = new SettingsWindow(_vm.Store, _vm.Session, _vm.Catalog, _vm.Detector);
            bool saved = await dialog.ShowDialog<bool>(this);
            if (saved) _vm.RefreshFromSettings();
            return saved;
        }
        catch (Exception ex)
        {
            Log.Error("Settings window failed", ex);
            _vm.Conversation.AddStatus($"The settings window could not open: {ex.Message}", StatusLevel.Error);
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
        if (IsVisible) viewer.Show(this);
        else viewer.Show();
    }

    private async Task CopyToClipboardAsync(string text)
    {
        try
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                ShowNote("No clipboard is available", isError: true);
                return;
            }
            await clipboard.SetTextAsync(text);
            ShowNote("Conversation copied to the clipboard");
        }
        catch (Exception ex)
        {
            Log.Warn($"Copy log failed: {ex.Message}");
            ShowNote("The clipboard is busy, try again", isError: true);
        }
    }

    /// <summary>A short message in the status bar that fades after a moment.</summary>
    public void ShowNote(string text, bool isError = false)
    {
        TransientNote.Text = text;
        TransientNote.Classes.Set("warn", isError);
        TransientNote.IsVisible = true;
        _noteTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _noteTimer.Stop();
        _noteTimer.Tick -= HideNote;
        _noteTimer.Tick += HideNote;
        _noteTimer.Start();
    }

    private void HideNote(object? sender, EventArgs e)
    {
        _noteTimer?.Stop();
        TransientNote.IsVisible = false;
    }

    // ---------------------------------------------------------------- input

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0) return; // Shift+Enter inserts a new line
        e.Handled = true;
        if (_vm == null || _vm.IsBusy) return;
        if (_vm.SendOrStopCommand.CanExecute(null)) _vm.SendOrStopCommand.Execute(null);
    }

    private void OnModelBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // With the list open, Enter picks the highlighted model; let the combo box handle that.
        if (e.Key is (Key.Enter or Key.Return) && !ModelBox.IsDropDownOpen)
        {
            if (_vm != null && ModelBox.Text is { } typed) _vm.ModelText = typed;
            FocusInput();
            e.Handled = true;
        }
    }

    private void OnThumbnailClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ToolStepItem step } && _vm?.OpenImageCommand.CanExecute(step) == true)
            _vm.OpenImageCommand.Execute(step);
    }

    private void OnExampleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: string text } && _vm?.UseExampleCommand.CanExecute(text) == true)
            _vm.UseExampleCommand.Execute(text);
    }

    // ---------------------------------------------------------------- scrolling

    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var sv = LogScroll;
        double scrollable = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        bool atBottom = sv.Offset.Y >= scrollable - 8;
        if (e.OffsetDelta.Y < 0 && e.ExtentDelta.Y == 0 && !atBottom) _stickToBottom = false;
        if (atBottom) _stickToBottom = true;
        if (_stickToBottom && !atBottom && (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)) ScheduleScrollToEnd();
        UpdateJumpButton();
    }

    private void ScheduleScrollToEnd()
    {
        if (!_stickToBottom) return;
        // After layout, so the new item's height is part of the extent.
        Dispatcher.UIThread.Post(() =>
        {
            if (_stickToBottom) LogScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void UpdateJumpButton()
    {
        var sv = LogScroll;
        bool scrollable = sv.Extent.Height - sv.Viewport.Height > 1;
        JumpToLatest.IsVisible = !_stickToBottom && scrollable;
    }

    private void OnJumpToLatest(object? sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        LogScroll.ScrollToEnd();
        UpdateJumpButton();
        FocusInput();
    }
}
