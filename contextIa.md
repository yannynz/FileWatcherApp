# FileWatcherApp – Contexto Operacional

## Estrutura atual
- **Watcher principal (`Program.cs`)**
  - Monitora diretórios de Laser, Facas, Dobradeira e NR.
  - Ao detectar `.dxf` ligado a NR/OP, resolve o arquivo real em `D:\Dobradeira\Facas para Dobrar\*.dxf` antes de publicar o pedido de análise (`facas.analysis.request`).
  - Publica metadados (`opId`, `sourcePath`, `resolvedPath`, flags diversas) via RabbitMQ.

- **Pipeline de análise (`Services/DXFAnalysis/…`)**
  - `DXFAnalyzer` calcula métricas geométricas e agora também gera `DXFSerrilhaSummary` combinando:
    - Configuração `DXFAnalysis.SerrilhaSymbols` (regex para blocos/atributos, `SemanticType`, `BladeCode`, defaults).
    - Explosão de blocos para estimar comprimento/tooth count por tipo.
    - Anotações textuais via `DXFAnalysis.SerrilhaTextSymbols` (regex com grupos nomeados para código, comprimento e dentes, além de `SemanticTypeGroup`/`SemanticTypeFormat`) – cobre casos como `X=2x1 23,8` em desenhos “explodidos”.
    - Apuração de símbolos desconhecidos com logging + counter `serrilha_unknown_symbol`.
  - `ComplexityScorer` sumariza com pesos fracionados (`Scoring.Serrilha`) e explica o score.
  - `DXFAnalysisWorker` consome a fila, publica o resultado em `facas.analysis.result`, gera imagem (SkiaSharp) e utiliza cache conforme configuração.

- **Ferramentas auxiliares**
  - `Tools/DxfInspector`: inspeciona layers/blocos/textos de DXFs, faz fallback de AC1014→AC1015.
  - `Tools/DxfFixtureGenerator`: produz fixtures sintéticos (`tests/resources/dxf/*.dxf`).
  - `scripts/dxf-symbol-audit.csx`: roda a análise diretamente (`dotnet script …`), útil para debug manual.

## Configuração e Ambientes
- `appsettings.json`: espelha a configuração de produção (RabbitMQ em `192.168.10.36`, pastas reais em `C:\FacasDXF` etc.).
- `appsettings.Development.json`: RabbitMQ `localhost`, cache/renders em `./artifacts`, facilita testes com `DOTNET_ENVIRONMENT=Development`.
- Overrides via ambiente: prefixo `RabbitMq__…` ou `DXFAnalysis__…`.

## Testes
- Executar `dotnet test tests/FileWatcherApp.Tests/FileWatcherApp.Tests.csproj --no-build`.
  - Inclui `SerrilhaAnalysisTests` para fixtures sintéticos.
- Regerar fixtures: `dotnet run --project Tools/DxfFixtureGenerator`.

### Abril/2025
- Novos fixtures (`tests/resources/dxf/*`) para calibrar zipper, 3 pt volumoso e layouts simples.
- `ComplexityCalibrationTests` garante score + explicações (tolerância ±0.25) para NRs reais e sintéticos.
- `DXFMetricsExtractionTests` valida métricas extraídas: bocas (`ClosedLoops`), materiais (`SpecialMaterials`) e vinco 3 pt.

## Operação local (ensaio end-to-end)
1. `docker run -d --rm --name rabbitmq-dev -p 5672:5672 -p 15672:15672 rabbitmq:3-management`.
2. `DOTNET_ENVIRONMENT=Development dotnet run --project FileWatcherApp.csproj` (ou binário publicado).
3. Publicar payload JSON em `facas.analysis.request` com `fullPath` do DXF observado (o watcher resolve para “Facas para Dobrar” automaticamente).
4. Consumir `facas.analysis.result` para validar métricas/score/imagem.

## Observações
- Fallback: se o DXF não for localizado em `Facas para Dobrar`, o pipeline usa o arquivo original que disparou o evento.
- Logs (via `ILogger`) acompanham símbolos desconhecidos, arquivos resolvidos e eventuais falhas de render/análise.
