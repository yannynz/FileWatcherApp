# SkiaSharp no FileWatcherApp - guia de runtime cross-platform

Este documento consolida **tudo** que aprendemos ao investigar a exceção `System.DllNotFoundException: libSkiaSharp` quando o `FileWatcherApp` roda no Linux (e também o que é necessário para continuar funcionando no Windows Server). A ideia é que qualquer pessoa — mesmo sem histórico do problema — consiga entender o porquê, o que foi alterado e como manter o ambiente saudável.

## 1. Sintomas observados

- Durante o processamento de um DXF, o serviço `DXFImageRenderer` explode com:
  ```
  System.TypeInitializationException: The type initializer for 'SkiaSharp.SKImageInfo' threw an exception.
    ---> System.DllNotFoundException: Unable to load shared library 'libSkiaSharp' or one of its dependencies.
  ```
- O log mostra que o runtime procura `libSkiaSharp.so` em vários diretórios (pasta do .NET runtime, `bin/Debug/net8.0/`, etc.) e não encontra.
- Após a falha de renderização, o `DXFAnalysisWorker` finaliza de forma abrupta (`Unhandled exception. System.TypeInitializationException: The type initializer for 'SkiaSharp.SKObject' threw an exception`).

### Relevância

Sem a imagem renderizada, nenhuma das funcionalidades visuais (geração de previews, métricas dependentes de bitmap, validações de geometria) funciona. Em Windows isso passava despercebido porque o `SkiaSharp` traz a DLL nativa junto com o sandbox de desenvolvimento, mas no Linux a dependência não vinha automaticamente.

## 2. Causa raiz

1. O pacote `SkiaSharp` (nuget `SkiaSharp`) contém **apenas os assemblies gerenciados** (`SkiaSharp.dll`).
2. As bibliotecas nativas (`libSkiaSharp.so`, `skia.dll`, etc.) são entregues separadamente nos pacotes `SkiaSharp.NativeAssets.{Linux|Win32|macOS|...}`.
3. Como o `FileWatcherApp.csproj` não referenciava as native assets, o publish e o binário de desenvolvimento no Linux não copiavam `libSkiaSharp.so`.
4. Quando o runtime inicializa qualquer tipo de objeto `SkiaSharp`, ele invoca interop com `libSkiaSharp`. Sem o `.so`, a inicialização falha e desencadeia a cascata de exceções acima.

## 3. Diagnóstico e confirmação

Para qualquer ambiente:

1. Rodar `dotnet build` e inspecionar `bin/Debug/net8.0/runtimes/*/native/`.
   - Antes da correção, a pasta `runtimes/linux-x64/native/` simplesmente não existia.
2. Verificar `FileWatcherApp.deps.json`: o arquivo listava `SkiaSharp` mas não citava nenhum `SkiaSharp.NativeAssets.*`.
3. No Linux, executar `ldd ./bin/Debug/net8.0/runtimes/linux-x64/native/libSkiaSharp.so` (depois da correção) para checar dependências de sistema.

## 4. Correções aplicadas no projeto

### 4.1 Referenciar os pacotes nativos corretos

```xml
<!-- FileWatcherApp.csproj:37-39 -->
<PackageReference Include="SkiaSharp" Version="2.88.6" />
<PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="2.88.6" />
<PackageReference Include="SkiaSharp.NativeAssets.Win32" Version="2.88.6" />
```

- O SDK resolve automaticamente qual runtime usar na hora do publish/execução.
- Mantemos as versões alinhadas (`2.88.6`) para evitar incompatibilidades de ABI.
- Se surgirem novos ambientes (ex.: macOS), basta incluir o pacote correspondente (`SkiaSharp.NativeAssets.macOS`).

### 4.2 Ajustar o *item include* da solução

O repositório tem pastas com nomes estilo Windows (`C:\FacasDXF\Cache`). No Linux, o SDK interpreta isso como caminho relativo e tenta expandir `**/*.cs`, resultando em erros de globbing.

Alterações no `.csproj`:

```xml
<!-- FileWatcherApp.csproj:9 -->
<DefaultItemExcludes>$(DefaultItemExcludes);C:\FacasDXF\**;C:/FacasDXF/**</DefaultItemExcludes>

<!-- FileWatcherApp.csproj:10 -->
<EnableDefaultCompileItems>false</EnableDefaultCompileItems>

<!-- FileWatcherApp.csproj:20-24 -->
<ItemGroup>
  <Compile Include="Program.cs" />
  <Compile Include="PdfParser.cs" />
  <Compile Include="Messaging/**/*.cs" />
  <Compile Include="Services/**/*.cs" />
  <Compile Include="Util/**/*.cs" />
</ItemGroup>
```

Racional:

- Bloqueia o globbing automático que estava tentando varrer as pastas “C:\FacasDXF”.
- Lista explicitamente os diretórios que compõem o app.
- Mantém os testes/ferramentas fora do build principal (`<None Remove="tests/**" />` etc.).

Sem esses ajustes o `dotnet build` falha com `CS2001`/`CS2021` quando roda em Linux.

## 5. Procedimento completo de build e execução

### 5.1 Após clonar ou atualizar dependências

```bash
dotnet restore
dotnet build
```

- *Importante*: depois de adicionar/alterar pacotes, **sempre** rodar `dotnet build` (ou `dotnet run` sem `--no-build`) pelo menos uma vez.
- Isso garante que o SDK copie os nativos para `bin/Debug/net8.0/runtimes/<rid>/native/`.

### 5.2 Execução em desenvolvimento

```bash
export DOTNET_ENVIRONMENT=Development
dotnet run --project FileWatcherApp.csproj
```

- Evitar `--no-build` na primeira execução após mudanças de pacote; caso contrário, o runtime pode reaproveitar artefatos antigos sem `libSkiaSharp`.
- O host pode ficar aberto indefinidamente; use `Ctrl+C` para parar.

### 5.3 Publicação

```bash
dotnet publish FileWatcherApp.csproj -c Release -r linux-x64 --self-contained false
dotnet publish FileWatcherApp.csproj -c Release -r win-x64  --self-contained false
```

- As pastas `publish` includem agora os nativos corretos (`libSkiaSharp.so` para Linux, `libSkiaSharp.dll` para Windows).
- Se precisar de *self-contained*, manter o RID correspondente e testar porque o pacote nativo também muda de pasta.

## 6. Dependências do sistema operacional

Mesmo com `libSkiaSharp.so`, o SkiaSharp depende de algumas libs básicas:

- `libfontconfig1`
- `libfreetype6`
- `libpng16-16`
- `libuuid1`

Em distribuições baseadas em Debian/Ubuntu:

```bash
sudo apt-get install -y libfontconfig1 libfreetype6 libpng16-16 libuuid1
```

Em Windows Server, essas dependências já acompanham o runtime padrão do .NET. Apenas garanta que o VC++ redistributable mais recente esteja instalado (Skia usa ponte nativa compilada em C++).

## 7. Como validar se tudo está correto

1. Rodar `dotnet build` e confirmar saída limpa.
2. Conferir a presença de `bin/Debug/net8.0/runtimes/linux-x64/native/libSkiaSharp.so`.
3. Executar `dotnet run` e observar logs do worker `DXFAnalysisWorker` **sem** warnings de renderização.
4. (Opcional) Gerar um publish e testar no Windows Server: `bin/Release/net8.0/win-x64/publish/libSkiaSharp.dll` deve existir.

Se qualquer etapa falhar, revisar:

- Se os pacotes nativos estão com mesma versão do `SkiaSharp`.
- Se foi feito `dotnet build` após atualizar pacotes.
- Se o host Linux tem as libs do sistema instaladas.
- Se não há limpeza parcial (por exemplo, deletar só `bin/` e esquecer `obj/`).

## 8. Checklist rápido

- [x] `FileWatcherApp.csproj` contém as referências `SkiaSharp.NativeAssets.Linux` e `SkiaSharp.NativeAssets.Win32`.
- [x] `dotnet build` executa sem erros relacionados a `**/*.cs`.
- [x] `libSkiaSharp.so` aparece na pasta `runtimes/linux-x64/native/`.
- [x] `DXFAnalysisWorker` processa um DXF completo sem `DllNotFoundException`.
- [x] Dependências do SO instaladas no Linux.

## 9. Práticas recomendadas daqui para frente

- Sempre alinhar a versão dos pacotes `SkiaSharp` + `SkiaSharp.NativeAssets.*`.
- Quando adicionar novas pastas de código, atualizar o `ItemGroup` de `<Compile Include="...">`.
- Se surgir uma nova plataforma (por exemplo, ARM ou macOS), incluir o pacote nativo correspondente e validar com `dotnet publish -r <rid>`.
- Evitar rodar com `--no-build` após mudanças de dependências — especialmente em ambientes de CI.
- Versionar qualquer script de provisionamento que instale as libs do sistema (para reproduzir ambientes com facilidade).

## 10. FAQ

**Q: Por que não usamos `SkiaSharp.NativeAssets.Linux.NoDependencies`?**  
Esse pacote assume que todas as dependências de sistema já estão provisionadas e entrega apenas `libSkiaSharp.so`. Como ainda estamos montando a infraestrutura, preferimos o pacote “completo”, que inclui os binários pré-compilados e tem melhor suporte a múltiplos RIDs.

**Q: Preciso fazer algo diferente para `dotnet publish --self-contained`?**  
Não. Os pacotes nativos também funcionam em modo self-contained. Apenas certifique-se de publicar com o RID correto e verificar que os arquivos estão em `publish/runtimes/<rid>/native/`.

**Q: E se eu quiser rodar testes que usam SkiaSharp?**  
Garante que o projeto de testes também referencie os pacotes nativos. Hoje os testes compartilham o `ProjectReference`, então ao compilar o app principal o runtime do teste já herda os assets.

---

Com essas orientações, o pipeline fica previsível e a equipe não precisa reviver o pesadelo do `DllNotFoundException`. Manter este documento atualizado sempre que o setup de build/execução mudar.
