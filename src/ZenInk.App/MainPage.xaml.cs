using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using ZenInk_App.Rendering;

namespace ZenInk_App;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
        Viewer.ViewChanged += (_, _) => UpdateViewStatus();
    }

    private async void OnOpenClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(App.Current.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".pdf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;

        OpenButton.IsEnabled = false;
        try
        {
            await Viewer.OpenAsync(file.Path);
            DocumentNameText.Text = file.Name;
            SetToolButtonsEnabled(true);
            UpdateViewStatus();
        }
        catch (Exception ex)
        {
            DocumentNameText.Text = $"Error al abrir: {ex.Message}";
            SetToolButtonsEnabled(false);
        }
        finally
        {
            OpenButton.IsEnabled = true;
        }
    }

    private void SetToolButtonsEnabled(bool enabled)
    {
        FitButton.IsEnabled = enabled;
        PanToolButton.IsEnabled = enabled;
        TextToolButton.IsEnabled = enabled;
    }

    private void OnFitClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Viewer.FitToWidth();

    private void OnPanToolClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => SelectTool(ViewerTool.Pan);

    private void OnTextToolClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => SelectTool(ViewerTool.SelectText);

    private void SelectTool(ViewerTool tool)
    {
        Viewer.Tool = tool;
        PanToolButton.IsChecked = tool == ViewerTool.Pan;
        TextToolButton.IsChecked = tool == ViewerTool.SelectText;
    }

    private void UpdateViewStatus()
    {
        if (Viewer.PageCount == 0)
        {
            ViewStatusText.Text = string.Empty;
            return;
        }

        string selection = Viewer.HasSelection ? "  ·  texto seleccionado" : string.Empty;
        ViewStatusText.Text = $"Hoja {Viewer.CurrentPageNumber} de {Viewer.PageCount}  ·  {Viewer.ZoomPercent:0}%{selection}";
    }
}
