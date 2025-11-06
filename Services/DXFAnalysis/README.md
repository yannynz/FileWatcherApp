# DXFAnalysis pipeline quickstart

## Serrilha symbol mapping

- Configure `DXFAnalysis:SerrilhaSymbols` in `appsettings.json` (or environment) with regexes for block names/attributes, semantic types and optional blade metadata:

```json
"DXFAnalysis": {
  "SerrilhaSymbols": [
    {
      "SymbolNamePattern": "^SERRILHA_FINA$",
      "SemanticType": "serrilha_fina",
      "BladeCode": "FINA",
      "DefaultToothCount": 40
    },
    {
      "SymbolNamePattern": "^SERRILHA_MISTA_.*$",
      "AttributePattern": "LAMINA\\s*MISTA",
      "SemanticType": "serrilha_mista",
      "BladeCode": "MISTA"
    }
  ]
}
```

- `DXFAnalyzer` gera `metrics.serrilha` no resultado com contagem por tipo, códigos de lâmina, comprimento estimado e lista de símbolos não mapeados. Unknowns são logados com o evento `serrilha_unknown_symbol`.
- O `ComplexityScorer` usa `Scoring.Serrilha` para atribuir pesos fracionados (presence/mista/múltiplos tipos/lâmina manual). Ajuste conforme a heurística do time.

## Ferramentas auxiliares

- `dotnet run --project Tools/DxfInspector -- <arquivo.dxf>`: inspeciona layers, blocos, atributos e entidades textuais; converte DXF R14 em memória.
- `dotnet run --project Tools/DxfFixtureGenerator`: regenera os DXFs sintéticos usados nos testes em `tests/resources/dxf/`.
- `dotnet script scripts/dxf-symbol-audit.csx -- <arquivo.dxf> [appsettings.json]`: executa a análise completa (pré-processamento + métricas) e imprime o resumo de serrilha. Execute `dotnet build` antes para garantir que `bin/Debug/net8.0/FileWatcherApp.dll` esteja disponível.
- Para rodar o worker contra um RabbitMQ local (docker `rabbitmq:3-management`), defina `DOTNET_ENVIRONMENT=Development` ou exporte variáveis `DXFAnalysis__RabbitMq__HostName=localhost` / `RabbitMq__HostName=localhost` antes de iniciar o `FileWatcherApp`.
- Para anotações textuais como `X=2x1 23,8`, configure `DXFAnalysis:SerrilhaTextSymbols` com regex de grupos nomeados (`SemanticTypeGroup`, `BladeCodeGroup`, `LengthGroup`, `ToothCountGroup`). Você pode usar `SemanticTypeFormat` para montar o tipo (ex.: `serrilha_{value}`), escolher se códigos/tipos devem ser upper-case e indicar se múltiplas ocorrências no mesmo texto devem ser contabilizadas (`AllowMultipleMatches`).
- A heurística de corte seco (`DXFAnalysis.CorteSeco`) procura pares de segmentos quase paralelos com offset pequeno e códigos de lâmina complementares. Quando ativa, `metrics.serrilha.isCorteSeco` fica `true`, `corteSecoPairs` lista os pares e o scorer aplica `Scoring.MinRadius.CorteSecoAdjustment` (além de neutralizar a penalização de raio mínimo).

## Testes

- `dotnet test tests/FileWatcherApp.Tests/FileWatcherApp.Tests.csproj --no-build` cobre contagem de símbolos, atributos, casos desconhecidos e o novo scorer fracionado.
- Os fixtures sintéticos (`serrilha_fina.dxf`, `serrilha_mista.dxf`, `serrilha_nao_map.dxf`) foram construídos com blocos simples e atributos para validar as regexes de configuração.
