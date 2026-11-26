using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace AssinaPDF.MacPrototype.Services;

public static class CertificateCnpjExtractor
{
    private static readonly Regex[] ExplicitPatterns =
    {
        new(@"(?:SERIALNUMBER|2\.5\.4\.5)\s*=\s*(?:CNPJ[:\s]*)?([A-Z0-9]{12}[0-9]{2})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"CNPJ[:\s]*([A-Z0-9]{12}[0-9]{2})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"CN\s*=\s*[^,\r\n]*:\s*([A-Z0-9]{12}[0-9]{2})(?![A-Z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    };

    private static readonly Regex CandidatePattern = new(
        @"(?<![A-Z0-9])([A-Z0-9]{12}[0-9]{2})(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Extract(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Extract(certificate.Subject + "\n" + certificate.SubjectName.Name);
    }

    public static string Extract(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return string.Empty;

        foreach (Regex pattern in ExplicitPatterns)
        {
            Match match = pattern.Match(subject);
            if (match.Success && NfeXmlReader.IsValidCnpj(match.Groups[1].Value))
                return match.Groups[1].Value.ToUpperInvariant();
        }

        foreach (Match match in CandidatePattern.Matches(subject))
        {
            string candidate = match.Groups[1].Value.ToUpperInvariant();
            if (NfeXmlReader.IsValidCnpj(candidate))
                return candidate;
        }

        return string.Empty;
    }
}
