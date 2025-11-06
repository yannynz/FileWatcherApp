# Relatório de Calibração — Serrilha & Vinco 3 pt (abril/2025)

## Objetivo

Alinhar o motor determinístico de complexidade (`FileWatcherApp.Services.DXFAnalysis`) aos critérios operacionais atuais: penalizações por serrilha travada/zipper, bonificações para vinco 3 pt manual, contabilização de bocas (loops) e materiais especiais.

## Cenários avaliados

| Caso | Tipo | Score desejado | Score anterior | Score calibrado | Observações |
| --- | --- | --- | --- | --- | --- |
| NR 120184.dxf | NR real, alta densidade de bocas/curvas | 5.0 | 3.6 | **5.0** | Loops densos + arcos delicados, sem serrilha simbólica |
| NR119812.dxf | NR real com serrilha delicada | 3.0 | 1.9 | **2.97** | Serrilha “explodida”, min-radius ajustado para 0.35 mm |
| calibration_threept_complexity.dxf | Fixture sintético (3 pt volumoso) | 5.0 | 2.8 | **5.0** | 18 segmentos 3 pt, ratio 145 %, bônus manual ativado |
| calibration_zipper_complexity.dxf | Fixture sintético (serrilha zipper + adesivo) | 3.6 | 2.3 | **3.6** | Serrilha `zipper`, diversidade de bocas e adesivo |
| calibration_low_complexity.dxf | Fixture sintético (layout simples) | 1.6 | 0.4 | **1.45** | Poucas bocas, apenas cut length + material especial |

## Ajustes implementados

- **Extração de métricas**
  - `DXFAnalyzer` agora preenche `Quality.ClosedLoops`, `ClosedLoopsByType`, `DelicateArcDensity`, `SpecialMaterials` e as métricas de vinco 3 pt (`TotalThreePtLength`, `ThreePtSegmentCount`, `ThreePtCutRatio`, `RequiresManualThreePtHandling`).
  - `DXFSerrilhaSummary` inclui `Classification` (simple/travada/zipper/mista) e contadores de tipos/códigos distintos.
- **Pesos configuráveis (`DXFAnalysisOptions.Scoring`)**
  - `TotalCutLengthWeight = 0.8`, `NumCurvesWeight = 0.5`, `BonusIntersectionsWeight = 0.25`.
  - `MinRadius.PenaltyWeight = 0.37` com ajuste **positivo** de corte seco (`+0.3`).
  - `Serrilha`: presença (0.6), mista (0.7), travada (0.7), zipper (0.6), diversidade (0.4) e corte seco multi-tipo (0.35).
  - `ClosedLoops`: thresholds 2/4/8/20/40 e densidades `4.5e-5` / `5.5e-5`.
  - `ThreePt`: thresholds de comprimento (150/300 mm), segmentos (6/12), razão vs. corte (8 % / 15 %) e bônus manual (0.45).
  - `CurveDensity`: densidade 0.0005/0.00055 e contagem 12/28 arcos delicados.
  - `Materials`: `DefaultWeight = 0.5`, override para `adesivo = 0.5`, `borracha = 0.4`.
- **Fixtures de regressão (`tests/resources/dxf`)**
  - Gerados por `Tools/DxfFixtureGenerator` (`dotnet run --project Tools/DxfFixtureGenerator`).
  - Exercitam zipper + adesivo, 3 pt volumoso e desenhos simples/básicos.
- **Testes automatizados**
  - `ComplexityCalibrationTests` verifica score + explicações (tolerância ±0.25).
  - `DXFMetricsExtractionTests` garante os novos campos (3 pt, materiais, bocas).

## Exemplos de saída

### NR 120184.dxf — Score 5.0
```json
{
  "score": 5.0,
  "explanations": [
    "Comprimento de corte alto (25674.63 mm >= 2000 mm): +0.8",
    "Densidade de curvas elevada (266 >= 60): +0.5",
    "Bocas abundantes (42 loops >= 40): +0.8",
    "Densidade de bocas 5.749E-5 >= 5.5E-5: +0.8",
    "Densidade de curvas delicadas 0.1% >= 0.1%: +0.5",
    "Muitas interseções (250 >= 30): +0.25"
  ],
  "metrics": {
    "quality": {
      "closedLoops": 42,
      "specialMaterials": [],
      "delicateArcDensity": 0.000570067
    }
  }
}
```

### Fixture 3 pt — Score 5.0
```json
{
  "score": 5.0,
  "explanations": [
    "Vinco 3pt extenso (2520 mm >= 300 mm): +0.45",
    "Muitos segmentos 3pt (18 >= 12): +0.3",
    "Vinco 3pt dominante (145.0% >= 15.0%): +0.35",
    "Vinco 3pt exige dobra manual: +0.45",
    "Material especial (adesivo) demanda cuidado: +0.5",
    "Bocas abundantes (21 loops >= 20): +0.25"
  ],
  "metrics": {
    "totalThreePtLength": 2520.0,
    "threePtSegmentCount": 18,
    "threePtCutRatio": 1.45,
    "requiresManualThreePtHandling": true
  }
}
```

## Próximos passos sugeridos

1. Coletar novos DXFs reais contendo serrilha zipper/travada para ajustar tolerâncias de `DensityThresholds`.
2. Avaliar pesos diferenciados para materiais (`laminado`, `emborrachado`) conforme feedback da produção.
3. Integrar snapshots JSON no pipeline de QA e publicar histórico no Confluence.
