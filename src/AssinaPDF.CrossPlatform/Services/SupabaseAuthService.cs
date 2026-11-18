using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssinaPDF.MacPrototype.Services;

public sealed class SupabaseAuthService
{
    private const string SupabaseUrl = "https://zcquhikpsqohrclflnjo.supabase.co";
    private const string PublishableKey = "sb_publishable_QGohhsJ9qwrmjzzT_QKqrw_dPQ_lRY7";
    private const string KeychainService = "com.futuresolucoes.assinapdf";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    public static SupabaseAuthService Instance { get; } = new();

    private string? _accessToken;
    private string? _refreshToken;
    private string? _email;
    private bool _initialized;

    public bool IsSignedIn => !string.IsNullOrWhiteSpace(_accessToken);
    public string? Email => _email;
    private SupabaseAuthService() { }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var saved = await CredentialStore.ReadAsync();
            if (string.IsNullOrWhiteSpace(saved)) return;
            using var document = JsonDocument.Parse(saved);
            _refreshToken = document.RootElement.GetProperty("refresh_token").GetString();
            _email = document.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
            if (!string.IsNullOrWhiteSpace(_refreshToken)) await RefreshSessionAsync();
        }
        catch
        {
            _accessToken = null;
            _refreshToken = null;
            _email = null;
        }
    }

    public async Task<string> SignUpAsync(string name, string email, string password)
    {
        ValidateCredentials(email, password);
        using var response = await SendAuthAsync("/auth/v1/signup",
            new { email = email.Trim(), password, data = new { full_name = name.Trim() } });
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(json, "Não foi possível criar sua conta."));
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("access_token", out _))
        {
            SetSession(doc.RootElement);
            await SaveSessionAsync();
            return "Conta criada e sessão iniciada. Agora você pode ativar sua chave Premium.";
        }
        return "Conta criada. Confirme seu e-mail pelo link enviado e depois entre no AssinaPDF.";
    }

    public async Task<string> SignInAsync(string email, string password)
    {
        ValidateCredentials(email, password);
        using var response = await SendAuthAsync("/auth/v1/token?grant_type=password",
            new { email = email.Trim(), password });
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(json, "E-mail ou senha inválidos."));
        using var doc = JsonDocument.Parse(json);
        SetSession(doc.RootElement);
        await SaveSessionAsync();
        return "Acesso realizado com sucesso.";
    }

    public async Task<string> RedeemKeyAsync(string key)
    {
        await EnsureSessionAsync();
        using var response = await SendFunctionAsync(new { action = "redeem_key", key });
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(json, "Não foi possível ativar a chave."));
        return "Premium ativado com sucesso!";
    }


    public async Task<string> CreateMonthlyCheckoutAsync()
    {
        await EnsureSessionAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, SupabaseUrl + "/functions/v1/assinapdf-asaas");
        request.Headers.Add("apikey", PublishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { action = "create_monthly_checkout" }), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadError(json, "A contratação online ainda não está disponível."));
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("checkout_url", out var url) || string.IsNullOrWhiteSpace(url.GetString()))
            throw new InvalidOperationException("O Asaas não retornou o link de pagamento.");
        return url.GetString()!;
    }

    public async Task<bool> HasPremiumAsync()
    {
        await EnsureSessionAsync();
        if (!IsSignedIn) return false;
        using var response = await SendFunctionAsync(new { action = "license_status" });
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return false;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("license", out var license) || license.ValueKind == JsonValueKind.Null) return false;
        if (!license.TryGetProperty("status", out var status) || status.GetString() != "active") return false;
        if (license.TryGetProperty("valid_until", out var until) && until.ValueKind != JsonValueKind.Null
            && DateTimeOffset.TryParse(until.GetString(), out var expiry) && expiry <= DateTimeOffset.UtcNow) return false;
        if (!license.TryGetProperty("assinapdf_plans", out var plan) || plan.ValueKind != JsonValueKind.Object) return false;
        return plan.TryGetProperty("code", out var code) && string.Equals(code.GetString(), "premium", StringComparison.OrdinalIgnoreCase);
    }


    public async Task RegisterCurrentDeviceAsync()
    {
        await EnsureSessionAsync();
        var fingerprint = await CredentialStore.GetDeviceFingerprintAsync();
        using var response = await SendFunctionAsync(new
        {
            action = "register_device",
            device_fingerprint = fingerprint,
            device_label = Environment.MachineName,
            platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "unknown",
            app_version = "1.0.0"
        });
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadError(json, "Não foi possível validar este dispositivo."));
    }

    public async Task SignOutAsync()
    {
        if (IsSignedIn)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, SupabaseUrl + "/auth/v1/logout");
                request.Headers.Add("apikey", PublishableKey);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                using var response = await Http.SendAsync(request);
            }
            catch { }
        }
        _accessToken = null; _refreshToken = null; _email = null;
        await CredentialStore.DeleteAsync();
    }

    private async Task EnsureSessionAsync()
    {
        if (!_initialized) await InitializeAsync();
        if (string.IsNullOrWhiteSpace(_accessToken) && !string.IsNullOrWhiteSpace(_refreshToken))
            await RefreshSessionAsync();
        if (string.IsNullOrWhiteSpace(_accessToken))
            throw new InvalidOperationException("Entre na sua conta AssinaPDF para continuar.");
    }

    private async Task RefreshSessionAsync()
    {
        if (string.IsNullOrWhiteSpace(_refreshToken)) return;
        using var response = await SendAuthAsync("/auth/v1/token?grant_type=refresh_token", new { refresh_token = _refreshToken });
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _accessToken = null; _refreshToken = null;
            await CredentialStore.DeleteAsync();
            return;
        }
        using var doc = JsonDocument.Parse(json);
        SetSession(doc.RootElement);
        await SaveSessionAsync();
    }

    private void SetSession(JsonElement root)
    {
        _accessToken = root.GetProperty("access_token").GetString();
        _refreshToken = root.GetProperty("refresh_token").GetString();
        if (root.TryGetProperty("user", out var user) && user.TryGetProperty("email", out var email))
            _email = email.GetString();
    }

    private async Task SaveSessionAsync()
    {
        if (!string.IsNullOrWhiteSpace(_refreshToken))
            await CredentialStore.WriteAsync(JsonSerializer.Serialize(new { refresh_token = _refreshToken, email = _email }));
    }

    private static async Task<HttpResponseMessage> SendAuthAsync(string path, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SupabaseUrl + path);
        request.Headers.Add("apikey", PublishableKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await Http.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendFunctionAsync(object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SupabaseUrl + "/functions/v1/assinapdf-license");
        request.Headers.Add("apikey", PublishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await Http.SendAsync(request);
    }

    private static string ReadError(string json, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var name in new[] { "msg", "message", "error_description", "error" })
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? fallback;
        }
        catch { }
        return fallback;
    }

    private static void ValidateCredentials(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            throw new InvalidOperationException("Informe um e-mail válido.");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            throw new InvalidOperationException("A senha deve ter pelo menos 6 caracteres.");
    }

    private static class CredentialStore
    {
        private const string KeychainServiceName = "com.futuresolucoes.assinapdf";
        private static string WindowsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FutureSolucoes", "AssinaPDF", "session.bin");


        public static async Task<string> GetDeviceFingerprintAsync()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FutureSolucoes", "AssinaPDF");
            var path = Path.Combine(directory, "device.id");
            Directory.CreateDirectory(directory);
            if (File.Exists(path))
            {
                var existing = (await File.ReadAllTextAsync(path)).Trim();
                if (existing.Length >= 32) return existing;
            }
            var fingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await File.WriteAllTextAsync(path, fingerprint);
            return fingerprint;
        }

        public static async Task WriteAsync(string value)
        {
            if (OperatingSystem.IsWindows())
            {
                var clear = Encoding.UTF8.GetBytes(value);
                var encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(WindowsPath)!);
                await File.WriteAllBytesAsync(WindowsPath, encrypted);
                CryptographicOperations.ZeroMemory(clear);
                return;
            }
            if (OperatingSystem.IsMacOS())
            {
                await RunSecurityAsync(new[] { "add-generic-password", "-U", "-s", KeychainServiceName, "-a", "current-user", "-w", value });
                return;
            }
            throw new PlatformNotSupportedException("O armazenamento seguro está disponível no Windows e macOS.");
        }

        public static async Task<string?> ReadAsync()
        {
            if (OperatingSystem.IsWindows())
            {
                if (!File.Exists(WindowsPath)) return null;
                var encrypted = await File.ReadAllBytesAsync(WindowsPath);
                var clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                try { return Encoding.UTF8.GetString(clear); }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            if (OperatingSystem.IsMacOS())
            {
                var result = await RunSecurityAsync(new[] { "find-generic-password", "-s", KeychainServiceName, "-a", "current-user", "-w" }, true);
                return result.ExitCode == 0 ? result.Output.Trim() : null;
            }
            return null;
        }

        public static async Task DeleteAsync()
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(WindowsPath)) File.Delete(WindowsPath);
            }
            else if (OperatingSystem.IsMacOS())
                await RunSecurityAsync(new[] { "delete-generic-password", "-s", KeychainServiceName, "-a", "current-user" }, true);
        }

        private static async Task<(int ExitCode, string Output)> RunSecurityAsync(string[] arguments, bool allowFailure = false)
        {
            var start = new ProcessStartInfo("/usr/bin/security")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível acessar as Chaves do macOS.");
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 && !allowFailure)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Falha no armazenamento seguro do macOS." : error.Trim());
            return (process.ExitCode, output);
        }
    }
}