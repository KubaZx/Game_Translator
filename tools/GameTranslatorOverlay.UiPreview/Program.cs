using System.IO;
using System.Windows;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? output = null;
        string? icon = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
            {
                output = args[++i];
            }
            else if (args[i] == "--write-icon" && i + 1 < args.Length)
            {
                icon = args[++i];
            }
            else if (args[i] is "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }
            else
            {
                Console.Error.WriteLine($"Nieznany argument: {args[i]}");
                PrintUsage();
                return 2;
            }
        }
        if (icon is not null)
        {
            var iconPath = Path.GetFullPath(icon);
            File.WriteAllBytes(iconPath, GameTranslatorOverlay.App.Ui.AppLogo.CreateIco(16, 20, 24, 32, 40, 48, 64, 256));
            Console.WriteLine($"Zapisano ikonę: {iconPath}");
            if (output is null) return 0;
        }
        if (string.IsNullOrWhiteSpace(output))
        {
            PrintUsage();
            return 2;
        }

        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/GameTranslatorOverlay;component/Ui/Theme.xaml", UriKind.Absolute),
        });

        var exitCode = 0;
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(4));
            Console.Error.WriteLine("Przekroczony limit czasu podglądu.");
            Environment.Exit(4);
        }) { IsBackground = true };
        watchdog.Start();

        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                exitCode = await PreviewSession.RunAsync(output);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                exitCode = 3;
            }
            finally
            {
                app.Dispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private static void PrintUsage() =>
        Console.WriteLine(
            "UiPreview --out FOLDER [--write-icon PLIK.ico]\n" +
            "Tworzy prawdziwe okno konfiguracji (MainWindow) poza ekranem, na tymczasowym folderze danych,\n" +
            "z dostawcą Mock, w trybie prywatnym i bez sieci. Przełącza sekcje i zapisuje PNG w skali 1.0 i 1.5,\n" +
            "przy rozmiarze domyślnym, minimalnym i dużym, a także panel wyniku, słownik, menu zasobnika i ikony.\n" +
            "Raport z ucinanymi napisami: FOLDER\\raport.txt. Nie czyta ustawień ani kluczy użytkownika.\n" +
            "--write-icon zapisuje znak aplikacji (AppLogo) jako plik .ico; samo --write-icon kończy działanie.");
}
