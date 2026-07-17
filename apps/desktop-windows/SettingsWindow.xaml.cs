using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IPMultiViewer.Desktop;

public partial class SettingsWindow : Window
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    private readonly MainWindow _owner;
    private bool _allowClose;

    public SettingsWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        ShowInTaskbar = false;
        SourceInitialized += (_, _) =>
        {
            AttachOwner();
            TryEnableDarkTitleBar();
        };
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

    private void AttachOwner()
    {
        if (Owner is null && _owner.IsVisible)
        {
            Owner = _owner;
        }
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

    private void SettingsWindow_Closing(object? sender, CancelEventArgs e)
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
