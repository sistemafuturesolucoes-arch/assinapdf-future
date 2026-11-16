using System.Xml;
using System.Xml.Linq;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>Identifica com segurança o tipo de XML processado e encaminha ao leitor fiscal correspondente.</summary>
public static class CteDocumentReader
{
    public static CteDocumentSummary ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = 10_000_000
        });
        var root = XDocument.Load(reader, LoadOptions.None).Root
            ?? throw new InvalidDataException("O XML está vazio.");

        return root.Name.LocalName switch
        {
            "cteProc" => CteXmlReader.ReadFile(path),
            "cteOSProc" => CteOsXmlReader.ReadFile(path),
            _ => throw new InvalidDataException("XML não reconhecido como CT-e modelo 57 ou CT-e OS modelo 67 processado.")
        };
    }
}
