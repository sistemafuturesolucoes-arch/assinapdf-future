# AssinaPDF Future — Editor Web

Editor React/Vite isolado, baseado na tecnologia e no fluxo de edição do Typecraft PDF Studio (Lovable), integrado ao aplicativo Avalonia por `NativeWebView`.

## Build local

```powershell
cd src/AssinaPDF.CrossPlatform/EditorWeb
npm install
npm run build
```

A saída estática é gerada em `dist/`. Em seguida, compile o projeto .NET CrossPlatform normalmente. O projeto copia `EditorWeb/dist` para a distribuição desktop.

## Integração desktop

- `EditorWebWindow` hospeda os arquivos estáticos em um servidor HTTP temporário ligado exclusivamente a `127.0.0.1`, com porta aleatória.
- A janela de edição nova e os documentos recentes são abertos pelo novo editor.
- A abertura de PDFs usa o seletor nativo Avalonia e cria uma cópia de trabalho no histórico local.
- A exportação solicita o destino pelo seletor nativo; o PDF exportado e a cópia de trabalho são preservados.
- O bridge aceita somente mensagens tipadas `editorReady`, `openPdf` e `savePdf`; caminhos de arquivo não são recebidos do JavaScript.
- O servidor local aplica Content Security Policy e não disponibiliza endpoints de escrita.

## Limites de segurança e escopo

- Não há autenticação, telemetria, analytics, fontes remotas ou chamadas para serviços externos.
- PDFs são processados localmente com PDF.js e pdf-lib.
- O frontend nunca recebe certificados digitais nem senhas.
- Assinatura PAdES, validação de assinaturas e conversões continuam nos serviços nativos C#; não foram substituídos pelo editor web.
- O projeto original `src/AssinaPDF.Desktop/` permanece intocado.
- A remoção de glifos originais é aplicada a textos horizontais que o analisador consegue localizar. Em PDFs complexos, texto rotacionado ou conteúdo não reconhecido pode exigir uma máscara branca de fallback; a exportação deve ser conferida visualmente.
- A extração atual prioriza texto horizontal e fontes PDF padrão Helvetica, Times e Courier. Fontes incorporadas especiais, tabelas complexas e texto vertical precisam de validação adicional.

## Verificação

Os arquivos foram revisados estaticamente e integrados na branch de desenvolvimento. Ainda não foram executados `npm run build`, `dotnet build` ou testes automatizados nesta etapa.