using System.Reflection;
using PdfSharp.Fonts;

namespace AssinaPDF.MacPrototype.Services;

internal sealed class NfePdfFontResolver : IFontResolver
{
    private static readonly NfePdfFontResolver Instance = new();

    private static readonly Lazy<byte[]> RegularFont =
        new(() => LoadFont("Inter-Regular.ttf"));

    private static readonly Lazy<byte[]> BoldFont =
        new(() => LoadFont("Inter-Bold.ttf"));

    private static readonly object RegistrationLock = new();

    public static void Register()
    {
        lock (RegistrationLock)
        {
            if (GlobalFontSettings.FontResolver is null)
                GlobalFontSettings.FontResolver = Instance;
        }
    }

    public FontResolverInfo? ResolveTypeface(
        string familyName,
        bool bold,
        bool italic)
    {
        if (!familyName.Equals("Inter", StringComparison.OrdinalIgnoreCase) &&
            !familyName.Equals("Arial", StringComparison.OrdinalIgnoreCase))
            return null;

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
