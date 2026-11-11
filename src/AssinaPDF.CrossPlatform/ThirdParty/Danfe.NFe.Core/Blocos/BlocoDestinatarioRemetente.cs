using Danfe.NFe.Core.Elementos;
using Danfe.NFe.Core.Enumeracoes;
using Danfe.NFe.Core.Modelo;
using Danfe.NFe.Core.Tools;

namespace Danfe.NFe.Core.Blocos
{
    internal class BlocoDestinatarioRemetente : BlocoBase
    {
        public BlocoDestinatarioRemetente(DanfeViewModel viewModel, Estilo estilo) : base(viewModel, estilo)
        {
            var destinatario = viewModel.Destinatario;

            AdicionarLinhaCampos()
            .ComCampo(Strings.RazaoSocial, destinatario.RazaoSocial)
            .ComCampo(Strings.CnpjCpf, Formatador.FormatarCpfCnpj(destinatario.CnpjCpf))
            .ComCampo("Data de Emissão", viewModel.DataHoraEmissao.Formatar())
            .ComLarguras(0, 45F * Proporcao, 30F * Proporcao);

            AdicionarLinhaCampos()
            .ComCampo(Strings.Endereco, destinatario.EnderecoLinha1)
            .ComCampo(Strings.BairroDistrito, destinatario.EnderecoBairro)
            .ComCampo(Strings.Cep, Formatador.FormatarCEP(destinatario.EnderecoCep))
            .ComCampo("Data Entrada / Saída", ViewModel.DataSaidaEntrada.Formatar())
            .ComLarguras(0, 45F * Proporcao, 25F * Proporcao, 30F * Proporcao);

            AdicionarLinhaCampos()
            .ComCampo(Strings.Municipio, destinatario.Municipio)
            .ComCampo(Strings.FoneFax, Formatador.FormatarTelefone(destinatario.Telefone))
            .ComCampo(Strings.UF, destinatario.EnderecoUf)
            .ComCampo(Strings.InscricaoEstadual, destinatario.Ie)
            .ComCampo("Hora Entrada / Saída", ViewModel.HoraSaidaEntrada.Formatar())
            .ComLarguras(0, 35F * Proporcao, 7F * Proporcao, 40F * Proporcao, 30F * Proporcao);
        }

        public override string Cabecalho => "Destinatário / Remetente";
        public override PosicaoBloco Posicao => PosicaoBloco.Topo;
    }
}
