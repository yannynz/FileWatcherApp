# FileWatcherApp

Via binário>
subir no linux 
> dotnet publish -c Release -r linux-x64 --self-contained true
> cd bin/Release/net8.0/linux-x64/publish
> ./FileWatcherApp

subir no Win
> dotnet publish FileWatcherApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64 
> cd bin/Release/net8.0/win-x64/publish
> FileWatcherApp.exe

Via build n run>
> dotnet build
develop
>export DOTNET_ENVIRONMENT=Development
production
>export DOTNET_ENVIRONMENT=Production
> dotnet run --project FileWatcherApp/FileWatcherApp.csproj

## Nota de atualização 0.2.5
- Parser de OP passou a preservar o bloco de observações para extrair data, hora e modalidade de entrega com maior precisão.
- Removido o `RuntimeIdentifier` fixo; informe o runtime desejado no `dotnet publish` ao gerar binário self-contained.

• Dobras Queue

  - Abra Services/FileWatcher/FileWatcherService.cs:508 e identifique o método HandleDobrasFileAsync.
  - Garanta que a variável hasSavedSuffix use FileWatcherNaming.HasDobrasSavedSuffix(originalName) (Services/FileWatcher/FileWatcherService.cs:533).
  - Em seguida, mantenha o return logo após PublishAnalysisRequest para arquivos sem sufixo salvo (Services/FileWatcher/FileWatcherService.cs:541-559). Isso
    bloqueia a publicação desses .dxf no dobra_notifications e apenas dispara a análise.
  - Para os salvos (.m.dxf/.dxf.fcd), deixe o bloco de retry já existente publicar como hoje (Services/FileWatcher/FileWatcherService.cs:562-592).
  - Se quiser um log mais explícito de descarte, adicione _logger.LogDebug antes do return (mesmo método, entre as linhas 555-559).

  Complexidade

  - A lógica está em Services/DXFAnalysis/ComplexityScorer.cs. Altere pesos e limiares dentro dos métodos ApplySerrilhaScore, ApplyMinRadiusScore,
    ApplyCurveCountScore e ApplyMaterialScore para permitir notas fracionadas (ex.: 1.5 ou 3.7).
  - Para reforçar serrilhas misto/travada/cola, ajuste DXFAnalysisOptions.SerrilhaScoringOptions (valores lidos em ApplySerrilhaScore, linhas 168-200). Adicione
    pesos maiores e extras específicos se summary.Classification.Mista ou Travada for alto.
  - Penalize mais cortes secos e “travados” via ApplyMinRadiusScore (linhas 130-166) configurando CorteSecoAdjustment com valores decimais > 0 e, se necessário,
    adicionando novas entradas em DanglingEndThresholds (ApplyDanglingEndsScore, linhas 100-128).
  - Para arquivos de materiais sensíveis (adesivo, plástico, vinil), use ApplyMaterialScore (procure no mesmo arquivo) ajustando
    DXFAnalysisOptions.MaterialScoringOptions para acrescentar peso ao detectar palavras-chave.
  - Os pesos vêm do seu appsettings.json/appsettings.Development.json (seção DXFAnalysis:Scoring); edite lá para manter tudo configurável sem recompilar.

  Documentação

  - Atualize docs/complexidade-facas.md criando uma subseção (por exemplo “Fluxo Dobras vs Análise”) descrevendo que apenas .m.dxf/.dxf.fcd entram na fila
    dobra_notifications, enquanto .dxf base disparam apenas análise.
  - Nesse mesmo arquivo, documente os novos critérios numéricos: explique como serrilhas mistas/travadas, cola e cortes secos influenciam pesos, incluindo
    exemplos citados (NR 120253 → 1.7, NR 120184 → 5, NR 120247 → 4), e liste quais chaves ajustar em DXFAnalysisOptions.
  - Se preferir registrar histórico, adicione ao final do doc uma tabela “Ajustes Recentes” com data, motivo e valores sugeridos.
