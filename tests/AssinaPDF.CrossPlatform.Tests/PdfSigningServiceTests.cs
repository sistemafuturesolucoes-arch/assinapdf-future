using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class PdfSigningServiceTests
{
    [Fact]
    public async Task SignAsync_RejectsEmptyPdf()
    {
        using var certificate = CreateCertificate();
        var service = new PdfSigningService();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SignAsync(
                Array.Empty<byte>(),
                certificate,
                "Teste",
                new[] { new SignaturePlacement(1, 100, 100) }));
    }

    [Fact]
    public async Task SignAsync_RejectsEmptyPlacements()
    {
        using var certificate = CreateCertificate();
        var service = new PdfSigningService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SignAsync(
                CreateMinimalPdf(),
                certificate,
                "Teste",
                Array.Empty<SignaturePlacement>()));
    }

    [Fact]
    public async Task SignAsync_CreatesSignatureWithValidIntegrity()
    {
        using var certificate = CreateCertificate();
        var service = new PdfSigningService();

        var signedPdf = await service.SignAsync(
            CreateMinimalPdf(),
            certificate,
            "Teste automatizado",
            new[] { new SignaturePlacement(1, 100, 100) });

        Assert.NotEmpty(signedPdf);

        var results = await service.ValidateAsync(signedPdf);

        Assert.Single(results);
        Assert.True(
            results[0].IsIntegrityValid,
            string.Join("; ", results[0].Errors));
        Assert.True(
            results[0].IsSignatureValid,
            string.Join("; ", results[0].Errors));
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=AssinaPDF CrossPlatform Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature |
                X509KeyUsageFlags.NonRepudiation,
                critical: true));

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                false, false, 0, critical: true));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private static byte[] CreateMinimalPdf()
    {
        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new long[5];

        AppendObject(builder, offsets, 1,
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        AppendObject(builder, offsets, 2,
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        AppendObject(builder, offsets, 3,
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << >> /Contents 4 0 R >>\nendobj\n");
        AppendObject(builder, offsets, 4,
            "4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 5\n");
        builder.Append("0000000000 65535 f \n");

        for (var i = 1; i <= 4; i++)
        {
            builder.Append(offsets[i].ToString("D10",
                System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n");
        builder.Append(xrefOffset.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static void AppendObject(
        StringBuilder builder,
        long[] offsets,
        int objectNumber,
        string content)
    {
        offsets[objectNumber] =
            Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append(content);
    }
}
