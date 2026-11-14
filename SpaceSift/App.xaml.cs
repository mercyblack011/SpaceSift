using System.Windows;
using System.Windows.Threading;
using SpaceSift.Core;

namespace SpaceSift;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Init();
        // every window gets a title bar that matches the theme
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => ThemeManager.TintTitleBar((Window)s)));
        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceSift");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"{DateTime.Now:u} {e.Exception}\r\n\r\n");
        }
        catch { }
        MessageBox.Show(e.Exception.Message, "SpaceSift", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
