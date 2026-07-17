using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace IPMultiViewer.Desktop;

public partial class AlertHistoryWindow : Window
{
    private const int MaxVisibleAlertRows = 500;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    private readonly MainWindow _owner;
    private readonly DispatcherTimer _filterRefreshTimer;
    private IReadOnlyList<AlertRecord> _allAlerts = [];
    private bool _allowClose;
    private bool _suppressFilterEvents;

    public AlertHistoryWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        ShowInTaskbar = false;
        SourceInitialized += (_, _) =>
        {
            AttachOwner();
            TryEnableDarkTitleBar();
        };

        _filterRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _filterRefreshTimer.Tick += (_, _) =>
        {
            _filterRefreshTimer.Stop();
            ApplyFilters();
        };

        ConfigureFilters();
        AcknowledgeAlertButton.Click += (_, _) => _owner.SetAlertStatus(SelectedAlert, "acknowledged");
        SnoozeAlertButton.Click += (_, _) => _owner.SetAlertStatus(SelectedAlert, "snoozed");
        ResolveAlertButton.Click += (_, _) => _owner.SetAlertStatus(SelectedAlert, "resolved");
        IgnoreAlertButton.Click += (_, _) => _owner.SetAlertStatus(SelectedAlert, "ignored");
        AlertHistoryList.SelectionChanged += (_, _) => RefreshSelectionState();
        SearchTextBox.TextChanged += (_, _) => QueueFilterRefresh();
        StatusFilterCombo.SelectionChanged += (_, _) => ApplyFiltersUnlessSuppressed();
        SeverityFilterCombo.SelectionChanged += (_, _) => ApplyFiltersUnlessSuppressed();
        ClearFiltersButton.Click += (_, _) => ClearFilters();
        RefreshAlerts();
    }

    private void AttachOwner()
    {
        if (Owner is null && _owner.IsVisible)
        {
            Owner = _owner;
        }
    }

    internal AlertRecord? SelectedAlert => AlertHistoryList.SelectedItem as AlertRecord;

    internal void RefreshAlerts()
    {
        var previousId = SelectedAlert?.Id;
        _allAlerts = _owner.AlertHistorySnapshot();
        RefreshMetrics();
        ApplyFilters(previousId);
    }

    private void ConfigureFilters()
    {
        _suppressFilterEvents = true;
        try
        {
            StatusFilterCombo.ItemsSource = new FilterOption[]
            {
                new("All statuses", ""),
                new("Open", "open"),
                new("Acknowledged", "acknowledged"),
                new("Snoozed", "snoozed"),
                new("Resolved", "resolved"),
                new("Ignored", "ignored")
            };
            SeverityFilterCombo.ItemsSource = new FilterOption[]
            {
                new("All severities", ""),
                new("Critical", "critical"),
                new("Error", "error"),
                new("Warning", "warning"),
                new("Info", "info")
            };
            StatusFilterCombo.SelectedIndex = 0;
            SeverityFilterCombo.SelectedIndex = 0;
            SearchTextBox.ToolTip = "Search stream, type, message, status, or severity";
        }
        finally
        {
            _suppressFilterEvents = false;
        }
    }

    private void QueueFilterRefresh()
    {
        if (_suppressFilterEvents)
        {
            return;
        }

        _filterRefreshTimer.Stop();
        _filterRefreshTimer.Start();
    }

    private void ApplyFiltersUnlessSuppressed()
    {
        if (!_suppressFilterEvents)
        {
            ApplyFilters();
        }
    }

    private void ClearFilters()
    {
        _filterRefreshTimer.Stop();
        _suppressFilterEvents = true;
        try
        {
            SearchTextBox.Clear();
            StatusFilterCombo.SelectedIndex = 0;
            SeverityFilterCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        ApplyFilters();
        SearchTextBox.Focus();
    }

    private void ApplyFilters(string? preferredSelectionId = null)
    {
        var previousId = preferredSelectionId ?? SelectedAlert?.Id;
        var query = SearchTextBox.Text.Trim();
        var status = (StatusFilterCombo.SelectedItem as FilterOption)?.Value ?? "";
        var severity = (SeverityFilterCombo.SelectedItem as FilterOption)?.Value ?? "";

        IEnumerable<AlertRecord> filtered = _allAlerts;
        if (!string.IsNullOrWhiteSpace(status))
        {
            filtered = filtered.Where(alert => string.Equals(alert.Status, status, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(severity))
        {
            filtered = filtered.Where(alert => string.Equals(alert.Severity, severity, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(alert => AlertMatchesSearch(alert, query));
        }

        var matchingAlerts = filtered.ToList();
        var visibleAlerts = matchingAlerts.Take(MaxVisibleAlertRows).ToList();
        AlertHistoryList.ItemsSource = visibleAlerts;
        AlertHistoryList.SelectedItem = visibleAlerts.FirstOrDefault(alert =>
            string.Equals(alert.Id, previousId, StringComparison.OrdinalIgnoreCase));

        ResultsSummaryText.Text = matchingAlerts.Count > MaxVisibleAlertRows
            ? $"Showing newest {MaxVisibleAlertRows:N0} of {matchingAlerts.Count:N0} matching · {_allAlerts.Count:N0} total"
            : $"{matchingAlerts.Count:N0} matching · {_allAlerts.Count:N0} total";
        EmptyResultsPanel.Visibility = visibleAlerts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSelectionState();
    }

    private static bool AlertMatchesSearch(AlertRecord alert, string query)
    {
        return Contains(alert.StreamId, query) ||
               Contains(alert.Type, query) ||
               Contains(alert.Message, query) ||
               Contains(alert.Status, query) ||
               Contains(alert.Severity, query) ||
               Contains(alert.Id, query);
    }

    private static bool Contains(string? value, string query)
    {
        return value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void RefreshMetrics()
    {
        var active = 0;
        var open = 0;
        var acknowledged = 0;
        var snoozed = 0;
        foreach (var alert in _allAlerts)
        {
            if (MainWindow.IsActiveAlertStatus(alert.Status))
            {
                active++;
            }

            if (string.Equals(alert.Status, "open", StringComparison.OrdinalIgnoreCase))
            {
                open++;
            }
            else if (string.Equals(alert.Status, "acknowledged", StringComparison.OrdinalIgnoreCase))
            {
                acknowledged++;
            }
            else if (string.Equals(alert.Status, "snoozed", StringComparison.OrdinalIgnoreCase))
            {
                snoozed++;
            }
        }

        ActiveCountText.Text = active.ToString("N0", CultureInfo.CurrentCulture);
        OpenCountText.Text = open.ToString("N0", CultureInfo.CurrentCulture);
        AcknowledgedCountText.Text = acknowledged.ToString("N0", CultureInfo.CurrentCulture);
        SnoozedCountText.Text = snoozed.ToString("N0", CultureInfo.CurrentCulture);
        TotalCountText.Text = _allAlerts.Count.ToString("N0", CultureInfo.CurrentCulture);
        SummaryText.Text = $"{active:N0} active / {_allAlerts.Count:N0} total";
    }

    private void RefreshSelectionState()
    {
        var alert = SelectedAlert;
        var hasSelection = alert is not null;
        DetailEmptyState.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        DetailContentPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

        if (alert is null)
        {
            AcknowledgeAlertButton.IsEnabled = false;
            SnoozeAlertButton.IsEnabled = false;
            ResolveAlertButton.IsEnabled = false;
            IgnoreAlertButton.IsEnabled = false;
            return;
        }

        DetailStreamText.Text = alert.StreamId;
        DetailSeverityText.Text = alert.Severity.ToUpperInvariant();
        DetailStatusText.Text = alert.Status;
        DetailTypeText.Text = alert.Type;
        DetailMessageText.Text = alert.Message;
        DetailStartedText.Text = FormatLocalTime(alert.StartedAt);
        DetailUpdatedText.Text = FormatLocalTime(alert.UpdatedAt == default ? alert.StartedAt : alert.UpdatedAt);
        DetailSnoozedPanel.Visibility = alert.SnoozedUntil is null ? Visibility.Collapsed : Visibility.Visible;
        DetailSnoozedText.Text = alert.SnoozedUntil is null ? "" : FormatLocalTime(alert.SnoozedUntil.Value);

        var active = MainWindow.IsActiveAlertStatus(alert.Status);
        AcknowledgeAlertButton.IsEnabled = active &&
            !string.Equals(alert.Status, "acknowledged", StringComparison.OrdinalIgnoreCase);
        SnoozeAlertButton.IsEnabled = active &&
            !string.Equals(alert.Status, "snoozed", StringComparison.OrdinalIgnoreCase);
        ResolveAlertButton.IsEnabled = active;
        IgnoreAlertButton.IsEnabled = active;
    }

    private static string FormatLocalTime(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int value,
        int valueSize);

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var enabled = 1;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // Older Windows builds may not expose DWM theming. The content remains dark.
        }
        catch (EntryPointNotFoundException)
        {
            // The window is still usable when the dark-title-bar attribute is unavailable.
        }
    }

    private void AlertHistoryWindow_Closing(object sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        _filterRefreshTimer.Stop();
        Hide();
    }

    internal void ForceClose()
    {
        _allowClose = true;
        _filterRefreshTimer.Stop();
        Close();
    }

    private sealed record FilterOption(string Label, string Value)
    {
        public override string ToString() => Label;
    }
}
