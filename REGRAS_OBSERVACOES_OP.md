# Regras para preencher Observações das OPs

## Como o sistema lê o bloco de observações
- O parser procura um título `Observação` ou `Observações` seguido do texto; tudo o que estiver nesse bloco pode gerar informações estruturadas.
- Escreva cada instrução em uma linha separada para facilitar o reconhecimento.
- Evite anexar novas etiquetas (`Campo:`) depois das observações; isso faz o bloco terminar antes do esperado.

## Campos reconhecidos automaticamente
- **Modalidade de entrega**: palavras como `RETIRADA`, `RETIRA`, `VEM BUSCAR` geram `RETIRADA`; qualquer ocorrência de `ENTREGA` ou `ENTREGAR` gera `A ENTREGAR`.
- **Destacador**: use `DESTACADOR: M`, `DESTACADOR: F` ou `DESTACADOR: M/F`. Também é aceito escrever `M DESTACADOR` ou `F DESTACADOR`.
- **Data e hora de entrega**:
  - Formato preferido: `ENTREGA REQUERIDA: 25/04/2024 14:00`.
  - Alternativas aceitas: `DATA ENTREGA: 25/04/2024` e `HORA ENTREGA: 14:00` (ou `10 horas`).
  - Se não indicar o ano, o sistema recicla o ano da OP. Se houver mais de uma data, a primeira válida será usada.
- **Materiais especiais**: qualquer menção a `PERTINAX`, `POLIÉSTER`/`POLIESTER` ou `PAPEL CALIBRADO` (até `CALIBRADO` sozinho) aciona as respectivas flags.

## Boas práticas
- Mantenha o texto em português simples, sem abreviações pouco usuais.
- Use letras maiúsculas apenas quando a OP já vier assim; não é obrigatório.
- Prefira separar data e hora com dois pontos (`14:30`). Evite formatos raros, como `14h30min`.
- Conferir se informações essenciais (destacador, entrega, materiais especiais) estão presentes; se não houver, deixe a linha vazia para não gerar falso positivo.

## Exemplos
```
Observações:
RETIRADA
ENTREGA REQUERIDA: 12/06 10:00
DESTACADOR: M/F
PAPEL CALIBRADO
PERTINAX
POLIESTER
```

```
Observação:
A ENTREGAR
DATA ENTREGA: 28/07/2024
HORA ENTREGA: 13:30
M DESTACADOR
PERTINAX
```
