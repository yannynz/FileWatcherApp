# Prompt técnico (cole como está)

> **Contexto do repositório**
>
> * App: [`FileWatcherApp`](https://github.com/yannynz/FileWatcherApp) — hoje já observa arquivos e publica eventos via RabbitMQ.
> * Backend: [`organizador-producao`](https://github.com/yannynz/organizador-producao) — vai consumir resultados.
>
> **Objetivo**
> Implementar, no `FileWatcherApp`, uma **pipeline determinística** de análise de **complexidade de facas** (arquivos `.dxf`) **sem dependência de ML**. O sistema deve:
>
> 1. extrair métricas geométricas/semânticas do DXF,
> 2. renderizar uma imagem padronizada (PNG),
> 3. calcular um **Score 0–5** por **regras heurísticas** transparentes,
> 4. publicar um JSON de resultado em RabbitMQ (`facas.analysis.result`),
> 5. ser **robusto, performático e observável**, sem bloquear o watcher.
>
> **Stack desejado**
>
> * .NET 8 / C# 12
> * `netDxf` (parsing DXF)
> * `SkiaSharp` (renderização PNG) **ou** `System.Drawing.Common` em Windows; preferir `SkiaSharp` por compatibilidade cross-platform.
> * `Newtonsoft.Json` (serializar mensagens)
> * `RabbitMQ.Client`
>
> **Arquitetura e pastas**
ultilize a estrutura ja presente adaptando se necessario  

## Levantamento dos DXFs reais (NR119812 / NR 120184)

- Criamos o utilitário `Tools/DxfInspector` para auditar rapidamente layers, blocos, inserts e entidades textuais usando `netDxf`. O loader trata automaticamente DXFs AutoCAD 14 convertendo o header `$ACADVER` para `AC1015` em memória.
- Ambos os arquivos fornecidos (`NR119812.dxf`, `NR 120184.dxf`) possuem apenas duas layers (`0` e `LAYOUT_PONTES`/`FACA_PONTES`), e nenhum bloco além de `*MODEL_SPACE`/`*PAPER_SPACE`; não há entidades `INSERT`, `TEXT` ou `MTEXT` registradas.
- As entidades encontradas restringem-se a `LINE`, `ARC` e `CIRCLE` (NR119812: 3218 linhas, 570 arcos, 35 círculos; NR 120184: 3016 linhas, 224 arcos, 42 círculos), todas na mesma camada de layout. Os desenhos de serrilha aparentam estar “explodidos” como geometria bruta, sem nomes de bloco ou atributos que identifiquem o tipo da lâmina.
- Conclusão preliminar: a codificação de serrilhas por símbolo ainda não é visível nesses dois DXFs; será necessário obter amostras com `INSERT`/`BLOCK` nomeados ou alinhar com o time se o dicionário visual permanecer explodido. Mesmo assim, manteremos o design preparado para mapear símbolos configuráveis (`SymbolNamePattern`, `AttributePattern`, etc.).

### Detecção de serrilha via símbolos

- `DXFAnalysisOptions` agora expõe `SerrilhaSymbols`, cada item com `SymbolNamePattern`, `AttributePattern` opcional, `SemanticType`, `BladeCode` e valores default (`DefaultToothCount`, `DefaultLength`). As expressões são pré-compiladas para acelerar o matching.
- O `DXFAnalyzer` varre `Insert`/`BlockReference`, explode a geometria para estimar comprimento (quando necessário) e agrega um `DXFSerrilhaSummary` (publicado em `DXFAnalysisResult.metrics.serrilha`). Contadores de símbolos desconhecidos são logados (`serrilha_unknown_symbol`) e enviados na estrutura `UnknownSymbols`.
- Além dos blocos, textos (`TEXT`/`MTEXT`) podem ser mapeados via `DXFAnalysis:SerrilhaTextSymbols`: cada regex captura código, comprimento e dentes (via grupos nomeados). Exemplo:

```json
"SerrilhaTextSymbols": [
  {
    "TextPattern": "(?<code>[A-Z])\\s*[-=]\\s*(?<descriptor>[0-9]+(?:x[0-9\\.,]+)?)\\s+(?<length>[0-9]+[\\.,]?[0-9]*)(?:\\s+(?<teeth>[0-9]+)\\s*(?:d|dentes?)?)?",
    "SemanticType": "serrilha",
    "SemanticTypeGroup": "code",
    "SemanticTypeFormat": "serrilha_{value}",
    "BladeCodeGroup": "descriptor",
    "UppercaseBladeCode": false,
    "UppercaseSemanticType": true,
    "AllowMultipleMatches": true,
    "LengthGroup": "length",
    "ToothCountGroup": "teeth"
  }
]
```

Esse exemplo converte `X=2x1 23,8 12d` em `semanticType=SERRILHA_X`, `bladeCode=2x1`, `length=23.8 mm` e `toothCount=12`. Valores numéricos aceitam vírgula ou ponto; múltiplos matches no mesmo texto são suportados.
- A heurística de corte seco cruza códigos de lâmina complementares com pares de linhas quase paralelas e offset pequeno. A configuração vive em `DXFAnalysis.CorteSeco` (offset máximo, tolerância angular, comprimento mínimo, camadas alvo).
- O watcher agora normaliza o caminho antes de publicar para análise: quando identifica um `.dxf` a partir das NRs, ele procura o arquivo correspondente em `D:\Dobradeira\Facas para Dobrar\*.dxf` (priorizando a versão final para produção) e envia esse path resolvido para a fila.
- O `ComplexityScorer` passou a trabalhar com `double` e pesos configuráveis em `Scoring.Serrilha` (presence, mista, múltiplos tipos, lâminas manuais via `BladeCode`). As explicações citam quantidade e nomes de símbolos, e o bloco `Scoring.MinRadius` define faixa neutra / ajuste quando `metrics.serrilha.isCorteSeco == true`.
- Fixtures sintéticos para testes vivem em `tests/resources/dxf/` e podem ser regenerados com `dotnet run --project Tools/DxfFixtureGenerator`. O script `scripts/dxf-symbol-audit.csx` roda a análise pontual (`dotnet script scripts/dxf-symbol-audit.csx -- <arquivo.dxf> [appsettings.json]`) para auditar serrilha antes do deploy – requer `dotnet build` prévio para popular `bin/Debug/net8.0`.

## Fluxo Dobras vs Análise

- Somente arquivos finalizados (`*.m.dxf` ou `*.dxf.fcd`) entram em `dobras_notifications`. A verificação usa `FileWatcherNaming.HasDobrasSavedSuffix` diretamente sobre o nome original.
- Arquivos base (`*.dxf` sem sufixo) agora disparam apenas a análise automática (`PublishAnalysisRequest`) e retornam em seguida. Nenhuma mensagem de dobra é publicada, evitando ruído para a esteira.
- O log `_logger.LogDebug("[DOBRAS] Publicação na fila '{Queue}' suprimida...")` reforça quando um arquivo sem sufixo salvo é descartado da publicação.
- O comportamento antigo permanece para arquivos salvos: o bloco de retry existente continua publicando para `dobras_notifications` após o sucesso da análise.

### Escalonamento fracionado de complexidade

- `Scoring.NumCurvesStep`, `NumCurvesStepWeight` e `NumCurvesStepMaxContribution` introduzem incrementos progressivos proporcionais ao excesso de curvas acima do limiar base (`NumCurves`). Isso gera notas intermediárias (ex.: 1.5, 3.7) em vez de saltos inteiros.
- A seção `Scoring.Serrilha` ganhou `TotalCountThresholds`, `MistaCountThresholds` e `TravadaCountThresholds`, permitindo graduar o peso conforme a incidência de serrilhas mistas/travadas. `ColaSemanticHints` + `ColaWeight`/`ColaCountThresholds` reforçam serrilhas de cola/ser-col.
- Para corte seco, `Scoring.MinRadius.CorteSecoPairThresholds` adiciona penalidades adicionais conforme o número de pares detectados, somando-se a `CorteSecoAdjustment`.
- Materiais sensíveis são tratados em `Scoring.Materials`: `KeywordOverrides` cobre textos como "lam adesivo" ou "vinil adesivo", ampliando a pontuação para trabalhos de maior responsabilidade.
- Na inicialização, o `ComplexityScorer` registra (`[SCORER] Pesos carregados`) os pesos efetivos vindos do appsettings — útil para confirmar que o publish carregou a calibração correta antes de validar os scores.
- `DXFAnalysis.Cache.Bypass` força o reprocessamento ignorando `.analysis.json` antigos; com ele ativo nenhum resultado é lido ou gravado em cache.
- Referências rápidas:
  - `NR 120253.dxf` → meta de 1.7 (serrilhas pequenas, poucas curvas acima do mínimo).
  - `NR 120184.dxf` → meta de 5.0 (serrilhas mistas/travadas abundantes, combinações de materiais).
  - `NR119812.dxf` → permanece ~3.0 (dimensão + serrilhas travadas controladas).
  - `NR 120247.dxf` → meta entre 3.7 e 4.0 (adesivo/plástico/vinil com alta responsabilidade).

> **Configuração (appsettings.json)**
>
> ```json
> "DXFAnalysis": {
>   "WatchFolder": "C:\\FacasDXF",
>   "OutputImageFolder": "C:\\FacasDXF\\Renders",
>   "RabbitQueueRequest": "facas.analysis.request",
>   "RabbitQueueResult": "facas.analysis.result",
>   "ImageDpi": 300,
>   "ImagePadding": 20,
>   "DefaultUnit": "mm",
>   "Parallelism": 4,
>   "ReprocessSameHash": false,
>   "MinCurveRadiusTolerance": 0.01,
>   "GapTolerance": 0.05,
>   "OverlapTolerance": 0.05,
>   "ChordTolerance": 0.2,
>   "ParseTimeout": "00:00:20",
>   "RenderTimeout": "00:00:15",
>   "CacheFolder": "C:\\FacasDXF\\Cache",
>   "Cache": {
>     "Bypass": false
>   },
>   "CorteSeco": {
>     "Enabled": true,
>     "MaxParallelAngleDegrees": 5.0,
>     "MaxOffsetMillimeters": 0.45,
>     "MinOverlapRatio": 0.65,
>     "MinLengthMillimeters": 8.0,
>     "TargetLayerTypes": ["serrilha", "serrilha_mista", "corte"]
>   },
>   "LayerMapping": {
>     "corte": ["CUT", "CORTE", "CUTTER", "FACA_PONTES", "LAYOUT_PONTES"],
>     "vinco": ["VINCO", "FOLD", "SCORE"],
>     "serrilha": ["SERRILHA", "PERF"],
>     "serrilha_mista": ["MISTA"],
>     "tresPt": ["3PT", "THREE_PT", "THREE-POINT", "VINCO3PT", "VINCO_3PT"]
>   },
>   "SpecialMaterialLayerMapping": {
>     "adesivo": ["ADES", "ADESIVO", "LAM_ADESIVO", "SEL[_\\s-]*COLA", "^COLA$", "SELAGEM"],
>     "borracha": ["BORRACHA", "GOMA"]
>   },
>   "SerrilhaSymbols": [
>     {
>       "SymbolNamePattern": "^SERRILHA_FINA$",
>       "SemanticType": "serrilha_fina",
>       "BladeCode": "FINA_PADRAO",
>       "DefaultToothCount": 40,
>       "DefaultLength": 100.0
>     },
>     {
>       "SymbolNamePattern": "^SERRILHA_MISTA_[A-Z]+$",
>       "AttributePattern": "LAMINA\\s*MISTA",
>       "SemanticType": "serrilha_mista",
>       "BladeCode": "MISTA",
>       "DefaultToothCount": 28
>     },
>     {
>       "SymbolNamePattern": "^SERRILHA_ZIPPER$",
>       "SemanticType": "serrilha_zipper",
>       "BladeCode": "ZIPPER"
>     }
>   ],
>   "SerrilhaTextSymbols": [
>     {
>       "TextPattern": "(?<code>[A-Z])\\s*[-=]\\s*(?<descriptor>[0-9]+(?:x[0-9\\.,]+)?)\\s+(?<length>[0-9]+[\\.,]?[0-9]*)(?:\\s+(?<teeth>[0-9]+)\\s*(?:d|dentes?)?)?",
>       "SemanticType": "serrilha",
>       "SemanticTypeGroup": "code",
>       "SemanticTypeFormat": "serrilha_{value}",
>       "BladeCodeGroup": "descriptor",
>       "UppercaseBladeCode": false,
>       "UppercaseSemanticType": true,
>       "AllowMultipleMatches": true,
>       "LengthGroup": "length",
>       "ToothCountGroup": "teeth"
>     }
>   ],
>   "Scoring": {
>     "TotalCutLength": 2000.0,
>     "TotalCutLengthWeight": 0.75,
>     "NumCurves": 60,
>     "NumCurvesWeight": 0.6,
>     "NumCurvesExtraThresholds": [
>       { "Threshold": 140, "Weight": 0.25 },
>       { "Threshold": 220, "Weight": 0.35 },
>       { "Threshold": 360, "Weight": 0.4 },
>       { "Threshold": 520, "Weight": 0.5 }
>     ],
>     "NumCurvesStep": 45,
>     "NumCurvesStepWeight": 0.2,
>     "NumCurvesStepMaxContribution": 1.2,
>     "BonusIntersections": 60,
>     "BonusIntersectionsWeight": 0.4,
>     "IntersectionThresholds": [
>       { "Threshold": 120, "Weight": 0.6 },
>       { "Threshold": 180, "Weight": 0.75 },
>       { "Threshold": 240, "Weight": 0.9 }
>     ],
>     "DanglingEndThresholds": [
>       { "Threshold": 250, "Weight": 0.15 },
>       { "Threshold": 650, "Weight": 0.25 },
>       { "Threshold": 1100, "Weight": 0.5 },
>       { "Threshold": 1800, "Weight": 0.8 }
>     ],
>     "MinRadius": {
>       "DangerThreshold": 0.35,
>       "NeutralThreshold": 1.0,
>       "PenaltyWeight": 0.65,
>       "CorteSecoAdjustment": 0.55,
>       "CorteSecoPairThresholds": [
>         { "Threshold": 2, "Weight": 0.35 },
>         { "Threshold": 4, "Weight": 0.45 }
>       ]
>     },
>     "Serrilha": {
>       "PresenceWeight": 0.65,
>       "MistaWeight": 0.85,
>       "MultiTypeWeight": 0.35,
>       "MultiTypeThreshold": 2,
>       "ManualBladeWeight": 0.45,
>       "ManualBladeCodes": ["MANUAL"],
>       "TravadaWeight": 0.8,
>       "ZipperWeight": 0.6,
>       "DiversityWeight": 0.4,
>       "DiversityThreshold": 2,
>       "DistinctBladeWeight": 0.25,
>       "DistinctBladeThreshold": 2,
>       "CorteSecoMultiTypeWeight": 0.45,
>       "TotalCountThresholds": [
>         { "Threshold": 4, "Weight": 0.25 },
>         { "Threshold": 8, "Weight": 0.35 },
>         { "Threshold": 14, "Weight": 0.45 }
>       ],
>       "MistaCountThresholds": [
>         { "Threshold": 2, "Weight": 0.35 },
>         { "Threshold": 4, "Weight": 0.45 }
>       ],
>       "TravadaCountThresholds": [
>         { "Threshold": 2, "Weight": 0.3 },
>         { "Threshold": 5, "Weight": 0.4 }
>       ],
>       "ColaSemanticHints": ["COLA", "SER_COL", "SER-COL", "COL"],
>       "ColaWeight": 0.45,
>       "ColaCountThresholds": [
>         { "Threshold": 2, "Weight": 0.35 },
>         { "Threshold": 5, "Weight": 0.45 }
>       ],
>       "SmallPieceMaxCount": 2,
>       "SmallPieceMaxTotalLength": 80.0,
>       "SmallPieceAdjustment": -0.4
>     },
>     "ClosedLoops": {
>       "CountThresholds": [
>         { "Threshold": 2, "Weight": 0.15 },
>         { "Threshold": 4, "Weight": 0.2 },
>         { "Threshold": 8, "Weight": 0.2 },
>         { "Threshold": 20, "Weight": 0.25 },
>         { "Threshold": 40, "Weight": 0.8 }
>       ],
>       "VarietyThreshold": 2,
>       "VarietyWeight": 0.3,
>       "DensityThresholds": [
>         { "Threshold": 4.5e-05, "Weight": 0.25 },
>         { "Threshold": 5.5e-05, "Weight": 0.8 }
>       ]
>     },
>     "ThreePt": {
>       "LengthThresholds": [
>         { "Threshold": 150, "Weight": 0.35 },
>         { "Threshold": 300, "Weight": 0.45 }
>       ],
>       "SegmentThresholds": [
>         { "Threshold": 6, "Weight": 0.3 },
>         { "Threshold": 12, "Weight": 0.3 }
>       ],
>       "RatioThresholds": [
>         { "Threshold": 0.08, "Weight": 0.3 },
>         { "Threshold": 0.15, "Weight": 0.35 }
>       ],
>       "ManualHandlingWeight": 0.45
>     },
>     "CurveDensity": {
>       "DensityThresholds": [
>         { "Threshold": 0.0005, "Weight": 0.2 },
>         { "Threshold": 0.00055, "Weight": 0.5 }
>       ],
>       "DelicateArcCountThresholds": [
>         { "Threshold": 12, "Weight": 0.25 },
>         { "Threshold": 28, "Weight": 0.35 }
>       ]
>     },
>     "Materials": {
>       "DefaultWeight": 0.45,
>       "Overrides": {
>         "adesivo": 1.0,
>         "vinil": 0.75,
>         "plastico": 0.7,
>         "pvc": 0.7,
>         "borracha": 0.55
>       },
>       "KeywordOverrides": {
>         "ades": 0.9,
>         "lam_adesivo": 0.95,
>         "lam adesivo": 0.95,
>         "selagem": 0.9,
>         "vinil": 0.75,
>         "plast": 0.7
>       }
>     }
>   }
> }
> ```

Observações:

- O mapeamento de materiais especiais considera camadas como `SEL_COLA`, `SELAGEM`, `VINIL`, `PLAST`, `PVC` e correlatos; os novos `KeywordOverrides` capturam variantes como “lam adesivo” ou “vinil adesivo”, garantindo o peso adicional esperado.
- Facas com poucas peças de serrilha predominantemente retas (≤2 ocorrências somando até 80 mm) recebem o ajuste configurável `SmallPieceAdjustment`, reduzindo o score quando o trabalho é naturalmente mais simples.
- Thresholds fracionados (`NumCurvesStep*`, `TotalCountThresholds`, `Mista/TravadaCountThresholds`, `ColaCountThresholds`, `CorteSecoPairThresholds`) permitem calibrar granularmente casos com muitas curvas, serrilhas travadas/mistas/cola e cortes secos sobrepostos, aproximando o score ao esforço real informado pelo time.
- Um detector de loops baseado na malha de segmentos identifica bocas mesmo quando o desenho veio explodido (linhas e arcos soltos), alimentando `ClosedLoops`/`ClosedLoopDensity` para o scorer.

### Calibração 2025-04

- Novas métricas publicadas em `DXFMetrics` e `DXFSerrilhaSummary`:
  - `Quality.ClosedLoops`, `ClosedLoopsByType`, densidade de bocas e materiais especiais (`SpecialMaterials`).
  - Métricas completas de vinco 3 pt (`TotalThreePtLength`, `ThreePtSegmentCount`, `ThreePtCutRatio`, `RequiresManualThreePtHandling`).
  - Classificação agregada (`Classification.Mista/Zipper/Travada/Simple`) e contagem de códigos distintos.
- Pesos revisados nas seções de scoring (ver snippet acima) calibra exemplos reais e fixtures sintéticos.

| Caso | Tipo | Score desejado | Score calibrado |
| --- | --- | --- | --- |
| `NR 120184.dxf` | NR real, muitas bocas/curvas | 5.0 | 5.0 |
| `NR119812.dxf` | NR real com serrilha delicada | 3.0 | 2.97 |
| `calibration_threept_complexity.dxf` | Vinco 3 pt volumoso + bocas | 5.0 | 5.0 |
| `calibration_zipper_complexity.dxf` | Serrilha zipper + adesivo | 3.6 | 3.6 |
| `calibration_low_complexity.dxf` | Layout simples com poucas bocas | 1.6 | 1.45 |

- Testes: `ComplexityCalibrationTests` (scores + explicações), `DXFMetricsExtractionTests` (novas métricas) e fixtures regeneráveis via `Tools/DxfFixtureGenerator`.

### Ajustes Recentes

| Data | Motivo | Principais mudanças |
| --- | --- | --- |
| 2025-10-23 | Dobras queue e granularidade de score | Bloqueio da publicação de `.dxf` sem sufixo salvo na fila de dobras; novos thresholds fracionados (`NumCurvesStep*`, `Serrilha.*CountThresholds`, `Cola*`, `CorteSecoPairThresholds`); reforço dos pesos de materiais sensíveis via `KeywordOverrides`. |
| 2025-10-23 | Bypass de cache para recalibração imediata | `DXFAnalysis.Cache.Bypass` ignora `.analysis.json`; orientação de limpeza rápida de `artifacts/cache` para reprocessar NRs após ajustes de peso. |

### Limpeza rápida do cache

- `rm -rf artifacts/cache`
- `rm -rf bin/Release/net8.0/linux-x64/publish/artifacts/cache`
- Opcional: `rm -rf artifacts/renders` remove prévias PNG caso queira regenerar screenshots junto com o score.

> **Mensagens RabbitMQ**
>
> * **Request** (consumida pelo `DXFAnalysisWorker` da fila `facas.analysis.request`):
>
> ```json
> {
>   "opId": "OP-1234",             // opcional
>   "filePath": "C:\\FacasDXF\\faca_123.dxf",
>   "fileHash": "sha256:...",      // opcional, se já calculado
>   "flags": { "emborrachada": true, "laminada": false },  // opcionais
>   "meta": { "cliente": "ACME", "descricao": "Cartucho 200g" } // livre
> }
> ```
>
> * **Result** (publicada na fila `facas.analysis.result`):
>
> ```json
> {
>   "analysisId": "uuid",
>   "timestampUtc": "2025-10-13T12:34:56Z",
>   "opId": "OP-1234",
>   "fileName": "faca_123.dxf",
>   "fileHash": "sha256:...",
>   "metrics": {
>     "unit": "mm",
>     "extents": { "minX": 0.0, "minY": 0.0, "maxX": 500.0, "maxY": 350.0 },
>     "bboxArea": 175000.0,
>     "bboxPerimeter": 1700.0,
>     "totalCutLength": 2345.6,
>     "totalFoldLength": 345.2,
>     "totalPerfLength": 120.0,
>     "total3PtLength": 15.0,
>     "numCurves": 87,
>     "numNodes": 142,
>     "numIntersections": 23,
>     "minArcRadius": 0.25,
>     "polylineCount": 18,
>     "splineCount": 3,
>     "lineCount": 250,
>     "arcCount": 45,
>     "layerStats": [
>       { "name": "CUT", "type": "corte", "entityCount": 290, "totalLength": 2345.6 },
>       { "name": "VINCO", "type": "vinco", "entityCount": 40, "totalLength": 345.2 }
>     ],
>     "quality": { "tinyGaps": 12, "overlaps": 5, "danglingEnds": 2 },
>     "serrilha": {
>       "totalCount": 4,
>       "entries": [
>         { "semanticType": "SERRILHA_X", "bladeCode": "2x1", "count": 2, "estimatedLength": 48.0, "estimatedToothCount": 24 },
>         { "semanticType": "SERRILHA_Y", "bladeCode": "2x1", "count": 2, "estimatedLength": 47.5, "estimatedToothCount": 24 }
>       ],
>       "isCorteSeco": true,
>       "corteSecoBladeCodes": ["2x1"],
>       "corteSecoPairs": [
>         { "layerA": "SERRILHA", "layerB": "SERRILHA", "typeA": "serrilha", "typeB": "serrilha", "overlapMm": 180.0, "offsetMm": 0.32, "angleDeg": 0.7 }
>       ]
>     }
>   },
>   "flags": { "emborrachada": true, "laminada": false },
>   "image": {
>     "path": "C:\\FacasDXF\\Renders\\faca_123.png",
>     "widthPx": 2000,
>     "heightPx": 1400,
>     "dpi": 300
>   },
>   "score": 3.0,
>   "explanations": [
>     "Comprimento de corte muito alto (>2000 mm)",
>     "Muitos segmentos curvos (>60)",
>     "Serrilha detectada (4 símbolo(s) mapeado(s))",
>     "Múltiplos tipos de serrilha detectados (SERRILHA_X, SERRILHA_Y)",
>     "Corte seco detectado: redução de 0.5 ponto(s)"
>   ],
>   "version": "complexity-engine/1.0.0",
>   "durationMs": 358
> }
> ```
>
> **Heurística determinística (0–5)**
>
> * Pesos **transparentes** e **idempotentes**. Cada “fator ativado” soma 1 ponto; clamp em 5.
> * Fatores (todos configuráveis em `DXFAnalysisOptions.Scoring`):
>
>   ```csharp
>   double score = 0;
>   if (metrics.TotalCutLength > thresholds.TotalCutLength) score += 1;
>   if (metrics.NumCurves > thresholds.NumCurves) score += 1;
>   if (minRadiusOptions.PenaltyWeight > 0 &&
>       metrics.MinArcRadius > 0 &&
>       metrics.MinArcRadius <= minRadiusOptions.DangerThreshold &&
>       !(metrics.Serrilha?.IsCorteSeco ?? false))
>   {
>       score += minRadiusOptions.PenaltyWeight;
>   }
>   score += SerrilhaHeuristics(metrics.Serrilha); // presença, mista, multitype, manual
>   if (HasLayer(metrics.LayerStats, "trespt")) score += 1;
>   if (score < 5 && metrics.NumIntersections > thresholds.BonusIntersections) score += 1;
>   if ((metrics.Serrilha?.IsCorteSeco ?? false) && minRadiusOptions.CorteSecoAdjustment != 0)
>   {
>       score += minRadiusOptions.CorteSecoAdjustment;
>   }
>   score = Math.Clamp(score, 0, 5);
>   ```
> * **Explanations** devem listar exatamente quais fatores dispararam, com valores e thresholds.
>
> **Regras de extração (DXFAnalyzer)**
>
> * Ler **EXTMIN/EXTMAX** do header; fallback: iterar entidades para extents.
> * Unidades: normalizar para **mm**. Se unit desconhecida, assumir `DefaultUnit`.
> * Entidades suportadas: `LINE`, `ARC`, `CIRCLE` (como arco completo), `LWPOLYLINE`, `POLYLINE`, `SPLINE`.
> * Comprimentos:
>
>   * `LINE`: distância euclidiana.
>   * `ARC/CIRCLE`: arco = raio * ângulo; círculo = 2πr.
>   * `LWPOLYLINE/POLYLINE`: somar segmentos; se tiver bulge, converter para arcos.
>   * `SPLINE`: aproximar por discretização adaptativa (tolerância em `DXFAnalysisOptions`).
> * Interseções:
>
>   * Construa índice espacial simples (grid hashing) para **broad-phase**; teste intersecções exatas em **narrow-phase** (line-line, line-arc, arc-arc).
> * Nós (nodes): total de vértices somados (útil p/ densidade).
> * Raio mínimo: menor raio encontrado em `ARC`, bulge de polylines, ou spline (aproximação).
> * Estatísticas por layer: mapear `LayerMapping` (regex `^CUT|CORTE$` etc. é bem-vinda).
> * Qualidade:
>
>   * `tinyGaps`: endpoints a < `GapTolerance` (ex.: 0.05 mm).
>   * `overlaps`: duplicidades / segmentos coincidentes > `OverlapTolerance`.
>   * `danglingEnds`: endpoint sem vizinho próximo e ângulo fora de junção (corte mal fechado).
>
> **Pré-processamento (DXFPreprocessor)**
>
> * Normalizar entidades degeneradas (linhas zero-length).
> * Snapping leve de endpoints até `GapTolerance`.
> * Merge de colineares contíguos (opcional, para medir melhor comprimentos).
>
> **Renderização (DXFImageRenderer)**
>
> * Fundo branco, escala única por documento, padding fixo, stroke fino (1 px).
> * Padronizar ordem: corte > vinco > serrilha > 3pt > outros, com tons de cinza distintos (opcional).
> * Dimensão alvo baseada em DPI e extents; salvar PNG no `OutputImageFolder`.
> * Embutir watermark pequena com `fileName` e `score` no canto (opcional via `SkiaSharp`).
>
> **Resiliência e performance**
>
> * `DXFAnalysisWorker` como `BackgroundService`, consumo assíncrono da fila de **requests**, fan-out com `Parallelism` do config.
> * Hash de arquivo (`SHA256`): cache de resultados em disco (arquivo `.analysis.json`) se `ReprocessSameHash == false`.
> * Timeouts configuráveis para parsing e render.
> * **Nunca** bloquear o watcher principal.
>
> **Registros no Program.cs**
>
> ```csharp
> builder.Services.Configure<DXFAnalysisOptions>(builder.Configuration.GetSection("DXFAnalysis"));
> builder.Services.AddSingleton<DXFAnalyzer>();
> builder.Services.AddSingleton<DXFPreprocessor>();
> builder.Services.AddSingleton<DXFImageRenderer>();
> builder.Services.AddSingleton<ComplexityScorer>();
> builder.Services.AddHostedService<DXFAnalysisWorker>();
> ```
>
> **Logs e telemetria**
>
> * `ILogger` com `AnalysisId`, `FileName`, `DurationMs`, `Score`.
> * Contadores: `analysis_ok`, `analysis_failed`, `render_failed`, `cache_hit`, `cache_miss`.
>
> **Testes**
>
> * `DXFAnalyzerTests`: casos com DXFs sintéticos (linhas, arcos, bulge, splines, layers).
> * `ComplexityScorerTests`: thresholds, bordas, combinações.
> * `DXFPreprocessorTests`: correção de gaps/overlaps, preservando métricas.
>
> **Entregáveis**
>
> * Código compilando, com DI, logs, testes básicos e exemplos de DXF de fixture.
> * Documentação XML em classes públicas.
> * `README.md` curto em `/Services/DXFAnalysis` com instruções e exemplos de mensagem.
>
> **Saída esperada do assistente**
>
> * Gerar os arquivos acima com **código completo** (classes, DI, comentários).
> * Inserir os trechos de `Program.cs`/`appsettings.json` conforme especificado.
> * Sugerir 3 amostras de DXF de teste (descritas) e como rodar testes.

---

# PRD — “Complexity Engine” determinístico de facas (DXF)

## 1. Visão geral

Criar um **motor determinístico** de análise de facas, integrado ao `FileWatcherApp`, que calcula **métricas** e um **score 0–5** por **regras fixas** (sem ML), publica o resultado em `facas.analysis.result` e fornece **imagem** padronizada do desenho.

## 2. Objetivos

* **Padronizar** a leitura e medição de DXF.
* **Automatizar** o score de complexidade de forma **transparente e auditável**.
* **Integrar** sem fricção ao Organizador (consumo por fila).
* **Resiliente**: não travar o watcher; tratar DXFs “sujos”.
* **performático** temos Prometheus e o grafana afim de monitorar e mensurar a performance

## 3. Não-objetivos

* Treinamento/serviço de ML (futuro opcional).
* Edição corretiva de DXF original (apenas leitura + limpeza leve para métricas).

## 4. Usuários/atores

* Operação/prod: consultam score e imagem.
* Backend Organizador: consome `result` e associa a OP.

## 5. Fluxo de ponta a ponta

1. Watcher detecta DXF → publica request em `facas.analysis.request` **ou** invoca diretamente o `DXFAnalysisWorker` (dependendo do que já existe).
2. `DXFAnalysisWorker` consome, cria `analysisId`, lê arquivo e hash.
3. `DXFPreprocessor` normaliza.
4. `DXFAnalyzer` extrai métricas.
5. `DXFImageRenderer` gera PNG padronizado.
6. `ComplexityScorer` calcula score + explanations.
7. Publica `FacaAnalysisResult` em `facas.analysis.result`.
8. Organizador consome, associa à OP (se presente ou quando surgir), persiste.

## 6. Requisitos funcionais

* RF1: Consumir requests com `filePath` local ou UNC.
* RF2: Tolerar DXFs sem header de unidades (usar `DefaultUnit`).
* RF3: Mapear layers a tipos (corte, vinco, serrilha, 3pt) via config e regex.
* RF4: Calcular métricas listadas no “esquema metrics”.
* RF5: Gerar imagem PNG padronizada.
* RF6: Publicar resultado com `score` e `explanations`.
* RF7: Cache por `fileHash` opcional.
* RF8: Logs estruturados e duração (ms).

## 7. Requisitos não-funcionais

* RNF1: Processar DXF típico (≤10 MB, ≤10k entidades) em **< 1s** p95 em máquina padrão (ajustável).
* RNF2: Processar **em paralelo** até `Parallelism`.
* RNF3: Idempotente dado mesmo `fileHash`.
* RNF4: Observável: contadores e logs de erro/latência.
* RNF5: Seguro: não publicar DXF bruto; só caminho/URL e PNG.

## 8. Esquemas e contrato

* **Request** e **Result** já definidos no prompt (acima). **Imutável** após merge; qualquer evolução via `version` e campos novos opcionais.

## 9. Regras de negócio (Scoring)

* Tabela padrão (ajustável via config):

  * `TotalCutLength > 2000 mm` → +1
  * `NumCurves > 60` → +1
  * `MinArcRadius 0 < r < 1.0 mm` → +1
  * `serrilha` presente → +1
  * `serrilhaMista` presente → +1
  * `3pt` presente → +1
  * Bônus (se `score < 5`): `NumIntersections > 30` → +1
* Clamp 0–5.
* `explanations[]` deve refletir **cada** fator ativado com valores.

## 10. Extração e métricas (detalhe)

* **Unidades**: `INSUNITS` no header; fallback `DefaultUnit`.
* **Extents**: usar `EXTMIN/EXTMAX`; se ausentes, calcular iterando entidades.
* **Comprimentos**:

  * Polylines com `bulge`: converter para arcos (fórmula padrão bulge/ângulo).
  * Splines: discretização adaptativa com tolerância `ChordTolerance`.
* **Interseções**:

  * Grid hashing (tamanho de célula ≈ bboxDiagonal/100) → pares candidatos.
  * Testes exatos (line-line, line-arc, arc-arc) com tolerância.
* **Qualidade**:

  * `tinyGaps`: endpoints distantes < `GapTolerance`.
  * `overlaps`: distância Hausdorff < `OverlapTolerance` para segmentos co-lineares/concêntricos.
  * `danglingEnds`: endpoint sem par próximo e sem continuidade angular.

## 11. Renderização

* PNG com DPI e padding do config; escala por bbox maior.
* Traços 1 px (sem antialias forte para leitura nítida).
* Ordem de pintura por tipo; tons cinza distintos (ou tudo preto se preferir simplicidade).
* Texto discreto com `fileName` (opcional).

## 12. Operação e observabilidade

* Métricas:

  * `analysis_ok`, `analysis_failed`, `render_failed`, `cache_hit`, `cache_miss`.
  * `analysis_duration_ms` (histograma).
* Logs por etapa com `analysisId`.
* Health-check opcional do worker (fila legível, pasta acessível).

## 13. Erros e fallback

* DXF corrompido → publicar `result` com `score = null`, `explanations = ["parse_failed: ..."]`, `metrics` parciais se possível.
* Unidades ausentes → usar `DefaultUnit` e registrar aviso.
* Render falhou → publicar sem `image`, com explicação.

## 14. Segurança e dados

* Não trafegar DXF por fila; apenas caminhos/URLs internos.
* Sanear caminhos (sem path traversal).
* PNGs e JSONs sem dados sensíveis.

## 15. Migração e rollout

* Feature **opt-in** por config no começo.
* “Shadow mode” opcional: publicar resultado e **não** usar no Organizador até validação (flag do Organizador).
* Coletar amostras p/ calibrar thresholds com time de produção (arquivo CSV com métricas vs. avaliação humana).

## 16. Testes (mínimos)

* **Unitários**:

  * `DXFAnalyzer`: linhas retas (comprimentos exatos), arco 90° (πr/2), polylines com bulge, splines discretizadas.
  * `ComplexityScorer`: casos nos limiares (59/60 curvas, 1.0/0.99 mm etc.).
  * `DXFPreprocessor`: reduzir `tinyGaps`, preservar topologia.
* **Integração**:

  * Request → Result (end-to-end) com DXF fictício.
  * Cache hit/miss.
  * Falhas de parsing simuladas.
* **DXFs de fixture**:

  1. **“Linha Reta”**: 4 linhas, sem curvas, sem layers especiais → score 0–1.
  2. **“Curvo Denso”**: 100 arcos/splines, raio min 0.7 mm → score alto.
  3. **“Com Serrilha e 3pt”**: layers mapeadas, comprimento alto → score 4–5.

## 17. Aceite

* Processa 100 arquivos de exemplo com p95 < 1s (máquina padrão).
* Publica 100% com `analysisId` e logs consistentes.
* Scores batem com a regra declarada em 100% dos casos.
* Organizador recebe e associa corretamente à OP quando `opId` presente.

---

## O que você recebe com isso

* **Prompt** pronto para gerar o código C# (sem ML), incluindo **pastas, classes, DI, mensagens, regras** e **testes**.
* **PRD** completo para o time implementar, revisar e publicar com segurança.

Se quiser, eu já te entrego **o esqueleto de código** dessas classes (C# .NET 8) seguindo exatamente o prompt acima — é só dizer “gera o código base com SkiaSharp” (ou com `System.Drawing` se for só Windows).
