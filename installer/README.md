# Instalador Windows x64

O pacote WiX gera um MSI x64 por máquina, instala os arquivos em Program Files e cria um atalho no Menu Iniciar. A instalação exige permissão de administrador; a desinstalação é feita por Aplicativos instalados do Windows.

## Build no CI

O workflow de AssinaPDF publica:
- `AssinaPDF.exe`: build self-contained Windows x64 para validação técnica.
- `installer/AssinaPDF-Setup-x64.msi`: pacote MSI, quando o job do WiX conclui.
- `SHA256SUMS.txt`: hashes SHA-256 dos arquivos publicados.

## WebView2 Runtime

O aplicativo usa o Microsoft Edge WebView2 Runtime para exibir PDFs. O runtime é separado do SDK que compila o aplicativo e pode não estar instalado em algumas máquinas Windows 10. Antes de distribuir o MSI a clientes, deve-se decidir e testar o bootstrapper do runtime, validar a assinatura Authenticode do instalador oficial da Microsoft e documentar a instalação silenciosa. O app exibe um erro útil caso o runtime esteja ausente.

Documentação oficial: https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution

## Situação e limites

Este instalador é experimental até passar por teste de instalação/atualização/desinstalação em máquinas Windows limpas. Não está assinado com certificado de assinatura de código da Future Soluções. Por isso, o Windows SmartScreen pode exibir avisos; não se deve tentar contornar esses avisos nem afirmar que o pacote está confiável. A assinatura de código comercial deverá ser configurada antes da distribuição pública.
