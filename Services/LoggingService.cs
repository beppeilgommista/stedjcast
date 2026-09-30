using System.IO;

namespace Stedjcast.Services;

public static class LoggingService
{
    public static bool Enabled { get; set; }

    public static void Write(string message)
    {
        if (!Enabled)
            return;

        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            File.AppendAllText(
                Path.Combine(SettingsService.Folder, "Stedjcast.log"),
                $"[{DateTime.Now:O}] {message}\r\n");
        }
        catch
        {
        }
    }
}
