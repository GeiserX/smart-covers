# Third-Party Notices

SmartCovers is licensed under the [GNU General Public License v3.0](LICENSE). The plugin
release zip redistributes the following third-party components, whose licenses and
copyright notices are reproduced or referenced here as required by their terms.

| Component | Version source | License | Copyright |
|-----------|----------------|---------|-----------|
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) (`SharpCompress.dll`) | NuGet | MIT | © 2014 Adam Hathcock |
| [PDFtoImage](https://github.com/sungaila/PDFtoImage) (`PDFtoImage.lib`) | NuGet | MIT | © David Sungaila |
| [PDFium](https://pdfium.googlesource.com/pdfium/) (native `pdfium` binaries under `runtimes/`, via [bblanchon.PDFium](https://github.com/bblanchon/pdfium-binaries)) | NuGet | Apache-2.0 / BSD-3-Clause | © PDFium Authors |
| [SkiaSharp](https://github.com/mono/SkiaSharp) (not bundled — provided by the Jellyfin server) | NuGet | MIT | © Xamarin Inc. / Microsoft Corporation |

## MIT License (SharpCompress, PDFtoImage, SkiaSharp)

> Permission is hereby granted, free of charge, to any person obtaining a copy of this
> software and associated documentation files (the "Software"), to deal in the Software
> without restriction, including without limitation the rights to use, copy, modify,
> merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
> permit persons to whom the Software is furnished to do so, subject to the following
> conditions:
>
> The above copyright notice and this permission notice shall be included in all copies
> or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
> INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
> PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
> HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
> CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE
> OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

PDFium's license (Apache-2.0 with BSD-3-Clause components) is available at
<https://pdfium.googlesource.com/pdfium/+/refs/heads/main/LICENSE>.

RAR format note: SharpCompress implements RAR **decompression only** (reading CBR
archives); it cannot create RAR archives. Decompression implementations are expressly
permitted by the RAR license terms; no unrar license restrictions apply to this plugin.
