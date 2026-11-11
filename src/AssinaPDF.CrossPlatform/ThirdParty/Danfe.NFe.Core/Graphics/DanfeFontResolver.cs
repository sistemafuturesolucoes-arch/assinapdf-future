using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Fonts;

namespace Danfe.NFe.Core.Graphics
{
    /// <summary>
    /// Resolve "Times New Roman" e "Liberation Serif", nesta ordem: Times New Roman do sistema,
    /// Liberation Serif do sistema e Liberation Serif embutida no assembly. Outras famílias retornam null.
    /// </summary>
    internal class DanfeFontResolver : IFontResolver
    {
        private const string FamiliaTimes = "Times New Roman";
        private const string FamiliaLiberation = "Liberation Serif";
        private const string PrefixoRecurso = "Danfe.NFe.Core.Fonts.";
        private const string PrefixoEmbutida = "embutida:";

        // Índice = (negrito ? 1 : 0) + (itálico ? 2 : 0).
        private static readonly string[][] ArquivosTimes =
        {
            new[] { "times.ttf", "Times_New_Roman.ttf", "Times New Roman.ttf" },
            new[] { "timesbd.ttf", "Times_New_Roman_Bold.ttf", "Times New Roman Bold.ttf" },
            new[] { "timesi.ttf", "Times_New_Roman_Italic.ttf", "Times New Roman Italic.ttf" },
            new[] { "timesbi.ttf", "Times_New_Roman_Bold_Italic.ttf", "Times New Roman Bold Italic.ttf" },
        };

        private static readonly string[] ArquivosLiberation =
        {
            "LiberationSerif-Regular.ttf",
            "LiberationSerif-Bold.ttf",
            "LiberationSerif-Italic.ttf",
            "LiberationSerif-BoldItalic.ttf",
        };

        private readonly object _Lock = new object();
        private readonly Dictionary<string, byte[]> _Fontes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly bool _UsarFontesDoSistema;
        private Dictionary<string, string> _ArquivosSistema;

        public DanfeFontResolver() : this(true) { }

        internal DanfeFontResolver(bool usarFontesDoSistema)
        {
            _UsarFontesDoSistema = usarFontesDoSistema;
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            bool times = string.Equals(familyName, FamiliaTimes, StringComparison.OrdinalIgnoreCase);
            if (!times && !string.Equals(familyName, FamiliaLiberation, StringComparison.OrdinalIgnoreCase)) return null;

            int estilo = (isBold ? 1 : 0) + (isItalic ? 2 : 0);

            lock (_Lock)
            {
                if (_UsarFontesDoSistema)
                {
                    if (times)
                    {
                        foreach (var arquivo in ArquivosTimes[estilo])
                        {
                            var info = TentarSistema(arquivo);
                            if (info != null) return info;
                        }
                    }

                    var liberation = TentarSistema(ArquivosLiberation[estilo]);
                    if (liberation != null) return liberation;
                }

                // Não embutimos o BoldItalic: o DANFE não usa; simulamos o itálico sobre o Bold.
                if (estilo == 3)
                {
                    CarregarEmbutida(ArquivosLiberation[1]);
                    return new FontResolverInfo(PrefixoEmbutida + ArquivosLiberation[1], false, true);
                }

                CarregarEmbutida(ArquivosLiberation[estilo]);
                return new FontResolverInfo(PrefixoEmbutida + ArquivosLiberation[estilo]);
            }
        }

        public byte[] GetFont(string faceName)
        {
            lock (_Lock)
            {
                return _Fontes.TryGetValue(faceName, out var bytes) ? bytes : null;
            }
        }

        private FontResolverInfo TentarSistema(string arquivo)
        {
            if (_ArquivosSistema == null) _ArquivosSistema = MapearArquivosSistema();

            if (!_ArquivosSistema.TryGetValue(arquivo, out var caminho)) return null;
            if (_Fontes.ContainsKey(caminho)) return new FontResolverInfo(caminho);

            try
            {
                var bytes = File.ReadAllBytes(caminho);
                if (bytes.Length == 0) return null;
                _Fontes[caminho] = bytes;
                return new FontResolverInfo(caminho);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private void CarregarEmbutida(string arquivo)
        {
            var faceName = PrefixoEmbutida + arquivo;
            if (_Fontes.ContainsKey(faceName)) return;

            using (var stream = typeof(DanfeFontResolver).Assembly.GetManifestResourceStream(PrefixoRecurso + arquivo))
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                _Fontes[faceName] = ms.ToArray();
            }
        }

        private static Dictionary<string, string> MapearArquivosSistema()
        {
            var arquivos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var visitados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var diretorio in DiretoriosDeFontes())
            {
                Varrer(diretorio, 0, arquivos, visitados);
            }

            return arquivos;
        }

        private static void Varrer(string diretorio, int profundidade, Dictionary<string, string> arquivos, HashSet<string> visitados)
        {
            if (profundidade > 8 || !visitados.Add(diretorio)) return;

            try
            {
                if (!Directory.Exists(diretorio)) return;

                foreach (var arquivo in Directory.EnumerateFiles(diretorio, "*.ttf"))
                {
                    var nome = Path.GetFileName(arquivo);
                    if (!arquivos.ContainsKey(nome)) arquivos[nome] = arquivo;
                }

                foreach (var sub in Directory.EnumerateDirectories(diretorio))
                {
                    Varrer(sub, profundidade + 1, arquivos, visitados);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
            }
        }

        private static IEnumerable<string> DiretoriosDeFontes()
        {
            var fontes = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrEmpty(fontes)) yield return fontes;

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "Microsoft", "Windows", "Fonts");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".fonts");
                yield return Path.Combine(home, ".local", "share", "fonts");
                yield return Path.Combine(home, "Library", "Fonts");
            }

            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return "/Library/Fonts";
            yield return "/System/Library/Fonts";
        }
    }
}
