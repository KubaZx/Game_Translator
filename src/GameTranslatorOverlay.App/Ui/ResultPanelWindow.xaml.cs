using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Settings;

namespace GameTranslatorOverlay.App.Ui;

public sealed class ResultItem : ObservableObject
{
    public required TranslatedBlock Block { get; init; }
    public required string SourceText { get; init; }

    private string _translatedText = string.Empty;
    public string TranslatedText { get => _translatedText; set => SetField(ref _translatedText, value); }

    private string _originLabel = string.Empty;
    public string OriginLabel { get => _originLabel; set => SetField(ref _originLabel, value); }

    private bool _isEditing;
    public bool IsEditing { get => _isEditing; set => SetField(ref _isEditing, value); }

    private string _editText = string.Empty;
    public string EditText { get => _editText; set => SetField(ref _editText, value); }
}

/// <summary>
/// Panel wyniku obok przetłumaczonego regionu. Nie kradnie fokusu grze
/// (WS_EX_NOACTIVATE) — aktywuje się tylko na czas ręcznej edycji tłumaczenia.
/// </summary>
public partial class ResultPanelWindow : Window
{
    private readonly ObservableCollection<ResultItem> _items = [];
    private readonly DispatcherTimer _autoHideTimer = new();
    private Func<ResultItem, string, Task>? _saveCorrection;
    private Func<ResultItem, Task>? _addTerm;
    private IntPtr _hwnd;
    private int _autoHideSeconds;
    private bool _allowClose;

    public ResultPanelWindow()
    {
        InitializeComponent();
        ItemsHost.ItemsSource = _items;
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            Hide();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        SetNoActivate(true);
        // Panel nie może trafiać do przechwytywanego obrazu (OCR czytałby własne wyniki).
        NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    private void SetNoActivate(bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;
        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        if (enabled)
        {
            exStyle |= NativeMethods.WS_EX_NOACTIVATE;
        }
        else
        {
            exStyle &= ~NativeMethods.WS_EX_NOACTIVATE;
        }
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    public void ShowResults(
        RegionTranslationResult result,
        AppSettings settings,
        Func<ResultItem, string, Task> saveCorrection,
        Func<ResultItem, Task> addTerm)
    {
        _saveCorrection = saveCorrection;
        _addTerm = addTerm;
        _autoHideSeconds = settings.ResultAutoHideSeconds;

        _items.Clear();
        foreach (var block in result.Blocks)
        {
            _items.Add(new ResultItem
            {
                Block = block,
                SourceText = block.Block.Text,
                TranslatedText = block.Outcome.TranslatedText ?? "—",
                OriginLabel = LabelFor(block.Outcome),
                EditText = block.Outcome.TranslatedText ?? string.Empty,
            });
        }

        WarningText.Text = result.Warning ?? string.Empty;
        WarningText.Visibility = string.IsNullOrEmpty(result.Warning) ? Visibility.Collapsed : Visibility.Visible;
        TimingText.Text = result.Timings.ToString();

        // Porzucona edycja z poprzedniego wyniku nie może zostawić panelu w trybie
        // „kradnę fokus grze".
        SetNoActivate(true);

        Show();
        Dispatcher.BeginInvoke(() => PositionNear(result.Region), DispatcherPriority.Loaded);
        RestartAutoHide();
    }

    private static string LabelFor(TranslationOutcome outcome) => outcome.Origin switch
    {
        TranslationOrigin.Glossary => "Słownik",
        TranslationOrigin.Cache => "Cache",
        TranslationOrigin.Provider => "API",
        _ => "Niedostępne",
    };

    private void PositionNear(RectPx region)
    {
        UpdateLayout();
        var monitor = Displays.FromRect(region);
        var scale = monitor.Scale;
        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var work = monitor.WorkArea;

        var x = region.X;
        var y = region.Bottom + 10;
        if (x + width > work.Right) x = Math.Max(work.X, work.Right - width);
        if (y + height > work.Bottom) y = Math.Max(work.Y, region.Y - height - 10);

        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private void RestartAutoHide()
    {
        _autoHideTimer.Stop();
        if (_items.Any(static item => item.IsEditing)) return;
        if (_autoHideSeconds > 0 && PinToggle.IsChecked != true)
        {
            _autoHideTimer.Interval = TimeSpan.FromSeconds(_autoHideSeconds);
            _autoHideTimer.Start();
        }
    }

    private static ResultItem? ItemFrom(object sender) => (sender as FrameworkElement)?.DataContext as ResultItem;

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;
        try
        {
            Clipboard.SetText(item.TranslatedText);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            WarningText.Text = "Schowek jest chwilowo zajęty przez inny program — spróbuj ponownie.";
            WarningText.Visibility = Visibility.Visible;
        }
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;
        item.EditText = item.TranslatedText == "—" ? string.Empty : item.TranslatedText;
        item.IsEditing = true;
        _autoHideTimer.Stop();
        SetNoActivate(false);
        Activate();
    }

    private void OnEditBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private async void OnSaveCorrectionClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;

        var corrected = item.EditText.Trim();
        if (corrected.Length == 0 || _saveCorrection is null)
        {
            item.IsEditing = false;
            SetNoActivate(true);
            return;
        }

        try
        {
            await _saveCorrection(item, corrected);
            item.TranslatedText = corrected;
            item.OriginLabel = "Ręczna korekta ✔";
            item.IsEditing = false;
            SetNoActivate(true);
            RestartAutoHide();
        }
        catch (Exception ex)
        {
            // Edycja zostaje otwarta (tekst użytkownika nie może przepaść przez auto-ukrycie).
            WarningText.Text = "Nie udało się zapisać poprawki: " + ex.Message;
            WarningText.Visibility = Visibility.Visible;
        }
    }

    private async void OnAddTermClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item || _addTerm is null) return;
        if (item.TranslatedText is "—" or "") return;

        try
        {
            await _addTerm(item);
            item.OriginLabel = "Dodano do słownika ✔";
        }
        catch (Exception ex)
        {
            WarningText.Text = "Nie udało się dodać terminu: " + ex.Message;
            WarningText.Visibility = Visibility.Visible;
        }
    }

    private void OnPinChanged(object sender, RoutedEventArgs e) => RestartAutoHide();

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.IsEditing = false;
        }
        SetNoActivate(true);
        Hide();
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
