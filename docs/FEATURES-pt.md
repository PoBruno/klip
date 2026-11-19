# Recursos e atalhos do Klip

Tudo o que o Klip faz, área por área, com as teclas de cada coisa. Chegou agora? O [README](../README-pt.md) tem o tour curto.

- [Atalhos globais e bandeja](#atalhos-globais-e-bandeja)
- [Histórico de clipboard (painel do Win+V)](#histórico-de-clipboard)
- [Captura de tela](#captura-de-tela)
- [Gravação de tela](#gravação-de-tela)
- [Editor de imagem](#editor-de-imagem)
- [Editor de vídeo](#editor-de-vídeo)
- [Configurações](#configurações)
- [Dados, desinstalação e portable](#dados-desinstalação-e-portable)

## Atalhos globais e bandeja

| Ação | Padrão | Observação |
|---|---|---|
| Abrir o histórico | `Ctrl+Shift+V` | Troque para `Win+V` para substituir o painel do Windows |
| Nova captura | `Ctrl+Shift+S` | Troque para `PrintScreen` ou `Win+Shift+S` para substituir a Ferramenta de Captura |
| Parar a gravação | `Ctrl+Shift+X` | Só fica registrado enquanto uma gravação roda |
| Capturar texto (OCR) | desligado | Sugestão: `Ctrl+Shift+T` |
| Repetir a última região | desligado | Sugestão: `Ctrl+Shift+R` |
| Gravar vídeo | desligado | Sugestão: `Ctrl+Shift+G`, abre a captura no modo MP4 |

- Troque qualquer um em **Configurações > Atalhos**. Atalho já usado por outro app gera um aviso na bandeja.
- **Assumir teclas do Windows:** escolher `Win+V`, `Win+Shift+S` ou `PrintScreen` faz o Klip tomar essa tecla do Windows (pelo registro, com backup dos valores originais). Assumir o `Win+Shift+S` também desliga o `Win+S`, limitação do Windows. Cada cartão tem **Devolver ao Windows**, e **Restaurar atalhos padrão** desfaz tudo.
- Se uma atualização do Windows devolver uma tecla para a ferramenta do Windows, o Klip avisa uma vez.
- **Bandeja:** clique esquerdo abre o histórico (ou para uma gravação). O menu tem captura, capturar a última região, editor de vídeo, imagens fixadas, pausar o histórico, ignorar a próxima cópia e configurações.

## Histórico de clipboard

Tudo o que você copia, guardado num banco SQLite local com busca em texto completo. Abre ao lado do cursor de texto (ou do mouse).

**O que ele faz**
- **Abas:** Recentes, Favoritos, Imagens, Texto, Arquivos, Snippets, Emoji. Snippets e Emoji podem ser escondidas.
- **Busca** enquanto digita (é só começar a digitar) e **filtro por data**: hoje, ontem, últimos 7 ou 30 dias.
- **Fixar e favoritar:** itens fixados ficam no topo, e nem fixados nem favoritos são apagados pela limpeza.
- **Colar vários de uma vez:** Shift+clique escolhe itens para colar juntos (um por linha), Ctrl+clique monta uma sequência em que cada Ctrl+V cola o próximo. Até 20 itens, sem confirmação: escolheu, colou. Esc devolve o clipboard anterior. Uma sequência parada se cancela sozinha depois de 2 minutos.
- **Modo coleta** (menu do painel): tudo o que você copia vai sendo juntado, depois **Colar tudo**.
- **Pausar** o histórico por 15 min, 1 h ou até retomar, ou **ignorar a próxima cópia**.
- **Snippets:** textos salvos com nome e um apelido opcional. Digite o apelido na busca e aperte Enter. Marcadores `{date}` `{time}` `{datetime}` `{clipboard}`.
- **Seletor de emoji e símbolos**, colorido e com busca.
- **Prévia** com Espaço: texto longo, imagem com zoom e o texto encontrado na imagem (OCR) com botão de copiar.
- **Jeitos de colar:** texto puro, ou **digitar como teclas** para campos que bloqueiam colar. Opção "sempre colar como texto puro", com uma lista de apps que mantêm a formatação.
- **Arraste um cartão** para qualquer app ou para o Explorer: imagens viram arquivos png, texto vira texto.
- **Segredos:** tokens, chaves e connection strings não são guardados por padrão. Opcionalmente, guarde por 30 s a 2 min, mascarados. Cópias de gerenciadores de senha nunca são guardadas.
- **Apps excluídos:** nada copiado deles é salvo.

**Teclas com o painel aberto**

| Tecla | Ação |
|---|---|
| `Enter` | Colar (ou só copiar, ver Configurações). Com uma seleção, cola a seleção |
| `Shift+Enter` | Colar como texto puro |
| `Ctrl+Enter` | Só copiar, o painel continua aberto |
| `Alt+Enter` | Digitar como teclas (texto) |
| `Ctrl+1` ... `Ctrl+9` | Copiar o enésimo cartão (os números aparecem com o Ctrl apertado) |
| `Alt+1` ... `Alt+9` | Colar o enésimo cartão |
| `Espaço` | Prévia (Cima, Baixo, PgUp, PgDn rolam) |
| `Cima` / `Baixo` | Mover a seleção |
| `Shift+Cima` / `Shift+Baixo` | Juntar cartões numa seleção para colar juntos |
| `Ctrl+V` | Com uma seleção: cola e fecha |
| `Del` | Apagar o item |
| `Ctrl+P` | Fixar ou desafixar |
| `Ctrl+D` | Favoritar ou desfavoritar |
| `Ctrl+E` | Abrir a imagem no editor |
| `Ctrl+F` | Ir para a busca |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Próxima ou anterior aba |
| `Esc` | Limpa a seleção, depois a busca, depois fecha |

**Mouse**

| Gesto | Ação |
|---|---|
| Clique | Colar (entra na seleção quando há uma ativa) |
| `Shift+clique` | Escolher para colar juntos |
| `Ctrl+clique` | Escolher para colar em sequência |
| `Alt+clique` numa imagem | Abrir no editor |
| Clique direito ou `...` | Menu do item: colar, texto puro, copiar, prévia, salvar como, abrir no editor, fixar na tela, copiar texto da imagem, salvar como snippet, fixar, favoritar, apagar |
| Arrastar | Soltar o item em outro app ou pasta |

## Captura de tela

Abre como o overlay do Windows: todos os monitores congelam e a barra aparece no monitor onde está o mouse.

**Modos:** retângulo, janela, tela cheia, forma livre, **rolagem** (role no seu ritmo, depois Concluir), **texto** (OCR direto para o clipboard), **cor** (HEX, RGB ou HSL), e gravação em GIF ou MP4.

- **Abrir no editor:** segure `Ctrl` ao soltar a seleção (a tecla pode ser trocada), ou configure para sempre abrir no editor.
- **Atraso:** 3, 5 ou 10 segundos.
- **Lupa** com coordenadas e cor do pixel.
- O resultado vai para o clipboard e o histórico, com uma notificação: clique para editar, ou fixe na tela.
- **Fixar na tela:** a imagem flutua por cima. Arraste para mover, roda do mouse para zoom, `Ctrl+roda` para opacidade, clique duplo ou `Esc` para fechar, clique direito para deixar o clique passar.
- **Repetir a última região:** captura o último retângulo de novo, sem overlay.
- **Salvar automático** (opcional) em `Imagens\Capturas de Tela` ou numa pasta que você escolher.
- **Colar imagem como arquivo:** com uma imagem no clipboard, `Ctrl+V` numa pasta do Explorer ou na área de trabalho salva ali como png. A barra de endereço, a busca e a caixa de renomear continuam colando normalmente. Modelo do nome: `Klip {date} {time}`.

| Tecla | Ação |
|---|---|
| `1` `2` `3` `4` | Retângulo, janela, tela cheia, forma livre |
| `R` | Captura com rolagem |
| `T` | Texto (OCR) |
| `C` | Conta-gotas |
| `G` / `V` | Gravação em GIF / MP4 (aperte de novo para as opções) |
| Setas | Mover o cursor 1 px (`Shift`: 10 px) |
| `Ctrl` ao soltar | Abrir no editor |
| `Esc` | Cancelar |

## Gravação de tela

Escolha GIF (`G`) ou MP4 (`V`) no overlay de captura e arraste uma região. Contagem de 3 segundos, depois uma borda em volta da região e uma barrinha que você arrasta para onde quiser.

- **MP4:** 30 ou 60 fps, três níveis de qualidade, tamanho de saída (nativo, 1440, 1080, 720), som do sistema e quantos microfones quiser, mixados. Encoder de hardware (H.264, HEVC ou AV1 quando disponível).
- **GIF:** 10, 15 ou 20 fps, escala de 100, 75 ou 50%.
- **Barra:** parar, pausar e retomar (MP4), silenciar o microfone, silenciar o som do sistema, mostrar ou esconder o cursor e a borda. Avisa se quadros estiverem sendo perdidos.
- **Modo reunião** (MP4): nada na tela, pare pela bandeja ou pelo atalho.
- Pare pela barra, com `Ctrl+Shift+X` ou clicando no ícone da bandeja.
- Salva em `Vídeos\Gravacoes de Tela` (configurável) e entra no histórico. Um GIF abre no editor de vídeo quando termina.
- À prova de travamento: o MP4 é gravado em fragmentos, então um travamento mantém o que já foi gravado.

## Editor de imagem

Uma tela de quadro branco: mova livremente, zoom no cursor, cole imagens nela. Cada edição é copiada para o clipboard sozinha.

**Ferramentas:** selecionar, caneta, marca-texto (linhas retas, `Ctrl` para mão livre), borracha, retângulo, elipse, linha, seta, texto, emoji, recortar, borrar. Mais girar, **remover fundo**, **extrair texto** (OCR), **ocultar dados sensíveis** (emails, telefones, cartões, IPs e documentos achados por OCR), fixar na tela e salvar em PNG ou JPEG.

| Tecla | Ação |
|---|---|
| `V` `P` `H` `E` | Selecionar, caneta, marca-texto, borracha |
| `R` `O` `L` `A` | Retângulo, elipse, linha, seta |
| `T` `C` `B` | Texto, recortar, borrar |
| `Enter` / `Esc` | Aplicar / cancelar o recorte |
| `Del` | Apagar a seleção |
| `Ctrl+Z` / `Ctrl+Y` | Desfazer / refazer |
| `Ctrl+C` | Copiar agora |
| `Ctrl+V` | Colar uma imagem na tela |
| `Ctrl+S` | Salvar como |
| `Ctrl+0` | Ajustar à janela |
| `Ctrl+roda` | Zoom no ponteiro |
| `Espaço` + arrastar | Mover a tela |
| `Shift` ao desenhar | Manter a proporção, linhas a 45 graus |

## Editor de vídeo

Abre arquivos GIF e MP4: de uma gravação, do histórico, da bandeja, arrastando e soltando ou com `Ctrl+V`.

- **Linha do tempo:** arraste o cursor para navegar, arraste as bordas de um trecho para aparar, arraste o trecho para mover (ele encaixa nos vizinhos). Espaços vazios tocam em preto.
- **Faixas (MP4):** duplique um trecho para uma faixa nova, ajuste volume e opacidade por clipe, solte trechos entre faixas.
- **Clique direito num trecho:** remover deixando espaço, apagar fechando o espaço, trocar com o anterior ou o próximo, duplicar para uma faixa nova.
- **Áudio:** silenciar ou ajustar o volume.
- **Ferramentas de GIF:** reduzir fps (10, 15, 20) e redimensionar (de 100 a 25%).
- **Exportar** em GIF ou MP4. Exportar MP4 e converter MP4 em GIF precisam do `ffmpeg`; o Klip encontra no PATH ou oferece o download.

| Tecla | Ação |
|---|---|
| `Espaço` | Tocar / pausar |
| `S` | Dividir no cursor |
| `Del` | Remover o trecho, deixar o espaço |
| `Shift+Del` | Apagar o trecho e fechar o espaço |
| `Esquerda` / `Direita` | Quadro anterior / próximo |
| `Ctrl+Z` / `Ctrl+Y` | Desfazer / refazer |
| `Ctrl+D` | Duplicar para uma faixa nova (MP4) |
| `Ctrl+V` | Abrir um arquivo do clipboard |

## Configurações

| Página | O que tem |
|---|---|
| Geral | Iniciar com o Windows, tema (sistema, claro, escuro), idioma (15, aplicado na hora) |
| Atalhos | Os seis atalhos, devolver ao Windows, restaurar o padrão, a lista de teclas do painel |
| Clipboard | Posição e tamanho do painel, o que o Enter faz, abas, colar como texto puro, imagens como arquivos, limites de limpeza (itens, imagens, espaço em disco, idade) |
| Captura | Modo inicial, o que acontece depois da captura, tecla do editor, lupa, formato de cor, salvar automático |
| Gravação | Pasta, fps, qualidade, tamanho e codec do MP4, dispositivos de áudio, fps e escala do GIF, modo reunião, caminho do ffmpeg |
| Privacidade | Segredos, apps excluídos, limpar o histórico ao sair |
| Manutenção e backup | Exportar e importar o histórico em `.zip`, uso de disco, compactar o banco, diagnóstico |

## Dados, desinstalação e portable

- **Os dados** ficam em `%LOCALAPPDATA%\Klip`: configurações, o banco do histórico (`data\klip.db`) e as imagens dele. Nada sai da sua máquina.
- **Desinstalar** devolve `Win+V`, `Win+Shift+S` e `PrintScreen` ao Windows (a partir do próximo login). A pasta de dados fica, apague à mão para começar do zero. Se você usou a opção de administrador para o `Win+V`, rode **Restaurar atalhos padrão** antes de desinstalar.
- **Exe portable:** o mesmo app num arquivo só, com a mesma pasta de dados. Não tem desinstalador, então rode **Configurações > Atalhos > Restaurar atalhos padrão** antes de apagar, se o Klip assumiu alguma tecla do Windows.
- **Linha de comando:** `--minimized` inicia na bandeja, `--settings` abre as Configurações, `--verbose` grava um log detalhado em `%LOCALAPPDATA%\Klip\startup.log`.
