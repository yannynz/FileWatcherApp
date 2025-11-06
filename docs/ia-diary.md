# Diário de Bordo da IA – 2025-10-16

## Observações ao analisar `NR119812.dxf`

1. **Ambiente ainda em “Production”**
   - Comando usado: `DOTNET_ENVIRONMENT=Development; ./FileWatcherApp`
   - No `zsh`, o `;` encerra a atribuição antes do comando, então a aplicação manteve `Production`.
   - Impacto: uso de `appsettings.json` padrão e não de `appsettings.Development.json`.

2. **Eventos duplicados**
   - Logs mostraram duas entradas consecutivas de `"[DOBRAS] Encaminhando DXF para análise"`.
   - Causa provável: `FileSystemWatcher` gera `Created` e `Changed` sem de-dupe específico para o fluxo de `.dxf` simples.
   - Consequência: duas publicações idênticas na fila `facas.analysis.request`.

3. **Falha `AutoCad14`**
   - `DxfDocument.Load` arremessou `DxfVersionNotSupportedException: AutoCad14`.
   - O pipeline principal não inclui fallback (diferente do utilitário `Tools/DxfInspector` que ajusta `$ACADVER` em memória).
   - Resultado: métricas vazias e mensagem de erro enviada no campo `explanations`.

4. **Resumo**
   - O watcher reconheceu o arquivo e publicou corretamente.
   - Falta implementar:
     - De-duplicação para `.dxf` simples (ou usar um `HashSet` semelhante aos outros fluxos).
     - Fallback para DXF R14 (substituir `AC1014` → `AC1015` antes de carregar, possivelmente via stream temporário).
   - Sem essas correções, o backend continuará recebendo resultados com métricas zeradas ao processar arquivos AC1014.

## Ações implementadas

1. **Debounce de eventos Dobras (`.dxf` simples)**
   - Introduzido `DobrasEventDebounce` (timestamp por caminho normalizado) com janela de 2 s, evitando publicações duplicadas causadas por `Created/Changed`.
   - `PruneDobrasSeen` agora também expira entradas dessa estrutura; chamadas antecipadas garantem limpeza após encaminhar para análise.

2. **Fallback AutoCAD R14 no worker**
   - `LoadDocumentWithFallback` tenta o parse padrão e, ao capturar `DxfVersionNotSupportedException`, reabre o arquivo em memória substituindo `AC1014` por `AC1015` (mesma estratégia do `DxfInspector`).
   - Log informativo confirma quando o fallback é aplicado; warnings registram falhas residuals.

3. **Configuração robusta**
   - `Host.CreateApplicationBuilder` agora lê `appsettings.{Environment}.json`, variáveis de ambiente e, opcionalmente, `/etc/filewatcherapp/appsettings.json` – permitindo alinhar ambiente de execução sem depender do shell.

4. **Detecção por texto**
   - `SerrilhaTextSymbols` habilitada no `DXFAnalyzer` com regex de grupos nomeados (código, comprimento, dentes); ajusta casos `X=2x1 23,8` e similares.
   - Valores capturados alimentam `EstimatedLength`/`EstimatedToothCount`; quando ausentes, entram defaults configurados.

### Próximo passo sugerido
- Revalidar publicando um DXF AC1014 e confirmando que a fila `facas.analysis.result` traz métricas preenchidas (sem exceções em `explanations`).

## Insights adicionais (17/10)

- Ao revisar os resultados da noite (NR119812 e NR120184), notei que apesar de termos métrica geométrica completa, dois aspectos seguem ignorados:
  1. **Serrilha textual efetiva** – os DXFs reais parecem usar letras e medidas nas lâminas (e não blocos), mas os regex atuais não estão capturando esses textos. Precisamos mapear exemplos reais com o time para ajustar `SerrilhaTextSymbols`.
  2. **Corte seco / batida seca** – hoje tratamos o menor raio como “delicado”, mas muitas curvas pequenas representam batida seca (quando duas lâminas se completam, ganhando produtividade) ou pontas que já saem prontas. Isso deveria *reduzir* a complexidade, não aumentar.

- Próximos estudos:
  1. **Corte seco**: medir distância entre elementos complementares, detectar padrões em `LayerMapping` (ex.: pares de linhas convergindo). Talvez olhar por proximidade/ângulo ou cruzar com os textos de serrilha.
  2. **Raio real**: validar com especialistas quais curvas sinalizam delicadeza vs. batida; recalibrar limiar (talvez usar largura do corte e gabarito de batida).
  3. **Plano de testes**: gerar DXFs sintéticos com serrilha textual, batida seca e raios diversos para calibrar heurísticas antes de mexer no score.

- Ver plano detalhado em `docs/plano-serrilha-corte-seco.md` (objetivos, etapas e validações).
