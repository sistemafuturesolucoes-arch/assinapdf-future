
using System.Xml;
using System.Xml.Serialization;
using Danfe.NFe.Core;
using Danfe.NFe.Core.Esquemas;
using Danfe.NFe.Core.Modelo;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Gera o DANFE oficial da NF-e modelo 55 usando a biblioteca
/// Danfe.NFe.Core e os dados do XML autorizado.
///
/// O XML original nunca é alterado.
/// </summary>
public static class NfeDanfeGenerator
{
    private const long MaxXmlCharacters = 10_000_000;

    public static void Generate(NfeDocumentSummary invoice, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (string.IsNullOrWhiteSpace(invoice.SourcePath) ||
            !File.Exists(invoice.SourcePath))
        {
            throw new FileNotFoundException(
                "O XML original da NF-e não foi encontrado.",
                invoice.SourcePath);
        }

        if (invoice.AccessKey.Length != 44 ||
            invoice.AccessKey.Any(c => !char.IsAsciiDigit(c)))
        {
            throw new InvalidDataException(
                "A chave de acesso da NF-e é inválida.");
        }

        string sourcePath = Path.GetFullPath(invoice.SourcePath);
        string fullPath = Path.GetFullPath(outputPath);

        // Impede que o arquivo de saída substitua o XML original.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(sourcePath, fullPath, comparison))
        {
            throw new InvalidOperationException(
                "O DANFE não pode substituir o XML original.");
        }

        // Revalida o XML antes da geração.
        // O leitor do aplicativo verifica autorização, modelo 55
        // e consistência da chave de acesso.
        var validatedInvoice = NfeXmlReader.ReadFile(sourcePath);

        if (!string.Equals(
                validatedInvoice.AccessKey,
                invoice.AccessKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A chave do XML original não corresponde à NF-e selecionada.");
        }

        // Faz a desserialização com proteção contra DTD e resolução
        // de entidades externas. Não utiliza os métodos de leitura
        // simplificados da biblioteca.
        var serializer = new XmlSerializer(typeof(ProcNFe));

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlCharacters,
            MaxCharactersFromEntities = 1024,
            IgnoreComments = true
        };

        ProcNFe procNFe;

        using (var stream = new FileStream(
                   sourcePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        using (var reader = XmlReader.Create(stream, settings))
        {
            procNFe = serializer.Deserialize(reader) as ProcNFe
                ?? throw new InvalidDataException(
                    "Não foi possível interpretar o XML autorizado da NF-e.");
        }

        // Constrói o modelo completo do DANFE diretamente do XML.
        // Assim, os campos fiscais não são inventados nem simplificados.
        var viewModel = DanfeViewModelCreator.CreateFromXml(procNFe);

        string? directory = Path.GetDirectoryName(fullPath);

        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException(
                "Não foi possível determinar a pasta de destino.");

        Directory.CreateDirectory(directory);

        // Gera primeiro em arquivo temporário para não deixar um PDF
        // incompleto caso ocorra alguma falha durante a geração.
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(fullPath)}-{Guid.NewGuid():N}.tmp.pdf");

        try
        {
            using (var danfe = new DanfeDoc(viewModel))
            {
                danfe.Gerar();
                danfe.Salvar(temporaryPath);
            }

            if (!File.Exists(temporaryPath) ||
                new FileInfo(temporaryPath).Length < 5)
            {
                throw new InvalidDataException(
                    "A biblioteca não produziu um PDF válido.");
            }

            // Confere a assinatura do arquivo PDF.
            using (var pdfStream = File.OpenRead(temporaryPath))
            {
                Span<byte> signature = stackalloc byte[5];

                if (pdfStream.Read(signature) != 5 ||
                    !signature.SequenceEqual("%PDF-"u8))
                {
                    throw new InvalidDataException(
                        "O arquivo gerado não possui uma assinatura PDF válida.");
                }
            }

            // Só substitui o destino depois que o PDF foi gerado
            // e passou pelas verificações.
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            // Limpa o temporário mesmo quando a geração falha.
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // A limpeza não deve ocultar o erro original.
            }
        }
    }
}
