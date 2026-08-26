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
        }
        catch (Exception ex)
        {
            DocumentNameText.Text = $"Error al abrir: {ex.Message}";
        }
        finally
        {
            OpenButton.IsEnabled = true;
        }
    }
}
