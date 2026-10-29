# Roadmap por etapas

## Etapa 1 — Fundação (atual)

- [x] Pasta independente no repositório do CRM.
- [x] Projeto Windows x64 inicial.
- [x] Janela inicial e seleção de arquivo PDF.
- [x] Documentação de arquitetura e segurança.
- [ ] Confirmar build verde no GitHub Actions.

## Etapa 2 — Leitura real de PDF

- [ ] Avaliar motores de visualização e suas licenças.
- [ ] Abrir e renderizar documentos localmente.
- [ ] Zoom, navegação por páginas, rotação, pesquisa e impressão.
- [ ] Testes para arquivos inválidos, grandes, criptografados e corrompidos.

## Etapa 3 — Certificados Windows

- [ ] Listar certificados utilizáveis no repositório do usuário atual.
- [ ] Exibir emissor, titular, validade e finalidade sem expor segredos.
- [ ] Lidar com certificados expirados, sem chave privada ou com token/cartão indisponível.
- [ ] Confirmar que a chave privada nunca é exportada nem transmitida.

## Etapa 4 — Assinatura digital

- [ ] Selecionar e aprovar biblioteca de assinatura com licença compatível.
- [ ] Implementar assinatura PAdES e preservação do documento.
- [ ] Verificar integridade criptográfica após assinar.
- [ ] Diferenciar integridade, confiança da cadeia, revogação e carimbo do tempo.
- [ ] Testar documentos no validador apropriado, incluindo o ITI quando aplicável.

## Etapa 5 — Instalador e distribuição

- [ ] Definir licença e identidade comercial do produto.
- [ ] Gerar instalador x64 reproduzível e desinstalador.
- [ ] Assinatura de código do instalador quando houver certificado adequado.
- [ ] Testar instalação, atualização, remoção e execução em Windows limpo.
- [ ] Publicar apenas builds que passem nos testes.

## Etapa 6 — macOS

- [ ] Avaliar suporte nativo, Keychain e bibliotecas compatíveis.
- [ ] Implementar assinatura e empacotamento específicos do macOS.
- [ ] Validar assinatura/notarização e distribuição antes de anunciar compatibilidade.
