using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AssinaPDF.MacPrototype.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace AssinaPDF.MacPrototype.Services;

public enum NfeZipGroupingMode
{
    Recipient,
    Issuer
}

public sealed record NfeZipDocument(
    string SourcePath,
    NfeDocumentSummary? Summary,
    string? PdfPath,
    string? Error,
    bool IsDuplicate = false,
    string? DuplicateReason = null);

public static class NfeBatchZipExporter
{
    private sealed record ValidDocument(
        NfeZipDocument Document,
        string CompanyKey,
        string CompanyFolder,
        string Period);

    public static void Create(
        string outputZipPath,
        IEnumerable<NfeZipDocument> documents,
        NfeZipGroupingMode groupingMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputZipPath);
        ArgumentNullException.ThrowIfNull(documents);

        var items = documents.ToList();

        if (items.Count == 0)
            throw new InvalidOperationException(
                "Não existem registros para exportar.");

        string output = Path.GetFullPath(outputZipPath);
        string? outputDirectory = Path.GetDirectoryName(output);

        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new InvalidOperationException(
                "Não foi possível identificar a pasta de destino.");

        Directory.CreateDirectory(outputDirectory);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.SourcePath) &&
                string.Equals(
                    output,
                    Path.GetFullPath(item.SourcePath),
                    comparison))
            {
                throw new InvalidOperationException(
                    "O ZIP não pode substituir um XML original.");
            }
        }

        string temporaryZip = output + "." +
            Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var file = new FileStream(
                temporaryZip,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None))
            using (var archive = new ZipArchive(
                file,
                ZipArchiveMode.Create,
                leaveOpen: false,
                entryNameEncoding: Encoding.UTF8))
            {
                var usedNames = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

                var valid = new List<ValidDocument>();
                var invalid = new List<NfeZipDocument>();
                var duplicates = new List<NfeZipDocument>();

                foreach (var item in items)
                {
                    if (item.IsDuplicate)
                    {
                        duplicates.Add(item);
                        continue;
                    }

                    bool usable =
                        item.Error == null &&
                        item.Summary != null &&
                        !string.IsNullOrWhiteSpace(item.SourcePath) &&
                        File.Exists(item.SourcePath) &&
                        !string.IsNullOrWhiteSpace(item.PdfPath) &&
                        File.Exists(item.PdfPath);

                    if (!usable)
                    {
                        invalid.Add(item);
                        continue;
                    }

                    var summary = item.Summary!;

                    string documentNumber = groupingMode ==
                        NfeZipGroupingMode.Recipient
                        ? summary.RecipientDocument
                        : summary.IssuerDocument;

                    string companyName = groupingMode ==
                        NfeZipGroupingMode.Recipient
                        ? summary.RecipientName
                        : summary.IssuerName;

                    string digits = DigitsOnly(documentNumber);

                    string companyKey;

                    if (digits.Length > 0)
                    {
                        companyKey = digits;
                    }
                    else
                    {
                        string identity = Path.GetFullPath(item.SourcePath);
                        string hash = Convert.ToHexString(
                            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                            .Substring(0, 10);

                        companyKey = "SEM-DOCUMENTO-" + hash;
                    }

                    string companyFolder =
                        SafePart(companyKey) + "-" +
                        SafePart(companyName);

                    valid.Add(new ValidDocument(
                        item,
                        companyKey,
                        companyFolder,
                        GetPeriod(summary.IssueDate)));
                }

                // Exporta XMLs e DANFEs individuais, agrupados por empresa
                // e competência.
                foreach (var companyGroup in valid.GroupBy(
                    x => x.CompanyKey,
                    StringComparer.OrdinalIgnoreCase))
                {
                    string companyFolder =
                        companyGroup.First().CompanyFolder;

                    foreach (var periodGroup in companyGroup.GroupBy(
                        x => x.Period,
                        StringComparer.OrdinalIgnoreCase))
                    {
                        string root =
                            $"EMPRESAS/{companyFolder}/{periodGroup.Key}";

                        foreach (var entry in periodGroup)
                        {
                            var item = entry.Document;
                            var summary = item.Summary!;

                            string invoice = SafePart(
                                summary.InvoiceNumber);

                            string accessKey = SafePart(
                                string.IsNullOrWhiteSpace(summary.AccessKey)
                                    ? Path.GetFileNameWithoutExtension(item.SourcePath)
                                    : summary.AccessKey);

                            string xmlName = SafePart(
                                Path.GetFileName(item.SourcePath));

                            string pdfName =
                                $"DANFE-NFe-{invoice}-{accessKey}.pdf";

                            AddFile(
                                archive,
                                item.SourcePath,
                                $"{root}/XML_ORIGINAIS/{xmlName}",
                                usedNames);

                            AddFile(
                                archive,
                                item.PdfPath!,
                                $"{root}/DANFES/{pdfName}",
                                usedNames);
                        }

                        AddConsolidatedPdf(
                            archive,
                            periodGroup.ToList(),
                            $"{root}/DANFE-CONSOLIDADO-{periodGroup.Key}.pdf",
                            usedNames);
                    }
                }

                // XMLs inválidos ficam separados dos autorizados.
                int invalidNumber = 0;

                foreach (var item in invalid)
                {
                    invalidNumber++;

                    AddIssueXml(
                        archive,
                        item,
                        "XML_COM_ERRO",
                        invalidNumber,
                        item.Error ?? "XML inválido ou DANFE não disponível.",
                        usedNames);
                }

                // Duplicados são preservados, sem entrar nos DANFEs válidos.
                int duplicateNumber = 0;

                foreach (var item in duplicates)
                {
                    duplicateNumber++;

                    AddIssueXml(
                        archive,
                        item,
                        "DUPLICADOS",
                        duplicateNumber,
                        item.DuplicateReason ??
                            "Chave de acesso ou arquivo já importado.",
                        usedNames);
                }

                WriteCsv(
                    archive,
                    valid,
                    invalid,
                    duplicates);

                WriteText(
                    archive,
                    "LEIA-ME.txt",
                    BuildReadme(
                        groupingMode,
                        valid.Count,
                        invalid.Count,
                        duplicates.Count));
            }

            File.Move(temporaryZip, output, true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryZip))
                    File.Delete(temporaryZip);
            }
            catch
            {
                // Não mascara a exceção original.
            }

            throw;
        }
    }

    private static void AddConsolidatedPdf(
        ZipArchive archive,
        IReadOnlyList<ValidDocument> documents,
        string entryPath,
        HashSet<string> usedNames)
    {
        if (documents.Count == 0)
            return;

        using var outputDocument = new PdfDocument();

        foreach (var item in documents)
        {
            using var sourceDocument = PdfReader.Open(
                item.Document.PdfPath!,
                PdfDocumentOpenMode.Import);

            for (int i = 0; i < sourceDocument.Pages.Count; i++)
                outputDocument.AddPage(sourceDocument.Pages[i]);
        }

        if (outputDocument.Pages.Count == 0)
            return;

        string uniquePath = UniqueEntryName(entryPath, usedNames);

        var entry = archive.CreateEntry(
            uniquePath,
            CompressionLevel.Optimal);

        using var stream = entry.Open();
        outputDocument.Save(stream);
    }

    private static void AddIssueXml(
        ZipArchive archive,
        NfeZipDocument item,
        string folder,
        int number,
        string reason,
        HashSet<string> usedNames)
    {
        string originalName = string.IsNullOrWhiteSpace(item.SourcePath)
            ? $"arquivo-{number}.xml"
            : Path.GetFileName(item.SourcePath);

        string baseName =
            $"{folder}/{number:D4}-{SafePart(originalName)}";

        if (!string.IsNullOrWhiteSpace(item.SourcePath) &&
            File.Exists(item.SourcePath))
        {
            AddFile(
                archive,
                item.SourcePath,
                baseName,
                usedNames);
        }

        WriteText(
            archive,
            baseName + ".motivo.txt",
            reason);
    }

    private static void AddFile(
        ZipArchive archive,
        string sourcePath,
        string entryPath,
        HashSet<string> usedNames)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(
                "Arquivo de origem não encontrado para exportação.",
                sourcePath);

        string uniquePath = UniqueEntryName(
            entryPath,
            usedNames);

        var entry = archive.CreateEntry(
            uniquePath,
            CompressionLevel.Optimal);

        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        using var destination = entry.Open();
        source.CopyTo(destination);
    }

    private static string UniqueEntryName(
        string path,
        HashSet<string> usedNames)
    {
        string normalized = path.Replace('\\', '/');

        if (usedNames.Add(normalized))
            return normalized;

        string directory = Path.GetDirectoryName(normalized)?
            .Replace('\\', '/') ?? "";

        string name = Path.GetFileNameWithoutExtension(normalized);
        string extension = Path.GetExtension(normalized);

        int index = 2;
        string candidate;

        do
        {
            string file = $"{name}-{index}{extension}";

            candidate = string.IsNullOrEmpty(directory)
                ? file
                : $"{directory}/{file}";

            index++;
        }
        while (!usedNames.Add(candidate));

        return candidate;
    }

    private static void WriteCsv(
        ZipArchive archive,
        IReadOnlyList<ValidDocument> valid,
        IReadOnlyList<NfeZipDocument> invalid,
        IReadOnlyList<NfeZipDocument> duplicates)
    {
        var entry = archive.CreateEntry(
            "relatorio.csv",
            CompressionLevel.Optimal);

        using var stream = entry.Open();

        // Excel reconhece corretamente o UTF-8 com BOM.
        byte[] bom = Encoding.UTF8.GetPreamble();
        stream.Write(bom, 0, bom.Length);

        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            leaveOpen: true);

        writer.WriteLine(
            "Status;Motivo;Chave de acesso;CNPJ/CPF emitente;" +
            "Emitente;CNPJ/CPF destinatário;Destinatário;" +
            "Número NF-e;Data emissão;Arquivo XML;Arquivo PDF");

        foreach (var item in valid)
        {
            var summary = item.Document.Summary!;

            WriteCsvRow(writer, new[]
            {
                "PROCESSADO",
                "",
                summary.AccessKey,
                summary.IssuerDocument,
                summary.IssuerName,
                summary.RecipientDocument,
                summary.RecipientName,
                summary.InvoiceNumber,
                summary.IssueDate,
                Path.GetFileName(item.Document.SourcePath),
                Path.GetFileName(item.Document.PdfPath)
            });
        }

        foreach (var item in invalid)
        {
            WriteCsvRow(writer, new[]
            {
                "INVALIDO",
                item.Error ?? "XML inválido ou DANFE não disponível",
                item.Summary?.AccessKey ?? "",
                item.Summary?.IssuerDocument ?? "",
                item.Summary?.IssuerName ?? "",
                item.Summary?.RecipientDocument ?? "",
                item.Summary?.RecipientName ?? "",
                item.Summary?.InvoiceNumber ?? "",
                item.Summary?.IssueDate ?? "",
                Path.GetFileName(item.SourcePath),
                ""
            });
        }

        foreach (var item in duplicates)
        {
            WriteCsvRow(writer, new[]
            {
                "DUPLICADO",
                item.DuplicateReason ?? "Arquivo ou chave já importado",
                item.Summary?.AccessKey ?? "",
                item.Summary?.IssuerDocument ?? "",
                item.Summary?.IssuerName ?? "",
                item.Summary?.RecipientDocument ?? "",
                item.Summary?.RecipientName ?? "",
                item.Summary?.InvoiceNumber ?? "",
                item.Summary?.IssueDate ?? "",
                Path.GetFileName(item.SourcePath),
                ""
            });
        }

        writer.Flush();
    }

    private static void WriteCsvRow(
        TextWriter writer,
        IEnumerable<string?> values)
    {
        writer.WriteLine(string.Join(
            ";",
            values.Select(value =>
            {
                string text = value ?? "";
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            })));
    }

    private static void WriteText(
        ZipArchive archive,
        string path,
        string content)
    {
        var entry = archive.CreateEntry(
            path,
            CompressionLevel.Optimal);

        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(true));

        writer.Write(content);
    }

    private static string BuildReadme(
        NfeZipGroupingMode mode,
        int valid,
        int invalid,
        int duplicates)
    {
        string grouping = mode == NfeZipGroupingMode.Recipient
            ? "Destinatário"
            : "Emitente";

        return
            "ASSINAPDF FUTURE - CENTRAL XML FISCAL\r\n\r\n" +
            $"Agrupamento: {grouping}\r\n" +
            $"DANFEs processados: {valid}\r\n" +
            $"XMLs com erro: {invalid}\r\n" +
            $"Duplicados: {duplicates}\r\n\r\n" +
            "ESTRUTURA:\r\n" +
            "EMPRESAS: XMLs originais, DANFEs individuais e consolidados.\r\n" +
            "XML_COM_ERRO: arquivos que não puderam ser processados.\r\n" +
            "DUPLICADOS: arquivos repetidos, separados dos autorizados.\r\n" +
            "relatorio.csv: relatório dos registros e respectivos motivos.\r\n\r\n" +
            "Os XMLs originais não são modificados por esta exportação.\r\n";
    }

    private static string GetPeriod(string? issueDate)
    {
        if (DateTimeOffset.TryParse(
            issueDate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var date))
        {
            return date.ToString(
                "yyyy-MM",
                CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(
            issueDate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var localDate))
        {
            return localDate.ToString(
                "yyyy-MM",
                CultureInfo.InvariantCulture);
        }

        return "SEM-DATA";
    }

    private static string DigitsOnly(string? value)
    {
        return new string(
            (value ?? "").Where(char.IsDigit).ToArray());
    }

    private static string SafePart(string? value)
    {
        string result = (value ?? "")
            .Trim()
            .Replace("/", "-")
            .Replace("\\", "-");

        foreach (char invalid in
            Path.GetInvalidFileNameChars().Concat(
                new[] { '<', '>', ':', '"', '|', '?', '*' }))
        {
            result = result.Replace(invalid, '_');
        }

        result = result.Trim('.', ' ');

        if (string.IsNullOrWhiteSpace(result))
            return "SEM-NOME";

        return result.Length > 100
            ? result.Substring(0, 100)
            : result;
    }
}
