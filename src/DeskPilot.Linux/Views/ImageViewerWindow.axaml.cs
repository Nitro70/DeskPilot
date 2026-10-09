using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DeskPilot.Linux.Views;

/// <summary>Shows one screenshot from the log: fit to window, click or Space for 100%.</summary>
public partial class ImageViewerWindow : Window
{
    private bool _actualSize;

    /// <summary>Designer only.</summary>
    public ImageViewerWindow()
    {
        InitializeComponent();
    }

    public ImageViewerWindow(Bitmap image, string? caption = null) : this()
    {
        Picture.Source = image;
        CaptionText.Text = string.IsNullOrWhiteSpace(caption) ? "Screenshot" : caption;
        SizeText.Text = string.Create(CultureInfo.InvariantCulture, $"{image.PixelSize.Width} × {image.PixelSize.Height} px, as the model saw it");
        Picture.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) ToggleZoom();
        };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) => Picture.Source = null;
    }

    public bool IsActualSize => _actualSize;

    public void ToggleZoom()
    {
        _actualSize = !_actualSize;
        if (_actualSize)
        {
            Picture.Stretch = Stretch.None;
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            ZoomButton.Content = "Fit to window";
        }
        else
        {
            Picture.Stretch = Stretch.Uniform;
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ZoomButton.Content = "Actual size";
        }
    }

    private void OnToggleZoom(object? sender, RoutedEventArgs e) => ToggleZoom();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.Source is not Button)
        {
            ToggleZoom();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
