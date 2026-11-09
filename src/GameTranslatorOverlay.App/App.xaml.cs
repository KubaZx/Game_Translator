using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Hotkeys;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.App.Ui;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Diagnostics;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Secrets;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace GameTranslatorOverlay.App;

public partial class App : Application
{
    private IHost? _host;
    private AppPaths? _paths;
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Nieobsłużony wyjątek zadania w tle");
            args.SetObserved();
        };

        // Druga kopia aplikacji dublowałaby skróty globalne, nakładkę i dostęp do bazy —
        // pilnujemy pojedynczej instancji.
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\GameTranslatorOverlay.SingleInstance", out var isFirstInstance);
            if (!isFirstInstance)
            {
                MessageBox.Show(
                    "GameTranslatorOverlay już działa.\n\nSprawdź ikonę z dymkiem „PL” w zasobniku systemowym (przy zegarze) — " +
                    "kliknij ją, aby przywrócić okno.",
                    "GameTranslatorOverlay", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Nie udało się sprawdzić, czy program już działa.\n\n" + ex.Message +
                "\n\nUruchom program ponownie. Jeśli błąd wraca, uruchom komputer od nowa.",
                "GameTranslatorOverlay", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            _paths = new AppPaths();
            _paths.EnsureCreated();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .WriteTo.File(
                    Path.Combine(_paths.LogsDirectory, "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7)
                .CreateLogger();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show(
                "Nie udało się przygotować folderu danych programu.\n\n" + ex.Message +
                "\n\nSprawdź, czy dysk ma wolne miejsce i czy masz prawo zapisu do folderu %LOCALAPPDATA%\\" + AppPaths.AppFolderName + ".",
                "GameTranslatorOverlay", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        LogStartupReport();

        try
        {
            var paths = _paths;
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices(services => ConfigureServices(services, paths))
                .Build();
            _host.Start();

            var settingsStore = _host.Services.GetRequiredService<JsonSettingsStore>();
            _ = _host.Services.GetRequiredService<AppSettings>();
            ShowSettingsNotice(settingsStore);

            var cache = _host.Services.GetRequiredService<SqliteTranslationCache>();
            try
            {
                cache.Initialize();
                if (cache.RecoveryNotice is { } recovery)
                {
                    MessageBox.Show(recovery, "GameTranslatorOverlay — cache",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
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

    private static void LogStartupReport()
    {
        try
        {
            Log.Information("{StartupReport}", StartupDiagnostics.Describe());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Nie udało się zebrać danych startowych");
        }
    }

    private void ShowSettingsNotice(JsonSettingsStore settingsStore)
    {
        if (settingsStore.LastLoad is not { NeedsAttention: true } load
            || SettingsLoadMessages.Describe(load) is not { } notice)
        {
            return;
        }

        var window = new SettingsNoticeWindow(notice);
        MainWindow = null;
        window.ShowDialog();
        if (window.Choice == SettingsNoticeChoice.StartFromDefaults) settingsStore.AllowSaving();
    }

    private static void ConfigureServices(IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<JsonSettingsStore>();
        services.AddSingleton(static sp => sp.GetRequiredService<JsonSettingsStore>().Load());
        services.AddSingleton<ISecretsStore, DpapiSecretsStore>();
        services.AddSingleton(sp => new SqliteTranslationCache(
            sp.GetRequiredService<AppPaths>().DatabasePath, sp.GetRequiredService<ILogger<SqliteTranslationCache>>()));
        services.AddSingleton<IGlossaryService, GlossaryService>();
        services.AddSingleton(sp => ProfileCatalog.CreateDefault(sp.GetRequiredService<AppPaths>()));
        services.AddSingleton(sp => GlossaryCatalog.CreateDefault(sp.GetRequiredService<AppPaths>()));
        services.AddSingleton(sp => CorpusCatalog.CreateDefault(
            sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<ILogger<CorpusCatalog>>()));
        services.AddSingleton<UserGlossaryStore>();
        services.AddSingleton<UsageTracker>();
        services.AddSingleton<MockTranslationProvider>();
        services.AddSingleton(static _ => ProviderHttpClientFactory.Create());
        services.AddSingleton(static sp => new DeepLTranslationProvider(
            sp.GetRequiredService<HttpClient>(),
            ApiKey(sp, TranslationProviderCatalog.DeepL),
            logger: sp.GetRequiredService<ILogger<DeepLTranslationProvider>>()));
        services.AddSingleton(static sp => new AzureTranslatorProvider(
            sp.GetRequiredService<HttpClient>(),
            ApiKey(sp, TranslationProviderCatalog.Azure),
            () => sp.GetRequiredService<AppSettings>().AzureRegion,
            logger: sp.GetRequiredService<ILogger<AzureTranslatorProvider>>()));
        services.AddSingleton(static sp => new GoogleTranslateProvider(
            sp.GetRequiredService<HttpClient>(),
            ApiKey(sp, TranslationProviderCatalog.Google),
            logger: sp.GetRequiredService<ILogger<GoogleTranslateProvider>>()));
        services.AddSingleton(static sp => new OpenAiCompatibleTranslationProvider(
            sp.GetRequiredService<HttpClient>(),
            ApiKey(sp, TranslationProviderCatalog.Llm),
            () => sp.GetRequiredService<AppSettings>().LlmKeyHost,
            () => sp.GetRequiredService<AppSettings>().LlmEndpoint,
            () => sp.GetRequiredService<AppSettings>().LlmModel,
            logger: sp.GetRequiredService<ILogger<OpenAiCompatibleTranslationProvider>>(),
            serverOptionsAccessor: () => sp.GetRequiredService<AppSettings>().LlmServerOptions));
        // Osobny HttpClient: SDK Anthropic konfiguruje klienta po swojemu — nie dzielimy
        // go z pozostałymi dostawcami.
        services.AddSingleton(static sp => new ClaudeTranslationProvider(
            ProviderHttpClientFactory.Create(),
            ApiKey(sp, TranslationProviderCatalog.Claude),
            () => sp.GetRequiredService<AppSettings>().ClaudeModel,
            logger: sp.GetRequiredService<ILogger<ClaudeTranslationProvider>>()));
        services.AddSingleton<ITranslationProvider>(static sp => sp.GetRequiredService<AzureTranslatorProvider>());
        services.AddSingleton<ITranslationProvider>(static sp => sp.GetRequiredService<GoogleTranslateProvider>());
        services.AddSingleton<ITranslationProvider>(static sp => sp.GetRequiredService<OpenAiCompatibleTranslationProvider>());
        services.AddSingleton<ITranslationProvider>(static sp => sp.GetRequiredService<ClaudeTranslationProvider>());
        services.AddSingleton<IOcrProvider, WindowsOcrProvider>();
        services.AddSingleton<TranslationOrchestrator>();
        services.AddSingleton<HotkeyManager>();
        services.AddSingleton<MainWindow>();
    }

    /// <summary>Klucz czytany z DPAPI przy każdym zapytaniu — zmiana klucza w UI działa od razu.</summary>
    private static Func<string?> ApiKey(IServiceProvider sp, TranslationProviderInfo provider) =>
        () => sp.GetRequiredService<ISecretsStore>().Load(provider.SecretName!);

    private readonly ErrorDialogThrottle _errorDialogThrottle = new();

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Nieobsłużony wyjątek interfejsu");
        e.Handled = true;

        var window = MainWindow as global::GameTranslatorOverlay.App.MainWindow;
        var liveRunning = window?.IsLiveRunning == true;
        if (!_errorDialogThrottle.TryAcquire(DateTime.UtcNow, liveRunning))
        {
            window?.ReportUnexpectedError();
            return;
        }

        MessageBox.Show(
            "Wystąpił nieoczekiwany błąd. Aplikacja spróbuje działać dalej.\n\n" +
            "Szczegóły znajdziesz w logu: " + (_paths?.LogsDirectory ?? "%LOCALAPPDATA%\\" + AppPaths.AppFolderName + "\\logs"),
            "GameTranslatorOverlay", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) Log.Fatal(exception, "Nieobsłużony wyjątek — program się zamyka");
        else Log.Fatal("Nieobsłużony wyjątek nieznanego typu — program się zamyka");
        if (e.IsTerminating) Log.CloseAndFlush();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            try
            {
                // Liczniki użycia cache są zapisywane zbiorczo — domykamy ostatnią partię.
                _host?.Services.GetService<SqliteTranslationCache>()?.FlushUsageStatistics();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Nie udało się zapisać liczników użycia cache przy zamknięciu");
            }
            _host?.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            _host?.Dispose();
        }
        finally
        {
            Log.Information("Zamknięcie programu");
            Log.CloseAndFlush();
            _singleInstanceMutex?.Dispose();
        }
        base.OnExit(e);
    }
}
