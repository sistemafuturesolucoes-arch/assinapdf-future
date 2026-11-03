using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Aspose.Pdf.Text;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Downloads openly licensed Google Fonts into the application's private cache.
/// Fonts are never installed into the operating system.
/// </summary>
internal static class FontDownloadService
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FutureSolucoes", "AssinaPDF", "Fonts");
    private static bool _fontFolderRegistered;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("AssinaPDF-Future", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static bool IsAvailable(string familyName)
    {
        string family = CleanFamilyName(familyName);
        if (string.IsNullOrWhiteSpace(family))
            return false;

        try
        {
            _ = FontRepository.FindFont(family, FontStyles.Regular, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> EnsureAvailableAsync(Window owner, string familyName)
    {
        string family = CleanFamilyName(familyName);
        if (string.IsNullOrWhiteSpace(family) || IsAvailable(family))
            return true;

        bool accepted = await AskPermissionAsync(owner, family);
        if (!accepted)
            return false;

        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            RegisterCacheFolder();

            if (IsAvailable(family))
                return true;

            await DownloadGoogleFontAsync(family);
            FontRepository.LoadFonts();
            return IsAvailable(family);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string CleanFamilyName(string? value)
    {
        string family = (value ?? string.Empty).Trim();
        int subsetSeparator = family.IndexOf('+');
        if (subsetSeparator >= 0 && subsetSeparator < 8)
            family = family[(subsetSeparator + 1)..];
        return family.Trim();
    }

    private static void RegisterCacheFolder()
    {
        if (_fontFolderRegistered)
            return;

        FontRepository.Sources.Add(new FolderFontSource(CacheDirectory));
        _fontFolderRegistered = true;
    }

    private static async Task DownloadGoogleFontAsync(string family)
    {
        string slug = new string(family
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (slug.Length == 0 || slug.Length > 80)
            throw new InvalidOperationException("O nome da fonte não é válido para pesquisa online.");

        string apiUrl = $"https://api.github.com/repos/google/fonts/contents/ofl/{Uri.EscapeDataString(slug)}";
        using var response = await Http.GetAsync(apiUrl);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"A fonte '{family}' não foi encontrada no catálogo Google Fonts (HTTP {(int)response.StatusCode}).");

        using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        var entries = json.RootElement.EnumerateArray().ToArray();

        bool hasOpenFontLicense = entries.Any(item =>
            item.TryGetProperty("name", out var name) &&
            string.Equals(name.GetString(), "OFL.txt", StringComparison.OrdinalIgnoreCase));

        if (!hasOpenFontLicense)
            throw new InvalidOperationException(
                $"A fonte '{family}' não apresentou licença OFL no catálogo. Por segurança, o download foi bloqueado.");

        var fontEntries = entries.Where(item =>
            item.TryGetProperty("name", out var name) &&
            name.GetString() is { } fileName &&
            fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) &&
            item.TryGetProperty("download_url", out var url) &&
            url.ValueKind == JsonValueKind.String).ToList();

        var staticDirectory = entries.FirstOrDefault(item =>
            item.TryGetProperty("name", out var name) &&
            string.Equals(name.GetString(), "static", StringComparison.OrdinalIgnoreCase) &&
            item.TryGetProperty("type", out var type) &&
            string.Equals(type.GetString(), "dir", StringComparison.OrdinalIgnoreCase));

        if (staticDirectory.ValueKind == JsonValueKind.Object &&
            staticDirectory.TryGetProperty("url", out var staticUrl))
        {
            using var staticResponse = await Http.GetAsync(staticUrl.GetString());
            if (staticResponse.IsSuccessStatusCode)
            {
                using var staticStream = await staticResponse.Content.ReadAsStreamAsync();
                using var staticJson = await JsonDocument.ParseAsync(staticStream);
                var staticFonts = staticJson.RootElement.EnumerateArray()
                    .Where(item =>
                        item.TryGetProperty("name", out var name) &&
                        name.GetString() is { } fileName &&
                        fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) &&
                        item.TryGetProperty("download_url", out var url) &&
                        url.ValueKind == JsonValueKind.String)
                    .ToList();

                if (staticFonts.Count > 0)
                    fontEntries = staticFonts;
            }
        }

        if (fontEntries.Count == 0)
            throw new InvalidOperationException(
                $"O catálogo não disponibilizou uma fonte TrueType compatível para '{family}'.");

        // Download only a small set of faces; the application cache is shared by Windows and macOS.
        foreach (var entry in fontEntries.Take(8))
        {
            string fileName = entry.GetProperty("name").GetString()!;
            string downloadUrl = entry.GetProperty("download_url").GetString()!;
            string safeName = string.Concat(fileName.Select(character =>
                char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_'));
            string destination = Path.Combine(CacheDirectory, slug + "-" + safeName);

            if (File.Exists(destination) && new FileInfo(destination).Length > 1024)
                continue;

            using var fontResponse = await Http.GetAsync(
                downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            fontResponse.EnsureSuccessStatusCode();

            if (fontResponse.Content.Headers.ContentLength is > 20_000_000)
                throw new InvalidOperationException("O arquivo da fonte excede o limite de segurança de 20 MB.");

            await using var input = await fontResponse.Content.ReadAsStreamAsync();
            await using var output = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output);
            if (output.Length < 1024 || output.Length > 20_000_000)
            {
                output.Close();
                File.Delete(destination);
                throw new InvalidDataException("O arquivo baixado não parece ser uma fonte TrueType válida.");
            }
        }
    }

    private static async Task<bool> AskPermissionAsync(Window owner, string family)
    {
        var dialog = new Window
        {
            Title = "Fonte não encontrada",
            Width = 460,
            Height = 220,
            MinWidth = 420,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var message = new TextBlock
        {
            Text = $"A fonte '{family}' não está disponível neste computador. Deseja buscar e baixar uma versão com licença aberta no catálogo Google Fonts? O arquivo ficará somente na pasta interna do AssinaPDF; nenhuma fonte será instalada no Windows ou macOS.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children =
            {
                new Button { Content = "Agora não", MinWidth = 100 },
                new Button { Content = "Buscar e baixar", MinWidth = 130 }
            }
        };

        ((Button)buttons.Children[0]).Click += (_, _) => dialog.Close(false);
        ((Button)buttons.Children[1]).Click += (_, _) => dialog.Close(true);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 18,
            Children = { message, buttons }
        };

        return await dialog.ShowDialog<bool>(owner);
    }
}
