using FluxStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FluxStudio;

public sealed partial class SettingsView : UserControl
{
    public FluxStudioSettings Settings { get; private set; }
    private readonly IntPtr _ownerHandle;

    public SettingsView(FluxStudioSettings settings, IntPtr ownerHandle)
    {
        InitializeComponent();
        Settings = settings.Clone();
        _ownerHandle = ownerHandle;
        SettingsRoot.RequestedTheme = Settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ThemeCombo.SelectedIndex = Settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        FontFamilyBox.Text = Settings.EditorFontFamily;
        FontSizeBox.Value = Settings.EditorFontSize;
        IndentSizeBox.Value = Settings.IndentSize;
        AutoIndentCheck.IsChecked = Settings.AutoIndent;
        AutoEndCheck.IsChecked = Settings.AutoInsertEnd;
        CompletionCheck.IsChecked = Settings.EnableCompletion;
        InterpreterPathBox.Text = Settings.InterpreterPath;
        WorkingDirectoryBox.Text = Settings.WorkingDirectory;
    }

    private async void BrowseInterpreter_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, _ownerHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) InterpreterPathBox.Text = file.Path;
    }

    private async void BrowseWorkingDirectory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _ownerHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) WorkingDirectoryBox.Text = folder.Path;
    }

    public FluxStudioSettings ReadSettings()
    {
        Settings.Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";
        Settings.EditorFontFamily = string.IsNullOrWhiteSpace(FontFamilyBox.Text) ? "Consolas" : FontFamilyBox.Text.Trim();
        double fontSize = double.IsFinite(FontSizeBox.Value) ? FontSizeBox.Value : Settings.EditorFontSize;
        double indentSize = double.IsFinite(IndentSizeBox.Value) ? IndentSizeBox.Value : Settings.IndentSize;
        Settings.EditorFontSize = Math.Clamp(fontSize, 8, 40);
        Settings.IndentSize = Math.Clamp((int)Math.Round(indentSize), 1, 8);
        Settings.AutoIndent = AutoIndentCheck.IsChecked == true;
        Settings.AutoInsertEnd = AutoEndCheck.IsChecked == true;
        Settings.EnableCompletion = CompletionCheck.IsChecked == true;
        Settings.InterpreterPath = string.IsNullOrWhiteSpace(InterpreterPathBox.Text) ? "Runtime/bin/flux.exe" : InterpreterPathBox.Text.Trim();
        Settings.WorkingDirectory = WorkingDirectoryBox.Text?.Trim() ?? string.Empty;
        return Settings.Clone();
    }
}
