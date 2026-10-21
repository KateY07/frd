# Third-party notices / 第三方声明

FRD itself is released under the [MIT License](LICENSE). This file lists what else is in, or can be used with, an FRD release, and under which terms. It is a summary, not legal advice; the license texts of each component are authoritative.

FRD 自身采用 [MIT 许可证](LICENSE)。本文件列出发布包中包含或可搭配使用的第三方组件及其许可条款，仅为说明，不构成法律意见；以各组件自身的许可证文本为准。

## What is in which package

| Package | Third-party code it contains | License |
|---|---|---|
| `frd-vX-win-x64.zip` (base) | .NET runtime and libraries (statically compiled into `FRD.exe` / `frd-wintools.exe` by NativeAOT) | MIT, plus the notices in `licenses/dotnet-ThirdPartyNotices.txt` |
| | Vortice.Windows (Direct3D 11, DXGI, Direct2D, DirectComposition, Media Foundation, DirectX, Mathematics) | MIT |
| | SharpGen.Runtime | MIT |
| | FFmpeg.AutoGen (C# bindings only, no FFmpeg code) | MIT |
| | [Glasspane](https://github.com/KateY07/Glasspane) (UI library, same author) | MIT |
| `frd-vX-ffmpeg-win-x64.zip` (optional extension) | FFmpeg 8.1.2 shared libraries `avcodec`, `avutil`, `swscale`, `swresample` (Gyan.dev "full" build) | **GPL-3.0** (`--enable-gpl --enable-version3`), see below |

Windows Media Foundation, Direct3D, Direct2D and DirectWrite are parts of Windows and are used through the operating system; nothing from them is redistributed.

## FFmpeg (optional extension only)

The base package contains **no FFmpeg code**. FRD loads FFmpeg only if the user installs the extension (the installer writes to `%LOCALAPPDATA%\FRD\ffmpeg\<version>\`, or the zip is unpacked next to `FRD.exe`); without it the FFmpeg presets are reported as unavailable and FRD uses the Windows built-in Media Foundation codecs.

The extension package contains a Gyan.dev FFmpeg 8.1.2 "full" shared build (source and checksum pinned in `third_party/ffmpeg/provenance.json`). That build is configured with `--enable-gpl --enable-version3` and includes GPL components such as libx264 and libx265, so the FFmpeg libraries are licensed under the **GNU GPL version 3**. The license text is shipped in `ffmpeg/LICENSE.txt` and the source offer in `ffmpeg/SOURCE-OFFER.txt`.

What this means in practice:

- The extension package is a separate download and a mere aggregation with FRD. FRD's own source stays MIT.
- MIT code is compatible with GPL-3.0. If you distribute FRD **together with** the FFmpeg libraries and treat the result as one combined program, the combination as a whole must be offered under the GPL-3.0 terms, including the corresponding source for the FFmpeg libraries (see `ffmpeg/SOURCE-OFFER.txt`) and for any changes you make.
- If you want to avoid the GPL altogether, do not ship the extension. FRD works without it. You may also point `ffmpeg/` at an LGPL-only build of FFmpeg; the presets that need GPL encoders (for example `libx264rgb`) are then unavailable.
- Video codecs such as H.264 and H.265 can be covered by patents in some jurisdictions. Windows includes licensed H.264 support used by the default Media Foundation path; if you ship FFmpeg's encoders you are responsible for any patent licenses they need.

Sources and licensing information: [FFmpeg legal](https://ffmpeg.org/legal.html), [Gyan.dev builds](https://www.gyan.dev/ffmpeg/builds/).

## MIT-licensed components

Each of the following is licensed under the MIT License, reproduced once below. Copyright notices:

- FRD and Glasspane: Copyright (c) 2026 KateY07
- Vortice.Windows (Vortice.Direct3D11, Vortice.DXGI, Vortice.Direct2D1, Vortice.DirectComposition, Vortice.MediaFoundation, Vortice.DirectX, Vortice.Mathematics): Copyright (c) Amer Koleci and Contributors
- SharpGen.Runtime and SharpGen.Runtime.COM: (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci
- FFmpeg.AutoGen: Copyright (c) 2025 Ruslan Balanukhin (Rationale One)
- .NET runtime and libraries: Copyright (c) .NET Foundation and Contributors (additional third-party notices in `licenses/dotnet-ThirdPartyNotices.txt`)

```
MIT License

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

## Not in the repository or the packages

Test and demo projects under `demo/` and `tests/` still reference Avalonia (MIT) in their sources; they are not built into any release. The repository contains no third-party binaries, fonts or images. The icons are drawn by the author.
