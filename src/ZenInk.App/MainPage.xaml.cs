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
            SetDocumentControlsEnabled(true);
            UpdateViewStatus();
        }
        catch (Exception ex)
        {
            DocumentNameText.Text = $"Error al abrir: {ex.Message}";
            SetDocumentControlsEnabled(false);
        }
        finally
        {
            OpenButton.IsEnabled = true;
        }
    }

    private void SetDocumentControlsEnabled(bool enabled)
    {
        FitButton.IsEnabled = enabled;
        PanToolButton.IsEnabled = enabled;
        TextToolButton.IsEnabled = enabled;
        ContinuousModeButton.IsEnabled = enabled;
        SingleModeButton.IsEnabled = enabled;
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

    private void OnContinuousModeClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        SelectLayoutMode(ViewerLayoutMode.Continuous);

    private void OnSingleModeClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        SelectLayoutMode(ViewerLayoutMode.SinglePage);

    private void SelectLayoutMode(ViewerLayoutMode mode)
    {
        Viewer.LayoutMode = mode;
        ContinuousModeButton.IsChecked = mode == ViewerLayoutMode.Continuous;
        SingleModeButton.IsChecked = mode == ViewerLayoutMode.SinglePage;
        UpdateViewStatus();
    }

    private void OnPreviousPageClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Viewer.PreviousPage();

    private void OnNextPageClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Viewer.NextPage();

    private void UpdateViewStatus()
    {
        PreviousPageButton.IsEnabled = Viewer.CanGoPrevious;
        NextPageButton.IsEnabled = Viewer.CanGoNext;

        if (Viewer.PageCount == 0)
        {
            ViewStatusText.Text = string.Empty;
            return;
        }

        string selection = Viewer.HasSelection ? "  ·  texto seleccionado" : string.Empty;
        ViewStatusText.Text = $"Hoja {Viewer.CurrentPageNumber} de {Viewer.PageCount}  ·  {Viewer.ZoomPercent:0}%{selection}";
    }
}
