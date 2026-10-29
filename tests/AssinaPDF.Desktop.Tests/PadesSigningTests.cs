using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SimpleSign.Core.Extensions;
using SimpleSign.Core.Signing;
using SimpleSign.Core.Validation;
using SimpleSign.PAdES;
using SimpleSign.PAdES.Signing;
using SimpleSign.PAdES.Validation;
using Xunit;

namespace FutureSolucoes.AssinaPDF.Tests;

public sealed class PadesSigningTests
{
    [Fact]
    public async Task SignAsync_CreatesPadesSignature_AndPreservesInputBytes()
    {
        var pdf = MinimalPdf.Create();
        var original = pdf.ToArray();
        using var certificate = CreateSigningCertificate();

        var signed = await PadesSigner
            .Document(pdf)
            .WithCertificate(certificate)
            .WithLevel(AdesBaselineProfile.Basic())
            .SignAsync();

        Assert.NotEmpty(signed);
        Assert.NotEqual(original, signed);
        Assert.Equal(original, pdf);

        var signedText = Encoding.Latin1.GetString(signed);
        Assert.Contains("/ByteRange", signedText, StringComparison.Ordinal);
        Assert.Contains("/ETSI.CAdES.detached", signedText, StringComparison.Ordinal);

        var validation = await ValidateSignatureAsync(signed);
        Assert.Single(validation);
        Assert.True(validation[0].IsIntegrityValid, string.Join("; ", validation[0].Errors));
        Assert.True(validation[0].IsSignatureValid, string.Join("; ", validation[0].Errors));
    }

    [Fact]
    public async Task SignAsync_WithVisibleAppearance_AddsSignatureMetadata()
    {
        var pdf = MinimalPdf.Create();
        using var certificate = CreateSigningCertificate();

        var signed = await PadesSigner
            .Document(pdf)
            .WithCertificate(certificate)
            .WithMetadata(new SignatureMetadata
            {
                SignerName = "AssinaPDF Automated Test",
                Reason = "Teste de aparência",
                Location = "Future Soluções"
            })
            .WithAppearance(new SignatureAppearance
            {
                Page = 1,
                X = 50,
                Y = 50,
                ShowDate = true,
                ShowReason = true
            })
            .WithLevel(AdesBaselineProfile.Basic())
            .SignAsync();

        var signedText = Encoding.Latin1.GetString(signed);
        Assert.Contains("/ByteRange", signedText, StringComparison.Ordinal);
        Assert.Contains("/Reason", signedText, StringComparison.Ordinal);

        var validation = await ValidateSignatureAsync(signed);
        Assert.Single(validation);
        Assert.True(validation[0].IsIntegrityValid, string.Join("; ", validation[0].Errors));
        Assert.True(validation[0].IsSignatureValid, string.Join("; ", validation[0].Errors));
    }

    [Fact]
    public async Task SignAsync_CanApplyTwoVisibleSignaturesSequentially()
    {
        var pdf = MinimalPdf.Create();
        using var certificate = CreateSigningCertificate();

        var first = await PadesSigner
            .Document(pdf)
            .WithCertificate(certificate)
            .WithMetadata(new SignatureMetadata { SignerName = "AssinaPDF Automated Test", Reason = "Local 1", Location = "Future Soluções" })
            .WithAppearance(new SignatureAppearance { Page = 1, X = 50, Y = 50, ShowDate = true })
            .WithLevel(AdesBaselineProfile.Basic())
            .SignAsync();

        var second = await PadesSigner
            .Document(first)
            .WithCertificate(certificate)
            .WithMetadata(new SignatureMetadata { SignerName = "AssinaPDF Automated Test", Reason = "Local 2", Location = "Future Soluções" })
            .WithAppearance(new SignatureAppearance { Page = 1, X = 250, Y = 150, ShowDate = true })
            .WithLevel(AdesBaselineProfile.Basic())
            .SignAsync();

        var validation = await ValidateSignatureAsync(second);
        Assert.Equal(2, validation.Count);
        Assert.All(validation, result =>
        {
            Assert.True(result.IsIntegrityValid, string.Join("; ", result.Errors));
            Assert.True(result.IsSignatureValid, string.Join("; ", result.Errors));
        });
    }

    private static async Task<IReadOnlyList<SimpleSign.Core.Validation.SignatureValidationResult>> ValidateSignatureAsync(byte[] signed)
    {
        var validator = new PdfSignatureValidator(new ValidationOptions
        {
            CheckRevocation = false,
            TrustSystemRoots = false
        });
        using var stream = new MemoryStream(signed);
        return await validator.ValidateAsync(stream);
    }

    private static X509Certificate2 CreateSigningCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=AssinaPDF Automated Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation,
                critical: true));
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private static class MinimalPdf
    {
        public static byte[] Create()
        {
            var builder = new StringBuilder("%PDF-1.7\n");
            var offsets = new long[5];

            AppendObject(builder, offsets, 1, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            AppendObject(builder, offsets, 2, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            AppendObject(builder, offsets, 3, "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << >> /Contents 4 0 R >>\nendobj\n");
            AppendObject(builder, offsets, 4, "4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");

            var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
            builder.Append("xref\n0 5\n");
            builder.Append("0000000000 65535 f \n");
            for (var i = 1; i <= 4; i++)
            {
                builder.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture));
                builder.Append(" 00000 n \n");
            }

            builder.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n");
            builder.Append(xrefOffset.ToString(CultureInfo.InvariantCulture));
            builder.Append("\n%%EOF\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        private static void AppendObject(StringBuilder builder, long[] offsets, int objectNumber, string content)
        {
            offsets[objectNumber] = Encoding.ASCII.GetByteCount(builder.ToString());
            builder.Append(content);
        }
    }
}
