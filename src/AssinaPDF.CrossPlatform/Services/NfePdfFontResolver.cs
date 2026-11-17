using System.Reflection;
using System.Runtime.CompilerServices;
using PdfSharp.Fonts;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Registra as fontes incorporadas do aplicativo no PDFsharp.
/// O registro ocorre no carregamento do módulo para anteceder qualquer criação de XFont.
/// </summary>
internal sealed class NfePdfFontResolver : IFontResolver
{
    private static readonly NfePdfFontResolver Instance = new();

    private static readonly Lazy<byte[]> RegularFont =
        new(() => LoadFont("Inter-Regular.ttf"));

    private static readonly Lazy<byte[]> BoldFont =
        new(() => LoadFont("Inter-Bold.ttf"));

    private static readonly object RegistrationLock = new();

    [ModuleInitializer]
    internal static void InitializeModule() => Register();

    public static void Register()
    {
        lock (RegistrationLock)
        {
            IFontResolver? current = GlobalFontSettings.FontResolver;

            if (current is null)
            {
                GlobalFontSettings.FontResolver = Instance;
                return;
            }

            if (!ReferenceEquals(current, Instance) &&
                GlobalFontSettings.FallbackFontResolver is null)
            {
                GlobalFontSettings.FallbackFontResolver = Instance;
            }
        }
    }

    public FontResolverInfo ResolveTypeface(
        string familyName,
        bool bold,
        bool italic)
    {
        // O aplicativo distribui Inter incorporada e não deve depender
        // das fontes instaladas no Windows ou no macOS. Ela também serve
        // como fallback para nomes solicitados por bibliotecas auxiliares.
        string faceName = bold ? "Inter-Bold" : "Inter-Regular";
        return new FontResolverInfo(faceName, false, italic);
    }

    public byte[]? GetFont(string faceName)
    {
        return faceName switch
        {
            "Inter-Regular" => RegularFont.Value,
            "Inter-Bold" => BoldFont.Value,
            _ => null
        };
    }

    private static byte[] LoadFont(string fileName)
    {
        string suffix = ".Assets.Fonts." + fileName;
        Assembly assembly = typeof(NfePdfFontResolver).Assembly;

        string? resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name =>
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            throw new FileNotFoundException(
                $"Fonte incorporada não encontrada: {fileName}");

        using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Não foi possível carregar a fonte {fileName}.");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return memory.ToArray();
    }
}
