using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class UpdateInstallerLauncherTests
{
    [Fact]
    public void Launch_WhenPackageDoesNotExist_ThrowsFileNotFoundException()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(), $"assinapdf-missing-{Guid.NewGuid():N}.msi");

        Assert.Throws<FileNotFoundException>(() =>
            UpdateInstallerLauncher.Launch(missingPath, "windows-x64"));
    }

    [Fact]
    public void Launch_WhenPackageExtensionDoesNotMatchPlatform_ThrowsInvalidOperationException()
    {
        var packagePath = Path.Combine(
            Path.GetTempPath(), $"assinapdf-test-{Guid.NewGuid():N}.dmg");

        try
        {
            File.WriteAllText(packagePath, "synthetic test package");

            Assert.Throws<InvalidOperationException>(() =>
                UpdateInstallerLauncher.Launch(packagePath, "windows-x64"));
        }
        finally
        {
            if (File.Exists(packagePath))
                File.Delete(packagePath);
        }
    }

    [Fact]
    public void Launch_WhenPlatformIsUnsupported_ThrowsPlatformNotSupportedException()
    {
        var packagePath = Path.Combine(
            Path.GetTempPath(), $"assinapdf-test-{Guid.NewGuid():N}.bin");

        try
        {
            File.WriteAllText(packagePath, "synthetic test package");

            Assert.Throws<PlatformNotSupportedException>(() =>
                UpdateInstallerLauncher.Launch(packagePath, "linux-x64"));
        }
        finally
        {
            if (File.Exists(packagePath))
                File.Delete(packagePath);
        }
    }
}
