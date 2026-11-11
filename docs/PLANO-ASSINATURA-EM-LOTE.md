# Plano — Assinatura em Lote

## Objetivo
Permitir selecionar vários PDFs, atribuir um certificado digital a cada documento (ou aplicar um certificado a todos) e assinar a fila em uma única operação, mantendo o processamento local.

## Requisitos funcionais
- Importar múltiplos PDFs em uma lista, sem duplicar caminhos.
- Exibir nome, quantidade de páginas e situação de cada arquivo.
- Atribuir certificado individualmente a cada PDF.
- Permitir aplicar um certificado escolhido a todos os documentos da fila.
- Exibir certificados disponíveis no repositório do usuário e bloquear certificados sem chave privada acessível, expirados ou sem uso de assinatura permitido.
- Configurar uma ou mais posições visuais de assinatura em um PDF de referência e reaproveitar as coordenadas normalizadas nos demais documentos; validar se as páginas existem em cada arquivo.
- Assinar a fila sequencialmente, com resultado individual por documento; uma falha não deve interromper os demais.
- Exportar arquivos assinados para uma pasta escolhida, com nomes distintos, arquivos temporários e preservação dos originais.
- Exibir resumo de sucesso/falha e caminho de saída.

## Modos de exportação planejados
1. **PDFs separados:** assinar cada PDF original com o certificado atribuído àquele documento.
2. **PDF único consolidado:** combinar os PDFs ainda não assinados e assinar o resultado consolidado. Para preservar a semântica da assinatura, a primeira versão exigirá um único certificado para o arquivo final; certificados diferentes por documento não podem ser apresentados como se cada um assinasse apenas suas páginas.
3. **Separar por páginas:** gerar um PDF por página e assinar cada arquivo com o certificado atribuído ao PDF de origem.

## Segurança e integridade
- Nunca gravar PIN, senha ou chave privada no histórico ou em arquivos de configuração.
- Nunca substituir o PDF de origem.
- Não juntar PDFs já assinados e declarar que as assinaturas originais permanecem válidas. A consolidação deve ocorrer antes da assinatura e deve alertar sobre documentos que já contenham assinaturas.
- Não afirmar validação oficial ICP-Brasil, cadeia de confiança, revogação ou carimbo do tempo sem executar essas verificações.
- Manter todos os arquivos e certificados no computador do usuário.

## Planos comerciais
Durante desenvolvimento e testes, não aplicar limite de quantidade. Limites por plano serão definidos posteriormente, sem acoplar a regra comercial ao motor de assinatura.

## Etapas de implementação
1. Tela, importação de vários PDFs, atribuição individual/em grupo de certificados e assinatura de PDFs separados.
2. Testes locais com um PDF e depois com vários PDFs, inclusive certificados diferentes e falhas isoladas.
3. Exportação de PDF consolidado com validações de integridade.
4. Exportação por páginas.
5. Histórico, progresso/cancelamento e refinamentos de UX.
6. Limites por plano em etapa comercial posterior.

## Restrições do projeto
- Alterar somente `src/AssinaPDF.CrossPlatform` e documentação específica do CrossPlatform.
- Não modificar `src/AssinaPDF.Desktop`.
- Não gerar instalador nesta etapa.
- Não fazer merge com `main`; validar localmente antes.
- Preservar arquivos locais não rastreados e backups.
