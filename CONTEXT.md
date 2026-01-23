Contexto rápido (FileWatcherApp)
================================

- Worker .NET 8 que monitora pastas e publica mensagens (RabbitMQ) e resultados de análise DXF.
- Principais serviços:
  - `FileWatcherService`: watchers para Laser/Facas/Dobras/Ops; publica `facas.analysis.request` (análise DXF) e notificações legado. Sufixos `.m.dxf/.fcd.dxf` são ignorados para análise (ShouldSkipDxf).
  - `DXFAnalysisWorker`: consome `facas.analysis.request`, processa DXF, renderiza PNG, faz upload via `IImageStorageClient` (S3/MinIO) e publica em `facas.analysis.result`.
  - `PdfParser`: extrai OP (número, cliente, endereço, modalidade entrega etc.). Endereço vira `enderecosSugeridos` para o Organizador enriquecer cliente/endereço.
- Configuração prod (appsettings.Production.json):
- RabbitMQ host: `192.168.10.13:5672`.
- DXFAnalysis.ImageStorage: `Endpoint/PublicBaseUrl = http://192.168.10.13:9000/facas-renders`, bucket `facas-renders`, `PersistLocalImageCopy=false`.
  - Pastas Linux (se usado): `/home/laser`, `/home/laser/FACASOK`, `/home/dobras`, `/home/nr`.
- Upload de imagem:
  - `DXFImageInfo` envia `storageBucket`, `storageKey`; `storageUri` é nulo para evitar URL de ambiente. Backend compõe `imageUrl` com `image-base-url`.
  - Falhas de upload aparecem como `image_upload_status=error/failed` no banco do Organizador e logs do worker.
- Dicas de depuração:
  - Verificar se `DXFAnalysisResult` publicado contém `storageKey/bucket`.
  - Confirmar acesso MinIO: `curl -I http://<host>:9000/minio/health/live`.
  - Nomes de DXF: `FileWatcherNaming` extrai OP apenas de `NR <num>`/`CL <num>`; underscores quebram o vínculo (use espaço).
- Build/run:
  - `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64`
  - Em Windows: copiar appsettings.Production.json ajustado e rodar o executável; em Linux: `DOTNET_ENVIRONMENT=Production dotnet FileWatcherApp.dll`.

Estado local vs remoto
----------------------
- appsettings.Production.json já está com os IPs originais (192.168.10.13); alterações locais visíveis no git status são apenas artefatos de build (bin/obj).  
