using Microsoft.UI.Xaml;

namespace ZenInk_App;

/// <summary>
/// The application window. UI and logic live in MainPage, which also supplies
/// the title bar's drag region now that the content is extended into it.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        RootFrame.Navigate(typeof(MainPage));
    }
}
