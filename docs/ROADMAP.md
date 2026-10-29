# Roadmap por etapas

## Etapa 1 — Fundação

- [x] Pasta independente no repositório do CRM.
- [x] Projeto Windows x64 inicial.
- [x] Workflow de build separado.
- [x] Build/publicação inicial confirmado no GitHub Actions.

## Etapa 2 — Leitura de PDF e descoberta de certificados (em implementação)

- [x] Abrir PDF local por seletor de arquivo.
- [x] Aceitar arrastar e soltar PDF.
- [x] Integrar visualizador PDF via Microsoft WebView2.
- [x] Consultar certificados do repositório Pessoal do usuário atual.
- [ ] Validar zoom, pesquisa, páginas, rotação e impressão em máquinas Windows limpas.
- [ ] Testar PDFs grandes, protegidos, inválidos e corrompidos.
- [ ] Criar testes automatizados e tratamento de erros adicional.

## Etapa 3 — Elegibilidade e seleção de certificados

- [ ] Filtrar certificados adequados para assinatura conforme política definida.
- [ ] Exibir titular, emissor, validade, uso de chave e chave privada disponível.
- [ ] Lidar com certificados expirados, sem chave privada ou com token/cartão indisponível.
- [ ] Confirmar suporte a chaves não exportáveis via Windows CryptoAPI/CNG e CSP/KSP.

## Etapa 4 — Assinatura digital PAdES

- [ ] Selecionar biblioteca com licença compatível com o produto comercial.
- [ ] Implementar assinatura PAdES sem exportar chaves privadas.
- [ ] Salvar em novo arquivo por padrão e preservar o original.
- [ ] Verificar integridade criptográfica após assinar.
- [ ] Diferenciar integridade, confiança da cadeia, revogação e carimbo do tempo.
- [ ] Testar com certificados de software e tokens/cartões relevantes.
- [ ] Validar em ferramentas apropriadas, incluindo ITI quando aplicável.

## Etapa 5 — Distribuição Windows

- [ ] Criar instalador x64 reproduzível e desinstalador.
- [ ] Detectar ou instalar WebView2 Runtime conforme estratégia aprovada.
- [ ] Definir licença e identidade comercial do produto.
- [ ] Assinar executável/instalador quando houver certificado de assinatura de código adequado.
- [ ] Testar instalação, atualização, remoção e execução em Windows limpo.
- [ ] Publicar somente builds aprovados.

## Etapa 6 — macOS

- [ ] Avaliar UI nativa, Keychain e motores PDF compatíveis.
- [ ] Implementar assinatura e empacotamento específicos do macOS.
- [ ] Validar assinatura/notarização antes de anunciar compatibilidade.
