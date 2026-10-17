using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.App.Ui;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Core.Windows;
using GameTranslatorOverlay.Infrastructure.Content;
using Microsoft.Extensions.Logging;

namespace GameTranslatorOverlay.App;

public partial class MainWindow
{
    private enum ProfileEditor { None, Creating, Renaming, Tuning }

    private readonly UserProfileStore _userProfiles;
    private ProfileEditor _profileEditor;
    private string? _profileEditorProcess;
    private string? _tuningId;
    private ProfileTuning? _tuningBefore;
    private readonly ObservableCollection<string> _tuningProcesses = [];
    private readonly ObservableCollection<string> _tuningTitles = [];
    private readonly ObservableCollection<IgnoreRegionRow> _tuningRegions = [];

    private bool IsOwnProfile(GameProfile profile) => _userProfiles.Owns(_orchestrator.ProfileFile(profile));

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
            : ProfileProcessMatcher.FindByProcess(_orchestrator.Profiles, window.ProcessName, IsOwnProfile);
        var file = _orchestrator.ProfileFile(match);
        return new ProfileCardState(window, match, file, match is not null && _userProfiles.Owns(file));
    }

    private void UpdateGameProfileCard()
    {
        var (window, match, file, own) = CurrentProfileCard();
        var privateMode = _settings.PrivateMode;
        if (privateMode && _profileEditor is ProfileEditor.Creating or ProfileEditor.Renaming) _profileEditor = ProfileEditor.None;
        var sameWindow = window is not null && LiveTargetResolver.SameProcess(_profileEditorProcess, window.ProcessName);
        if (_profileEditor == ProfileEditor.Creating && (!sameWindow || match is not null)) _profileEditor = ProfileEditor.None;
        if (_profileEditor == ProfileEditor.Renaming && (!sameWindow || !own)) _profileEditor = ProfileEditor.None;
        if (_profileEditor == ProfileEditor.Tuning && TuningTarget() is null) _profileEditor = ProfileEditor.None;
        if (_profileEditor != ProfileEditor.Tuning) ForgetTuning();
        if (_profileEditor == ProfileEditor.None) _profileEditorProcess = null;
        var tuning = _profileEditor == ProfileEditor.Tuning;

        var hasProcess = window is not null && LiveTargetResolver.NormalizeProcessName(window.ProcessName).Length > 0;
        TxtProfileNoWindow.Visibility = window is null && !tuning ? Visibility.Visible : Visibility.Collapsed;
        PnlProfileMatched.Visibility = match is not null && _profileEditor is not (ProfileEditor.Renaming or ProfileEditor.Tuning)
            ? Visibility.Visible : Visibility.Collapsed;
        PnlProfileRename.Visibility = _profileEditor == ProfileEditor.Renaming ? Visibility.Visible : Visibility.Collapsed;
        PnlProfileMissing.Visibility = window is not null && match is null && _profileEditor is not (ProfileEditor.Creating or ProfileEditor.Tuning)
            ? Visibility.Visible : Visibility.Collapsed;
        PnlProfileCreate.Visibility = _profileEditor == ProfileEditor.Creating ? Visibility.Visible : Visibility.Collapsed;
        PnlProfileTune.Visibility = tuning ? Visibility.Visible : Visibility.Collapsed;

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
            ListProfileSettings.ItemsSource = ProfileSummary.Describe(match);
            PnlOwnProfileActions.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
            PnlBundledProfileActions.Visibility = own ? Visibility.Collapsed : Visibility.Visible;
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
        BtnTuneProfile.IsEnabled = !live;
        BtnCopyProfile.IsEnabled = !live && !privateMode && file is not null;
        if (tuning) UpdateTuningControls(window, live, privateMode);
        TxtProfileLiveLock.Text = window is null && !tuning ? string.Empty
            : live ? "Zatrzymaj tryb live, żeby tworzyć i zmieniać profile — przeładowanie listy przerwałoby tłumaczenia w locie."
            : privateMode && !tuning ? "Tryb prywatny: profili nie tworzy się, nie kopiuje ani nie przemianowuje — zapisałyby grę na dysku. OCR, krój, słownik i wykrywanie zmian własnego profilu zmienisz w „Dostosuj profil”."
            : string.Empty;
    }

    private (GameProfile Profile, string File)? TuningTarget()
    {
        if (_tuningId is not { } id) return null;
        var profile = _orchestrator.Profiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return profile is not null && _orchestrator.ProfileFile(profile) is { } file && _userProfiles.Owns(file) ? (profile, file) : null;
    }

    private void ForgetTuning()
    {
        if (_tuningId is null && _tuningBefore is null) return;
        _tuningId = null;
        _tuningBefore = null;
        _tuningProcesses.Clear();
        _tuningTitles.Clear();
        _tuningRegions.Clear();
    }

    private void UpdateTuningControls(TargetWindow? window, bool live, bool privateMode)
    {
        if (privateMode && _tuningBefore is { } before) RestoreTuningIdentity(before);

        TxtTuneName.IsEnabled = !privateMode;
        TxtTuneDescription.IsEnabled = !privateMode;
        ListTuneProcesses.IsEnabled = !privateMode;
        ListTuneTitles.IsEnabled = !privateMode;

        var process = window?.ProcessName;
        var hasProcess = LiveTargetResolver.NormalizeProcessName(process).Length > 0;
        var processListed = hasProcess && _tuningProcesses.Any(p => LiveTargetResolver.SameProcess(p, process));
        BtnTuneAddProcess.IsEnabled = !privateMode && hasProcess && !processListed && _tuningProcesses.Count < ProfileTuningRules.MaxListItems;
        TxtTuneProcessHint.Text = privateMode ? "W trybie prywatnym procesów nie dodaje się ani nie usuwa."
            : !hasProcess ? "Zaznacz okno gry na liście obok, żeby dodać jego proces."
            : processListed ? "Proces zaznaczonego okna jest już na liście. Gra z launcherem? Zaznacz jego okno i dodaj drugi proces."
            : $"Doda proces okna zaznaczonego na liście: {process}.";

        var title = ProfileNames.Clean(window?.Title);
        BtnTuneAddTitle.IsEnabled = !privateMode && title.Length is > 0 and <= ProfileTuningRules.MaxWindowTitleLength
            && !_tuningTitles.Contains(title, StringComparer.OrdinalIgnoreCase) && _tuningTitles.Count < ProfileTuningRules.MaxListItems;

        TxtTuneProcessesEmpty.Visibility = _tuningProcesses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtTuneTitlesEmpty.Visibility = _tuningTitles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnTuneAddRegion.IsEnabled = _tuningRegions.Count < ProfileTuningRules.MaxIgnoreRegions;
        BtnSaveTune.IsEnabled = !live;
        TxtTunePrivate.Text = privateMode
            ? "Tryb prywatny: nazwa, procesy, tytuły okien i opis są zablokowane — zapisałyby na dysku, w co grasz."
            : string.Empty;
    }

    private void RestoreTuningIdentity(ProfileTuning before)
    {
        if (ProfileNames.Clean(TxtTuneName.Text) != ProfileNames.Clean(before.Name)) TxtTuneName.Text = before.Name;
        if ((TxtTuneDescription.Text.Trim() is { Length: > 0 } description ? description : null) != before.Description?.Trim())
            TxtTuneDescription.Text = before.Description ?? string.Empty;
        if (!_tuningProcesses.SequenceEqual(before.ProcessNames, StringComparer.Ordinal)) Refill(_tuningProcesses, before.ProcessNames);
        if (!_tuningTitles.SequenceEqual(before.WindowTitles, StringComparer.Ordinal)) Refill(_tuningTitles, before.WindowTitles);
    }

    private static void Refill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private void OnTuneProfileClick(object sender, RoutedEventArgs e)
    {
        var (_, match, file, own) = CurrentProfileCard();
        if (_liveSession is not null || match is null || file is null || !own) return;
        OpenTuningEditor(match, file);
    }

    private void OpenTuningEditor(GameProfile profile, string file)
    {
        ProfileTuning tuning;
        try
        {
            tuning = _userProfiles.ReadTuning(file, profile.Id);
        }
        catch (UserProfileException ex)
        {
            _profileEditor = ProfileEditor.None;
            ApplyProfileCatalogChange(_settings.ActiveProfileId);
            TxtProfileEditError.Text = ex.Message;
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Nie udało się odczytać własnego profilu gry: {Error} (0x{HResult:X8})", ex.GetType().Name, ex.HResult);
            TxtProfileEditError.Text = "Nie udało się odczytać pliku profilu — szczegóły w logu diagnostycznym.";
            return;
        }

        _profileEditor = ProfileEditor.Tuning;
        _profileEditorProcess = null;
        _tuningId = profile.Id;
        _tuningBefore = tuning;
        FillTuningEditor(profile, tuning);
        TxtProfileEditError.Text = string.Empty;
        UpdateGameProfileCard();
        FocusEditor(TxtTuneName);
    }

    private void FillTuningEditor(GameProfile profile, ProfileTuning tuning)
    {
        TxtTuneName.Text = tuning.Name;
        Refill(_tuningProcesses, tuning.ProcessNames);
        Refill(_tuningTitles, tuning.WindowTitles);
        ListTuneProcesses.ItemsSource = _tuningProcesses;
        ListTuneTitles.ItemsSource = _tuningTitles;

        var languages = (CmbSourceLang.ItemsSource as IEnumerable<string> ?? []).ToList();
        if (!languages.Contains(tuning.SourceLanguage, StringComparer.Ordinal)) languages.Add(tuning.SourceLanguage);
        CmbTuneLanguage.ItemsSource = languages;
        CmbTuneLanguage.SelectedItem = tuning.SourceLanguage;
        CmbTuneLanguage.IsEnabled = languages.Count > 1;

        var glossaries = new List<ProfileChoice> { new("Brak słownika profilu", null) };
        glossaries.AddRange(_orchestrator.AvailableGlossaries().Select(static id => new ProfileChoice(id, id)));
        SelectChoice(CmbTuneGlossary, glossaries, tuning.Glossary, static id => $"{id} (nie znaleziono)");

        var fonts = new List<ProfileChoice> { new("Z ustawień aplikacji", null) };
        fonts.AddRange(OverlayFontChoices.Select(static font => new ProfileChoice(font, font)));
        SelectChoice(CmbTuneFont, fonts, tuning.FontFamily, static font => (string)font);

        List<ProfileChoice> modes =
        [
            new("Brak", null),
            new("Ręczne zaznaczenie regionu", ProfileTuningRules.ManualRegionMode),
            new("Tryb live", ProfileTuningRules.LiveMode),
        ];
        SelectChoice(CmbTuneMode, modes, tuning.RecommendedMode, static mode => (string)mode);

        List<ProfileChoice> upscales =
        [
            new("Automatycznie", null),
            new(ProfileSummary.UpscaleLabel(1.0), 1.0),
            new(ProfileSummary.UpscaleLabel(2.0), 2.0),
            new(ProfileSummary.UpscaleLabel(3.0), 3.0),
        ];
        SelectChoice(CmbTuneUpscale, upscales, tuning.OcrUpscale, static upscale => ProfileSummary.UpscaleLabel((double)upscale));

        TxtTuneMinHeight.Text = tuning.OcrMinTextHeight?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        TxtTuneFps.Text = ProfileNumbers.Format(tuning.Fps);
        TxtTuneThreshold.Text = ProfileNumbers.Format(tuning.ChangeThreshold);
        Refill(_tuningRegions, tuning.IgnoreRegions.Select(static (region, index) => new IgnoreRegionRow(index + 1, region)));
        ListTuneRegions.ItemsSource = _tuningRegions;
        TxtTuneDescription.Text = tuning.Description ?? string.Empty;

        var kept = new List<string>();
        if (profile.Overlay?.FontRules is { Count: > 0 } rules) kept.Add($"reguły kroju ({rules.Count})");
        if (profile.Corpus is not null) kept.Add("recepta korpusu");
        if (profile.Online is not null) kept.Add("oznaczenie gry online");
        TxtTuneKept.Text = kept.Count > 0 ? $"Edytor nie zmienia pozostałych pól pliku, m.in.: {string.Join(", ", kept)}." : string.Empty;
    }

    private static void SelectChoice(ComboBox combo, List<ProfileChoice> choices, object? current, Func<object, string> missingLabel)
    {
        var selected = choices.FirstOrDefault(c => c.Value is string text && current is string wanted
            ? string.Equals(text, wanted, StringComparison.OrdinalIgnoreCase)
            : Equals(c.Value, current));
        if (selected is not null && current is not null && !Equals(selected.Value, current))
        {
            var index = choices.IndexOf(selected);
            selected = selected with { Value = current };
            choices[index] = selected;
        }
        else if (selected is null && current is not null)
        {
            selected = new ProfileChoice(missingLabel(current), current);
            choices.Add(selected);
        }
        combo.ItemsSource = choices;
        combo.SelectedItem = selected ?? choices[0];
    }

    private static object? ChoiceValue(ComboBox combo) => (combo.SelectedItem as ProfileChoice)?.Value;

    private ProfileTuning? TuningFromEditor(ProfileTuning before, out string? error)
    {
        error = null;
        if (!ProfileNumbers.TryParseInteger(TxtTuneMinHeight.Text, out var minHeight))
        {
            error = "Minimalna wysokość tekstu to liczba całkowita pikseli (np. 10) albo puste pole.";
            return null;
        }
        if (!ProfileNumbers.TryParse(TxtTuneFps.Text, out var fps))
        {
            error = "Liczba analiz na sekundę to liczba (np. 6) albo puste pole.";
            return null;
        }
        if (!ProfileNumbers.TryParse(TxtTuneThreshold.Text, out var threshold))
        {
            error = "Próg zmiany obrazu to liczba (np. 0,02) albo puste pole.";
            return null;
        }
        var regions = new List<RelativeRegion>();
        foreach (var row in _tuningRegions)
        {
            if (!row.TryRead(out var region))
            {
                error = $"{row.Title}: wpisz cztery liczby — ułamki okna gry od 0 do 1 (np. 0,425).";
                return null;
            }
            regions.Add(region);
        }

        var name = ProfileNames.Clean(TxtTuneName.Text);
        var description = TxtTuneDescription.Text.Trim();
        return before with
        {
            Name = name == ProfileNames.Clean(before.Name) ? before.Name : name,
            ProcessNames = [.. _tuningProcesses],
            WindowTitles = [.. _tuningTitles],
            SourceLanguage = CmbTuneLanguage.SelectedItem as string ?? before.SourceLanguage,
            Glossary = ChoiceValue(CmbTuneGlossary) as string,
            OcrUpscale = ChoiceValue(CmbTuneUpscale) as double?,
            OcrMinTextHeight = minHeight,
            Fps = fps,
            ChangeThreshold = threshold,
            FontFamily = ChoiceValue(CmbTuneFont) as string,
            RecommendedMode = ChoiceValue(CmbTuneMode) as string,
            Description = description.Length == 0 ? null : description == before.Description?.Trim() ? before.Description : description,
            IgnoreRegions = regions,
        };
    }

    private void OnSaveTuneClick(object sender, RoutedEventArgs e)
    {
        if (_liveSession is not null || _profileEditor != ProfileEditor.Tuning || _tuningBefore is not { } before
            || TuningTarget() is not var (profile, file)) return;

        if (TuningFromEditor(before, out var parseError) is not { } after)
        {
            TxtProfileEditError.Text = parseError;
            return;
        }
        if (_settings.PrivateMode && ProfileTuningRules.PrivateModeViolation(before, after) is { } privacy)
        {
            TxtProfileEditError.Text = privacy;
            return;
        }
        if (ProfileTuningRules.Validate(before, after, TakenProfileNames(except: profile)).FirstOrDefault() is { } invalid)
        {
            TxtProfileEditError.Text = invalid;
            return;
        }
        if (after.Equals(before))
        {
            OnCancelProfileEditClick(sender, e);
            SetStatus("Profil bez zmian — nic nie zapisano.");
            return;
        }

        if (!TryEditUserProfile(() => _userProfiles.Update(file, profile.Id, before, after, _orchestrator.Profiles, _settings.PrivateMode))) return;

        _profileEditor = ProfileEditor.None;
        var active = string.Equals(_settings.ActiveProfileId, profile.Id, StringComparison.OrdinalIgnoreCase);
        ApplyProfileCatalogChange(_settings.ActiveProfileId);
        SetStatus(active
            ? $"Zapisano profil „{after.Name}” — nowe ustawienia już działają."
            : $"Zapisano profil „{after.Name}”. Zadziała, gdy go włączysz albo wybierzesz okno tej gry.");
    }

    private void OnTuneAddProcessClick(object sender, RoutedEventArgs e)
    {
        if (_profileEditor != ProfileEditor.Tuning || _settings.PrivateMode || WindowsList.SelectedItem is not TargetWindow window) return;
        if (LiveTargetResolver.NormalizeProcessName(window.ProcessName).Length == 0
            || _tuningProcesses.Any(p => LiveTargetResolver.SameProcess(p, window.ProcessName))) return;
        _tuningProcesses.Add(window.ProcessName.Trim());
        UpdateGameProfileCard();
    }

    private void OnTuneRemoveProcessClick(object sender, RoutedEventArgs e)
    {
        if (_settings.PrivateMode || (sender as FrameworkElement)?.DataContext is not string process) return;
        _tuningProcesses.Remove(process);
        UpdateGameProfileCard();
    }

    private void OnTuneAddTitleClick(object sender, RoutedEventArgs e)
    {
        if (_profileEditor != ProfileEditor.Tuning || _settings.PrivateMode || WindowsList.SelectedItem is not TargetWindow window) return;
        var title = ProfileNames.Clean(window.Title);
        if (title.Length == 0 || _tuningTitles.Contains(title, StringComparer.OrdinalIgnoreCase)) return;
        _tuningTitles.Add(title);
        UpdateGameProfileCard();
    }

    private void OnTuneRemoveTitleClick(object sender, RoutedEventArgs e)
    {
        if (_settings.PrivateMode || (sender as FrameworkElement)?.DataContext is not string title) return;
        _tuningTitles.Remove(title);
        UpdateGameProfileCard();
    }

    private void OnTuneAddRegionClick(object sender, RoutedEventArgs e)
    {
        if (_profileEditor != ProfileEditor.Tuning || _tuningRegions.Count >= ProfileTuningRules.MaxIgnoreRegions) return;
        _tuningRegions.Add(new IgnoreRegionRow(_tuningRegions.Count + 1));
        UpdateGameProfileCard();
    }

    private void OnTuneRemoveRegionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IgnoreRegionRow row) return;
        _tuningRegions.Remove(row);
        for (var i = 0; i < _tuningRegions.Count; i++) _tuningRegions[i].Renumber(i + 1);
        UpdateGameProfileCard();
    }

    private void OnCopyProfileClick(object sender, RoutedEventArgs e)
    {
        var (_, match, file, own) = CurrentProfileCard();
        if (_liveSession is not null || _settings.PrivateMode || match is null || file is null || own) return;

        GameProfile copy;
        try
        {
            copy = _userProfiles.CreateCopy(file, match.Id, _orchestrator.Profiles);
        }
        catch (UserProfileException ex)
        {
            TxtProfileEditError.Text = ex.Message;
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Nie udało się zapisać kopii profilu gry: {Error} (0x{HResult:X8})", ex.GetType().Name, ex.HResult);
            TxtProfileEditError.Text = "Nie udało się zapisać kopii w folderze danych — szczegóły w logu diagnostycznym.";
            return;
        }

        TxtProfileEditError.Text = string.Empty;
        ApplyProfileCatalogChange(copy.Id);
        if (_orchestrator.ActiveProfile is { } active && active.Id == copy.Id && _orchestrator.ProfileFile(active) is { } copyFile)
        {
            OpenTuningEditor(active, copyFile);
            SetStatus($"Utworzono kopię „{copy.Name}” — włączona zamiast profilu z zestawu. Dostosuj ją i zapisz.");
        }
        else
        {
            SetStatus($"Zapisano kopię „{copy.Name}”, ale nie udało się jej wczytać — szczegóły w logu diagnostycznym.");
        }
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
            if (ReferenceEquals(sender, PnlProfileTune) && (e.OriginalSource is not TextBox || ReferenceEquals(e.OriginalSource, TxtTuneDescription))) return;
            e.Handled = true;
            if (ReferenceEquals(sender, TxtRenameProfile)) OnSaveRenameClick(sender, e);
            else if (ReferenceEquals(sender, PnlProfileTune)) OnSaveTuneClick(sender, e);
            else OnSaveNewProfileClick(sender, e);
        }
    }

    private void OnProfileTuneEdited(object sender, RoutedEventArgs e)
    {
        if (TxtProfileEditError.Text.Length > 0) TxtProfileEditError.Text = string.Empty;
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
