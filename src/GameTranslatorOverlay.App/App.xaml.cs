using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Hotkeys;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Secrets;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace GameTranslatorOverlay.App;

internal static class SecretNames
{
    public const string DeepLApiKey = "deepl-api-key";
}

public partial class App : Application
{
    private IHost? _host;
    private AppPaths? _paths;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _paths = new AppPaths();
        _paths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(_paths.LogsDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Nieobsłużony wyjątek zadania w tle");
            args.SetObserved();
        };

        try
        {
            var paths = _paths;
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices(services => ConfigureServices(services, paths))
                .Build();
            _host.Start();

            var cache = _host.Services.GetRequiredService<SqliteTranslationCache>();
            try
            {
                cache.Initialize();
            }
            catch (CacheStorageException ex)
            {
                MessageBox.Show(ex.Message, "GameTranslatorOverlay — cache",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _host.Services.GetRequiredService<TranslationOrchestrator>().Initialize();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Błąd startu aplikacji");
            MessageBox.Show(
                "Nie udało się uruchomić aplikacji.\n\n" + ex.Message +
                "\n\nSzczegóły znajdziesz w logu: " + _paths.LogsDirectory,
                "GameTranslatorOverlay", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void ConfigureServices(IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<JsonSettingsStore>();
        services.AddSingleton(static sp => sp.GetRequiredService<JsonSettingsStore>().Load());
        services.AddSingleton<ISecretsStore, DpapiSecretsStore>();
        services.AddSingleton(sp => new SqliteTranslationCache(sp.GetRequiredService<AppPaths>().DatabasePath));
        services.AddSingleton<IGlossaryService, GlossaryService>();
        services.AddSingleton(sp => ProfileCatalog.CreateDefault(sp.GetRequiredService<AppPaths>()));
        services.AddSingleton(sp => GlossaryCatalog.CreateDefault(sp.GetRequiredService<AppPaths>()));
        services.AddSingleton<UserGlossaryStore>();
        services.AddSingleton<UsageTracker>();
        services.AddSingleton<MockTranslationProvider>();
        services.AddSingleton(static _ => new HttpClient());
        services.AddSingleton(static sp => new DeepLTranslationProvider(
            sp.GetRequiredService<HttpClient>(),
            () => sp.GetRequiredService<ISecretsStore>().Load(SecretNames.DeepLApiKey),
            logger: sp.GetRequiredService<ILogger<DeepLTranslationProvider>>()));
        services.AddSingleton<IOcrProvider, WindowsOcrProvider>();
        services.AddSingleton<TranslationOrchestrator>();
        services.AddSingleton<HotkeyManager>();
        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Nieobsłużony wyjątek interfejsu");
        MessageBox.Show(
            "Wystąpił nieoczekiwany błąd. Aplikacja spróbuje działać dalej.\n\n" +
            "Szczegóły znajdziesz w logu: " + (_paths?.LogsDirectory ?? "%LOCALAPPDATA%\\" + AppPaths.AppFolderName + "\\logs"),
            "GameTranslatorOverlay", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            _host?.Dispose();
        }
        finally
        {
            Log.CloseAndFlush();
        }
        base.OnExit(e);
    }
}
