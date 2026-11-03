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
/// Downloads openly licensed desktop font files into the application's private cache.
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

            var failures = new List<string>();
            try
            {
                await DownloadFontsourceAsync(family);
                FontRepository.LoadFonts();
                if (IsAvailable(family))
                    return true;
            }
            catch (Exception ex)
            {
                failures.Add("Fontsource: " + ex.Message);
            }

            try
            {
                await DownloadGoogleFontAsync(family);
                FontRepository.LoadFonts();
                if (IsAvailable(family))
                    return true;
            }
            catch (Exception ex)
            {
                failures.Add("Google Fonts: " + ex.Message);
            }

            throw new InvalidOperationException(
                $"Não foi possível localizar uma versão compatível da fonte '{family}'. " +
                string.Join(" | ", failures));
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string CleanFamilyName(string? value)
    {
        string family = (value ?? string.Empty).Trim();

        // PDF subset prefixes are exactly six uppercase ASCII letters plus '+'
        // (for example ABCDEF+Arial). Never strip names such as CIDFONT+F2.
        int separator = family.IndexOf('+');
        if (separator == 6 && family[..6].All(character =>
                character is >= 'A' and <= 'Z'))
            family = family[7..];

        return family.Trim();
    }

    private static void RegisterCacheFolder()
    {
        if (_fontFolderRegistered)
            return;

        FontRepository.Sources.Add(new FolderFontSource(CacheDirectory));
        _fontFolderRegistered = true;
    }

    private static async Task DownloadFontsourceAsync(string family)
    {
        string searchUrl = "https://api.fontsource.org/v1/fonts?family=" +
                           Uri.EscapeDataString(family);
        using var searchResponse = await Http.GetAsync(searchUrl);
        searchResponse.EnsureSuccessStatusCode();
        using var searchStream = await searchResponse.Content.ReadAsStreamAsync();
        using var searchJson = await JsonDocument.ParseAsync(searchStream);

        JsonElement match = searchJson.RootElement.EnumerateArray()
            .FirstOrDefault(item =>
                item.TryGetProperty("family", out var name) &&
                string.Equals(name.GetString(), family, StringComparison.OrdinalIgnoreCase));

        if (match.ValueKind != JsonValueKind.Object ||
            !match.TryGetProperty("id", out var idElement))
            throw new InvalidOperationException("família não encontrada no catálogo.");

        string id = idElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id) ||
            id.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
            throw new InvalidDataException("identificador de fonte inválido.");

        // Check the package's declared licence before retrieving any font binary.
        string packageUrl = $"https://registry.npmjs.org/@fontsource%2f{id}/latest";
        using var packageResponse = await Http.GetAsync(packageUrl);
        packageResponse.EnsureSuccessStatusCode();
        using var packageStream = await packageResponse.Content.ReadAsStreamAsync();
        using var packageJson = await JsonDocument.ParseAsync(packageStream);
        string license = packageJson.RootElement.TryGetProperty("license", out var licenseElement)
            ? licenseElement.ValueKind == JsonValueKind.String
                ? licenseElement.GetString() ?? string.Empty
                : licenseElement.ToString()
            : string.Empty;

        string normalizedLicense = license.Replace(" ", string.Empty).ToUpperInvariant();
        if (normalizedLicense is not ("OFL-1.1" or "APACHE-2.0" or "MIT" or "UFL-1.0"))
            throw new InvalidOperationException(
                $"licença '{license}' não está na lista de licenças abertas permitidas.");

        string detailUrl = $"https://api.fontsource.org/v1/fonts/{Uri.EscapeDataString(id)}";
        using var detailResponse = await Http.GetAsync(detailUrl);
        detailResponse.EnsureSuccessStatusCode();
        using var detailStream = await detailResponse.Content.ReadAsStreamAsync();
        using var detailJson = await JsonDocument.ParseAsync(detailStream);

        var ttfUrls = new List<string>();
        CollectTtfUrls(detailJson.RootElement, string.Empty, ttfUrls);
        var preferred = ttfUrls
            .Where(url => url.Contains("latin", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(url => url.Contains("400-normal", StringComparison.OrdinalIgnoreCase))
            .ThenBy(url => url, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        if (preferred.Length == 0)
            throw new InvalidOperationException(
                "o catálogo não forneceu arquivos TTF para caracteres latinos; WOFF/WOFF2 não são aceitos pelo mecanismo PDF.");

        await DownloadFontFilesAsync(preferred, id);
    }

    private static void CollectTtfUrls(JsonElement element, string path, List<string> urls)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "ttf", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    string? url = property.Value.GetString();
                    if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                        parsed.Scheme == Uri.UriSchemeHttps)
                        urls.Add(url!);
                }
                else
                {
                    CollectTtfUrls(property.Value, path + "/" + property.Name, urls);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectTtfUrls(item, path, urls);
        }
    }

    private static async Task DownloadGoogleFontAsync(string family)
    {
        string slug = new string(family
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (slug.Length == 0 || slug.Length > 80)
            throw new InvalidOperationException("nome de fonte inválido para pesquisa.");

        string apiUrl = $"https://api.github.com/repos/google/fonts/contents/ofl/{Uri.EscapeDataString(slug)}";
        using var response = await Http.GetAsync(apiUrl);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"catálogo respondeu HTTP {(int)response.StatusCode}.");

        using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        var entries = json.RootElement.EnumerateArray().ToArray();

        bool hasOpenFontLicense = entries.Any(item =>
            item.TryGetProperty("name", out var name) &&
            string.Equals(name.GetString(), "OFL.txt", StringComparison.OrdinalIgnoreCase));

        if (!hasOpenFontLicense)
            throw new InvalidOperationException("não foi encontrada licença OFL para esta família.");

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
            throw new InvalidOperationException("nenhum arquivo TTF compatível foi disponibilizado.");

        var urls = fontEntries.Take(8)
            .Select(entry => entry.GetProperty("download_url").GetString()!)
            .ToArray();
        await DownloadFontFilesAsync(urls, slug);
    }

    private static async Task DownloadFontFilesAsync(IEnumerable<string> urls, string prefix)
    {
        foreach (string downloadUrl in urls)
        {
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
                continue;

            string fileName = Path.GetFileName(uri.AbsolutePath);
            string safeName = string.Concat(fileName.Select(character =>
                char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_'));
            string destination = Path.Combine(CacheDirectory, prefix + "-" + safeName);

            if (File.Exists(destination) && new FileInfo(destination).Length > 1024)
                continue;

            using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > 20_000_000)
                throw new InvalidOperationException("arquivo de fonte excede o limite de 20 MB.");

            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output);

            if (output.Length < 1024 || output.Length > 20_000_000)
            {
                output.Close();
                File.Delete(destination);
                throw new InvalidDataException("o arquivo baixado não parece ser uma fonte TTF válida.");
            }
        }
    }

    private static async Task<bool> AskPermissionAsync(Window owner, string family)
    {
        var dialog = new Window
        {
            Title = "Fonte não encontrada",
            Width = 480,
            Height = 230,
            MinWidth = 420,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var message = new TextBlock
        {
            Text = $"A fonte '{family}' não está disponível neste computador. Deseja buscar uma versão com licença aberta nos catálogos Fontsource e Google Fonts? O arquivo ficará somente na pasta interna do AssinaPDF; nenhuma fonte será instalada no Windows ou macOS.",
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
