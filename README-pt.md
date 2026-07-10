<div align="center">

<img src="docs/banner.png" alt="Klip" width="720">


# Klip

**Um histórico de clipboard e ferramenta de captura melhores para o Windows 11.**

Tudo que o painel Win+V e a Ferramenta de Captura nativos deveriam ter sido, num app pequeno que fica na bandeja.

[![Build](https://github.com/PoBruno/klip/actions/workflows/ci.yml/badge.svg)](https://github.com/PoBruno/klip/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/PoBruno/klip?display_name=tag&sort=semver)](https://github.com/PoBruno/klip/releases/latest)
[![License: GPLv3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows11&logoColor=white)](#)

[Download](#download) - [Recursos](#recursos) - [Compilar](#compilar-do-código-fonte) - [Contribuindo](#contribuindo)

</div>

---

## Por que o Klip

O Klip começou como um remendo pra duas coisas que me irritam no Windows todo santo dia.

O painel de clipboard do **Win+V** é bonito e gostoso de usar, a interface é fluida e nativa, mas o histórico por trás dele é fraco: curto, sem busca de verdade, esquece as coisas, e você não consegue organizar nada. A experiência é boa, o recurso não.

A captura do **Win+Shift+S** é o outro tipo de frustração: tirar o print funciona bem, mas o editorzinho que abre depois não tem metade das ferramentas que você quer de verdade, então você acaba colando em outro app só pra desenhar uma seta.

Então a ideia é simples: manter a cara e o jeito nativo que a Microsoft já acertou, e consertar tudo que está por trás. Mesmo painel, mesmo overlay, nada dos limites.

## O que é o Klip

O Klip faz duas coisas e tenta fazer as duas muito bem:

- **Histórico de clipboard** com a mesma cara e o mesmo jeito do flyout nativo do Win+V, menos os limites. Histórico ilimitado, busca de verdade, filtro por data, favoritos, abas por tipo de conteúdo.
- **Captura de tela** que replica o overlay do Win+Shift+S, mais o que falta na ferramenta nativa: captura com rolagem e um editor de verdade.

Roda como app WPF nativo, usa Fluent Design e Mica, e pode assumir os atalhos `Win+V` e `Win+Shift+S` se você deixar. Sem Electron, sem navegador, sem telemetria.

## Recursos

### Histórico de clipboard
- Histórico ilimitado guardado num banco SQLite local (com busca full text).
- Mesma aparência do painel nativo do Win+V, então não tem nada novo pra aprender.
- Busca enquanto você digita, filtro por data, favoritos fixados.
- Abas por tipo: texto, imagens, arquivos, links.
- Imagens salvas em disco com thumbnails reais e cache LRU, pra rolagem continuar leve.
- Colar mantém a formatação original (HTML e RTF), ou cole como texto puro quando quiser.
- Detector de segredos embutido que sinaliza coisas tipo tokens e senhas pra não ficarem largadas no histórico.
- Respeita os formatos de clipboard que os gerenciadores de senha usam pra pedir exclusão.

### Captura de tela
- Cópia fiel do overlay do Win+Shift+S: escurecido, toolbar, borda pontilhada, a área selecionada fica acesa.
- Modos: retângulo, janela, tela cheia, forma livre.
- **Captura com rolagem** pra pegar uma página inteira que não cabe na tela (o stitching é inspirado no ShareX, veja os [créditos](#créditos)).
- Ciente de múltiplos monitores, geometria sempre em pixels físicos pra nada sair torto em setups com DPI misto.

### Editor rápido
- Editor pós-captura no estilo Ferramenta de Captura: caneta, marca-texto, formas, seta, recorte.
- Texto livre por cima da imagem, estilo Excalidraw.
- Cópia automática pro clipboard a cada edição, então a última versão está sempre pronta pra colar.

### Integração com o sistema
- Takeover opcional do `Win+V` e do `Win+Shift+S`. O Klip cuida das chaves de registro pra você e reverte tudo direitinho na desinstalação.
- Instância única, inicia com o Windows (opcional), fica quietinho na bandeja.
- Importa e exporta seu histórico como `.zip`.

## Download

> Aviso: a primeira versão pública está sendo preparada. Os links abaixo passam a funcionar quando ela sair.

### winget (recomendado)
```powershell
winget install pobruno.Klip
```

### Instalador
Pegue o `Klip-Setup-<versao>.exe` na [última release](https://github.com/PoBruno/klip/releases/latest). Instala por usuário (não precisa de admin), cria atalhos no menu Iniciar e opcionalmente na área de trabalho, e o desinstalador devolve seus atalhos de teclado do jeito que estavam.

### Portátil
Prefere não instalar? Pegue o `Klip-<versao>-portable.exe`. É um único arquivo self contained, então dá pra jogar em qualquer lugar e rodar. Não precisa ter o .NET instalado.

## Compilar do código fonte

Você precisa do **SDK do .NET 9** e do Windows 11.

```powershell
git clone https://github.com/PoBruno/klip.git
cd klip

dotnet build Klip.sln            # compila
dotnet test Klip.sln             # roda os testes (xunit)
dotnet run --project src/Klip.App   # roda o app (aparece na bandeja)
```

### Empacotamento

```powershell
.\tools\build-exe.ps1          # exe único self contained -> publish\Klip.exe
.\tools\build-installer.ps1    # instalador Inno Setup -> dist\Klip-Setup-<versao>.exe
```

O script do instalador precisa do [Inno Setup 6](https://jrsoftware.org/isdl.php) (`winget install JRSoftware.InnoSetup`).

> O Klip não é distribuído como MSIX de propósito: o sandbox bloqueia o takeover de atalhos (Win+V) e o hook global de teclado de que o Klip depende.

## Releases

O CI roda o build e os testes em todo push e pull request. Ao enviar uma tag `vX.Y.Z`, tudo é compilado, os testes rodam, e uma Release do GitHub é publicada com o instalador e o exe portátil anexados.

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## Tecnologia

- WPF no .NET 9 (`net9.0-windows`), C# 13.
- Separação limpa: `Klip.Core` (domínio puro, sem WPF), `Klip.Interop` (todo o P/Invoke Win32), `Klip.App` (WPF, MVVM).
- [WPF-UI](https://github.com/lepoco/wpfui) pro tema Fluent e Mica, [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) pro MVVM, [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) pra bandeja.
- SQLite com FTS5 pro histórico e a busca.

## Contribuindo

Issues e pull requests são bem vindos. Se for fazer algo maior, abre uma issue antes pra gente conversar antes de você escrever muito código.

## Créditos

- O stitching da captura com rolagem é inspirado no algoritmo usado no [ShareX](https://github.com/ShareX/ShareX). O Klip só usa a ideia, não reaproveita o código deles.

## Licença

O Klip é distribuído sob a [GNU GPLv3](LICENSE).
