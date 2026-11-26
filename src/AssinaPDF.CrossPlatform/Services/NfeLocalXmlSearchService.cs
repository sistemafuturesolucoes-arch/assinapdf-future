namespace AssinaPDF.MacPrototype.Services;

public sealed record NfeLocalSearchResult(
    IReadOnlyList<NfeDocumentSummary> Documents,
    int FilesExamined,
    int InvalidFiles,
    bool LimitReached);

public static class NfeLocalXmlSearchService
{
    public static NfeLocalSearchResult FindIssued(
        string cnpj,
        IEnumerable<string> folders,
        int maximumFiles = 3000)
    {
        cnpj = NfeDistributionService.NormalizeDigits(cnpj);
        if (cnpj.Length != 14)
            throw new ArgumentException("Informe um CNPJ com 14 dígitos.", nameof(cnpj));
        ArgumentNullException.ThrowIfNull(folders);
        if (maximumFiles < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumFiles));

        var documents = new List<NfeDocumentSummary>();
        var seenFiles = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        int examined = 0;
        int invalid = 0;
        bool limitReached = false;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 10
        };

        foreach (string folder in folders
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal))
        {
            if (!Directory.Exists(folder))
                continue;

            foreach (string pattern in new[] { "*.xml", "*.XML" })
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder, pattern, options);
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or
                    DirectoryNotFoundException or ArgumentException)
                {
                    invalid++;
                    continue;
                }

                try
                {
                    foreach (string file in files)
                    {
                        if (!seenFiles.Add(file))
                            continue;

                        if (examined >= maximumFiles)
                        {
                            limitReached = true;
                            break;
                        }

                        examined++;
                        try
                        {
                            var summary = NfeXmlReader.ReadFile(file);
                            if (NfeDistributionService.NormalizeDigits(summary.IssuerDocument) == cnpj)
                                documents.Add(summary);
                        }
                        catch
                        {
                            invalid++;
                        }
                    }
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or
                    DirectoryNotFoundException)
                {
                    invalid++;
                }

                if (limitReached)
                    break;
            }

            if (limitReached)
                break;
        }

        return new NfeLocalSearchResult(
            documents
                .OrderByDescending(item => item.IssueDate, StringComparer.Ordinal)
                .ThenBy(item => item.InvoiceNumber, StringComparer.Ordinal)
                .ToArray(),
            examined,
            invalid,
            limitReached);
    }
}
