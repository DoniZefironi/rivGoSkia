using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using RiveSkia;
class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    // Создание окна
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window
            {
                Title = "Rive в Avalonia",
                Width = 900,
                Height = 700,
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Content = new RiveControl(@"C:\Users\ShaboldaV\Downloads\17942-33773-character-test.riv")
                {
                    Width = 800,
                    Height = 600,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
// Инициализация программы
class Program
{
    // буфер обмена
    [STAThread]
    static void Main(string[] args) =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect() // определяет какой backend использовать (win32/x11/macos)
                  .LogToTrace() // внутренние логи авалония
                  .StartWithClassicDesktopLifetime(args); // запускает мезаж луп и весь остальной код из таймер, обработчиков событый, рендер-колбэков (пока пользователь не закроет окно)
}
