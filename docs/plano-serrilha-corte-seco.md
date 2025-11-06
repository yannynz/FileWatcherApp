# Plano de Otimização – Serrilha Textual, Corte Seco e Raios

## 1. Contexto e Objetivos

- Os DXFs reais usam letras/medidas para identificar serrilhas (`X=2x1 23,8`, `Y-10x0.4 23,6`). Esses textos não são capturados atualmente, deixando `metrics.serrilha` vazio.
- O “menor raio” simplesmente penaliza curvas pequenas. Em muitos casos isso indica **batida/corte seco** (laminas complementares) – algo que reduz complexidade.
- Precisamos:
  - Extrair serrilhas textuais com fidelidade (tipo, lâmina, comprimento, dentes).
  - Detectar batida seca e diferenciar cortes realmente delicados.
  - Ajustar o score conforme essas heurísticas.

## 2. Serrilha Textual

1. **Inventário**: recolher DXFs representativos. Para cada serrilha textual, registrar tipo, medida, comprimento, dentes e posição.
2. **Regex/Config**:
   - Ajustar `DXFAnalysis:SerrilhaTextSymbols` com grupos nomeados (`SemanticTypeGroup`, `BladeCodeGroup`, `LengthGroup`, `ToothCountGroup`).
   - Suportar múltiplos matches em um mesmo texto (`AllowMultipleMatches`).
3. **Validação**:
   - Rodar o `DXFAnalyzer` (ou `scripts/dxf-symbol-audit.csx`) sobre o conjunto etiquetado e comparar resultados.
   - Ajustar regex, normalização (upper-case, formatação) até alcançar boa cobertura.
4. **Testes**:
   - Expandir `SerrilhaAnalysisTests` com casos textuais reais.

## 3. Detecção de Corte/Batida Seco

1. **Definição com especialistas**:
   - Quais padrões geométricos caracterizam uma batida seca? Distância limite? Tipos específicos?
2. **Heurísticas sugeridas**:
   - Procurar pares de segmentos paralelos/espelhados com offset pequeno.
   - Cruzar com serrilha textual (ex.: mesmo código/letra aparecendo em regiões complementares).
3. **Protótipo**:
   - Criar função que marca `isCorteSeco` em `metrics.serrilha` quando as condições são atendidas.
4. **Impacto no score**:
   - Se detectado, remover o ponto de “raio delicado” ou até diminuir o score (batida seca reduz complexidade de produção).
5. **Validação**:
   - DXFs sintéticos e reais com feedback do time para evitar falsos positivos.

## 4. Recalibração do Raio Mínimo

1. **Analisar dados reais**: coletar os raios mínimos atuais e confirmar quais realmente exigem cuidado.
2. **Ajustes**:
   - Introduzir faixa neutra (p.ex. <0.3 mm penaliza, 0.3–1.0 mm neutro, >1 mm neutro).
   - Se houver `isCorteSeco`, ignorar penalização.
   - Considerar largura disponível ou contexto (ex.: segmento único vs. repetição).
3. **Testes de regressão**: rodar casos reais e sintéticos para garantir que apenas situações delicadas continuam penalizando.

## 5. Integração e Monitoração

1. Implementar em etapas:
   - Serrilha textual completa.
   - Corte seco.
   - Raio mínimo refinado.
2. Atualizar o `ComplexityScorer` com novos fatores (positivo/negativo) e registrar explicações claras.
3. Executar a suíte de testes e DXFs conhecidos após cada ajuste.
4. Monitorar as primeiras execuções em produção e coletar feedback das equipes de piso.

## 6. Próximos Passos Imediatos

- Montar um conjunto de DXFs reais anotado pelo time (tipos de serrilha, batidas secas).
- Ajustar `SerrilhaTextSymbols` com base nesses exemplos e validar com scripts/testes.
- Prototipar heurística de corte seco (talvez em uma branch separada) e revisar com especialistas antes de alterar o score.
