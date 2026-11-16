using Zion.NFe.Danfe;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Gera o DACTE do CT-e modelo 57 a partir do XML autorizado.
/// A validação estrita do AssinaPDF é executada antes da biblioteca de renderização.
/// </summary>
public static class CteDacteGenerator
{
    public static void Generate(CteDocumentSummary document, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (string.IsNullOrWhiteSpace(document.SourcePath) || !File.Exists(document.SourcePath))
            throw new FileNotFoundException("O XML original do CT-e não foi encontrado.", document.SourcePath);

        string sourcePath = Path.GetFullPath(document.SourcePath);
        string fullPath = Path.GetFullPath(outputPath);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (string.Equals(sourcePath, fullPath, comparison))
            throw new InvalidOperationException("O DACTE não pode substituir o XML original.");

        var validated = CteXmlReader.ReadFile(sourcePath);
        if (!string.Equals(validated.AccessKey, document.AccessKey, StringComparison.Ordinal))
            throw new InvalidDataException("A chave do XML original não corresponde ao CT-e selecionado.");

        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Não foi possível identificar a pasta de destino.");

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory,
            $".{Path.GetFileNameWithoutExtension(fullPath)}-{Guid.NewGuid():N}.tmp.pdf");

        try
        {
            string xml = File.ReadAllText(sourcePath);
            var model = DacteViewModelCreator.Criar57DeStringXml(xml);
            using (var dacte = new Dacte(model))
            {
                dacte.Gerar();
                dacte.Salvar(temporaryPath);
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
