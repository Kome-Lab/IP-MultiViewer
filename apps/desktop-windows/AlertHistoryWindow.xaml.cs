using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace IPMultiViewer.Desktop;

public partial class AlertHistoryWindow : Window
{
    private readonly MainWindow _owner;
    private bool _allowClose;

    public AlertHistoryWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        AcknowledgeAlertButton.Click += (_, _) => _owner.SetAlertStatus(SelectedAlert, "acknowledged");
        SnoozeAlertButton.Click += (_, _) => _owner.SetAlertStatus(SelectedAlert, "snoozed");
        ResolveAlertButton.Click += (_, _) => _owner.SetAlertStatus(SelectedAlert, "resolved");
        IgnoreAlertButton.Click += (_, _) => _owner.SetAlertStatus(SelectedAlert, "ignored");
        AlertHistoryList.SelectionChanged += (_, _) => RefreshButtons();
        RefreshAlerts();
    }

    internal AlertRecord? SelectedAlert => AlertHistoryList.SelectedItem as AlertRecord;

    internal void RefreshAlerts()
    {
        var previousId = SelectedAlert?.Id;
        var alerts = _owner.AlertHistorySnapshot();
        AlertHistoryList.ItemsSource = alerts;
        AlertHistoryList.SelectedItem = alerts.FirstOrDefault(alert => alert.Id == previousId);
        SummaryText.Text = $"{alerts.Count(alert => MainWindow.IsActiveAlertStatus(alert.Status))} active / {alerts.Count} total";
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        var enabled = SelectedAlert is not null;
        AcknowledgeAlertButton.IsEnabled = enabled;
        SnoozeAlertButton.IsEnabled = enabled;
        ResolveAlertButton.IsEnabled = enabled;
        IgnoreAlertButton.IsEnabled = enabled;
    }

    private void AlertHistoryWindow_Closing(object sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    internal void ForceClose()
    {
        _allowClose = true;
        Close();
    }
}
