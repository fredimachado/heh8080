# heh8080 - CP-500

Emulador do Prológica CP-500, o computador brasileiro compatível com o TRS-80 Modelo III. Também emula as CPUs Intel 8080 e Zilog Z80 e roda o LOLOS, um sistema operacional compatível com CP/M 2.2.

Funciona no Windows, Mac, Linux e no navegador.

![Tela de boot do CP-500 com o logo da Prológica](img/cp500.jpeg)

![CP-500 rodando o cp500-os com uma listagem no shell](img/cp500-os.jpeg)

![Tela de boot do DOS-500 pedindo a data](img/dos500.jpeg)

## O que o emulador faz

- Emula o CP-500 original: Z80 a 2,03 MHz, 48 KB de RAM, tela de 64 x 16 com acentos, semigráficos e katakana, e até quatro drives de disquete
- Abre disquetes `.dsk`, `.dmk` e `.jv3`, e salva o que for gravado neles
- Também roda CP/M (LOLOS) com CPU 8080 ou Z80
- Tela de monitor CRT antigo, com curvatura, brilho e linhas de varredura

## Como baixar

Baixe o arquivo do seu sistema na página de [Releases](https://github.com/fredimachado/heh8080/releases), descompacte e rode `Heh8080.Desktop`.

## ROMs do CP-500

> [!IMPORTANT]
> As ROMs do CP-500 não vêm com o emulador. Você precisa usar cópias (dumps) das suas próprias ROMs.

Renomeie seus arquivos para exatamente estes nomes, em minúsculas:

| Arquivo | Tamanho | Conteúdo |
|---|---|---|
| `cp500.rom` | 16.384 bytes | ROM do sistema (BASIC e boot) |
| `cp500-chargen.rom` | 2.048 bytes | Gerador de caracteres |

Coloque os dois numa pasta `cp500` ao lado do executável:

```
Heh8080.Desktop.exe
cp500/
  cp500.rom
  cp500-chargen.rom
```

Se o emulador encontrar as ROMs, ele já abre no CP-500. Se não encontrar, abre no CP/M.

> [!TIP]
> No Windows, as ROMs também podem ficar direto em `%LOCALAPPDATA%\heh8080\`, sem a subpasta `cp500`.

## Como usar o CP-500

- **Sem disquete**: a máquina pergunta `BASIC (S ou N)?`. Digite `S` e aperte Enter em `Cass?` e em `Mem. usada ?` para chegar ao `READY`.
- **Com disquete**: com um disco de boot no drive 0 (TRS-DOS, DOS-500 etc.), a máquina dá boot por ele.
- **Trocar de máquina ou de disquete**: clique no logo no canto superior esquerdo da moldura.
- **Teclas**: Esc é BREAK, Home é CLEAR e Backspace é a seta para a esquerda (apaga).

Opções de linha de comando:

- `--disk <arquivo>` coloca um disquete no drive 0 antes do boot.
- `--cpm` abre no CP/M mesmo que as ROMs estejam presentes.

## Como compilar

Precisa do [.NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/Heh8080.Desktop
```

Para gerar o executável nativo:

```bash
dotnet publish src/Heh8080.Desktop -c Release
```

## Projetos relacionados

- [cp500-os](https://github.com/fredimachado/cp500-os): sistema operacional para o CP-500
- [LOLOS](https://github.com/fjzeit/lolos): sistema operacional compatível com CP/M 2.2, escrito em assembly 8080

## Licença

MIT
