using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AssinaPDF.MacPrototype.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace AssinaPDF.MacPrototype.Services;

public sealed record CteZipDocument(string SourcePath, CteDocumentSummary? Summary, string? PdfPath,
    string? Error, bool IsDuplicate = false, string? DuplicateReason = null);

public static class CteBatchZipExporter
{
    private sealed record Entry(CteZipDocument Document, string CompanyKey, string Folder);

    public static void Create(string outputZipPath, IEnumerable<CteZipDocument> documents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputZipPath);
        var items = documents?.ToList() ?? throw new ArgumentNullException(nameof(documents));
        if (items.Count == 0) throw new InvalidOperationException("Não existem registros para exportar.");
        string output = Path.GetFullPath(outputZipPath);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var item in items)
        {
            if (File.Exists(item.SourcePath) && string.Equals(output, Path.GetFullPath(item.SourcePath), cmp))
                throw new InvalidOperationException("O ZIP não pode substituir um XML original.");
            if (item.PdfPath != null && string.Equals(output, Path.GetFullPath(item.PdfPath), cmp))
                throw new InvalidOperationException("O ZIP não pode substituir um DACTE.");
        }
        string directory = Path.GetDirectoryName(output) ?? throw new InvalidOperationException("Pasta de destino inválida.");
        Directory.CreateDirectory(directory);
        string temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, false, Encoding.UTF8))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var valid = new List<Entry>(); var invalid = new List<CteZipDocument>(); var duplicates = new List<CteZipDocument>();
                foreach (var item in items)
                {
                    if (item.IsDuplicate) { duplicates.Add(item); continue; }
                    if (item.Error != null || item.Summary == null || !File.Exists(item.SourcePath) ||
                        item.PdfPath == null || !File.Exists(item.PdfPath)) { invalid.Add(item); continue; }
                    string cnpj = Digits(item.Summary.IssuerDocument);
                    if (string.IsNullOrWhiteSpace(cnpj))
                        cnpj = "SEM-CNPJ-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(item.SourcePath))))[..10];
                    valid.Add(new Entry(item, cnpj, Safe(cnpj + "-" + item.Summary.IssuerName)));
                }
                foreach (var group in valid.GroupBy(x => x.CompanyKey, StringComparer.OrdinalIgnoreCase))
                {
                    string root = "EMPRESAS/" + group.First().Folder;
                    foreach (var entry in group)
                    {
                        var item = entry.Document; var s = item.Summary!;
                        string id = Safe(s.DocumentNumber + "-" + s.AccessKey);
                        AddFile(archive, item.SourcePath, $"{root}/XML_ORIGINAIS/{Path.GetFileName(item.SourcePath)}", used);
                        AddFile(archive, item.PdfPath!, $"{root}/DACTE/DACTE-CTe{s.Model}-{id}.pdf", used);
                    }
                    AddMerged(archive, group.ToList(), $"{root}/DACTE-CONSOLIDADO.pdf", used);
                }
                AddProblems(archive, invalid, "XML_COM_ERRO", used);
                AddProblems(archive, duplicates, "DUPLICADOS", used);
                var report = archive.CreateEntry("relatorio.csv", CompressionLevel.Optimal);
                using (var stream = report.Open())
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                {
                    writer.WriteLine("Status;Motivo;Chave;CNPJ/CPF emitente;Emitente;Modelo;Número CT-e;Emissão;Valor serviço;XML;PDF");
                    foreach (var x in valid) { var s=x.Document.Summary!; Row(writer,"PROCESSADO","",s.AccessKey,s.IssuerDocument,s.IssuerName,s.Model,s.DocumentNumber,s.IssueDate,s.ServiceValue.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture),Path.GetFileName(x.Document.SourcePath),Path.GetFileName(x.Document.PdfPath)); }
                    foreach (var x in invalid) { var s=x.Summary; Row(writer,"ERRO",x.Error,s?.AccessKey,s?.IssuerDocument,s?.IssuerName,s?.Model,s?.DocumentNumber,s?.IssueDate,s?.ServiceValue.ToString("0.00"),Path.GetFileName(x.SourcePath),""); }
                    foreach (var x in duplicates) { var s=x.Summary; Row(writer,"DUPLICADO",x.DuplicateReason,s?.AccessKey,s?.IssuerDocument,s?.IssuerName,s?.Model,s?.DocumentNumber,s?.IssueDate,s?.ServiceValue.ToString("0.00"),Path.GetFileName(x.SourcePath),""); }
                }
                WriteText(archive,"LEIA-ME.txt",$"ASSINAPDF FUTURE - CENTRAL CT-e\r\n\r\nEmpresas: {valid.Select(x=>x.Folder).Distinct().Count()}\r\nDACTEs / DACTE OS gerados: {valid.Count}\r\nXMLs com erro: {invalid.Count}\r\nDuplicados: {duplicates.Count}\r\n\r\nXMLs originais preservados. PDFs individuais e consolidados organizados por emitente.\r\n",used);
            }
            File.Move(temp, output, true);
        }
        catch { try { if (File.Exists(temp)) File.Delete(temp); } catch { } throw; }
    }

    private static void AddMerged(ZipArchive archive, IReadOnlyList<Entry> items, string path, HashSet<string> used)
    {
        if (items.Count == 0) return;
        using var merged = new PdfDocument();
        foreach (var item in items) { using var source = PdfReader.Open(item.Document.PdfPath!, PdfDocumentOpenMode.Import); for (int i=0;i<source.Pages.Count;i++) merged.AddPage(source.Pages[i]); }
        if (merged.Pages.Count == 0) return;
        var entry=archive.CreateEntry(Unique(path,used),CompressionLevel.Optimal); using var output=entry.Open(); merged.Save(output);
    }
    private static void AddProblems(ZipArchive archive, IReadOnlyList<CteZipDocument> docs, string folder, HashSet<string> used)
    {
        int i=0; foreach(var d in docs) { i++; string name=$"{folder}/{i:D4}-{Safe(Path.GetFileName(d.SourcePath))}";
            if(File.Exists(d.SourcePath)) AddFile(archive,d.SourcePath,name,used);
            WriteText(archive,name+".motivo.txt",d.DuplicateReason??d.Error??"XML não processado.",used); }
    }
    private static void AddFile(ZipArchive archive,string source,string target,HashSet<string> used)
    { if(!File.Exists(source)) throw new FileNotFoundException("Arquivo não encontrado para exportação.",source);
      var entry=archive.CreateEntry(Unique(target,used),CompressionLevel.Optimal); using var input=File.OpenRead(source); using var output=entry.Open(); input.CopyTo(output); }
    private static void Row(TextWriter w,params string?[] values)=>w.WriteLine(string.Join(";",values.Select(v=>"\""+(v??"").Replace("\"","\"\"")+"\"")));
    private static void WriteText(ZipArchive a,string path,string content,HashSet<string> used)
    { var e=a.CreateEntry(Unique(path,used),CompressionLevel.Optimal); using var s=e.Open(); using var w=new StreamWriter(s,new UTF8Encoding(true)); w.Write(content); }
    private static string Unique(string path,HashSet<string> used)
    { string p=path.Replace('\\','/'); if(used.Add(p))return p; string dir=Path.GetDirectoryName(p)?.Replace('\\','/')??""; string n=Path.GetFileNameWithoutExtension(p),ext=Path.GetExtension(p); int i=2; string c; do { string part=$"{n}-{i++}{ext}"; c=string.IsNullOrEmpty(dir)?part:dir+"/"+part; }while(!used.Add(c)); return c; }
    private static string Digits(string? value)=>new((value??"").Where(char.IsDigit).ToArray());
    private static string Safe(string? value)
    { string s=(value??"").Trim().Replace("/","-").Replace("\\","-"); foreach(char c in Path.GetInvalidFileNameChars().Concat(new[]{'<','>',':','"','|','?','*'}))s=s.Replace(c,'_'); s=s.Trim('.',' '); return string.IsNullOrWhiteSpace(s)?"SEM-NOME":s.Length>100?s[..100]:s; }
}
