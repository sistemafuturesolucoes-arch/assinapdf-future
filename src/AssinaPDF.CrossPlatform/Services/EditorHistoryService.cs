using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AssinaPDF.MacPrototype;

internal sealed class EditedPdfEntry
{
    public string Id { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string WorkingCopyPath { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastModifiedAtUtc { get; set; }
}

internal sealed class EditorHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _rootDirectory;
    private readonly string _historyFilePath;

    public EditorHistoryService()
    {
        string appData = OperatingSystem.IsMacOS()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        _rootDirectory = Path.Combine(
            appData, "FutureSolucoes", "AssinaPDF", "DocumentosEditados");
        _historyFilePath = Path.Combine(_rootDirectory, "historico-edicao.json");
    }

    public IReadOnlyList<EditedPdfEntry> GetRecentEntries()
    {
        Directory.CreateDirectory(_rootDirectory);
        List<EditedPdfEntry> entries = ReadEntries();
        bool changed = false;

        foreach (var entry in entries.ToArray())
        {
            if (!File.Exists(entry.WorkingCopyPath))
            {
                entries.Remove(entry);
                changed = true;
                continue;
            }

            DateTime fileTime = File.GetLastWriteTimeUtc(entry.WorkingCopyPath);
            if (fileTime > entry.LastModifiedAtUtc)
            {
                entry.LastModifiedAtUtc = fileTime;
                changed = true;
            }
        }

        if (changed)
            WriteEntries(entries);

        return entries
            .OrderByDescending(entry => entry.LastModifiedAtUtc)
            .ToArray();
    }

    public EditedPdfEntry CreateWorkingCopy(string originalPath)
    {
        string fullOriginalPath = Path.GetFullPath(originalPath);
        if (!File.Exists(fullOriginalPath))
            throw new FileNotFoundException("O PDF original não foi encontrado.", fullOriginalPath);

        Directory.CreateDirectory(_rootDirectory);

        string id = Guid.NewGuid().ToString("N");
        string documentDirectory = Path.Combine(_rootDirectory, id);
        Directory.CreateDirectory(documentDirectory);

        string workingCopyPath = Path.Combine(
            documentDirectory, Path.GetFileName(fullOriginalPath));

        try
        {
            File.Copy(fullOriginalPath, workingCopyPath, overwrite: false);

            var now = DateTime.UtcNow;
            var entry = new EditedPdfEntry
            {
                Id = id,
                OriginalFileName = Path.GetFileName(fullOriginalPath),
                OriginalPath = fullOriginalPath,
                WorkingCopyPath = workingCopyPath,
                CreatedAtUtc = now,
                LastModifiedAtUtc = now
            };

            List<EditedPdfEntry> entries = ReadEntries();
            entries.Add(entry);
            WriteEntries(entries);
            return entry;
        }
        catch
        {
            try
            {
                if (Directory.Exists(documentDirectory))
                    Directory.Delete(documentDirectory, recursive: true);
            }
            catch
            {
                // A limpeza de uma cópia incompleta não deve ocultar o erro original.
            }
            throw;
        }
    }

    public void Touch(string id)
    {
        List<EditedPdfEntry> entries = ReadEntries();
        EditedPdfEntry? entry = entries.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal));

        if (entry == null || !File.Exists(entry.WorkingCopyPath))
            return;

        entry.LastModifiedAtUtc = DateTime.UtcNow;
        WriteEntries(entries);
    }

    private List<EditedPdfEntry> ReadEntries()
    {
        if (!File.Exists(_historyFilePath))
            return new List<EditedPdfEntry>();

        try
        {
            return JsonSerializer.Deserialize<List<EditedPdfEntry>>(
                File.ReadAllText(_historyFilePath), JsonOptions)
                ?? new List<EditedPdfEntry>();
        }
        catch
        {
            // Um histórico inválido não deve impedir a abertura do editor.
            return new List<EditedPdfEntry>();
        }
    }

    private void WriteEntries(List<EditedPdfEntry> entries)
    {
        Directory.CreateDirectory(_rootDirectory);
        string temporaryPath = _historyFilePath + ".tmp";

        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(entries, JsonOptions));

            File.Move(temporaryPath, _historyFilePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Limpeza temporária best-effort.
            }
        }
    }
}
