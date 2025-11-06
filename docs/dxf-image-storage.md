# DXF Image Storage Pipeline

Este documento descreve em detalhes como o `FileWatcherApp` agora trata as imagens geradas da análise DXF, incluindo upload para um storage externo (S3/MinIO) e os metadados publicados para o Organizer. Use-o como referência quando for montar o container dedicado ao lado do Organizer ou ajustar o consumo no banco.

---

## 1. Visão geral

1. O `DXFAnalysisWorker` continua renderizando o DXF via `DXFImageRenderer`, mas agora recebe um `DXFRenderedImage` com:
   - `Data` (`byte[]`) com o PNG;
   - `SafeName` e `LocalPath` (copia local em `OutputImageFolder`);
   - `Sha256`, `WidthPx`, `HeightPx`, `Dpi`.
2. Após renderizar, o worker chama `IImageStorageClient.UploadAsync(...)`, passando um `ImageStorageUploadRequest`.
3. O client padrão (`S3ImageStorageClient`) envia o PNG para um bucket S3/MinIO usando um key baseado no hash do arquivo (`{hash}/{safeName}.png`).
4. O resultado do upload (`ImageStorageUploadResult`) alimenta o `DXFImageInfo` publicado na fila `facas.analysis.result`.
5. O Organizer passa a receber, além do caminho local, os campos `storageBucket`, `storageKey`, `storageUri`, `checksum`, `sizeBytes`, `uploadStatus`, etc.

> ⚠️ O upload é opcional. Se `DXFAnalysis.ImageStorage.Enabled = false`, o `NullImageStorageClient` registra `uploadStatus=disabled` e a imagem continua no disco local.

---

## 2. Estrutura de código

| Arquivo | Responsabilidade |
|---------|------------------|
| `Services/DXFAnalysis/DXFImageRenderer.cs` | Renderiza em memória e retorna `DXFRenderedImage` (dados + hash). |
| `Services/DXFAnalysis/DXFRenderedImage.cs` | DTO interno com payload do PNG. |
| `Services/DXFAnalysis/Storage/IImageStorageClient.cs` | Interface de upload. |
| `Services/DXFAnalysis/Storage/S3ImageStorageClient.cs` | Implementação S3/MinIO. |
| `Services/DXFAnalysis/Storage/NullImageStorageClient.cs` | No-op usada quando upload está desabilitado. |
| `Services/DXFAnalysis/Storage/ImageStorageUploadRequest(Result).cs` | Estruturas de entrada/saída do upload. |
| `Services/DXFAnalysis/DXFAnalysisWorker.cs` | Integra render + upload, constrói metadados para o resultado. |
| `Services/DXFAnalysis/Models/DXFAnalysisContracts.cs` | `DXFImageInfo` ampliado com novos campos. |
| `Program.cs` | Registra o client certo (S3 ou Null) baseado na configuração. |

---

## 3. Configuração (`appsettings.json`)

```json
"DXFAnalysis": {
  "OutputImageFolder": "C:\\FacasDXF\\Renders",
  "PersistLocalImageCopy": false,
  "ImageStorage": {
    "Enabled": true,
    "Provider": "s3",
    "Bucket": "facas-renders",
    "KeyPrefix": "renders",
    "Endpoint": "http://organizer-storage:9000",
    "Region": "us-east-1",
    "AccessKey": "minio",
    "SecretKey": "minio123",
    "UsePathStyle": true,
    "PublicBaseUrl": "https://cdn.empresa.com/facas",
    "SkipIfExists": true,
    "UploadTimeout": "00:00:20",
    "MaxRetries": 3
  }
}
```

### Campos importantes

- **Enabled**: liga/desliga upload (mantenha `false` em dev se não for subir storage).
- **Provider**: por enquanto somente `"s3"` está implementado.
- **Endpoint**: obrigatório para MinIO/self-hosted; deixe vazio para AWS real.
- **UsePathStyle**: `true` para compatibilidade com MinIO.
- **PublicBaseUrl**: opcional; gera URL pública (`storageUri`) usada pelo Organizer front.
- **SkipIfExists**: dedup simples (HEAD antes do PUT). Se `true`, o key repetido resulta em `uploadStatus=exists`.
- **UploadTimeout / MaxRetries**: evitam travar o worker quando o storage está lento.
- **PersistLocalImageCopy**: quando `false` (default), nenhum PNG fica gravado no disco do worker; o campo `path` na resposta vem vazio.

> As credenciais ficam neste arquivo apenas como exemplo. Em produção use variáveis de ambiente (`DOTNET_...`) ou Secret Manager.

---

## 4. Metadados enviados ao Organizer

`DXFAnalysisResult.image` agora possui:

| Campo | Descrição |
|-------|-----------|
| `path` | Caminho local (vazio quando `PersistLocalImageCopy=false`). |
| `widthPx`, `heightPx`, `dpi` | Dimensões do render. |
| `contentType` | MIME (`image/png`). |
| `sizeBytes` | Tamanho do PNG. |
| `checksum` | SHA-256 (hex minúsculo). |
| `storageBucket` / `storageKey` | Localização no storage. |
| `storageUri` | URL/CDN (quando `PublicBaseUrl` está configurado). |
| `uploadStatus` | `uploaded`, `exists`, `disabled`, `failed`, `error`. |
| `uploadedAtUtc` | Timestamp ISO 8601 do upload. |
| `etag` | ETag retornado pelo S3 (quando existe). |
| `uploadMessage` | Mensagem extra (erro, motivo do skip). |

O Organizer deve usar preferencialmente `storageUri`/`storageKey` para exibir imagens e guardar só metadados no banco. Se `uploadStatus=failed`, mantenha fallback para o `path` local até que a retentativa ocorra.

---

## 5. Estratégia de chave (`BuildStorageObjectKey`)

- Base: hash do arquivo (`SHA256`) se disponível; fallback para `analysisId`.
- Formato: `{hash-normalizado}/{safeName}.png`.
- `KeyPrefix` opcional (config) é aplicado antes do base (`renders/{hash}/...`).
- Hash tem `:` trocado por `_` para evitar problemas com S3.

Resultado: imagens idempotentes por hash (não multiplicam o storage) mas ainda preservam o nome amigável do arquivo.

---

## 6. Comportamento em falhas

- Upload lança exceção → worker registra warning, `uploadStatus=error`, `uploadMessage=...` e segue publicando resultado (para não travar fila).
- Timeout → respeita `UploadTimeout`. O worker tenta novamente até `MaxRetries`.
- Storage fora do ar (varias exceções) → status `failed` com mensagem agregada.
- Quando `SkipIfExists = true` e o objeto já está no bucket → `uploadStatus=exists` (sem sobrescrever).
- Se o cache contiver um resultado antigo sem metadados remotos (`storageBucket`/`storageUri`), o worker detecta a lacuna, reprocessa o DXF e tenta o upload novamente.
- Quando os metadados indicam imagem remota mas o objeto foi removido do bucket, o worker passa a consultar `ObjectExistsAsync` antes de reutilizar o cache. Caso o objeto esteja ausente, ele registra `Imagem remota ausente...` e refaz render/upload automaticamente.

### Logs úteis

- `Cache hit com imagem remota` / `Cache hit sem imagem remota` — indicam se o resultado foi reutilizado ou se precisou ser reprocessado para garantir o upload.
- `Iniciando upload da imagem renderizada` / `Upload concluído` — registram o ciclo de envio com bucket/key.
- Logs do `S3ImageStorageClient` detalham `SkipIfExists` e falhas (timeouts, autenticação etc.).

Próximos passos possíveis:

1. Agendador/organizer pode reprocessar `uploadStatus=error` posteriormente.
2. Excluir cópia local após upload (adicionar flag `PersistLocalCopy` no futuro).
3. Implementar outros providers (ex.: HTTP direto para o Organizer se ele virar proxy).

---

## 7. Checklist para colocar o container de storage em produção

- [ ] Provisionar MinIO/S3 com bucket `facas-renders` (ou equivalente).
- [ ] Criar usuário com política de acesso restrito (PUT/HEAD/GET) e gerar `AccessKey/SecretKey`.
- [ ] Configurar TLS no endpoint (recomendado) ou manter rede interna segura.
- [ ] Definir política de lifecycle (expiração ou versionamento) para controlar custo.
- [ ] Popular `appsettings.{Environment}.json` com `ImageStorage.Enabled=true` e credenciais opcionais via variáveis de ambiente.
- [ ] Garantir que o Organizer leia `storageUri` antes de gravar no banco (e use fallback local enquanto isso não existir).

---

## 8. Próximos passos (Organizer)

1. Atualizar o consumidor no Organizer para usar os novos campos (`storageUri`, `storageKey`, `checksum`, `sizeBytes`).
2. Evoluir a base de dados (nova tabela `dxf_renders` ou colunas) para armazenar somente metadados.
3. Criar endpoint/serviço no Organizer para expor download autenticado ou gerar links assinados (se necessário).
4. Opcional: mover a lógica de deduplicação/retentativas para lá (ex.: fila específica para upload).

Todos os detalhes acima já ficam documentados para quando atacarmos aquela parte.

---

## 9. Renderização e qualidade visual

- As imagens são geradas pelo `DXFImageRenderer` com fundo branco e ordem de camadas (`corte`, `vinco`, `serrilha`, `serrilhamista`, `trespt/3pt`, `outro`).
- O tamanho do canvas é ajustado automaticamente para enquadrar todo o bounding box geométrico com margem mínima de 5 % da peça **e** ao menos 48 px nas bordas. A renderização nunca ultrapassa 4 096 px no maior eixo.
- A espessura do traço é dinâmica (entre 2 px e 6 px) baseada na escala efetiva, garantindo nitidez mesmo quando a faca ocupa grande parte da imagem.
- O DPI informado no resultado corresponde à escala final calculada após possíveis reduções para caber no limite.

### Ferramenta de validação local

Para depurar a renderização sem passar por RabbitMQ/S3:

```bash
dotnet run --project Tools/RenderPreview/RenderPreview.csproj "NR 120184.dxf"
```

O utilitário reaproveita a mesma pipeline (`DXFPreprocessor` → `DXFAnalyzer` → `DXFImageRenderer`) com fallback para DXFs AC1014, salva o PNG em `C:\FacasDXF\Renders\` (ou `artifacts/renders` em Linux) e informa dimensões/DPI no console. Ideal para validar nitidez e centralização antes de reexecutar o worker completo.
