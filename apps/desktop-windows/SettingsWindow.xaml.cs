using System.ComponentModel;
using System.Windows;

namespace IPMultiViewer.Desktop;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _owner;

    public SettingsWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        ImportButton.Click += _owner.ImportButton_Click;
        ExportButton.Click += _owner.ExportButton_Click;
        SaveButton.Click += _owner.SaveButton_Click;
        ReloadButton.Click += _owner.ReloadButton_Click;
        GlobalMuteCheck.Checked += _owner.GlobalMuteCheck_Changed;
        GlobalMuteCheck.Unchecked += _owner.GlobalMuteCheck_Changed;
        AlertSoundCheck.Checked += _owner.AlertSoundCheck_Changed;
        AlertSoundCheck.Unchecked += _owner.AlertSoundCheck_Changed;
        ApplyServerButton.Click += _owner.ApplyServerButton_Click;
        StreamsList.SelectionChanged += _owner.StreamsList_SelectionChanged;
        ApplyStreamButton.Click += _owner.ApplyStreamButton_Click;
        AddStreamButton.Click += _owner.AddStreamButton_Click;
        DeleteStreamButton.Click += _owner.DeleteStreamButton_Click;
        LayoutCombo.SelectionChanged += _owner.LayoutCombo_SelectionChanged;
        AlertProfileCombo.SelectionChanged += _owner.AlertProfileCombo_SelectionChanged;
        AddAlertProfileButton.Click += _owner.AddAlertProfileButton_Click;
        DuplicateAlertProfileButton.Click += _owner.DuplicateAlertProfileButton_Click;
        DeleteAlertProfileButton.Click += _owner.DeleteAlertProfileButton_Click;
        ApplyAlertProfileButton.Click += _owner.ApplyAlertProfileButton_Click;
    }

    private void SettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
