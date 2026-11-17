using System.Diagnostics;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Converte documentos Word e planilhas Excel para PDF usando o LibreOffice
/// instalado localmente. Os arquivos de origem nunca são alterados.
/// </summary>
public sealed class OfficeToPdfService
{
    private static readonly HashSet<string> WordExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".odt", ".rtf" };
    private static readonly HashSet<string> ExcelExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".xltm", ".ods", ".csv" };

    public static bool IsSupported(string path) =>
        WordExtensions.Contains(Path.GetExtension(path)) ||
        ExcelExtensions.Contains(Path.GetExtension(path));

    public static string GetDocumentKind(string path)
    {
        var extension = Path.GetExtension(path);
        if (WordExtensions.Contains(extension)) return "Word";
        if (ExcelExtensions.Contains(extension)) return "Excel";
        throw new NotSupportedException($"Formato não suportado: {extension}");
    }

    public static string? FindLibreOffice()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            candidates.Add(Path.Combine(programFiles, "LibreOffice", "program", "soffice.exe"));
            candidates.Add(Path.Combine(programFilesX86, "LibreOffice", "program", "soffice.exe"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/LibreOffice.app/Contents/MacOS/soffice");
            candidates.Add("/opt/homebrew/bin/soffice");
            candidates.Add("/usr/local/bin/soffice");
        }
        else if (OperatingSystem.IsLinux())
        {
            candidates.Add("/usr/bin/soffice");
            candidates.Add("/usr/bin/libreoffice");
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(directory, OperatingSystem.IsWindows() ? "soffice.exe" : "soffice"));
                candidates.Add(Path.Combine(directory, OperatingSystem.IsWindows() ? "libreoffice.exe" : "libreoffice"));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<string> ConvertToPdfAsync(
        string sourcePath,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported(sourcePath))
            throw new NotSupportedException("Selecione um arquivo Word ou Excel compatível.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("O arquivo de origem não foi encontrado.", sourcePath);

        var executable = FindLibreOffice();
        if (executable is null)
            throw new InvalidOperationException(
                "O LibreOffice não foi encontrado. Instale o LibreOffice no computador e tente novamente.");

        Directory.CreateDirectory(workingDirectory);
        var profileDirectory = Path.Combine(workingDirectory, "lo-profile");
        Directory.CreateDirectory(profileDirectory);
        var outputDirectory = Path.Combine(workingDirectory, "output");
        Directory.CreateDirectory(outputDirectory);

        var extension = Path.GetExtension(sourcePath);
        var exportFilter = WordExtensions.Contains(extension)
            ? "pdf:writer_pdf_Export"
            : "pdf:calc_pdf_Export";

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--invisible");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--nodefault");
        startInfo.ArgumentList.Add("--nolockcheck");
        startInfo.ArgumentList.Add($"-env:UserInstallation={new Uri(profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri}");
        startInfo.ArgumentList.Add("--convert-to");
        startInfo.ArgumentList.Add(exportFilter);
        startInfo.ArgumentList.Add("--outdir");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add(Path.GetFullPath(sourcePath));

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Não foi possível iniciar o LibreOffice.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("A conversão excedeu o limite de 3 minutos.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"O LibreOffice não conseguiu converter o documento. {stderr} {stdout}".Trim());

        var outputPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(sourcePath) + ".pdf");
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidDataException(
                $"O LibreOffice não gerou o PDF esperado. {stderr} {stdout}".Trim());

        return outputPath;
    }
}
