# AssinaPDF Launcher

O workflow de CI gera os recursos binários antes de compilar este projeto:
- `Resources/AssinaPDF-Setup-x64.msi`
- `Resources/AssinaPDF-portable.zip`
- `Resources/logo-future.png`

O resultado publicado é um executável único self-contained. A opção de instalação abre o MSI embutido com elevação UAC; a opção portátil extrai o aplicativo para LocalAppData e inicia sem registrar uma instalação no Windows.