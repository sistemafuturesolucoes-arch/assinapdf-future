using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using SimpleSign.Core.Extensions;
using SimpleSign.Core.Signing;
using SimpleSign.Core.Validation;
using SimpleSign.PAdES;
using SimpleSign.PAdES.Signing;
using SimpleSign.PAdES.Validation;

namespace AssinaPDF.MacPrototype.Services;

public sealed record SignaturePlacement(int Page, float X, float Y);

public sealed class PdfSigningService
{
    public async Task<byte[]> SignAsync(
        byte[] pdfBytes,
        X509Certificate2 certificate,
        string reason,
        IReadOnlyList<SignaturePlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(placements);

        if (pdfBytes.Length == 0)
            throw new InvalidDataException("O PDF está vazio.");

        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException(
                "O certificado não possui uma chave privada acessível.");

        var now = DateTime.Now;
        if (now < certificate.NotBefore || now > certificate.NotAfter)
            throw new InvalidOperationException(
                "O certificado está fora do período de validade.");

        if (placements.Count == 0)
            throw new ArgumentException(
                "Selecione pelo menos um local para a assinatura.",
                nameof(placements));

        byte[] signedBytes = pdfBytes;

        foreach (var placement in placements)
        {
            if (placement.Page < 1 ||
                !float.IsFinite(placement.X) ||
                !float.IsFinite(placement.Y) ||
                placement.X < 0 ||
                placement.Y < 0)
            {
                throw new ArgumentException(
                    "Uma das posições de assinatura é inválida.",
                    nameof(placements));
            }

            signedBytes = await PadesSigner
                .Document(signedBytes)
                .WithCertificate(certificate)
                .WithMetadata(new SignatureMetadata
                {
                    SignerName = certificate.GetNameInfo(
                        X509NameType.SimpleName, false),
                    Reason = reason,
                    Location = "Future Soluções"
                })
                .WithAppearance(new SignatureAppearance
                {
                    Page = placement.Page,
                    X = placement.X,
                    Y = placement.Y,
                    ShowDate = true,
                    ShowReason = true,
                    ShowLocation = true
                })
                .WithLevel(AdesBaselineProfile.Basic())
                .SignAsync();

            if (signedBytes.Length == 0)
                throw new InvalidDataException(
                    "O mecanismo de assinatura retornou um documento vazio.");
        }

        return signedBytes;
    }

    public async Task<IReadOnlyList<SignatureValidationResult>> ValidateAsync(
        byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        if (pdfBytes.Length == 0)
            throw new InvalidDataException("O PDF está vazio.");

        var validator = new PdfSignatureValidator(new ValidationOptions
        {
            CheckRevocation = false,
            TrustSystemRoots = false
        });

        using var stream = new MemoryStream(pdfBytes, writable: false);
        return await validator.ValidateAsync(stream);
    }
}

