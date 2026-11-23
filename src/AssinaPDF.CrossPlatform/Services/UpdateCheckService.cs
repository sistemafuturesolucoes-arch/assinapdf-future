using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AssinaPDF.MacPrototype.Services;

public sealed class UpdateCheckService
{
    private const string SupabaseUrl = "https://zcquhikpsqohrclflnjo.supabase.co";
    private const string PublishableKey = "sb_publishable_QGohhsJ9qwrmjzzT_QKqrw_dPQ_lRY7";
    private static readonly string CurrentVersion = GetInstalledVersion();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static UpdateCheckService Instance { get; } = new();
    private UpdateCheckService() { }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var platform = GetPlatform();
        var payload = JsonSerializer.Serialize(new
        {
            platform,
            channel = "stable",
            current_version = CurrentVersion
        });

        using var request = new HttpRequestMessage(HttpMethod.Post,
            SupabaseUrl + "/functions/v1/assinapdf-updates");
        request.Headers.TryAddWithoutValidation("apikey", PublishableKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadError(json));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        bool available = root.TryGetProperty("update_available", out var availableElement) &&
            availableElement.ValueKind == JsonValueKind.True;

        if (!available || !root.TryGetProperty("release", out var release) ||
            release.ValueKind != JsonValueKind.Object)
            return new UpdateCheckResult(CurrentVersion, platform, false, null, null, null, false, null);

        return new UpdateCheckResult(
            CurrentVersion,
            platform,
            true,
            GetString(release, "version"),
            GetString(release, "download_url"),
            GetString(release, "sha256"),
            release.TryGetProperty("is_mandatory", out var mandatory) && mandatory.ValueKind == JsonValueKind.True,
            GetString(release, "release_notes"));
    }

    private static string GetInstalledVersion()
    {
        var version = typeof(UpdateCheckService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException(
                "A versão do AssinaPDF não foi definida nos metadados do aplicativo.");

        // O SDK pode acrescentar metadados de compilação (+commit). A API de versões
        // recebe somente a versão comercial, por exemplo 0.4.2.
        var metadataSeparator = version.IndexOf('+');
        if (metadataSeparator >= 0)
            version = version[..metadataSeparator];

        if (!Version.TryParse(version, out _))
            throw new InvalidOperationException(
                $"A versão instalada do AssinaPDF é inválida: {version}");

        return version;
    }

    private static string GetPlatform()
    {
        if (OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.X64)
            return "windows-x64";
        if (OperatingSystem.IsMacOS())
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "macos-arm64",
                System.Runtime.InteropServices.Architecture.X64 => "macos-x64",
                _ => throw new PlatformNotSupportedException("Arquitetura macOS não suportada.")
            };
        throw new PlatformNotSupportedException("Atualizações disponíveis para Windows x64 e macOS.");
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string ReadError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
                return error.GetString() ?? "Não foi possível verificar atualizações.";
        }
        catch { }
        return "Não foi possível verificar atualizações. Confira sua conexão e tente novamente.";
    }
}

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string Platform,
    bool UpdateAvailable,
    string? LatestVersion,
    string? DownloadUrl,
    string? Sha256,
    bool IsMandatory,
    string? ReleaseNotes);
