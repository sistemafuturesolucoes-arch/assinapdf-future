using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Danfe.NFe.Core.Blocos;
using Danfe.NFe.Core.Elementos;
using Danfe.NFe.Core.Enumeracoes;
using Danfe.NFe.Core.Graphics;
using Danfe.NFe.Core.Modelo;

namespace Danfe.NFe.Core
{
    public class DanfeDoc : IDisposable
    {
        public DanfeViewModel ViewModel { get; private set; }

        /// <summary>
        /// Documento PDF (PDFsharp).
        /// </summary>
        public PdfDocument PdfDocument { get; private set; }

        internal BlocoCanhoto Canhoto { get; private set; }
        internal BlocoIdentificacaoEmitente IdentificacaoEmitente { get; private set; }

        internal List<BlocoBase> _Blocos;
        internal Estilo EstiloPadrao { get; private set; }

        internal List<DanfePagina> Paginas { get; private set; }

        private readonly string _FonteFamilia;
        private bool _FoiGerado;

        /// <summary>
        /// Imagem raster (JPG/PNG) do logo. Exclusivo com <see cref="_LogoPdfForm"/>.
        /// </summary>
        private XImage _LogoImage;

        /// <summary>
        /// Logo em formato vetorial — primeira página de um PDF. Renderizado como XImage.
        /// </summary>
        private XImage _LogoPdfForm;

        public DanfeDoc(DanfeViewModel viewModel)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            // Registra o resolver de fontes do DANFE (Times New Roman com Liberation Serif
            // embutida como piso). Idempotente: só roda uma vez.
            DanfeFontResolverSetup.EnsureInitialized();

            _Blocos = new List<BlocoBase>();
            PdfDocument = new PdfDocument();

            // De acordo com o item 7.7, a fonte deve ser Times New Roman ou Courier New.
            _FonteFamilia = "Times New Roman";

            EstiloPadrao = CriarEstilo();

            Paginas = new List<DanfePagina>();
            Canhoto = CriarBloco<BlocoCanhoto>();
            IdentificacaoEmitente = AdicionarBloco<BlocoIdentificacaoEmitente>();
            AdicionarBloco<BlocoDestinatarioRemetente>();

            if (ViewModel.LocalRetirada != null && ViewModel.ExibirBlocoLocalRetirada)
                AdicionarBloco<BlocoLocalRetirada>();

            if (ViewModel.LocalEntrega != null && ViewModel.ExibirBlocoLocalEntrega)
                AdicionarBloco<BlocoLocalEntrega>();

            if (ViewModel.Duplicatas.Count > 0)
                AdicionarBloco<BlocoDuplicataFatura>();

            AdicionarBloco<BlocoCalculoImposto>(ViewModel.Orientacao == Orientacao.Paisagem ? EstiloPadrao : CriarEstilo(4.75F));
            AdicionarBloco<BlocoTransportador>();
            AdicionarBloco<BlocoDadosAdicionais>(CriarEstilo(tFonteCampoConteudo: 8));

            if (ViewModel.CalculoIssqn.Mostrar)
                AdicionarBloco<BlocoCalculoIssqn>();

            AdicionarMetadata();

            _FoiGerado = false;
        }

        /// <summary>
        /// Define a logo a partir de um stream de imagem raster (JPEG, PNG ou BMP).
        /// </summary>
        /// <exception cref="NotSupportedException">Formato de imagem não suportado.</exception>
        public void AdicionarLogoImagem(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            _LogoImage = CarregarLogo(stream);
        }

        /// <summary>
        /// Define a logo a partir da primeira página de um PDF (logo vetorial).
        /// </summary>
        public void AdicionarLogoPdf(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            // XImage.FromStream detecta o PDF e devolve um XPdfForm.
            _LogoPdfForm = CarregarLogo(stream);
        }

        /// <exception cref="NotSupportedException">Formato de imagem não suportado.</exception>
        public void AdicionarLogoImagem(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(nameof(path));
            _LogoImage = CarregarLogo(() => XImage.FromFile(path));
        }

        public void AdicionarLogoPdf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(nameof(path));
            _LogoPdfForm = CarregarLogo(() => XImage.FromFile(path));
        }

        private static XImage CarregarLogo(Stream stream)
        {
            // Cópia em memória: o XImage guarda o stream, e o chamador pode descartá-lo antes de Gerar().
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            return CarregarLogo(() => XImage.FromStream(ms));
        }

        private static XImage CarregarLogo(Func<XImage> carregar)
        {
            try
            {
                return carregar();
            }
            catch (InvalidOperationException ex)
            {
                throw new NotSupportedException(
                    "Formato de logo não suportado. Use JPEG, PNG (8 bits por canal) ou BMP em AdicionarLogoImagem, ou PDF em AdicionarLogoPdf. Detalhe: " + ex.Message, ex);
            }
        }

        private void AdicionarMetadata()
        {
            var info = PdfDocument.Info;
            info.CreationDate = DateTime.Now;
            info.Creator = string.Format("{0} {1} - {2}",
                "NFe.Danfe.PdfSharp",
                VersaoPacote(),
                "https://github.com/flavsjr/Danfe.NFe.Core");
            info.Title = "DANFE (Documento auxiliar da NFe)";
            info.Subject = $"DANFE - Chave: {ViewModel.ChaveAcesso}";
            info.Keywords = "DANFE;NFE;ChaveAcesso=" + ViewModel.ChaveAcesso;
        }

        // A AssemblyVersion fica presa em 2.0.1.0 por compatibilidade; a versão do pacote vem da InformationalVersion.
        private static string VersaoPacote()
        {
            var versao = typeof(DanfeDoc).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
            var mais = versao.IndexOf('+');
            return mais < 0 ? versao : versao.Substring(0, mais);
        }

        private Estilo CriarEstilo(float tFonteCampoCabecalho = 6, float tFonteCampoConteudo = 10)
        {
            return new Estilo(_FonteFamilia, tFonteCampoCabecalho, tFonteCampoConteudo);
        }

        public void Gerar()
        {
            if (_FoiGerado) throw new InvalidOperationException("O Danfe já foi gerado.");

            IdentificacaoEmitente.Logo = _LogoPdfForm ?? _LogoImage;
            var tabela = new TabelaProdutosServicos(ViewModel, EstiloPadrao);

            while (true)
            {
                DanfePagina p = CriarPagina();

                tabela.SetPosition(p.RetanguloCorpo.Location);
                tabela.SetSize(p.RetanguloCorpo.Size);
                tabela.Draw(p.Gfx);

                // No PDFsharp cada página tem seu próprio XGraphics que precisa ser
                // fechado (Dispose) para que o conteúdo seja gravado no PDF.
                p.Dispose();

                if (tabela.CompletamenteDesenhada) break;
            }

            PreencherNumeroFolhas();
            _FoiGerado = true;
        }

        private DanfePagina CriarPagina()
        {
            DanfePagina p = new DanfePagina(this);
            Paginas.Add(p);
            p.DesenharBlocos(Paginas.Count == 1);
            p.DesenharCreditos();

            if (ViewModel.TipoAmbiente == 2)
                p.DesenharAvisoHomologacao();

            return p;
        }

        internal T CriarBloco<T>() where T : BlocoBase
        {
            return (T)Activator.CreateInstance(typeof(T), ViewModel, EstiloPadrao);
        }

        internal T CriarBloco<T>(Estilo estilo) where T : BlocoBase
        {
            return (T)Activator.CreateInstance(typeof(T), ViewModel, estilo);
        }

        internal T AdicionarBloco<T>() where T : BlocoBase
        {
            var bloco = CriarBloco<T>();
            _Blocos.Add(bloco);
            return bloco;
        }

        internal T AdicionarBloco<T>(Estilo estilo) where T : BlocoBase
        {
            var bloco = CriarBloco<T>(estilo);
            _Blocos.Add(bloco);
            return bloco;
        }

        internal void AdicionarBloco(BlocoBase bloco)
        {
            _Blocos.Add(bloco);
        }

        internal void PreencherNumeroFolhas()
        {
            int nFolhas = Paginas.Count;
            for (int i = 0; i < Paginas.Count; i++)
            {
                // Precisamos reabrir o XGraphics da página para gravar o número.
                Paginas[i].DesenhaNumeroPaginas(i + 1, nFolhas);
            }
        }

        public void Salvar(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(nameof(path));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                SalvarDocumento(fs);
            }
        }

        public void Salvar(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            SalvarDocumento(stream);
        }

        /// <summary>
        /// Retorna os bytes do PDF gerado.
        /// </summary>
        public byte[] ObterPdfBytes(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            SalvarDocumento(stream);
            if (stream is MemoryStream ms) return ms.ToArray();

            using (var tmp = new MemoryStream())
            {
                stream.Position = 0;
                stream.CopyTo(tmp);
                return tmp.ToArray();
            }
        }

        /// <summary>
        /// O PDFsharp 6 só salva um documento uma vez e depois bloqueia o acesso a ele; o PdfSharpCore
        /// permitia salvar de novo e continuar lendo <see cref="PdfDocument"/>. Para manter esse
        /// comportamento, salvamos em memória e reabrimos o resultado como o novo <see cref="PdfDocument"/>.
        /// </summary>
        private void SalvarDocumento(Stream destino)
        {
            var ms = new MemoryStream();
            PdfDocument.Save(ms, false);
            ms.WriteTo(destino);
            if (destino.CanRead && destino.CanSeek) destino.Position = 0;

            ms.Position = 0;
            PdfDocument = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);
        }

        #region IDisposable Support
        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    foreach (var p in Paginas) p.Dispose();
                    PdfDocument?.Dispose();
                    _LogoImage?.Dispose();
                    _LogoPdfForm?.Dispose();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
        #endregion
    }
}
