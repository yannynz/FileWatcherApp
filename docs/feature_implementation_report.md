# Relatório de Implementação da Feature: Extração Detalhada de Dados de PDF

Este documento detalha as modificações realizadas no `FileWatcherApp` para expandir a extração de informações de arquivos PDF, conforme solicitado. O objetivo foi incluir dados como CNPJ/CPF, Inscrição Estadual, Telefone, Email e componentes detalhados de endereço (CEP, Logradouro, Bairro, Cidade, UF), que são cruciais para a feature em desenvolvimento.

**Data:** 28 de Novembro de 2025

---

## 1. Análise Inicial e Diagnóstico

A feature original apresentava um problema crítico: o campo `cliente` estava vindo como `null` ou com o cabeçalho (`Nome/Razão Social do Cliente`) em vez do nome real do cliente.

*   **Arquivo de Status Analisado:** `Documents/status_report_20251127.md`
*   **Problema Identificado:** `PdfParser.cs` não extraía o nome do cliente corretamente devido à formatação colunar de alguns PDFs, onde o "rótulo" do campo era capturado no lugar do "valor".
*   **Debug:** Foi adicionado um modo de debug temporário ao `Program.cs` (`debug-pdf`) para inspecionar a saída bruta do `PdfParser` em PDFs problemáticos.
*   **Saída `pdftotext` (exemplo `/home/nr/Ordem de Produção nº 120435.pdf`):**
    ```
          Nº O.P.               Data               Cód Cliente       Nome/Razão Social
     do Cliente
                                                                                       
          120435             12/11/2025               01276           YCAR ARTES GRÁFIC
     AS LIMITADA
         CNPJ/CPF              Inscrição             Telefone         Email
                                                                                       
    53.856.829/0001-57      635511994111         3531-6638-6607       luiza@ycar.com.br
    ;kelly@ycar.com.br;terceiros@ycar.com.br
            CEP          Endereço (rua, nº, complemento, bairro)
                     Cidade/UF
                                                                                       
         09691-350       RUA LIBERO BADARO 1201 - PAULICEIA
                     SAO BERNARDO DO CAMPO/SP
    ```

---

## 2. Modificações no `PdfParser.cs`

As seguintes alterações foram implementadas para aprimorar a extração de dados:

### a. Correção da Extração do Nome do Cliente

*   **Problema:** O `PdfParser` capturava "Nome/Razão Social do Cliente" em vez do nome real.
*   **Solução:** Implementado um fallback no método `Parse`. Se a extração inicial do `cliente` resultar em um cabeçalho ou valor vazio/inválido, o parser agora procura pelo cabeçalho "Cód Cliente Nome/Razão Social" e, em seguida, escaneia as linhas subsequentes em busca de um padrão de "dígitos + espaço + texto (começando com letra)", que corresponde ao `código do cliente` seguido do `nome do cliente`.

### b. Expansão dos Registros `ParsedOp` e `EnderecoSugerido`

*   **`EnderecoSugerido`:** Adicionado o campo `string? Cep`.
*   **`ParsedOp`:** Adicionados os campos `string? CnpjCpf`, `string? InscricaoEstadual`, `string? Telefone`, `string? Email`.

### c. Novas Expressões Regulares para Extração de Dados Detalhados

Foram definidas novas expressões regulares estáticas e read-only para identificar os padrões dos novos campos no texto bruto do PDF:

*   **`CnpjCpfRegex`**: Para extrair CNPJ no formato `99.999.999/9999-99` ou CPF no formato `999.999.999-99`.
*   **`InscricaoEstadualRegex`**: Para extrair uma sequência longa de dígitos (3 a 15), identificada como Inscrição Estadual.
*   **`TelefoneRegex`**: Para extrair números de telefone em vários formatos (e.g., `(99) 99999-9999`, `9999-9999`).
*   **`EmailRegex`**: Para extrair endereços de e-mail padrão.
*   **`CepRegex`**: Para extrair CEP no formato `99999-999`.

### d. Lógica de Extração Detalhada no Método `Parse`

*   Os novos campos (`CnpjCpf`, `InscricaoEstadual`, `Telefone`, `Email`, `Cep`) são agora extraídos diretamente do `allText` usando as novas Regexes.
*   O método `ExtractAddresses` foi completamente refatorado para lidar com linhas de endereço não estruturadas, como: `"09691-350 RUA LIBERO BADARO 1201 - PAULICEIA SAO BERNARDO DO CAMPO/SP"`.
    *   Ele primeiro busca um cabeçalho como "CEP Endereço (...) Cidade/UF".
    *   Em seguida, lê as próximas linhas para combinar o endereço completo.
    *   Utiliza `UnstructuredAddressLineRegex` para separar o `CEP` do restante do endereço.
    *   Utiliza `AddressComponentsRegex` para parsear o restante em `Logradouro`, `Bairro`, `Cidade` e `UF`.
    *   O `EnderecoSugerido` é então populado com `Uf`, `Cidade`, `Bairro`, `Logradouro`, `PadraoEntrega` e o novo campo `Cep`.

### e. Refatoração do Método `Parse` para Testabilidade

*   O método `Parse(string pdfPath)` original foi modificado para extrair o texto completo do PDF (`allText`) e então chamar um novo overload: `public static ParsedOp Parse(string allText, string pdfFileName = "test.pdf")`.
*   Toda a lógica de parsing foi movida para este novo overload, permitindo testar a lógica de extração diretamente com strings de texto, sem a necessidade de arquivos PDF reais.

---

## 3. Modificações no `FileWatcherService.cs`

*   O método assíncrono `HandleOpFileAsync` foi atualizado para incluir todos os novos campos extraídos (`CnpjCpf`, `InscricaoEstadual`, `Telefone`, `Email`) no payload da mensagem RabbitMQ enviada para a fila `op.imported`. O objeto `enderecosSugeridos` (que agora inclui o `Cep`) também é enviado.

---

## 4. Testes (Status Atual)

### a. `PdfParserAddressTests.cs`

*   Os testes antigos que dependiam de reflexão para acessar o método `ExtractAddresses` foram removidos.
*   Adicionado um novo teste unitário (`Parse_UnstructuredPdf_ExtractsAllFields`) que utiliza uma string `allText` simulando o conteúdo do PDF problemático (`/home/nr/Ordem de Produção nº 120435.pdf`).
*   Este teste realiza asserções em todos os campos expandidos do objeto `ParsedOp` resultante, incluindo os componentes detalhados do `EnderecoSugerido`.

### b. Problema de Compilação Bloqueador

*   Atualmente, o projeto `FileWatcherApp` **não está compilando** devido a erros persistentes:
    ```
    error CS1009: Unrecognized escape sequence
    ```
    Nas linhas 499 e 516 do `PdfParser.cs`.

*   **Diagnóstico:** As linhas em questão (`formatos` array e uma linha dentro de `ExtractAddresses`) não contêm sequências de escape (`\`) visíveis. A causa raiz deste erro permanece incerta, mas sugere um problema ambiental, de encoding do arquivo, ou de interpretação do compilador. Tentativas de correção (verificação de todas as Regexes, sobrescrita completa do arquivo) não resolveram o problema.

*   **Status:** A validação completa das novas extrações está bloqueada até que este problema de compilação seja resolvido no ambiente.

---

## 5. Próximos Passos (Recomendação)

1.  **Resolução do Erro de Compilação:** Investigar a fundo o ambiente de compilação C# para determinar a causa do erro `CS1009`. Isso pode envolver:
    *   Verificar a codificação do arquivo `PdfParser.cs`.
    *   Tentar compilar em um ambiente C# `.NET` diferente ou mais recente/estável.
    *   Verificar a integridade do SDK do `.NET`.
2.  Após a resolução do erro de compilação, executar os testes unitários (`dotnet test`) para confirmar que todas as extrações de dados estão funcionando conforme o esperado.
3.  Proceder com a integração e testes de ponta a ponta com o backend `OrganizadorProducao` para validar o fluxo completo de dados via RabbitMQ.
