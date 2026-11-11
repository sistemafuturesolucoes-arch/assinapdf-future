using Aspose.Pdf.Text;
using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class InstalledFontCatalogServiceTests
{
    [Fact]
    public void BuildCandidateDirectories_IncludesWindowsSystemAndPerUserFonts()
    {
        var paths = InstalledFontCatalogService.BuildCandidateDirectories(
            isWindows: true,
            isMacOS: false,
            windowsFonts: @"C:\Windows\Fonts",
            localAppData: @"C:\Users\Tester\AppData\Local",
            userProfile: @"C:\Users\Tester");

        Assert.Equal(
            new[]
            {
                @"C:\Windows\Fonts",
                Path.Combine(@"C:\Users\Tester\AppData\Local", "Microsoft", "Windows", "Fonts")
            },
            paths);
    }

    [Fact]
    public void BuildCandidateDirectories_IncludesMacSystemSharedAndPerUserFonts()
    {
        var paths = InstalledFontCatalogService.BuildCandidateDirectories(
            isWindows: false,
            isMacOS: true,
            windowsFonts: string.Empty,
            localAppData: string.Empty,
            userProfile: "/Users/tester");

        Assert.Equal(
            new[]
            {
                "/System/Library/Fonts",
                "/Library/Fonts",
                Path.Combine("/Users/tester", "Library", "Fonts")
            },
            paths);
    }

    [Fact]
    public void BuildCandidateDirectories_DoesNotScanUnsupportedOperatingSystems()
    {
        var paths = InstalledFontCatalogService.BuildCandidateDirectories(
            isWindows: false,
            isMacOS: false,
            windowsFonts: @"C:\Windows\Fonts",
            localAppData: @"C:\Users\Tester\AppData\Local",
            userProfile: "/Users/tester");

        Assert.Empty(paths);
    }

    [Theory]
    [InlineData("Arial Bold Italic", "Arial", true, true, true)]
    [InlineData("Arial-BoldItalicMT", "Arial", true, true, true)]
    [InlineData("BellMT-BoldItalic", "Bell MT", true, true, true)]
    [InlineData("Arial-Bold", "Arial", true, false, true)]
    [InlineData("Arial-Italic", "Arial", false, true, true)]
    [InlineData("Arial", "Arial", false, false, true)]
    [InlineData("Arial-BoldItalic", "Arial", true, false, false)]
    [InlineData("Arial-BoldItalic", "Arial", false, true, false)]
    [InlineData("LiberationSans-BoldItalic", "Arial", true, true, false)]
    public void FontNameMatchesRequestedStyle_UsesFaceNameAndFamily(
        string fontName,
        string family,
        bool bold,
        bool italic,
        bool expected)
    {
        Assert.Equal(expected,
            InstalledFontCatalogService.FontNameMatchesRequestedStyle(
                fontName, family, bold, italic));
    }

    [Fact]
    public void FontNameMatchesRequestedStyle_UsesAsposeFlagsWhenNameHasNoStyleSuffix()
    {
        Assert.True(InstalledFontCatalogService.FontNameMatchesRequestedStyle(
            "ArialMT", "Arial", fallbackBold: true, fallbackItalic: false,
            wantsBold: true, wantsItalic: false));
    }

    [Fact]
    public void NormalizeFontFileToken_RemovesSeparatorsWithoutChangingFamilyIdentity()
    {
        Assert.Equal("bellmt", InstalledFontCatalogService.NormalizeFontFileToken("Bell MT"));
        Assert.Equal("bellmtbolditalic", InstalledFontCatalogService.NormalizeFontFileToken("BellMT-BoldItalic"));
    }

    [Fact]
    public void WindowsArialBoldItalicFile_CanBeOpenedAndEmbeddedWhenInstalled()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string fontPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arialbi.ttf");
        if (!File.Exists(fontPath))
            return;

        Font font = FontRepository.OpenFont(fontPath);
        Assert.True(
            InstalledFontCatalogService.FontNameMatchesRequestedStyle(
                font.BaseFont, "Arial", wantsBold: true, wantsItalic: true,
                fallbackBold: font.IsBold, fallbackItalic: font.IsItalic) ||
            InstalledFontCatalogService.FontNameMatchesRequestedStyle(
                font.FontName, "Arial", wantsBold: true, wantsItalic: true,
                fallbackBold: font.IsBold, fallbackItalic: font.IsItalic));

        font.IsEmbedded = true;
        using var output = new MemoryStream();
        font.Save(output);
        Assert.True(output.Length > 12, font.GetLastFontEmbeddingError());
    }

    [Fact]
    public void EnumerateCandidateFontFiles_FindsOnlyLikelyFamilyTtfOtfAndTtcFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AssinaPDFFontDiscovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string[] names = { "arial.ttf", "arialbi.ttf", "Arial-BoldItalic.otf", "arial-collection.ttc", "calibri.ttf", "notes.txt" };
            foreach (string name in names)
                File.WriteAllBytes(Path.Combine(directory, name), [0x00]);

            var files = InstalledFontCatalogService.EnumerateCandidateFontFiles(
                new[] { directory }, "Arial");

            Assert.Equal(4, files.Count);
            Assert.All(files, file =>
                Assert.Contains(Path.GetFileName(file), names.Take(4)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
