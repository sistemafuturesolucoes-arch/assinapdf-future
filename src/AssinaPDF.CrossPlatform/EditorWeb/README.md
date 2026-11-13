# AssinaPDF Future — Editor Web

Frontend isolado do editor PDF, baseado no núcleo de edição do Typecraft PDF Studio (Lovable), adaptado para o aplicativo desktop Future.

## Build local

```powershell
cd src/AssinaPDF.CrossPlatform/EditorWeb
npm install
npm run build
```

A saída estática é gerada em `dist/`. O app desktop deve distribuir esses arquivos localmente e abrir somente a origem local controlada pelo próprio AssinaPDF.

## Limites de segurança

- Não há autenticação, telemetria, analytics ou chamadas para serviços remotos.
- Os PDFs são processados no navegador nativo local (PDF.js/pdf-lib).
- O frontend não recebe certificados nem senhas.
- Assinatura PAdES, validação ICP-Brasil e conversões continuam nos serviços nativos C#.
- A comunicação com o host desktop será feita por mensagens tipadas e validadas; não aceitar caminhos de arquivo arbitrários vindos do JavaScript.

## Estado

Este é o workspace isolado inicial. A janela Avalonia ainda não foi redirecionada para ele; a integração do host e o bridge serão feitos em etapa separada para não interromper o editor atual.
