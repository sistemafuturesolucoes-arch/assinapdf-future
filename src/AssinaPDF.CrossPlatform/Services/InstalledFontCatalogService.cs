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

    /// <summary>
    /// Returns physical TTF/OTF files whose filenames may belong to the requested
    /// family. The final family/style decision is made from the font program's
    /// internal names after FontRepository.OpenFont, not from the filename alone.
    /// </summary>
    internal static IReadOnlyList<string> GetCandidateFontFiles(string familyName)
    {
        string normalizedFamily = NormalizeFontFileToken(familyName);
        if (normalizedFamily.Length < 2)
            return Array.Empty<string>();

        string prefix = normalizedFamily[..Math.Min(4, normalizedFamily.Length)];
        string windowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var directories = BuildCandidateDirectories(
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS(),
            windowsFonts,
            localAppData,
            userProfile).ToList();

        if (!string.IsNullOrWhiteSpace(localAppData))
            directories.Add(Path.Combine(localAppData, "FutureSolucoes", "AssinaPDF", "Fonts"));

        return EnumerateCandidateFontFiles(directories, familyName);
    }

    internal static IReadOnlyList<string> EnumerateCandidateFontFiles(
        IEnumerable<string> directories,
        string familyName)
    {
        string normalizedFamily = NormalizeFontFileToken(familyName);
        if (normalizedFamily.Length < 2)
            return Array.Empty<string>();

        string prefix = normalizedFamily[..Math.Min(4, normalizedFamily.Length)];
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
                continue;

            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    string extension = Path.GetExtension(file);
                    if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string token = NormalizeFontFileToken(Path.GetFileNameWithoutExtension(file));
                    if (token.Contains(prefix, StringComparison.Ordinal))
                        files.Add(file);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AssinaPDF: não foi possível enumerar fontes em '{directory}': {ex.Message}");
            }
        }

        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool FontNameMatchesRequestedStyle(
        string? fontName,
        string requestedFamily,
        bool wantsBold,
        bool wantsItalic,
        bool fallbackBold = false,
        bool fallbackItalic = false)
    {
        string family = NormalizeFontFileToken(requestedFamily);
        string normalized = NormalizeFontFileToken(fontName);
        if (family.Length == 0 || !normalized.StartsWith(family, StringComparison.Ordinal))
            return false;

        string suffix = normalized[family.Length..];
        if (suffix.EndsWith("mt", StringComparison.Ordinal))
            suffix = suffix[..^2];
        if (suffix.EndsWith("ps", StringComparison.Ordinal))
            suffix = suffix[..^2];

        bool hasBoldMarker =
            suffix.Contains("bold", StringComparison.Ordinal) ||
            suffix.Contains("semibold", StringComparison.Ordinal) ||
            suffix.Contains("demibold", StringComparison.Ordinal) ||
            suffix.Contains("black", StringComparison.Ordinal) ||
            suffix.Contains("heavy", StringComparison.Ordinal);
        bool hasItalicMarker =
            suffix.Contains("italic", StringComparison.Ordinal) ||
            suffix.Contains("oblique", StringComparison.Ordinal) ||
            suffix.Contains("kursiv", StringComparison.Ordinal);

        bool hasExplicitStyle = hasBoldMarker || hasItalicMarker;
        bool actualBold = hasExplicitStyle ? hasBoldMarker : fallbackBold;
        bool actualItalic = hasExplicitStyle ? hasItalicMarker : fallbackItalic;
        return actualBold == wantsBold && actualItalic == wantsItalic;
    }

    internal static string NormalizeFontFileToken(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant).ToArray());

    private static void AddIfNotBlank(ICollection<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            paths.Add(path);
    }
}
