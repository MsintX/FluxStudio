using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace FluxStudio;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // WinUI 3 / Windows App SDK desktop apps handle file activation through
        // AppInstance, not by overriding the UWP Application.OnFileActivated method.
        // Read activation arguments early because packaged activation data may only
        // be available the first time this API is called.
        AppActivationArguments? activation = AppInstance.GetCurrent().GetActivatedEventArgs();

        MainWindow window = EnsureMainWindow();
        window.Activate();

        if (activation?.Kind == ExtendedActivationKind.File &&
            activation.Data is IFileActivatedEventArgs fileActivation)
        {
            StorageFile? file = fileActivation.Files.OfType<StorageFile>().FirstOrDefault();
            if (file is not null)
            {
                _ = window.OpenActivatedFileAsync(file);
            }
        }
    }

    private MainWindow EnsureMainWindow()
    {
        if (_window is MainWindow existing)
            return existing;

        var main = new MainWindow();
        _window = main;
        return main;
    }
}
