using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Aspose.Pdf.Text;
using Avalonia.Media;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Bridges Avalonia's platform font discovery with Aspose's PDF font resolver.
/// The UI list comes from the operating system; font folders are also registered
/// with Aspose so that selected system fonts can be resolved for PDF output.
/// </summary>
internal static class InstalledFontCatalogService
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> RegisteredDirectories =
        new(StringComparer.OrdinalIgnoreCase);

    internal sealed record FontFaceInfo(
        string FamilyName,
        int Weight,
        string Style,
        string Stretch);

    internal sealed record FontFamilyInfo(
        string Name,
        IReadOnlyList<FontFaceInfo> Faces);

    public static IReadOnlyList<string> GetInstalledFamilyNames()
    {
        RegisterSystemFontDirectories();

        return FontManager.Current.SystemFonts
            .Select(family => family.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<FontFamilyInfo> GetInstalledFamiliesWithFaces()
    {
        RegisterSystemFontDirectories();

        return FontManager.Current.SystemFonts
            .Where(family => !string.IsNullOrWhiteSpace(family.Name))
            .Select(family => new FontFamilyInfo(
                family.Name,
                family.FamilyTypefaces
                    .Select(face => new FontFaceInfo(
                        family.Name,
                        (int)face.Weight,
                        face.Style.ToString(),
                        face.Stretch.ToString()))
                    .Distinct()
                    .OrderBy(face => face.Weight)
                    .ThenBy(face => face.Style, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(family => family.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static int RegisterSystemFontDirectories()
    {
        string windowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = BuildCandidateDirectories(
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS(),
            windowsFonts,
            localAppData,
            userProfile);

        int added = 0;
        lock (Sync)
        {
            foreach (string directory in candidates)
            {
                if (!Directory.Exists(directory))
                    continue;

                string key;
                try
                {
                    key = Path.GetFullPath(directory);
                }
                catch
                {
                    continue;
                }

                if (!RegisteredDirectories.Add(key))
                    continue;

                try
                {
                    FontRepository.Sources.Add(new FolderFontSource(key));
                    added++;
                }
                catch (Exception ex)
                {
                    RegisteredDirectories.Remove(key);
                    Debug.WriteLine($"AssinaPDF: não foi possível registrar a pasta de fontes '{key}': {ex.Message}");
                }
            }
        }

        return added;
    }

    internal static IReadOnlyList<string> BuildCandidateDirectories(
        bool isWindows,
        bool isMacOS,
        string? windowsFonts,
        string? localAppData,
        string? userProfile)
    {
        var paths = new List<string>();

        if (isWindows)
        {
            AddIfNotBlank(paths, windowsFonts);
            if (!string.IsNullOrWhiteSpace(localAppData))
                paths.Add(Path.Combine(localAppData, "Microsoft", "Windows", "Fonts"));
        }
        else if (isMacOS)
        {
            paths.Add("/System/Library/Fonts");
            paths.Add("/Library/Fonts");
            if (!string.IsNullOrWhiteSpace(userProfile))
                paths.Add(Path.Combine(userProfile, "Library", "Fonts"));
        }

        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddIfNotBlank(ICollection<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            paths.Add(path);
    }
}
