using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

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
            FitButton.IsEnabled = true;
            UpdateViewStatus();
        }
        catch (Exception ex)
        {
            DocumentNameText.Text = $"Error al abrir: {ex.Message}";
            FitButton.IsEnabled = false;
        }
        finally
        {
            OpenButton.IsEnabled = true;
        }
    }

    private void OnFitClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Viewer.FitToWidth();

    private void UpdateViewStatus()
    {
        if (Viewer.PageCount == 0)
        {
            ViewStatusText.Text = string.Empty;
            return;
        }

        ViewStatusText.Text = $"Hoja {Viewer.CurrentPageNumber} de {Viewer.PageCount}  ·  {Viewer.ZoomPercent:0}%";
    }
}
