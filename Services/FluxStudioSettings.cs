using System.Text.Json;

namespace FluxStudio.Services;

/// <summary>Persisted user preferences for Flux Studio.</summary>
public sealed class FluxStudioSettings
{
    public string Theme { get; set; } = "System";
    public string EditorFontFamily { get; set; } = "Consolas";
    public double EditorFontSize { get; set; } = 14;
    public int IndentSize { get; set; } = 4;
    public bool AutoIndent { get; set; } = true;
    public bool AutoInsertEnd { get; set; } = true;
    public bool EnableCompletion { get; set; } = true;
    public string InterpreterPath { get; set; } = OperatingSystem.IsWindows() ? "Runtime/bin/flux.exe" : "Runtime/bin/flux";
    public string WorkingDirectory { get; set; } = string.Empty;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluxStudio", "settings.json");

    public FluxStudioSettings Clone()
        => JsonSerializer.Deserialize<FluxStudioSettings>(JsonSerializer.Serialize(this)) ?? new FluxStudioSettings();

    public static FluxStudioSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<FluxStudioSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null)
                {
                    loaded.EditorFontSize = Math.Clamp(loaded.EditorFontSize, 8, 40);
                    loaded.IndentSize = Math.Clamp(loaded.IndentSize, 1, 8);
                    if (string.IsNullOrWhiteSpace(loaded.EditorFontFamily)) loaded.EditorFontFamily = "Consolas";
                    if (string.IsNullOrWhiteSpace(loaded.InterpreterPath)
                        || loaded.InterpreterPath.Equals("flux-runtime.exe", StringComparison.OrdinalIgnoreCase)
                        || loaded.InterpreterPath.Equals("Runtime/bin/flux-runtime.exe", StringComparison.OrdinalIgnoreCase)
                        || loaded.InterpreterPath.Equals("Runtime\\bin\\flux-runtime.exe", StringComparison.OrdinalIgnoreCase)
                        || loaded.InterpreterPath.Equals("Runtime/bin/flux-runtime", StringComparison.OrdinalIgnoreCase))
                        loaded.InterpreterPath = OperatingSystem.IsWindows() ? "Runtime/bin/flux.exe" : "Runtime/bin/flux";
                    return loaded;
                }
            }
        }
        catch
        {
            // A corrupt settings file must never prevent the editor from opening.
        }
        return new FluxStudioSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings persistence is best-effort; it should not crash the editor.
        }
    }
}
