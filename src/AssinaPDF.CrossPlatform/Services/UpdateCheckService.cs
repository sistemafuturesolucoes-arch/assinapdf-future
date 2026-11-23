using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssinaPDF.MacPrototype.Services;

public sealed class UpdateCheckService
{
    private const string SupabaseUrl = "https://zcquhikpsqohrclflnjo.supabase.co";
    private const string PublishableKey = "sb_publishable_QGohhsJ9qwrmjzzT_QKqrw_dPQ_lRY7";
    private const long MaximumPackageBytes = 1_500_000_000;
    private static readonly string CurrentVersion = GetInstalledVersion();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HttpClient DownloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

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

    public async Task<string> DownloadAndVerifyAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!update.UpdateAvailable)
            throw new InvalidOperationException("Não há uma atualização disponível para baixar.");

        if (!Version.TryParse(update.LatestVersion, out _))
            throw new InvalidOperationException("A versão informada pelo serviço de atualizações é inválida.");

        if (string.IsNullOrWhiteSpace(update.DownloadUrl) ||
            !Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !downloadUri.IsDefaultPort ||
            !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !downloadUri.AbsolutePath.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(downloadUri.UserInfo))
            throw new InvalidOperationException(
                "O endereço do instalador não é um link HTTPS válido do GitHub Releases.");

        if (string.IsNullOrWhiteSpace(update.Sha256) ||
            update.Sha256.Length != 64 ||
            update.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("O SHA-256 publicado para este instalador é inválido.");

        string extension = update.Platform switch
        {
            "windows-x64" => ".msi",
            "macos-x64" or "macos-arm64" => ".dmg",
            _ => throw new PlatformNotSupportedException(
                $"Não há pacote de atualização configurado para {update.Platform}.")
        };

        if (!downloadUri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"O arquivo publicado não possui a extensão esperada ({extension}) para {update.Platform}.");

        byte[] expectedHash = Convert.FromHexString(update.Sha256);
        string version = update.LatestVersion!;
        string fileName = $"AssinaPDF-{version}-{update.Platform}{extension}";
        string updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FutureSolucoes", "AssinaPDF", "Updates");
        Directory.CreateDirectory(updateDirectory);

        string destinationPath = Path.Combine(updateDirectory, fileName);
        string temporaryPath = Path.Combine(
            updateDirectory, $".{fileName}.{Guid.NewGuid():N}.download");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            using var response = await DownloadHttp.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long declaredLength &&
                (declaredLength <= 0 || declaredLength > MaximumPackageBytes))
                throw new InvalidDataException("O tamanho informado para o instalador é inválido.");

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long totalBytes = 0;
                int bytesRead;

                while ((bytesRead = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > MaximumPackageBytes)
                        throw new InvalidDataException("O instalador excede o limite de tamanho permitido.");

                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

                    if (response.Content.Headers.ContentLength is long totalLength && totalLength > 0)
                        progress?.Report(Math.Min(1d, (double)totalBytes / totalLength));
                }

                if (totalBytes == 0)
                    throw new InvalidDataException("O servidor retornou um instalador vazio.");
            }

            byte[] actualHash;
            await using (var file = new FileStream(
                temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actualHash = await SHA256.HashDataAsync(file, cancellationToken);
            }

            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                throw new CryptographicException(
                    "A verificação de integridade falhou. O SHA-256 do instalador não corresponde ao publicado.");

            File.Move(temporaryPath, destinationPath, overwrite: true);
            progress?.Report(1d);
            return destinationPath;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // A limpeza não deve substituir o erro original do download ou da validação.
            }
        }
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
