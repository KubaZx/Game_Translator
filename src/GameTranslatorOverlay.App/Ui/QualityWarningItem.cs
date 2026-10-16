using System.Globalization;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.App.Ui;

public sealed class QualityWarningItem : ObservableObject
{
    public QualityWarningItem(QualityWarningEntry entry)
    {
        Entry = entry;
        _translatedText = entry.CorrectedText ?? entry.TranslatedText;
        _isCorrected = entry.IsCorrected;
    }

    public QualityWarningEntry Entry { get; private set; }

    public long Id => Entry.Id;

    public string Time => Entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string SourceText => Entry.SourceText;

    public string Reason => Entry.Reason;

    public string Hint => Entry.Hint;

    private string _translatedText;
    public string TranslatedText { get => _translatedText; private set => SetField(ref _translatedText, value); }

    private bool _isCorrected;
    public bool IsCorrected { get => _isCorrected; private set => SetField(ref _isCorrected, value); }

    private bool _isEditing;
    public bool IsEditing { get => _isEditing; set => SetField(ref _isEditing, value); }

    private string _editText = string.Empty;
    public string EditText { get => _editText; set => SetField(ref _editText, value); }

    public void Update(QualityWarningEntry entry)
    {
        Entry = entry;
        TranslatedText = entry.CorrectedText ?? entry.TranslatedText;
        IsCorrected = entry.IsCorrected;
    }
}
