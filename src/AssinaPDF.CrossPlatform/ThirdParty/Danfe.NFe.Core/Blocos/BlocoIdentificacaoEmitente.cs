using System.Drawing;
using PdfSharp.Drawing;
using Danfe.NFe.Core.Elementos;
using Danfe.NFe.Core.Enumeracoes;
using Danfe.NFe.Core.Modelo;
using Danfe.NFe.Core.Tools;

namespace Danfe.NFe.Core.Blocos
{
    internal class BlocoIdentificacaoEmitente : BlocoBase
    {
        public const float LarguraCampoChaveNFe = 93F;
        public const float AlturaLinha1 = 30;

        NumeroNfSerie2 ifdNfe;
        IdentificacaoEmitente idEmitente;

        public BlocoIdentificacaoEmitente(DanfeViewModel viewModel, Estilo estilo) : base(viewModel, estilo)
        {

            var textoConsulta = new TextoSimples(Estilo, Strings.TextoConsulta)
            {
                Height = 8,
                AlinhamentoHorizontal = AlinhamentoHorizontal.Centro,
                AlinhamentoVertical = AlinhamentoVertical.Centro,
                TamanhoFonte = 9
            };

            var campoChaveAcesso = new Campo("Chave de Acesso", Formatador.FormatarChaveAcesso(ViewModel.ChaveAcesso), estilo, AlinhamentoHorizontal.Centro) { Height = Constantes.CampoAltura };
            var codigoBarras = new Barcode128C(viewModel.ChaveAcesso, Estilo) { Height = AlturaLinha1 - textoConsulta.Height - campoChaveAcesso.Height };

            var coluna3 = new VerticalStack();
            coluna3.Add(codigoBarras, campoChaveAcesso, textoConsulta);

            ifdNfe = new NumeroNfSerie2(estilo, ViewModel);
            idEmitente = new IdentificacaoEmitente(Estilo, ViewModel);

            FlexibleLine fl = new FlexibleLine() { Height = coluna3.Height }
            .ComElemento(idEmitente)
            .ComElemento(ifdNfe)
            .ComElemento(coluna3)
            .ComLarguras(0, 15, 46.5F);

            MainVerticalStack.Add(fl);

            AdicionarLinhaCampos()
                .ComCampo("Natureza da operação", ViewModel.NaturezaOperacao)
                .ComCampo("Protocolo de autorização", ViewModel.ProtocoloAutorizacao)
                .ComLarguras(0, 46.5F);

            AdicionarLinhaCampos()
                .ComCampo("Inscrição Estadual", ViewModel.Emitente.Ie)
                .ComCampo("Inscrição Estadual do Subst. Tributário", ViewModel.Emitente.IeSt)
                .ComCampo("Cnpj", Formatador.FormatarCnpj(ViewModel.Emitente.CnpjCpf))
                .ComLargurasIguais();

        }

        public XImage Logo
        {
            get => idEmitente.Logo;
            set => idEmitente.Logo = value;
        }

        public override PosicaoBloco Posicao => PosicaoBloco.Topo;
        public override bool VisivelSomentePrimeiraPagina => false;

        public RectangleF RetanguloNumeroFolhas => ifdNfe.RetanguloNumeroFolhas;
    }
}
