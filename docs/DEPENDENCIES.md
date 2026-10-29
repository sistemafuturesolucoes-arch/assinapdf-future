# Inventário de dependências e licenças

Este inventário é inicial e deve ser revisado antes de cada versão distribuída.

| Dependência | Versão fixada | Uso | Licença declarada | Observações |
|---|---:|---|---|---|
| Microsoft.Web.WebView2 | 1.0.2903.40 | Hospedar o visualizador PDF do Edge no Windows Forms | MIT (pacote) | Depende do WebView2 Runtime instalado no Windows. Conferir avisos de redistribuição do runtime no pacote final. |
| SimpleSign.PAdES | 0.9.2 | Assinatura PDF PAdES Baseline-B-B | MIT | Biblioteca recente; versão fixada. Revisão de segurança e testes independentes são bloqueadores antes da distribuição. |

## Controles de liberação

- Conferir o arquivo LICENSE e os avisos da versão exata de cada pacote NuGet.
- Gerar lista de dependências transitivas e verificar CVEs antes da publicação.
- Não atualizar versões automaticamente em um build de lançamento sem executar os testes.
- Não afirmar que a licença permissiva ou a compilação bem-sucedida substitui revisão de código, validação de assinatura ou testes de interoperabilidade.
- Registrar as versões efetivamente usadas no artefato publicado.

## Referências

- Microsoft WebView2: https://www.nuget.org/packages/Microsoft.Web.WebView2/
- SimpleSign.PAdES: https://www.nuget.org/packages/SimpleSign.PAdES/0.9.2
- Código-fonte da versão fixada: https://github.com/eupassarin/simplesign/tree/v0.9.2
