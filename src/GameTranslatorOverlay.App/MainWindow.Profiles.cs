using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Core.Windows;
using GameTranslatorOverlay.Infrastructure.Content;
using Microsoft.Extensions.Logging;

namespace GameTranslatorOverlay.App;

public partial class MainWindow
{
    private enum ProfileEditor { None, Creating, Renaming }

    private readonly UserProfileStore _userProfiles;
    private ProfileEditor _profileEditor;
    private string? _profileEditorProcess;

    private void RefreshProfileList()
    {
        var wasLoading = _loadingUi;
        _loadingUi = true;
        try
        {
            var profileNames = new List<string> { NoProfileLabel };
            profileNames.AddRange(_orchestrator.Profiles.Select(static p => p.Name));
            CmbProfile.ItemsSource = profileNames;
            CmbProfile.SelectedItem = _orchestrator.ActiveProfile?.Name ?? NoProfileLabel;
        }
        finally
        {
            _loadingUi = wasLoading;
        }
    }

    private sealed record ProfileCardState(TargetWindow? Window, GameProfile? Match, string? File, bool Own);

    private ProfileCardState CurrentProfileCard()
    {
        var window = WindowsList.SelectedItem as TargetWindow;
        var active = _orchestrator.ActiveProfile;
        var match = window is null ? null
            : active is not null && active.ProcessNames.Any(name => LiveTargetResolver.SameProcess(name, window.ProcessName)) ? active
            : ProfileProcessMatcher.FindByProcess(_orchestrator.Profiles, window.ProcessName);
        var file = _orchestrator.ProfileFile(match);
        return new ProfileCardState(window, match, file, match is not null && _userProfiles.Owns(file));
    }

    private void UpdateGameProfileCard()
    {
        var (window, match, file, own) = CurrentProfileCard();
        var privateMode = _settings.PrivateMode;
        if (privateMode) _profileEditor = ProfileEditor.None;
        var sameWindow = window is not null && LiveTargetResolver.SameProcess(_profileEditorProcess, window.ProcessName);
        if (_profileEditor == ProfileEditor.Creating && (!sameWindow || match is not null)) _profileEditor = ProfileEditor.None;
        if (_profileEditor == ProfileEditor.Renaming && (!sameWindow || !own)) _profileEditor = ProfileEditor.None;
        if (_profileEditor == ProfileEditor.None) _profileEditorProcess = null;

        var hasProcess = window is not null && LiveTargetResolver.NormalizeProcessName(window.ProcessName).Length > 0;
        TxtProfileNoWindow.Visibility = window is null ? Visibility.Visible : Visibility.Collapsed;
        PnlProfileMatched.Visibility = match is not null && _profileEditor != ProfileEditor.Renaming ? Visibility.Visible : Visibility.Collapsed;
        PnlProfileRename.Visibility = _profileEditor == ProfileEditor.Renaming ? Visibility.Visible : Visibility.Collapsed;
        PnlProfileMissing.Visibility = window is not null && match is null && _profileEditor != ProfileEditor.Creating ? Visibility.Visible : Visibility.Collapsed;
        PnlProfileCreate.Visibility = _profileEditor == ProfileEditor.Creating ? Visibility.Visible : Visibility.Collapsed;

        if (match is not null)
        {
            TxtMatchedProfileName.Text = match.Name;
            ChipProfileOwn.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
            ChipProfileBundled.Visibility = own ? Visibility.Collapsed : Visibility.Visible;
            TxtMatchedProfileDetail.Text = own && file is not null
                ? $"Własny profil z folderu danych: profiles\\{_userProfiles.FolderName(file)}."
                : "Profil z zestawu aplikacji — tylko do odczytu.";
            TxtMatchedProfileActive.Text = string.Equals(_orchestrator.ActiveProfile?.Id, match.Id, StringComparison.OrdinalIgnoreCase)
                ? "Włączony — tłumaczenie korzysta z tego profilu."
                : _orchestrator.ActiveProfile is null
                    ? "Wyłączony — wybrany jest tryb uniwersalny; włączysz go w sekcji Tłumaczenie."
                    : "Teraz wybrany jest inny profil — zmienisz go w sekcji Tłumaczenie.";
            PnlOwnProfileActions.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        }

        TxtProfileMissing.Text = hasProcess
            ? "Ta gra nie ma jeszcze profilu — tłumacz działa w trybie uniwersalnym. Własny profil zapamięta ją i włączy się sam przy jej oknie."
            : "Nie udało się odczytać nazwy procesu tego okna (np. gra działa jako administrator), więc profilu nie da się do niej dopasować.";

        var live = _liveSession is not null;
        BtnCreateProfile.IsEnabled = hasProcess && !live && !privateMode;
        BtnSaveNewProfile.IsEnabled = !live && !privateMode;
        BtnRenameProfile.IsEnabled = !live && !privateMode;
        BtnSaveRename.IsEnabled = !live && !privateMode;
        BtnDeleteProfile.IsEnabled = !live;
        TxtProfileLiveLock.Text = window is null ? string.Empty
            : live ? "Zatrzymaj tryb live, żeby tworzyć i zmieniać profile — przeładowanie listy przerwałoby tłumaczenia w locie."
            : privateMode ? "Tryb prywatny: profili nie tworzy się ani nie zmienia nazwy — zapisałyby nazwę gry na dysku."
            : string.Empty;
    }

    private void OnCreateProfileClick(object sender, RoutedEventArgs e)
    {
        if (_liveSession is not null || _settings.PrivateMode || WindowsList.SelectedItem is not TargetWindow window) return;

        _profileEditor = ProfileEditor.Creating;
        _profileEditorProcess = window.ProcessName;
        TxtNewProfileName.Text = ProfileNames.SuggestFromWindow(window.Title, window.ProcessName);
        TxtNewProfileProcess.Text = window.ProcessName;
        TxtNewProfileProcess.ToolTip = window.ProcessName;
        CmbNewProfileLanguage.ItemsSource = CmbSourceLang.ItemsSource;
        CmbNewProfileLanguage.SelectedItem = _settings.SourceLanguage;
        if (CmbNewProfileLanguage.SelectedItem is null) CmbNewProfileLanguage.SelectedIndex = 0;
        CmbNewProfileLanguage.IsEnabled = CmbNewProfileLanguage.Items.Count > 1;
        TxtNewProfileNote.Text = "Profil trafi do folderu danych (profiles). Zawiera tylko nazwę, proces i język — nie zmienia ustawień OCR.";
        TxtProfileEditError.Text = string.Empty;
        UpdateGameProfileCard();
        FocusEditor(TxtNewProfileName);
    }

    private void OnRenameProfileClick(object sender, RoutedEventArgs e)
    {
        var (window, match, _, own) = CurrentProfileCard();
        if (_liveSession is not null || _settings.PrivateMode || window is null || match is null || !own) return;

        _profileEditor = ProfileEditor.Renaming;
        _profileEditorProcess = window.ProcessName;
        TxtRenameProfile.Text = match.Name;
        TxtProfileEditError.Text = string.Empty;
        UpdateGameProfileCard();
        FocusEditor(TxtRenameProfile);
    }

    private void FocusEditor(TextBox box) => Dispatcher.BeginInvoke(() =>
    {
        if (!box.IsVisible) return;
        box.Focus();
        box.SelectAll();
    }, DispatcherPriority.Input);

    private void OnCancelProfileEditClick(object sender, RoutedEventArgs e)
    {
        _profileEditor = ProfileEditor.None;
        TxtProfileEditError.Text = string.Empty;
        UpdateGameProfileCard();
    }

    private void OnProfileEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancelProfileEditClick(sender, e);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (ReferenceEquals(sender, TxtRenameProfile)) OnSaveRenameClick(sender, e);
            else OnSaveNewProfileClick(sender, e);
        }
    }

    private IEnumerable<string> TakenProfileNames(GameProfile? except) =>
        _orchestrator.Profiles.Where(p => !ReferenceEquals(p, except)).Select(static p => p.Name).Append(NoProfileLabel);

    private void OnSaveNewProfileClick(object sender, RoutedEventArgs e)
    {
        if (_liveSession is not null || _settings.PrivateMode || _profileEditor != ProfileEditor.Creating || _profileEditorProcess is not { } process) return;

        if (ProfileNames.Validate(TxtNewProfileName.Text, TakenProfileNames(except: null)) is { } nameError)
        {
            TxtProfileEditError.Text = nameError;
            return;
        }

        GameProfile created;
        try
        {
            var language = CmbNewProfileLanguage.SelectedItem as string ?? _settings.SourceLanguage;
            created = _userProfiles.Create(TxtNewProfileName.Text, process, language, _orchestrator.Profiles);
        }
        catch (UserProfileException ex)
        {
            TxtProfileEditError.Text = ex.Message;
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Nie udało się zapisać nowego profilu gry: {Error} (0x{HResult:X8})", ex.GetType().Name, ex.HResult);
            TxtProfileEditError.Text = "Nie udało się zapisać profilu w folderze danych — szczegóły w logu diagnostycznym.";
            return;
        }

        _profileEditor = ProfileEditor.None;
        TxtProfileEditError.Text = string.Empty;
        ApplyProfileCatalogChange(created.Id);
        SetStatus(_orchestrator.ActiveProfile?.Id == created.Id
            ? $"Utworzono profil „{created.Name}” — włączony dla tej gry."
            : $"Zapisano profil „{created.Name}”, ale nie udało się go wczytać — szczegóły w logu diagnostycznym.");
    }

    private void OnSaveRenameClick(object sender, RoutedEventArgs e)
    {
        var (_, match, file, own) = CurrentProfileCard();
        if (_liveSession is not null || _settings.PrivateMode || _profileEditor != ProfileEditor.Renaming || match is null || file is null || !own) return;

        var newName = ProfileNames.Clean(TxtRenameProfile.Text);
        if (newName == match.Name)
        {
            OnCancelProfileEditClick(sender, e);
            return;
        }
        if (ProfileNames.Validate(newName, TakenProfileNames(except: match)) is { } nameError)
        {
            TxtProfileEditError.Text = nameError;
            return;
        }

        if (!TryEditUserProfile(() => _userProfiles.Rename(file, match.Id, newName, _orchestrator.Profiles))) return;

        _profileEditor = ProfileEditor.None;
        ApplyProfileCatalogChange(_settings.ActiveProfileId);
        SetStatus($"Zmieniono nazwę profilu na „{newName}”.");
    }

    private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        var (_, match, file, own) = CurrentProfileCard();
        if (_liveSession is not null || match is null || file is null || !own) return;

        var confirmed = MessageBox.Show(this,
            $"Usunąć własny profil „{match.Name}”?\n\nZniknie jego folder w folderze danych (profiles\\{_userProfiles.FolderName(file)}). " +
            "Profile z zestawu aplikacji zostają bez zmian.",
            "GameTranslatorOverlay", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes) return;

        DeleteUserProfile(match, file);
    }

    private void DeleteUserProfile(GameProfile profile, string file)
    {
        if (!TryEditUserProfile(() => _userProfiles.Delete(file, profile.Id))) return;

        var active = string.Equals(_settings.ActiveProfileId, profile.Id, StringComparison.OrdinalIgnoreCase) ? null : _settings.ActiveProfileId;
        ApplyProfileCatalogChange(active);
        SetStatus($"Usunięto profil „{profile.Name}”.");
    }

    private bool TryEditUserProfile(Action edit)
    {
        try
        {
            edit();
            TxtProfileEditError.Text = string.Empty;
            return true;
        }
        catch (UserProfileException ex)
        {
            _profileEditor = ProfileEditor.None;
            ApplyProfileCatalogChange(_settings.ActiveProfileId);
            TxtProfileEditError.Text = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Nie udało się zmienić własnego profilu gry: {Error} (0x{HResult:X8})", ex.GetType().Name, ex.HResult);
            TxtProfileEditError.Text = "Nie udało się zmienić pliku profilu — szczegóły w logu diagnostycznym.";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            _logger.LogWarning("Plik własnego profilu gry ma niepoprawny format: {Error}", ex.GetType().Name);
            _profileEditor = ProfileEditor.None;
            ApplyProfileCatalogChange(_settings.ActiveProfileId);
            TxtProfileEditError.Text = "Plik profilu ma niepoprawny format — popraw go ręcznie w folderze danych albo usuń profil.";
        }
        return false;
    }

    private void ApplyProfileCatalogChange(string? activeProfileId)
    {
        _settings.ActiveProfileId = activeProfileId;
        _orchestrator.ReloadProfiles();
        if (_orchestrator.ActiveProfile is null) _settings.ActiveProfileId = null;
        RefreshProfileList();
        try
        {
            _settingsStore.Save(_settings);
            MarkSettingsApplied();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _appliedPipelineJson = _settings.PipelineSnapshot();
            _logger.LogWarning(ex, "Nie udało się zapisać ustawień po zmianie profili");
        }
        UpdateGameProfileCard();
    }
}
