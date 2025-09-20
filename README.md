# FileWatcherApp

## 1. O que é
FileWatcherApp é um serviço .NET 8 que monitora diretórios da operação de corte e dobra, normaliza os nomes dos arquivos e publica eventos estruturados no RabbitMQ. Ele trabalha em conjunto com o projeto Spring Boot **organizador-producao**, que consome as filas `laser_notifications`, `facas_notifications`, `dobra_notifications` e `op.imported` para atualizar o painel de produção.

Principais responsabilidades:
- Vigiar as pastas de laser, facas, dobra e ordens de produção.
- Aplicar debounce para evitar mensagens duplicadas e garantir que o arquivo terminou de ser gravado.
- Normalizar nomes de arquivos e extrair o número da NR, mesmo quando o operador inclui textos adicionais (ex.: `NR 999999 NOVO.m.DXF`).
- Enviar mensagens JSON consistentes para o RabbitMQ, incluindo metadados e o caminho real do arquivo.
- Responder a checks de saúde via fila RPC (`filewatcher.rpc.ping`).

## 2. Como funciona
1. **Watchers dedicados** (`Program.cs`): quatro `FileSystemWatcher`s acompanham as pastas de laser, facas, dobra e PDF de OP. Cada alteração cria um *debouncer* (Timer) para consolidar múltiplos eventos em um único processamento.
2. **Sanitização de nomes**:
   - `CleanFileName` (para laser/facas) normaliza o cliente, prioridade e sufixo `.CNC`.
   - `TrySanitizeDobrasName` aceita qualquer nome que contenha `NR <número>` e termine em `.m.DXF` ou `.DXF.FCD`, retornando `NR 123456.m.DXF` para o consumo do organizador.
3. **Deduplicação e espera**: `WaitFileReady` só publica após o arquivo estabilizar e o watcher confirma se o evento não aconteceu nos últimos 2 minutos (dobra).
4. **Publicação RabbitMQ**: `SendToRabbitMQ` serializa o payload com `System.Text.Json`, define cabeçalhos (`__TypeId__`), e envia para a fila configurada. O serviço também mantém conexões resilientes e um consumer RPC para heartbeat.
5. **Integração com organizador-producao**: o Spring Boot recebe os eventos, atualiza os pedidos (`OrderRepository`) e republica via WebSocket. A lógica de dobras agora aceita reprocessar facas já tiradas, sobrescrevendo `dataTirada` se houver retrabalho.

## 3. Por que dessa abordagem
- **Resiliência**: combinar debounce + `WaitFileReady` evita publicar arquivos incompletos, reduzindo retrabalho.
- **Normalização agressiva**: fabricantes frequentemente salvam arquivos com texto extra; sanitizar garante que o backend identifique a NR mesmo com variações.
- **Observabilidade**: logs claros (`[DOBRAS] Mensagem publicada ... como 'NR 999999.m.DXF'`) ajudam em auditoria e suporte.
- **Integração simples**: mensagens JSON planas com timestamps e caminhos permitem que o organizador-producao atualize status e históricos sem depender do sistema de arquivos.
- **Reprocessamento controlado**: sobrescrever `dataTirada` mantém histórico atualizado quando uma faca retorna para ajustes.

## 4. Configuração rápida
1. **Pré-requisitos**
   - .NET 8 SDK
   - RabbitMQ (com as filas listadas em `Program.cs`) e rede acessível.
   - Opcional: Windows para diretórios padrão (`D:\Laser`, `D:\Dobradeira`), ou adapte os paths comentados para Linux.
2. **Appsettings**: valores básicos estão embutidos no código. Caso precise de parâmetros dinâmicos, adicione em `appsettings.json` e injete via `Microsoft.Extensions.Configuration`.
3. **Build**
   ```bash
   dotnet build
   ```
4. **Execução**
   ```bash
   dotnet run
   ```
   O console exibirá os diretórios monitorados e manterá a aplicação rodando até `CTRL+C`.

## 5. Testes automatizados
Os testes cobrem os pontos críticos de normalização e integração com o backend:

### 5.1 FileWatcherApp (xUnit)
- **O que**: `ProgramDobrasTests` valida `TrySanitizeDobrasName` e `CleanFileName` com entradas ruidosas, sufixos alternativos e formatos inválidos.
- **Como**: chama os métodos privados via reflexão, checa NR extraído, nome sanitizado e bloqueio de palavras reservadas.
- **Por que**: garante que qualquer nome salvo de forma livre pelo operador seja transformado no formato esperado antes de publicar no RabbitMQ.

**Rodar**:
```bash
dotnet test tests/FileWatcherApp.Tests/FileWatcherApp.Tests.csproj
```

### 5.2 Organizador-producao (JUnit 5 + Mockito)
- **O que**: `OrganizadorProducaoApplicationTests` substitui o teste de contexto e exercita `DobrasFileService` — extração de NR, atualização do status e reprocessamento de facas já tiradas.
- **Como**: usa `MockitoExtension` para mockar `OrderRepository` e `SimpMessagingTemplate`, invoca `handleDobrasQueue` e `updateOrderStatusToTirada`, e verifica interações e timestamps com AssertJ.
- **Por que**: evita regressões na integração com o FileWatcher e confirma que retrabalhos sobrescrevem `dataTirada` corretamente.

**Rodar** (no diretório `organizador-producao`):
```bash
./mvnw test -Dtest=OrganizadorProducaoApplicationTests
```

## 6. Diagnóstico e manutenção
- Falhas de build devido a lock de NuGet (`/tmp/NuGetScratch...`) indicam processos travados; remova o arquivo ou reinicie o host.
- Use os logs do FileWatcher para conferir se nomes foram sanitizados como esperado.
- Para adicionar novos formatos de arquivos de dobra, basta estender `DobrasSuffixNormalization` (C#) e `DOBRAS_SUFFIXES` (Java).
- Reafine os thresholds (`DobrasDedupWindow`, timers) conforme necessidade do chão de fábrica.

---
Para detalhes do consumo e UI, veja `organizador-producao/README.md`. Ambos os projetos formam o pipeline completo: **FileWatcher** detecta e normaliza → **Organizador** atualiza a produção em tempo real.
