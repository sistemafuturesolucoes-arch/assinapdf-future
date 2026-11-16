using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AssinaPDF.MacPrototype.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace AssinaPDF.MacPrototype.Services;

public sealed record NfceZipDocument(
    string SourcePath,
    NfceDocumentSummary? Summary,
    string? PdfPath,
    string? Error,
    bool IsDuplicate = false,
    string? DuplicateReason = null);

public static class NfceBatchZipExporter
{
    private sealed record ValidEntry(NfceZipDocument Document, string CompanyKey, string Folder);

    public static void Create(string outputZipPath, IEnumerable<NfceZipDocument> documents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputZipPath);
        ArgumentNullException.ThrowIfNull(documents);
        var items = documents.ToList();
        if (items.Count == 0) throw new InvalidOperationException("Não existem registros para exportar.");

        string output = Path.GetFullPath(outputZipPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.SourcePath) &&
                string.Equals(output, Path.GetFullPath(item.SourcePath), comparison))
                throw new InvalidOperationException("O ZIP não pode substituir um XML original.");
            if (!string.IsNullOrWhiteSpace(item.PdfPath) &&
                string.Equals(output, Path.GetFullPath(item.PdfPath), comparison))
                throw new InvalidOperationException("O ZIP não pode substituir um DANFCE.");
        }

        string? directory = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Pasta de destino inválida.");
        Directory.CreateDirectory(directory);
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, false, Encoding.UTF8))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var valid = new List<ValidEntry>();
                var invalid = new List<NfceZipDocument>();
                var duplicates = new List<NfceZipDocument>();

                foreach (var item in items)
                {
                    if (item.IsDuplicate) { duplicates.Add(item); continue; }
                    if (item.Error != null || item.Summary == null || !File.Exists(item.SourcePath) ||
                        string.IsNullOrWhiteSpace(item.PdfPath) || !File.Exists(item.PdfPath))
                    { invalid.Add(item); continue; }

                    string document = Digits(item.Summary.IssuerDocument);
                    if (string.IsNullOrWhiteSpace(document))
                    {
                        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(item.SourcePath))))[..10];
                        document = "SEM-CNPJ-" + hash;
                    }
                    string folder = Safe(document + "-" + item.Summary.IssuerName);
                    valid.Add(new ValidEntry(item, document, folder));
                }

                foreach (var group in valid.GroupBy(x => x.CompanyKey, StringComparer.OrdinalIgnoreCase))
                {
                    string root = "EMPRESAS/" + group.First().Folder;
                    foreach (var entry in group)
                    {
                        var item = entry.Document;
                        string invoice = Safe(item.Summary!.InvoiceNumber);
                        string key = Safe(string.IsNullOrWhiteSpace(item.Summary.AccessKey)
                            ? Path.GetFileNameWithoutExtension(item.SourcePath) : item.Summary.AccessKey);
                        AddFile(archive, item.SourcePath,
                            $"{root}/XML_ORIGINAIS/{Path.GetFileName(item.SourcePath)}", used);
                        AddFile(archive, item.PdfPath!,
                            $"{root}/DANFCE/DANFCE-NFCe-{invoice}-{key}.pdf", used);
                    }
                    AddMergedPdf(archive, group.ToList(), $"{root}/DANFCE-CONSOLIDADO.pdf", used);
                }

                AddProblemEntries(archive, invalid, "XML_COM_ERRO", used);
                AddProblemEntries(archive, duplicates, "DUPLICADOS", used);
                WriteReport(archive, valid, invalid, duplicates);
                WriteText(archive, "LEIA-ME.txt",
                    $"ASSINAPDF FUTURE - CENTRAL NFC-e\r\n\r\nEmitentes: {valid.Select(x => x.Folder).Distinct().Count()}\r\n" +
                    $"DANFCEs gerados: {valid.Count}\r\nXMLs com erro: {invalid.Count}\r\nDuplicados: {duplicates.Count}\r\n\r\n" +
                    "Os XMLs originais não foram modificados. Os DANFCEs seguem em PDF individual e consolidado por emitente.\r\n");
            }
            File.Move(temporary, output, true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    private static void AddMergedPdf(ZipArchive archive, IReadOnlyList<ValidEntry> items, string path, HashSet<string> used)
    {
        if (items.Count == 0) return;
        using var merged = new PdfDocument();
        foreach (var entry in items)
        {
            using var source = PdfReader.Open(entry.Document.PdfPath!, PdfDocumentOpenMode.Import);
            for (int i = 0; i < source.Pages.Count; i++) merged.AddPage(source.Pages[i]);
        }
        if (merged.Pages.Count == 0) return;
        var zipEntry = archive.CreateEntry(Unique(path, used), CompressionLevel.Optimal);
        using var stream = zipEntry.Open();
        merged.Save(stream);
    }

    private static void AddProblemEntries(ZipArchive archive, IReadOnlyList<NfceZipDocument> items, string folder, HashSet<string> used)
    {
        int number = 0;
        foreach (var item in items)
        {
            number++;
            string original = string.IsNullOrWhiteSpace(item.SourcePath) ? $"arquivo-{number}.xml" : Path.GetFileName(item.SourcePath);
            string name = $"{folder}/{number:D4}-{Safe(original)}";
            if (!string.IsNullOrWhiteSpace(item.SourcePath) && File.Exists(item.SourcePath))
                AddFile(archive, item.SourcePath, name, used);
            WriteText(archive, name + ".motivo.txt",
                item.DuplicateReason ?? item.Error ?? "XML não processado.", used);
        }
    }

    private static void AddFile(ZipArchive archive, string source, string destination, HashSet<string> used)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Arquivo não encontrado para exportação.", source);
        var entry = archive.CreateEntry(Unique(destination, used), CompressionLevel.Optimal);
        using var input = File.OpenRead(source);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private static void WriteReport(ZipArchive archive, IReadOnlyList<ValidEntry> valid,
        IReadOnlyList<NfceZipDocument> invalid, IReadOnlyList<NfceZipDocument> duplicates)
    {
        var entry = archive.CreateEntry("relatorio.csv", CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bom = Encoding.UTF8.GetPreamble();
        stream.Write(bom, 0, bom.Length);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.WriteLine("Status;Motivo;Chave;CNPJ/CPF emitente;Emitente;Número NFC-e;Data emissão;Valor total;XML;PDF");
        foreach (var item in valid)
        {
            var s = item.Document.Summary!;
            Row(writer, "PROCESSADO", "", s.AccessKey, s.IssuerDocument, s.IssuerName,
                s.InvoiceNumber, s.IssueDate, s.InvoiceTotal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Path.GetFileName(item.Document.SourcePath), Path.GetFileName(item.Document.PdfPath));
        }
        foreach (var item in invalid)
        {
            var s = item.Summary;
            Row(writer, "ERRO", item.Error ?? "PDF não disponível", s?.AccessKey ?? "", s?.IssuerDocument ?? "",
                s?.IssuerName ?? "", s?.InvoiceNumber ?? "", s?.IssueDate ?? "", s?.InvoiceTotal.ToString("0.00") ?? "",
                Path.GetFileName(item.SourcePath), "");
        }
        foreach (var item in duplicates)
        {
            var s = item.Summary;
            Row(writer, "DUPLICADO", item.DuplicateReason ?? "Arquivo ou chave repetida", s?.AccessKey ?? "",
                s?.IssuerDocument ?? "", s?.IssuerName ?? "", s?.InvoiceNumber ?? "", s?.IssueDate ?? "",
                s?.InvoiceTotal.ToString("0.00") ?? "", Path.GetFileName(item.SourcePath), "");
        }
        writer.Flush();
    }

    private static void Row(TextWriter writer, params string?[] values) =>
        writer.WriteLine(string.Join(";", values.Select(v => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"")));

    private static void WriteText(ZipArchive archive, string path, string content, HashSet<string>? used = null)
    {
        string target = used == null ? path : Unique(path, used);
        var entry = archive.CreateEntry(target, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        writer.Write(content);
    }

    private static string Unique(string path, HashSet<string> used)
    {
        string normalized = path.Replace('\\', '/');
        if (used.Add(normalized)) return normalized;
        string directory = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? "";
        string name = Path.GetFileNameWithoutExtension(normalized);
        string ext = Path.GetExtension(normalized);
        int i = 2;
        string candidate;
        do
        {
            string part = $"{name}-{i++}{ext}";
            candidate = string.IsNullOrEmpty(directory) ? part : directory + "/" + part;
        } while (!used.Add(candidate));
        return candidate;
    }

    private static string Digits(string? value) => new((value ?? "").Where(char.IsDigit).ToArray());
    private static string Safe(string? value)
    {
        string result = (value ?? "").Trim().Replace("/", "-").Replace("\\", "-");
        foreach (char invalid in Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '|', '?', '*' }))
            result = result.Replace(invalid, '_');
        result = result.Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(result)) return "SEM-NOME";
        return result.Length > 100 ? result[..100] : result;
    }
}
