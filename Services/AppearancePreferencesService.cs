using System.IO;
using System.Text.Json;

namespace Accounting.Services;

public sealed class UserAppearanceSettings
{
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    public string? PreferredThemeName { get; set; }
}

public sealed class AppearancePreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppearancePreferencesService()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Accounting");

        _settingsPath = Path.Combine(root, "appearance.settings.json");
    }

    public UserAppearanceSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new UserAppearanceSettings();
            }

            var raw = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<UserAppearanceSettings>(raw) ?? new UserAppearanceSettings();
        }
        catch (Exception)
        {
            return new UserAppearanceSettings();
        }
    }

    public void Save(UserAppearanceSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception)
        {
            // Ignore preference persistence errors to keep login flow uninterrupted.
        }
    }
}
