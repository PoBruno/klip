# Avisos de terceiros (third-party notices)

Este arquivo lista softwares de terceiros cujos MECANISMOS foram adaptados no
Klip, com as respectivas licencas e atribuicoes. Dependencias NuGet declaram
suas proprias licencas nos pacotes.

## ScreenRecorderLib

- Projeto: <https://github.com/sskodje/ScreenRecorderLib>
- Licenca: MIT
- Uso no Klip: mecanismos ADAPTADOS (nao ha copia de codigo) no motor de
  gravacao MP4 (`src/Klip.Interop/Recording/`):
  - Silence padding no muxer (`OutputManager::RenderFrame`): frames de video
    sem audio correspondente recebem silencio PCM do tamanho exato do
    intervalo (RF-M2.03, `Mp4Recorder.WritePendingSilencePadding`).
  - Video escravizado ao audio entregue (`PrepareAndRenderFrame`): timestamps
    de video reconciliados com o PCM efetivamente escrito na trilha
    (RF-M2.04, `Mp4Recorder.ReconcileVideoTimestamp`).
  - Tuning do encoder via `MF_SINK_WRITER_ENCODER_CONFIG`/IPropertyStore e
    faststart com `MF_MPEG4SINK_MOOV_BEFORE_MDAT` (RF-M2.06/08,
    `Mp4SinkWriter`).
  - Re-aplicacao defensiva de `GraphicsCaptureSession.IsCursorCaptureEnabled`
    a cada frame (RF-M2.10, `FrameCaptureEngine`).

```text
MIT License

Copyright (c) 2017 Sverre Skodje

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
