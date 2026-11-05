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
}
