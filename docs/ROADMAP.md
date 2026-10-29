# Roadmap por etapas

## Etapa 1 — Fundação

- [x] Pasta independente no repositório do CRM.
- [x] Projeto Windows x64 inicial.
- [x] Workflow de build separado.
- [x] Build/publicação self-contained inicial confirmado no GitHub Actions.

## Etapa 2 — Leitura de PDF e descoberta de certificados

- [x] Abrir PDF local por seletor de arquivo.
- [x] Aceitar arrastar e soltar PDF.
- [x] Integrar visualizador PDF via Microsoft WebView2.
- [x] Consultar certificados do repositório Pessoal do usuário atual.
- [ ] Validar zoom, pesquisa, páginas, rotação e impressão em máquinas Windows limpas.
- [ ] Testar PDFs grandes, protegidos, inválidos e corrompidos.
- [ ] Ampliar tratamento de erros e testes de documentos.

## Etapa 3 — Elegibilidade e seleção de certificados (parcial)

- [x] Exibir titular, emissor, validade e presença de chave privada.
- [x] Impedir tentativa de assinatura quando o certificado está fora da validade temporal local ou não indica chave privada.
- [ ] Aplicar política completa de usos de chave e certificados apropriados para assinatura.
- [ ] Confirmar suporte a chaves não exportáveis via Windows CryptoAPI/CNG e CSP/KSP.
- [ ] Testar certificados A1, token/cartão, PIN e provedores diferentes.

## Etapa 4 — Assinatura digital PAdES (implementação inicial)

- [x] Selecionar uma biblioteca com licença MIT para a prova de conceito.
- [x] Implementar assinatura PAdES básica com o certificado selecionado.
- [x] Configurar aparência visível por página e coordenadas PDF.
- [x] Salvar em novo arquivo por padrão e preservar o original.
- [ ] Permitir selecionar a área da assinatura clicando diretamente no preview do PDF.
- [ ] Executar testes independentes de integridade e interoperabilidade.
- [x] Adicionar verificação local de integridade/assinatura criptográfica com aviso de que cadeia, revogação e carimbo do tempo não são validados nesta etapa.
- [ ] Diferenciar resultados de integridade, cadeia de confiança, revogação e carimbo do tempo na validação completa.
- [ ] Implementar carimbo do tempo e perfis de validação de longo prazo somente após definir provedores e política.
- [ ] Validar com ferramentas apropriadas, incluindo ITI quando aplicável.

## Etapa 5 — Qualidade, segurança e distribuição Windows

- [x] Adicionar teste automatizado inicial que gera um PDF mínimo e certificado de laboratório, assina em PAdES básico e verifica preservação dos bytes de entrada.
- [ ] Expandir testes para validação criptográfica independente, PDFs existentes, certificados A1 e token/cartão.
- [ ] Auditar a dependência PAdES recente e fixar a versão aprovada.
- [ ] Fazer revisão de segurança e análise de dependências/licenças.
- [x] Criar projeto WiX para MSI x64 com instalação e desinstalação via Windows Installer.
- [ ] Validar instalação, atualização e remoção do MSI em máquinas Windows limpas.
- [ ] Detectar ou instalar WebView2 Runtime conforme estratégia aprovada.
- [ ] Definir licença e identidade comercial do produto.
- [ ] Assinar executável/instalador quando houver certificado de assinatura de código adequado.
- [ ] Testar instalação, atualização, remoção e execução em Windows limpo.
- [ ] Publicar somente builds aprovados.

## Etapa 6 — macOS

- [ ] Avaliar UI nativa, Keychain e motores PDF compatíveis.
- [ ] Implementar assinatura e empacotamento específicos do macOS.
- [ ] Validar assinatura/notarização antes de anunciar compatibilidade.
