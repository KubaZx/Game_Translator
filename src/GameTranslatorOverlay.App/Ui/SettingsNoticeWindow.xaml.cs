using System.Windows;
using System.ComponentModel;
using GameTranslatorOverlay.Infrastructure.Settings;

namespace GameTranslatorOverlay.App.Ui;

public enum SettingsNoticeChoice
{
    Acknowledged,
    StartFromDefaults,
    KeepFile,
}

public partial class SettingsNoticeWindow : Window
{
    private readonly bool _hasChoice;

    public SettingsNoticeWindow(SettingsLoadNotice notice)
    {
        InitializeComponent();
        Title = "GameTranslatorOverlay — " + notice.Title;
        TxtTitle.Text = notice.Title;
        TxtMessage.Text = notice.Message;

        _hasChoice = notice.DefaultsButton is not null && notice.KeepButton is not null;
        if (_hasChoice)
        {
            BtnDefaults.Content = notice.DefaultsButton;
            BtnDefaults.Style = null;
            BtnKeep.Content = notice.KeepButton;
            BtnKeep.Style = (Style)FindResource("Btn.Primary");
        }
        else
        {
            BtnKeep.Visibility = Visibility.Collapsed;
            BtnDefaults.Content = "OK";
        }
    }

    public SettingsNoticeChoice Choice { get; private set; } = SettingsNoticeChoice.Acknowledged;

    private void OnDefaultsClick(object sender, RoutedEventArgs e)
    {
        Choice = _hasChoice ? SettingsNoticeChoice.StartFromDefaults : SettingsNoticeChoice.Acknowledged;
        DialogResult = true;
    }

    private void OnKeepClick(object sender, RoutedEventArgs e)
    {
        Choice = SettingsNoticeChoice.KeepFile;
        DialogResult = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DialogResult is null && _hasChoice) Choice = SettingsNoticeChoice.KeepFile;
    }
}
