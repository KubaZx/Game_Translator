using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Infrastructure.Content;
using Microsoft.Win32;

namespace GameTranslatorOverlay.App.Ui;

public sealed class GlossaryTermRow
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public int Priority { get; set; } = 100;
    public bool CaseSensitive { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Edytor prywatnego słownika użytkownika: dodawanie/edycja/usuwanie terminów,
/// import i eksport JSON, wykrywanie konfliktów (ten sam termin → różne tłumaczenia).
/// </summary>
public partial class GlossaryEditorWindow : Window
{
    private readonly UserGlossaryStore _store;
    private readonly TranslationOrchestrator _orchestrator;
    private readonly string _sourceLanguage;
    private readonly string _targetLanguage;
    private readonly ObservableCollection<GlossaryTermRow> _rows = [];

    public GlossaryEditorWindow(
        UserGlossaryStore store,
        TranslationOrchestrator orchestrator,
        string sourceLanguage,
        string targetLanguage)
    {
        InitializeComponent();
        _store = store;
        _orchestrator = orchestrator;
        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;

        TxtHeader.Text =
            $"Prywatny słownik ({sourceLanguage} → {targetLanguage}). Termin tłumaczy CAŁĄ dopasowaną frazę lokalnie " +
            "(bez API) i ma pierwszeństwo przed tłumaczeniem online. Ręczne korekty tłumaczeń są ważniejsze od terminów. " +
            "Wyższy priorytet wygrywa przy konfliktach.";

        foreach (var term in _store.Load(sourceLanguage, targetLanguage).Terms)
        {
            _rows.Add(new GlossaryTermRow
            {
                Source = term.Source,
                Target = term.Target,
                Priority = term.Priority,
                CaseSensitive = term.CaseSensitive,
                Note = term.Note,
            });
        }

        TermsGrid.ItemsSource = _rows;
        UpdateConflictInfo();
    }

    private void OnAddRowClick(object sender, RoutedEventArgs e)
    {
        var row = new GlossaryTermRow();
        _rows.Add(row);
        TermsGrid.SelectedItem = row;
        TermsGrid.ScrollIntoView(row);
    }

    private bool TryCommitPendingEdit()
    {
        if (!TermsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true))
        {
            TxtInfo.Text = "Popraw zaznaczoną komórkę (priorytet musi być liczbą całkowitą), zanim wykonasz tę operację.";
            return false;
        }
        return true;
    }

    private void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (!TryCommitPendingEdit()) return;

        foreach (var row in TermsGrid.SelectedItems.Cast<GlossaryTermRow>().ToList())
        {
            _rows.Remove(row);
        }
        UpdateConflictInfo();
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (!TryCommitPendingEdit()) return;

        var dialog = new OpenFileDialog { Filter = "Słownik JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var document = GlossarySerializer.FromJson(File.ReadAllText(dialog.FileName));
            var errors = GlossaryValidator.Validate(document);
            if (errors.Count > 0)
            {
                TxtInfo.Text = "Plik odrzucony: " + string.Join(" ", errors);
                return;
            }

            // Słownik innej pary językowej podmieniałby tłumaczenia po cichu.
            if (!document.SourceLanguage.Equals(_sourceLanguage, StringComparison.OrdinalIgnoreCase)
                || !document.TargetLanguage.Equals(_targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                TxtInfo.Text = $"Plik odrzucony: to słownik pary {document.SourceLanguage}→{document.TargetLanguage}, " +
                               $"a edytujesz parę {_sourceLanguage}→{_targetLanguage}.";
                return;
            }

            var imported = 0;
            var overwritten = 0;
            foreach (var term in document.Terms)
            {
                var existing = _rows.FirstOrDefault(r =>
                    r.Source.Equals(term.Source, StringComparison.OrdinalIgnoreCase)
                    && r.CaseSensitive == term.CaseSensitive);
                if (existing is not null)
                {
                    existing.Target = term.Target;
                    existing.Priority = term.Priority;
                    existing.Note = term.Note;
                    overwritten++;
                }
                else
                {
                    _rows.Add(new GlossaryTermRow
                    {
                        Source = term.Source,
                        Target = term.Target,
                        Priority = term.Priority,
                        CaseSensitive = term.CaseSensitive,
                        Note = term.Note,
                    });
                }
                imported++;
            }

            TermsGrid.Items.Refresh();
            TxtInfo.Text = $"Zaimportowano {imported} terminów z „{Path.GetFileName(dialog.FileName)}”" +
                           (overwritten > 0 ? $" (nadpisano {overwritten} istniejących)." : ".");
            UpdateConflictInfo(prefix: TxtInfo.Text + " ");
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or IOException)
        {
            TxtInfo.Text = "Nie udało się wczytać pliku: " + ex.Message;
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Słownik JSON|*.json",
            FileName = $"user-glossary.{_sourceLanguage}-{_targetLanguage}.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, GlossarySerializer.ToJson(BuildDocument()));
            TxtInfo.Text = $"Wyeksportowano do „{dialog.FileName}”.";
        }
        catch (IOException ex)
        {
            TxtInfo.Text = "Nie udało się zapisać pliku: " + ex.Message;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!TryCommitPendingEdit()) return;

        _store.ReplaceAll(BuildDocument(), _sourceLanguage, _targetLanguage);
        _orchestrator.RebuildPipeline();
        Close();
    }

    private GlossaryDocument BuildDocument()
    {
        var document = new GlossaryDocument
        {
            Name = "user",
            SourceLanguage = _sourceLanguage,
            TargetLanguage = _targetLanguage,
            Description = "Prywatny słownik użytkownika — terminy dodane w aplikacji.",
        };
        document.Terms.AddRange(_rows
            .Where(static r => !string.IsNullOrWhiteSpace(r.Source) && !string.IsNullOrWhiteSpace(r.Target))
            .Select(static r => new GlossaryTerm(
                r.Source.Trim(),
                r.Target.Trim(),
                r.CaseSensitive,
                r.Priority,
                string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim())));
        return document;
    }

    private void UpdateConflictInfo(string prefix = "")
    {
        var conflicts = _rows
            .Where(static r => !string.IsNullOrWhiteSpace(r.Source))
            .GroupBy(static r => r.Source.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(static g => g.Select(static r => r.Target.Trim()).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(static g => g.Key)
            .ToList();

        TxtInfo.Text = conflicts.Count > 0
            ? prefix + "⚠ Konflikty (ten sam termin, różne tłumaczenia): " + string.Join(", ", conflicts) +
              " — wygra wpis o wyższym priorytecie."
            : prefix;
    }
}
