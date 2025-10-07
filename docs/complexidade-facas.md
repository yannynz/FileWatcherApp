# Complexidade de Facas a partir de DXF

## Visão geral
- O FileWatcherApp já captura eventos de facas e envia mensagens via RabbitMQ (`Program.cs`).
- A proposta estende esse fluxo para extrair métricas do DXF, gerar uma imagem da faca e publicar tudo em uma fila dedicada que alimenta um serviço de ML.
- O resultado (score 0–5 + justificativas) volta para o backend/organizador, que associa automaticamente a uma OP existente ou futura.

## Fluxo proposto
1. **FileWatcherService** detecta um novo DXF e dispara uma tarefa de análise.
2. **Parser DXF** (ex.: [`netDxf`](https://github.com/haplokuon/netDxf) em .NET ou [`ezdxf`](https://ezdxf.readthedocs.io/en/stable/introduction.html) via worker Python) extrai features geométricas e semânticas.
3. **Renderização**: gerar uma imagem padronizada da faca (PNG/SVG) usando [`ezdxf.addons.drawing`](https://ezdxf.readthedocs.io/en/stable/addons/drawing.html) ou equivalente em .NET.
4. **Publicação** em uma nova fila (`facas.analysis.request`) contendo JSON com identificadores, métricas, observações e referência à imagem.
5. **Serviço ML** consome a fila, aplica heurísticas/modelo, devolve o score na fila `facas.analysis.result`.
6. **Backend/organizador** consome o resultado, associa à OP (ou guarda até ela existir) e persiste para consulta/auditoria.

## Extração de atributos
- Dimensão global (EXTMIN/EXTMAX) e área/perímetro do bounding box do desenho.
- Comprimento total de entidades de corte (`LINE`, `ARC`, `LWPOLYLINE`, `SPLINE`), segmentando por layer/linetype para diferenciar corte, vinco, serrilha, 3 pt.
- Contagem de interseções, nós e curvas, bem como o menor raio de arco para identificar trabalhos delicados.
- Leituras do “dicionário”/blocos de metadados no DXF quando presentes.
- Flags externas (ex.: emborrachada) obtidas da OP e anexadas às features.
- Referência útil: “How to assess the complexity of DXF drawings” discute pesos calibrados e métricas rápidas [quaoar.su/blog/page/how-to-assess-the-complexity-of-dxf-drawings](https://quaoar.su/blog/page/how-to-assess-the-complexity-of-dxf-drawings).

## Geração da imagem
- Renderizar sempre no mesmo zoom, unidade e espessura de linha para padronizar entradas.
- `ezdxf` já oferece backend Matplotlib que salva PNG com uma chamada (`matplotlib.qsave(doc.modelspace(), "out.png")`).
- Em .NET, libs como DXFReaderNET também convertem DXF para bitmap, caso prefiram manter tudo no mesmo runtime.

## Serviço de ML
- Baseline heurístico: soma ponderada de fatores (tamanho, metragem de corte, quantidade de serrilhas/vincos, presença de 3 pt, densidade de curvas). Pesos calibrados com especialistas.
- Evolução: modelos tabulares (Gradient Boosting/XGBoost) alimentados pelas mesmas features; usar o dataset rotulado (~50 GB disponíveis) para treinamento.
- Possível complemento: CNN sobre a imagem da faca para capturar padrões visuais difíceis de codificar manualmente.
- Sempre registrar a lista de fatores ativados para justificar o score, mesmo quando o modelo for ML.

## Integração com o backend
- Mensagens devem carregar `opId` quando já conhecido; caso contrário, armazenar o score até que uma OP correspondente apareça e executar o match (mesma lógica de debounce/regex usada hoje para OPs).
- Persistir features + imagem + score em armazenamento acessível ao time de produção para auditoria posterior.
- Monitorar tempos de parsing e inferência para identificar DXFs problemáticos ainda no início do pipeline.

## Pontos de atenção
- Padronizar o dicionário de layers/linetypes/cores com a equipe de produção antes de automatizar; cada tipo (corte, vinco, serrilha, 3 pt) precisa estar mapeado.
- Tratar DXFs “sujos” (gaps, overlaps) antes de medir comprimento; algumas libs exigem pré-processamento.
- Evitar enviar DXFs brutos nas filas: armazenar em diretório compartilhado/S3 e referenciar por caminho/URL.
- Desacoplar parsing pesado do watcher principal (ex.: mover para worker assíncrono) para não travar o monitoramento em tempo real.

## Próximos passos sugeridos
1. Inventariar um conjunto de facas recentes e documentar layers/linetypes usados para corte, vinco, serrilha e 3 pt.
2. Prototipar o extrator: ler DXF, calcular métricas principais e gerar PNG; validar resultados com o time de produção.
3. Definir o schema das mensagens novas (request/result) e adaptar o FileWatcher para publicar o pacote mínimo viável.
4. Implementar o serviço ML começando pelo baseline heurístico e planejar coleta de rótulos para treinar modelos supervisionados.

