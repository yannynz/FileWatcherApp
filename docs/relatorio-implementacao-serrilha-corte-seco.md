# Relatório de Implementação – Serrilha Textual, Corte Seco e Recalibração de Raio

## 1. Pedido original

A solicitação formal (Plano de Otimização – Serrilha Textual, Corte Seco e Raios) exigia:

- Extrair serrilhas baseadas em anotações textuais (`TEXT`/`MTEXT`) preservando tipo, código de lâmina, comprimento e contagem de dentes.
- Detectar ocorrências de “batida/corte seco” a partir da geometria (pares de segmentos paralelos com offset reduzido) e refletir isso nos `metrics.serrilha`.
- Rever a penalização do raio mínimo, introduzindo faixa neutra e ignorando a penalização em caso de corte seco, além de aplicar um ajuste no score.
- Cobrir tudo com testes, ajustes de configuração e documentação observando o padrão de código limpo e performático usado na aplicação.

## 2. Contexto operacional

- Ambiente definido: `sandbox_mode=danger-full-access`, `approval_policy=never`, `network_access=enabled`.
- Stack existente: .NET 8, `netDxf`, pipeline `DXFAnalysis`.
- Restrições: sem pedir aprovações, respeitar alterações pré-existentes, evitar comandos destrutivos.

## 3. Raciocínio adotado

### 3.1 Serrilha textual

- O regex padrão aceitava apenas `X=2x1 23,8`. Precisávamos lidar com múltiplos tokens no mesmo texto, tolerância a hífens (`Y-...`), vírgula como separador decimal e captura opcional de dentes.
- As informações extraídas deveriam alimentar `SerrilhaTextSymbolMatcher` com grupos nomeados (`SemanticTypeGroup`, `BladeCodeGroup`, `LengthGroup`, `ToothCountGroup`) já previstos na configuração, mas sem uso completo.

### 3.2 Corte seco

- O plano sugeria correlacionar serrilhas textuais com geometrias complementares.
- Optei por heurística leve dentro do `DXFAnalyzer`, reusando os segmentos coletados para interseções:
  - Filtra segmentos lineares nas camadas de interesse (`serrilha`, `serrilha_mista`, `corte` por padrão).
  - Calcula direções unitárias, usa grid para reduzir pares candidatos (mesmo raciocínio do detector de interseções).
  - Valida paralelismo (produto escalar), sobreposição proporcional ao menor comprimento e offset médio acima da tolerância (`GapTolerance`) mas abaixo do limite configurado.
  - Exige pelo menos um par válido e códigos de lâmina duplicados (normalizados) para ativar `isCorteSeco`.

### 3.3 Score / raio mínimo

- Mantida compatibilidade com o campo antigo (`Scoring.MinArcRadiusMax`), mas introduzida estrutura `MinRadiusScoringOptions` que define:
  - `DangerThreshold`, `NeutralThreshold`, `PenaltyWeight`, `CorteSecoAdjustment`.
- Penalização só acontece se o raio mínimo for <= limite perigoso **e** se não houver corte seco.
- Quando `isCorteSeco=true`, o score recebe o ajuste configurado (default `-0.5`) e é emitida explicação específica.

## 4. Alterações realizadas

### 4.1 Opções e modelos

- `Services/DXFAnalysis/DXFAnalysisOptions.cs`:
  - Novos blocos `MinRadiusScoringOptions` e `CorteSecoOptions` com parâmetros configuráveis (linhas 253-330).
  - Exposição via propriedades `Scoring.MinRadius` e `CorteSeco`.
- `Services/DXFAnalysis/Models/DXFAnalysisContracts.cs`:
  - `DXFSerrilhaSummary` ganhou campos `IsCorteSeco`, `CorteSecoPairs`, `CorteSecoBladeCodes`.
  - Criada classe `DXFCorteSecoPair` com camadas, tipos, overlap, offset e ângulo.

### 4.2 Analisador (`DXFAnalyzer`)

- Na fase de consolidação (linhas 68-73) chamamos `ApplyCorteSecoHeuristic` logo após extrair serrilha.
- Implementada heurística completa (linhas ~449-845) com:
  - Seleção de candidatos.
  - Grid espacial e triagem com limite angular (`MaxParallelAngleDegrees`).
  - Cálculo de sobreposição, offset médio e validação de códigos duplicados.
  - Agregação das top 10 ocorrências em `summary.CorteSecoPairs`.
- Funções auxiliares adicionadas: normalização de código, cálculo de comprimento, dot products, etc.

### 4.3 Marcadores textuais

- Regex padrão atualizado para capturar dentes opcionais com unidade e aceitar vírgula/ponto (`appsettings.json`, `tests`).
- No matcher (`ProcessTextSymbols`), os comprimentos e dentes passam por `TryParseGroup` com substituição de vírgula por ponto.

### 4.4 Score (`ComplexityScorer`)

- Ajuste para configurar faixa neutra e ajuste por corte seco:
  - Normalização dos valores antigos (`MinArcRadiusMax`) para `MinRadius`.
  - Ignorar penalização se `isCorteSeco`.
  - Explicações distintas para cada cenário (linhas 37-120).

### 4.5 Testes unitários

- `tests/FileWatcherApp.Tests/SerrilhaAnalysisTests.cs`:
  - Novo caso validando captura de dentes e múltiplos tokens.
  - Caso sintético para corte seco com duas linhas paralelas.
- `tests/FileWatcherApp.Tests/DXFAnalysisTests.cs`:
  - Adicionado teste cobrindo faixa neutra e ajuste negativo no score.
- `dotnet test` executado com sucesso.

### 4.6 Configurações e docs

- `appsettings.json`: Regex textual expandido, bloco `CorteSeco` e nova seção `Scoring.MinRadius`.
- `docs/complexidade-facas.md`: Atualizado com o fluxo completo, parâmetros, exemplos de JSON e heurística detalhada.
- `Services/DXFAnalysis/README.md`: Acrescentada explicação sobre uso de corte seco e impacto no score.
- Criado este relatório em `docs/relatorio-implementacao-serrilha-corte-seco.md`.

## 5. Testes realizados

- `dotnet test` (projeto `FileWatcherApp.Tests`).
- Verificação manual de regex/configurações via inspeção de trechos relevantes (sed/nl).

## 6. Limitações conhecidas e próximos passos

- Heurística de corte seco depende de paralelismo e overlap — falsos negativos podem ocorrer em desenhos desalinhados ou com poucas repetições; tolerâncias devem ser validadas com DXFs reais.
- `CorteSecoBladeCodes` exige que códigos textuais sejam repetidos; se faltar anotação, a heurística não dispara.
- Ajustes finos de thresholds (offset, overlap, min length) recomendados após feedback de especialistas.
- Sugerido rodar `scripts/dxf-symbol-audit.csx` e revisar explicações do score com o time de produção antes de liberar em produção.

---

**Arquivos tocados** (principais):

- `Services/DXFAnalysis/DXFAnalysisOptions.cs`
- `Services/DXFAnalysis/Models/DXFAnalysisContracts.cs`
- `Services/DXFAnalysis/DXFAnalyzer.cs`
- `Services/DXFAnalysis/ComplexityScorer.cs`
- `tests/FileWatcherApp.Tests/SerrilhaAnalysisTests.cs`
- `tests/FileWatcherApp.Tests/DXFAnalysisTests.cs`
- `appsettings.json`
- `docs/complexidade-facas.md`
- `Services/DXFAnalysis/README.md`
- `docs/relatorio-implementacao-serrilha-corte-seco.md` (novo)

