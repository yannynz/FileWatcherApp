# Atualização de Vinco

## Parser e Mensageria
- Passamos a detectar menções a vinco dentro do bloco de matéria-prima, normalizando os termos para evitar variações de acentuação.
- O novo campo `VaiVinco` segue no record `ParsedOp` e é enviado na payload do RabbitMQ, garantindo que o Organizador receba o indicador.
- Logs de importação agora exibem `emborrachada` e `vaiVinco`, facilitando auditoria em produção.

## Heurísticas complementares
- Mantemos um fallback no serviço principal que tenta identificar vinco em materiais já carregados, preservando compatibilidade com lotes antigos.

## Testes
- Nova suíte em `PdfParserObservacoesTests` cobre detecção positiva/negativa do helper de vinco e garante que o PDF exemplo não acione o flag.

Execute `dotnet test tests/FileWatcherApp.Tests/FileWatcherApp.Tests.csproj --nologo` para validar.
