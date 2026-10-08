using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskPilot.Views;

/// <summary>Shows one screenshot from the log: fit to window, click or Space for 100%.</summary>
public partial class ImageViewerWindow : Window
{
    private bool _actualSize;

    public ImageViewerWindow(BitmapSource image, string? caption = null)
    {
        InitializeComponent();
        Picture.Source = image;
        CaptionText.Text = string.IsNullOrWhiteSpace(caption) ? "Screenshot" : caption;
        SizeText.Text = string.Create(CultureInfo.InvariantCulture, $"{image.PixelWidth} × {image.PixelHeight} px, as the model saw it");
        PreviewKeyDown += OnPreviewKeyDown;
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

    private void OnPictureClicked(object sender, MouseButtonEventArgs e) => ToggleZoom();

    private void OnToggleZoom(object sender, RoutedEventArgs e) => ToggleZoom();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.FocusedElement is not Button)
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
