using DanfeSharp.NFCe;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Gera o DANFCE modelo 65 em PDF térmico (80 mm), preservando o QR Code
/// e os dados do XML autorizado. A leitura estrita do AssinaPDF ocorre antes
/// da desserialização feita pela biblioteca de impressão.
/// </summary>
public static class NfceDanfeGenerator
{
    public static void Generate(NfceDocumentSummary invoice, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (string.IsNullOrWhiteSpace(invoice.SourcePath) || !File.Exists(invoice.SourcePath))
            throw new FileNotFoundException("O XML original da NFC-e não foi encontrado.", invoice.SourcePath);

        var sourcePath = Path.GetFullPath(invoice.SourcePath);
        var fullPath = Path.GetFullPath(outputPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(sourcePath, fullPath, comparison))
            throw new InvalidOperationException("O DANFCE não pode substituir o XML original.");

        var validated = NfceXmlReader.ReadFile(sourcePath);
        if (!string.Equals(validated.AccessKey, invoice.AccessKey, StringComparison.Ordinal))
            throw new InvalidDataException("A chave do XML original não corresponde à NFC-e selecionada.");

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Não foi possível identificar a pasta de destino.");

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(fullPath)}-{Guid.NewGuid():N}.tmp.pdf");

        try
        {
            var model = DanfceViewModelCreator.CriarDeArquivoXml(sourcePath);
            model.LarguraBobina = LarguraBobina.Mm80;

            using (var danfce = new Danfce(model))
            {
                danfce.Gerar();
                danfce.Salvar(temporaryPath);
            }

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length < 5)
                throw new InvalidDataException("A biblioteca não produziu um PDF válido.");

            using (var stream = File.OpenRead(temporaryPath))
            {
                Span<byte> signature = stackalloc byte[5];
                if (stream.Read(signature) != 5 || !signature.SequenceEqual("%PDF-"u8))
                    throw new InvalidDataException("O arquivo gerado não possui uma assinatura PDF válida.");
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { /* A limpeza não deve ocultar a falha original. */ }
        }
    }
}
