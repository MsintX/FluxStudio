using FluxStudio.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;
using WinRT.Interop;

namespace FluxStudio;

/// <summary>
/// A modal-style WinUI window. WinUI 3's Window has no built-in ShowDialog,
/// so this class provides an awaitable ShowDialog() that disables its owner
/// until the settings window closes.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const int GwlHwndParent = -8;

    private readonly IntPtr _ownerHandle;
    private readonly TaskCompletionSource<bool> _dialogCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _shown;
    private bool _ownerWasEnabled;
    private bool _saved;

    public FluxStudioSettings? SavedSettings { get; private set; }

    public SettingsWindow(FluxStudioSettings settings, Window owner)
    {
        InitializeComponent();
        Title = "Flux Studio 设置";
        _ownerHandle = WindowNative.GetWindowHandle(owner);

        RootGrid.RequestedTheme = settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        IntPtr thisHandle = WindowNative.GetWindowHandle(this);
        SettingsContent.Content = new SettingsView(settings, thisHandle);
        Closed += SettingsWindow_Closed;

        try
        {
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(thisHandle);
            AppWindow.GetFromWindowId(id).Resize(new SizeInt32(760, 820));
        }
        catch
        {
            // Window sizing is optional; preserve the default size if unsupported.
        }
    }

    /// <summary>
    /// Shows this window modally relative to its owner. Await the returned task
    /// to resume after the user saves, cancels, or closes the window.
    /// </summary>
    public Task<bool> ShowDialog()
    {
        if (_shown)
            return _dialogCompletion.Task;

        _shown = true;

        // Establish native ownership before activation so the settings window
        // stays above the main window and minimizes/restores with it.
        IntPtr thisHandle = WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(thisHandle, GwlHwndParent, _ownerHandle);

        // EnableWindow returns the PREVIOUS enabled state, not the new state.
        // Reading it as a boolean result makes this false for an enabled owner, so
        // the owner would stay disabled forever after closing settings (looks frozen).
        _ownerWasEnabled = IsWindowEnabled(_ownerHandle);
        if (_ownerWasEnabled)
            EnableWindow(_ownerHandle, false);
        Activate();
        return _dialogCompletion.Task;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsContent.Content is SettingsView view)
        {
            SavedSettings = view.ReadSettings();
            _saved = true;
        }
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _saved = false;
        SavedSettings = null;
        Close();
    }

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_ownerWasEnabled)
        {
            EnableWindow(_ownerHandle, true);
            SetForegroundWindow(_ownerHandle);
        }

        _dialogCompletion.TrySetResult(_saved);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
