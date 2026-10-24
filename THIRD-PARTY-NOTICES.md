# Third-party notices

## CodeWalker (lib\CodeWalker, a git submodule)

CodeWalker by **dexyfex**: https://github.com/dexyfex/CodeWalker, pinned at commit 485d56b. The launcher and
the dev kit build its CodeWalker.Core project from that source, to read and write RPF7 archives and GTA V
resources. Its code isn't copied into this repository: the submodule points at the upstream repository.

CodeWalker's source says: "This source code is released for educational purposes only." Its repository has no
other licence file; some of its files carry their own notices from their authors. Builds of the launcher and the
dev kit include CodeWalker.Core.dll, built from that source. All credit for it goes to dexyfex and CodeWalker's
contributors.

## NuGet packages

- SharpCompress — MIT — https://github.com/adamhathcock/sharpcompress
- SharpDX, SharpDX.Mathematics (CodeWalker.Core dependencies) — MIT — https://github.com/sharpdx/SharpDX
- Microsoft.Web.WebView2 — Microsoft Software License (BSD-style) — https://www.nuget.org/packages/Microsoft.Web.WebView2

## Fonts

Pricedown (Typodermic Fonts Inc.) is **not** included: its licence doesn't allow shipping the font with an app.
The launcher uses it for headings when it's installed on the PC, and Bahnschrift (part of Windows) otherwise.
