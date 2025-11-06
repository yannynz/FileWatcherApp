# DXF Complexity Engine – Implementação

## Visão geral

Este documento resume a entrega do motor determinístico de análise de facas concluída em 13/10/2025. A pipeline foi adicionada ao `FileWatcherApp`, mantendo os watchers existentes e introduzindo um `BackgroundService` dedicado (`DXFAnalysisWorker`) que consome a fila `facas.analysis.request`, gera métricas, imagem PNG e publica resultados estruturados em `facas.analysis.result`.

## Componentes principais

- `DXFAnalysisOptions`: agrega toda a configuração (pastas, filas, limites de tolerância, métricas e thresholds de score). Os valores padrão seguem o PRD e podem ser ajustados via `appsettings.json`.
- `DXFPreprocessor`: limpa degenerescências, contabiliza gaps/overlaps/dangling e normaliza vértices para facilitar a análise posterior.
- `DXFAnalyzer`: converte entidades `netDxf` 3.0.1 em métricas milimetrizadas, estimando extensões, comprimentos por tipo de camada, curvas, interseções (hashing espacial simples) e qualidade.
- `DXFImageRenderer`: usa SkiaSharp para renderizar PNG padronizado (fundo branco, stroke proporcional 2–6 px, ordenação por tipo). Enquadra o bounding box completo com margem dinâmica e respeita limite de 4 096 px no maior eixo; quando necessário reduz escala preservando nitidez. O arquivo é salvo na pasta configurada com fallback para nome único por `analysisId`.
- `ComplexityScorer`: aplica as regras determinísticas (comprimento de corte, curvas, raio mínimo, serrilha, 3pt e bônus de interseções) produzindo `score` e `explanations` transparentes.
- `DXFAnalysisCache`: cache opcional em disco por `fileHash` (sha256). Reutiliza métricas e imagem já calculadas quando `ReprocessSameHash == false`.
- `DXFAnalysisWorker`: consome a fila de requests, orquestra as etapas, registra métricas (via `System.Diagnostics.Metrics`) e publica `DXFAnalysisResult` JSON usando `RabbitMQ.Client`. Antes de reutilizar o cache verifica se o objeto remoto ainda existe; se não existir, refaz render/upload automaticamente.
- `IImageStorageClient` (`S3ImageStorageClient`/`NullImageStorageClient`): envia o PNG renderizado para storage externo (S3/MinIO) e devolve metadados integrados ao resultado.

## Fluxo resumido

1. Mensagem recebida (`DXFAnalysisRequest`) → validação de caminho e hash.
2. Cache consultado; em caso de hit, resultado é republicado com novo `analysisId` e `timestamp`.
3. `DxfDocument` carregado (`netDxf 3.0.1`), preprocessado e analisado.
4. Renderização PNG (com tolerância de cancelamento configurável) e upload opcional para storage externo.
5. Score calculado e resultado publicado na fila `facas.analysis.result`; cache persistido.
6. Métricas Prometheus-like atualizadas: `analysis_ok`, `analysis_failed`, `render_failed`, `cache_hit`, `cache_miss`, `analysis_duration_ms`.

## Configuração

O bloco `DXFAnalysis` em `appsettings.json` cobre:

- Pastas monitoradas (`WatchFolder`, `OutputImageFolder`, `CacheFolder`) e `PersistLocalImageCopy` (controla se o PNG é mantido no disco do worker).
- Filas (`RabbitQueueRequest`, `RabbitQueueResult`) e credenciais RabbitMQ.
- Paralelismo (`Parallelism`), tempos limite (`ParseTimeout`, `RenderTimeout`) e tolerâncias geométricas (`GapTolerance`, `OverlapTolerance`, `ChordTolerance`).
- Mapeamento de camadas (regex) para tipos semânticos e thresholds de score.
- `ShadowMode` para modo sombra durante rollout.

## Testes

- `DXFAnalysisTests.Analyzer_ComputesBasicMetrics_ForSimpleLine`: garante métricas básicas para uma linha de teste, validando totais por camada.
- `DXFAnalysisTests.ComplexityScorer_AwardsScorePerThresholds`: cobre as regras determinísticas, incluindo ativação do bônus por interseções.

Para rodar: `dotnet test`. Observação: o NuGet resolve automaticamente `netDxf 3.0.1`, gerando o aviso `NU1603` – aguardando publicação das versões antigas; o build permanece estável.

## Amostras de DXF recomendadas

1. **Linha Reta**: 4 linhas ortogonais, sem curvas ou layers especiais – esperado `score` 0–1.
2. **Curvo Denso**: ~100 arcos com raio mínimo ≈0,7 mm e splines discretizadas – esperado `score` alto (≥4).
3. **Serrilha + 3pt**: múltiplas camadas mapeadas para `serrilha`, `serrilha_mista` e `tresPt`, comprimento de corte >2000 mm – esperado `score` 4–5.

Armazene essas fixtures em `tests/resources/dxf/` ou repositório compartilhado para validação manual.

## Notas operacionais

- O `DXFAnalysisWorker` é registrado via `Host.CreateApplicationBuilder` em `Program.cs` e roda em paralelo aos watchers legados.
- O renderer captura exceções (ex.: falta de bibliotecas Skia nativas) e publica resultados sem imagem, adicionando `render_failed` à telemetria.
- Logs utilizam `ILogger` com `analysisId`, `fileName` e duração da análise (ms) para correlação.
- Ajuste os limites de tolerância antes do rollout para evitar contagens exageradas de gaps/interseções em DXFs com ruído.
- Ferramenta auxiliar `Tools/RenderPreview` permite gerar o PNG localmente (sem Rabbit/S3) via `dotnet run --project Tools/RenderPreview/RenderPreview.csproj <arquivo.dxf>`; útil para validar nitidez e enquadramento.

### Atualização 2025-10-14

- **Sintoma**: mesmo após copiar `NR119812.dxf` para `/home/nr`, nenhuma mensagem chegava em `facas.analysis.request` ou `facas.analysis.result`; o backend continuava com `dxf_analysis` vazio.
- **Causas identificadas**:
  1. Os watchers do `OpsDir` ignoravam extensões `.dxf` (somente PDFs eram processados). Logo, nenhum `DXFAnalysisRequest` era enfileirado.
  2. As credenciais do RabbitMQ estavam hard-coded no código (`Host = "192.168.0.xxx"`). Em ambientes diferentes, o `DXFAnalysisWorker` não conseguia se conectar e morria logo após startar.
- **Correções**:
  - `Program.cs` passa a carregar `RabbitMq` e `DXFAnalysis` via `IOptions<>`, permitindo definir `RabbitMq__HostName`, `RabbitMq__Port`, etc., por ambiente (Windows server em produção, Docker em homologação).
  - O `CreateOpWatcher` agora detecta `.dxf` e publica um `DXFAnalysisRequest` automaticamente na fila configurada (`DXFAnalysis.RabbitQueueRequest`). O request inclui `filePath`, `opId` (quando o NR é encontrado) e flags de rastreabilidade.
  - `SetupRabbitMQ` declara a fila de análise para evitar race quando o worker ainda não foi inicializado.

### Passo a passo de teste (full pipeline)

1. Configure o RabbitMQ: ajuste `RabbitMq:HostName` em `appsettings.json` ou via variável de ambiente `RabbitMq__HostName=<ip do servidor docker>`.
2. Execute `FileWatcherApp` no Windows Server e verifique os logs iniciais: `Iniciando DXFAnalysisWorker... fila=facas.analysis.request ...`.
3. Copie um DXF válido para o diretório monitorado (`/home/nr` no Linux ou compartilhamento equivalente no Windows). O console exibirá `[DXF] Request publicado ...`.
4. No RabbitMQ (http://<docker-host>:15672) confira:
   - `facas.analysis.request` → mensagem consumida.
   - `facas.analysis.result` → mensagem produzida pelo worker após análise.
5. No backend (Organizador): `curl http://<docker-host>:8081/api/dxf-analysis/order/<NR>` deve retornar o score recém-processado.
6. Opcional: validar `dxf_analysis` no Postgres e receber a notificação em `/topic/dxf-analysis` via WebSocket.

## Próximos passos sugeridos

- Integrar health-check HTTP leve para o worker e exportação explícita das métricas (`MeterListener` → Prometheus).
- Alimentar o Organizador em modo sombra consumindo a fila `facas.analysis.result` com `shadowMode=true`.
- Validar performance (p95 < 1 s) com 100 arquivos reais, calibrando `Parallelism` e tesselação (`ChordTolerance`).
- Finalizar integração do Organizer com o novo storage externo (detalhes em `docs/dxf-image-storage.md`).
