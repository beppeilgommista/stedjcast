using System.IO;

namespace Stedjcast.Services;

public sealed record Vst3PluginInfo(string Path, string DisplayName);

/// <summary>Scans a folder for VST3 plugins.</summary>
public static class Vst3Catalog
{
    public static IReadOnlyList<Vst3PluginInfo> Scan(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return Array.Empty<Vst3PluginInfo>();

        var results = new List<Vst3PluginInfo>();
        ScanFolder(folder, results);
        return results
            .OrderBy(plugin => plugin.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanFolder(string folder, List<Vst3PluginInfo> results)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(folder); }
        catch { return; }

        foreach (var entry in entries)
        {
            if (entry.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
            {
                // A .vst3 bundle is a folder: don't descend into it, treat it as a single
                // plugin (like a flat .vst3 file on Windows).
                results.Add(new Vst3PluginInfo(entry, Path.GetFileNameWithoutExtension(entry)));
            }
            else if (entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                // Some VST3 plugins ship as .dll, but most .dll files in these folders are
                // legacy VST2 plugins this host can never load: index only those that
                // actually export the VST3 entry point (checked by reading the export
                // table, without running any of the file's code).
                if (PeModuleInspector.ExportsFunction(entry, "GetPluginFactory"))
                    results.Add(new Vst3PluginInfo(entry, Path.GetFileNameWithoutExtension(entry)));
            }
            else if (Directory.Exists(entry))
            {
                ScanFolder(entry, results);
            }
        }
    }
}
