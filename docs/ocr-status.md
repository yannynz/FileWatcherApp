<!--
OCR via render/Tesseract – estado atual
=======================================

- Escopo: ler textos “vetorizados” (linhas/raios) em DXF via render + OCR e propagar para `DXFAnalysisResult` (`RawAttrs["ocrText"]` + parse de atributos), habilitando inspeção visual automatizada e extração de códigos (serrilha, materiais, OP, etc.) mesmo quando não há entidades TEXT/MTEXT no arquivo.

Feito
-----
- Implementado OCR no pipeline (`OcrEngine` + fallback no `DXFAnalysisWorker` quando não há TEXT/MTEXT).
- Configurações de OCR expostas em `DXFAnalysis:Ocr` (habilitado, psm, idiomas, binário).
- Regex de atributos e modelos adicionados ao `appsettings` (Attributes + Ocr) e replicados para publish (win-x64) com IPs atualizados para `192.168.0.116`.
- Varrido OCR em **todos** os DXFs de `~/Desktop/ferreira` (saída: `~/Desktop/ferreira_ocr_summary.txt`), confirmando que:
  - A maioria não contém TEXT/MTEXT; OCR retornou apenas ruído.
  - Serrilha textual detectada em: `NR 120753.dxf` (8x75) e `NR 515860/515866/515887/515888/515890.dxf` (2x1).
  - Muitos `.m.DXF/.fcd.*` não abriram (versão DXF “Unknown”).

Não feito / bloqueios
---------------------
- Não há extração confiável de textos vetorizados; OCR padrão (render atual) não recupera conteúdo útil.
- Não foram convertidos os DXFs em formato desconhecido (AutoCAD R14/2000) para permitir render/OCR.
- Não há pós-processamento de imagem (binarização/dilatação) específico para OCR.

Possibilidades e opções para concluir a feature
-----------------------------------------------
1. Melhorar legibilidade do render para OCR
   - Aumentar DPI do render somente para OCR (ex.: 600) e ajustar `Ocr:AdditionalArgs` (`--psm 11 --oem 1`).
   - Aplicar filtros na imagem gerada (binarização, dilatação leve, inversão se fundo ficar escuro).
   - Tornar a espessura mínima de stroke maior no render OCR-friendly.
2. Converter DXFs que falham por versão
   - Regravar os arquivos “DXF file version not supported : Unknown” em R2000/R2004 e reprocessar.
3. Afinar regex e heurísticas
   - Ampliar regex de serrilha e materiais para aproveitar ruídos que contenham padrões (ex.: “ADESIVO”, “BORRACHA”, códigos de OP).
   - Aceitar OCR bruto em `RawAttrs["ocrText"]` mesmo sem match para apoiar revisão manual.
4. Validação incremental
   - Testar novamente após ajustes de DPI/filtros com um subconjunto (ex.: NR 120247, NR 120258, NR 120735) para calibrar legibilidade.
-->