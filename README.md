# FileWatcherApp

subir no linux
> dotnet publish -c Release -r linux-x64 --self-contained true
> cd bin/Release/net8.0/linux-x64/publish
> ./FileWatcherApp

subir no Win
> dotnet publish -c Release -r win-x64 --self-contained true
> cd bin/Release/net8.0/win-x64/publish
> FileWatcherApp.exe

## Nota de atualização 0.2.5
- Parser de OP passou a preservar o bloco de observações para extrair data, hora e modalidade de entrega com maior precisão.
- Removido o `RuntimeIdentifier` fixo; informe o runtime desejado no `dotnet publish` ao gerar binário self-contained.
