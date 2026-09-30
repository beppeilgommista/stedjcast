using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stedjcast.Models;

namespace Stedjcast.Services;

public static class SettingsService
{
    // %LocalAppData% is always writable for the current user, while the executable may
    // live in a protected folder (e.g. Program Files) where saving would silently fail.
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stedjcast");

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            if (!string.IsNullOrEmpty(settings.ProtectedPassword))
            {
                settings.SourcePassword = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(settings.ProtectedPassword), null, DataProtectionScope.CurrentUser));
            }

            return settings;
        }
        catch (Exception exception)
        {
            LoggingService.Write($"Unable to read {FilePath}, using defaults: {exception.Message}");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        settings.ProtectedPassword = string.IsNullOrEmpty(settings.SourcePassword)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(settings.SourcePassword), null, DataProtectionScope.CurrentUser));

        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
