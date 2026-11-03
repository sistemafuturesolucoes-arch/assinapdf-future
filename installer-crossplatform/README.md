# AssinaPDF CrossPlatform - Windows Installer



## Project



Independent MSI installer for AssinaPDF Future CrossPlatform.



The original AssinaPDF.Desktop project and installer must remain unchanged.



## Requirements



\- Windows 10 or later, 64-bit.

\- .NET Desktop Runtime 10 x64.

\- Windows x64 architecture.



## Published application



Source directory:



publish/windows-x64-distribuicao/



Executable:



AssinaPDF.MacPrototype.exe



Native PDF library:



pdfium.dll



## Installation



Installation directory:



C:\\Program Files\\Future Solucoes\\AssinaPDF



The installer creates Start Menu and Desktop shortcuts.



## .NET dependency



The current publication is framework-dependent.



.NET Desktop Runtime 10 x64 must be installed.



Automatic runtime installation or verification will be implemented in a later stage.



## Build



dotnet build installer-crossplatform/AssinaPDF.CrossPlatform.wixproj -c Release -p:PublishDir=publish/windows-x64-distribuicao/



## Pending tests



\- Installation on a clean Windows machine.

\- Application startup.

\- PDF opening and rendering.

\- PDFium native library loading.

\- Start Menu and Desktop shortcuts.

\- Version upgrade.

\- Uninstallation.

\- .NET 10 runtime verification.



## Notes



This installer is independent of the legacy installer.



Do not distribute publicly before completing tests and evaluating digital code signing.


