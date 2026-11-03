using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AssinaPDF.MacPrototype.Services;

public sealed class CertificateInfo : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public string DisplayName { get; }
    public string Details { get; }
    public bool CanAttemptSigning { get; }

    public CertificateInfo(X509Certificate2 certificate)
    {
        Certificate = certificate;

        var now = DateTime.Now;
        var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
        var issuer = certificate.GetNameInfo(X509NameType.SimpleName, true);
        var hasPrivateKey = false;
        var keyUsageAllowsSigning = true;

        try
        {
            hasPrivateKey = certificate.HasPrivateKey;
        }
        catch (CryptographicException)
        {
            // Um provider indisponível não deve interromper a listagem.
        }

        try
        {
            foreach (var extension in certificate.Extensions)
            {
                if (extension is X509KeyUsageExtension usage)
                {
                    keyUsageAllowsSigning =
                        (usage.KeyUsages & X509KeyUsageFlags.DigitalSignature) != 0 ||
                        (usage.KeyUsages & X509KeyUsageFlags.NonRepudiation) != 0;
                    break;
                }
            }
        }
        catch (CryptographicException)
        {
            keyUsageAllowsSigning = false;
        }

        var valid = now >= certificate.NotBefore && now <= certificate.NotAfter;
        CanAttemptSigning = hasPrivateKey && valid && keyUsageAllowsSigning;

        DisplayName =
            $"{name} — válido até {certificate.NotAfter:dd/MM/yyyy}" +
            (CanAttemptSigning ? "" : " — não pronto para assinar");

        Details =
            $"Titular: {name}\n" +
            $"Emissor: {issuer}\n" +
            $"Válido de: {certificate.NotBefore:dd/MM/yyyy} " +
            $"até {certificate.NotAfter:dd/MM/yyyy}\n" +
            $"Chave privada acessível: {(hasPrivateKey ? "sim" : "não")}\n" +
            $"Uso de assinatura digital: " +
            $"{(keyUsageAllowsSigning ? "permitido ou não restringido" : "não permitido")}\n" +
            $"Validade local: {(valid ? "dentro do prazo" : "fora do prazo")}";
    }

    public override string ToString() => DisplayName;

    public void Dispose() => Certificate.Dispose();
}

public sealed class CertificateStoreService
{
    public IReadOnlyList<CertificateInfo> LoadCertificates()
    {
        var result = new List<CertificateInfo>();

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

            foreach (var certificate in store.Certificates)
            {
                try
                {
                    result.Add(new CertificateInfo(
                        new X509Certificate2(certificate)));
                }
                catch (CryptographicException)
                {
                    // Ignora certificados que não podem ser lidos.
                }
            }
        }
        catch
        {
            foreach (var item in result)
                item.Dispose();

            throw;
        }

        return result;
    }
}
