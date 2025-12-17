# Histórico de Atualizações do Projeto FileWatcherApp

**Data da Última Atualização:** 17 de Dezembro de 2025
**Projeto:** FileWatcherApp (Worker Service .NET 8.0)

Este documento descreve cronologicamente e detalhadamente as evoluções, correções e implementações realizadas no projeto `FileWatcherApp`, cobrindo desde a estabilização do parser de PDF até a integração completa com RabbitMQ e Minio (S3).

---

## 1. Otimização e Correção do Parser de PDF (`PdfParser.cs`)

**Objetivo:** Permitir a leitura robusta de Ordens de Produção (OPs) em formato PDF não estruturado (extração de texto cru), visto que os layouts variavam significativamente.

### Detalhes da Implementação:
*   **Correção de Compilação:** Resolução do erro `CS1009` (Unrecognized escape sequence) e remoção de blocos de código duplicados que causavam inconsistência na lógica de extração.
*   **Refinamento de Expressões Regulares (Regex):**
    *   **Número da OP (`NumeroOp`):** A lógica foi alterada para suportar quebras de linha entre o rótulo "Nº O.P." e o valor numérico. Foi implementado `LastDigitsRegex` para validar OPs extraídas do nome do arquivo como fallback.
    *   **Inscrição Estadual:** A regex `InscricaoEstadualRegex` foi ajustada para exigir explicitamente o rótulo "Inscrição", evitando que números aleatórios (como anos "2025") fossem capturados incorretamente.
    *   **Telefones:** A regex `TelefoneRegex` foi expandida para suportar o formato `DDDD-DDDD-DDDD` e ignorar textos intermediários extensos entre o rótulo e o número.
    *   **Emails:** A `EmailRegex` foi ajustada para capturar múltiplos e-mails separados por ponto e vírgula ou vírgula.
*   **Extração de Endereços:**
    *   Implementada lógica de "Lookahead negativo" manual: a leitura das linhas do endereço é interrompida imediatamente ao encontrar keywords de outros campos (CNPJ, Email, Data), prevenindo "sujeira" nos dados do endereço.
    *   Estratégia de duas passagens: Primeiro tenta extrair endereço completo (com Bairro); se falhar, tenta extrair sem o campo Bairro.

---

## 2. Sistema de Comandos via RabbitMQ (`FileCommandConsumer.cs`)

**Objetivo:** Permitir que sistemas externos solicitem alterações nos arquivos monitorados (especificamente renomeação de prioridade) através de mensagens na fila `file_commands`.

### Detalhes da Implementação:
*   **Consumer Dedicado:** Criação do `FileCommandConsumer` (BackgroundService) que escuta a fila `file_commands`.
*   **Ação `RENAME_PRIORITY`:**
    *   Recebe um JSON contendo `{ "Action": "RENAME_PRIORITY", "Nr": "12345", "NewPriority": "VERMELHO", "Directory": "LASER" }`.
    *   **Busca Segura:** Utiliza `Directory.GetFiles` com padrão wildcard, mas aplica um **filtro Regex secundário** (`(?:^|[^0-9]){Nr}(?:[^0-9]|$)`) para garantir que o número "123" não dê match no arquivo "12345.nc".
    *   **Lógica de Sufixo:**
        *   Detecta se o arquivo já possui prioridade (ex: `_AMARELO`, `_VERDE`) e a substitui.
        *   Se não possuir, anexa o novo sufixo antes da extensão (ex: `12345.nc` -> `12345_VERMELHO.nc`).
    *   **Preservação de Extensão:** A lógica garante que a extensão original (`.nc`, `.dxf`, etc.) seja mantida intacta.

---

## 3. Monitoramento e Detecção de Arquivos (`FileWatcherService.cs` & `FileWatcherNaming.cs`)

**Objetivo:** Monitorar múltiplas pastas (Laser, Facas, Dobras, OPs) e disparar eventos corretos baseados em convenções de nomenclatura.

### Detalhes da Implementação:
*   **Debounce (Anti-Flood):** Implementação de timers (`System.Timers.Timer`) para evitar múltiplos eventos disparados pelo sistema operacional enquanto o arquivo está sendo copiado/escrito.
*   **Separação de Responsabilidades:**
    *   **Watcher Laser/Facas:** Utiliza `FileWatcherNaming.CleanFileName` para validar nomes de peças CNC (exige cor/cliente).
    *   **Watcher Dobras:** Utiliza `FileWatcherNaming.TrySanitizeDobrasName` para validar arquivos DXF de facas de dobra (aceita sufixos como `.DXF`, `.M.DXF`).
*   **Integração com Análise:**
    *   Ao detectar um novo arquivo (seja via Watcher de Facas ou Dobras), o serviço extrai o ID da OP e publica uma solicitação na fila `facas.analysis.request` para processamento assíncrono.

---

## 4. Pipeline de Análise DXF e Integração Minio (`DXFAnalysisWorker.cs`)

**Objetivo:** Renderizar uma imagem de preview (PNG) a partir de arquivos DXF, calcular complexidade e armazenar o resultado em Object Storage (S3/Minio).

### Detalhes da Implementação:
*   **Fluxo de Execução:**
    1.  **Detecção:** Recebe mensagem da fila `facas.analysis.request`.
    2.  **Hashing:** Calcula o SHA256 do arquivo físico para garantir unicidade e integridade.
    3.  **Verificação de Cache Local:** Verifica se este hash já foi processado (`DXFAnalysisCache`).
    4.  **Verificação de Cache Remoto (Minio):**
        *   Antes de renderizar, consulta o Minio (`S3ImageStorageClient`) para ver se a imagem já existe.
        *   Se existir, pula a renderização pesada e retorna o resultado cacheado (Otimização crítica de performance).
    5.  **Renderização:** Se não houver cache, utiliza `CalibratedDxfRenderer` (via `SkiaSharp`) para gerar o PNG.
    6.  **Upload:** Envia o PNG para o bucket `facas-renders` no Minio.
    7.  **Publicação de Resultado:** Publica métricas e status na fila `facas.analysis.result`.
*   **Configuração S3/Minio:**
    *   Implementado suporte a configuração via `appsettings.json` (`DXFAnalysis:ImageStorage`).
    *   Utiliza `AWSSDK.S3` para compatibilidade com Minio.

---

## 5. Infraestrutura e Configuração (`Program.cs`, `appsettings.json`)

**Objetivo:** Garantir que a aplicação rode corretamente em ambientes de Desenvolvimento (Localhost) e Produção (Docker/Server), conectando-se aos serviços corretos.

### Detalhes da Implementação:
*   **Ambientes:**
    *   Configuração de `appsettings.Development.json` para apontar RabbitMQ e Minio para `127.0.0.1` (localhost).
    *   Configuração de `appsettings.Production.json` para apontar para os nomes de serviço Docker ou IPs de rede (`192.168.x.x`).
*   **Dependency Injection:**
    *   Registro de serviços como Singleton (`RabbitMqConnection`, `S3ImageStorageClient`) para manter conexões vivas e eficientes.
    *   Configuração de HostedServices (`FileWatcherService`, `DXFAnalysisWorker`, `FileCommandConsumer`) para execução em background.
*   **Resiliência:**
    *   Políticas de Retry (tentativas) implementadas no upload do S3 e na conexão inicial do RabbitMQ.

---

## Resumo do Status Atual

O sistema encontra-se **totalmente funcional** com os seguintes fluxos validados:
1.  **Leitura de PDF:** Processa OPs corretamente e envia dados para fila `op.imported`.
2.  **Renomeação:** Aceita comandos externos e renomeia arquivos físicos com sufixos de prioridade.
3.  **Processamento DXF:** Detecta arquivos, gera previews, verifica cache inteligente e faz upload para o Minio.
4.  **Conectividade:** Validada comunicação com RabbitMQ e Minio tanto em ambiente local quanto simulando produção.
