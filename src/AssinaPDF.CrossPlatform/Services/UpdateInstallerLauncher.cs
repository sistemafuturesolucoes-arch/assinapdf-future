using System.Diagnostics;

namespace AssinaPDF.MacPrototype.Services;

public static class UpdateInstallerLauncher
{
    public static void Launch(string packagePath, string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        if (!File.Exists(packagePath))
            throw new FileNotFoundException("O instalador verificado não foi encontrado.", packagePath);

        string expectedExtension = platform switch
        {
            "windows-x64" => ".msi",
            "macos-x64" or "macos-arm64" => ".dmg",
            _ => throw new PlatformNotSupportedException(
                $"Não há instalador configurado para {platform}.")
        };

        if (!string.Equals(Path.GetExtension(packagePath), expectedExtension,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"O pacote selecionado não corresponde à plataforma {platform}.");

        if (platform == "windows-x64" && OperatingSystem.IsWindows())
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{packagePath}\" /passive /norestart",
                UseShellExecute = true,
                Verb = "runas"
            };

            var process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException("Não foi possível iniciar o instalador do Windows.");
            return;
        }

        if ((platform == "macos-x64" || platform == "macos-arm64") && OperatingSystem.IsMacOS())
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "open",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(packagePath);

            var process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException("Não foi possível abrir a imagem de disco do macOS.");
            return;
        }

        throw new PlatformNotSupportedException(
            "O instalador baixado não corresponde ao sistema operacional em execução.");
    }
}
