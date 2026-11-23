# Third-party notices

## osz2-go

`src/TomYum.Server/Beatmaps/Osz2/` reads osu!'s `.osz2` packages. Its layout, hashes and ciphers
are ported from [osz2-go](https://github.com/Lekuruu/osz2-go), and
`tests/TomYum.Server.Tests/Fixtures/stable-submission.osz2` was written with its package writer.

```
Copyright (c) 2026 Levi <contact@lekuru.xyz>

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## DB-IP IP to Country Lite

Players' countries, when Cloudflare doesn't give one (`src/TomYum.Server/Bancho/IpCountry.cs`), come
from DB-IP's free database, downloaded at run time (not included in this repository).

IP Geolocation by [DB-IP](https://db-ip.com), licensed under
[Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/).

## MaxMind.Db

The database above is read with [MaxMind.Db](https://github.com/maxmind/MaxMind-DB-Reader-dotnet)
(NuGet package), licensed under the Apache License 2.0.

## NuGet packages (reviewed 2026-11-23)

The server image ships 156 NuGet packages, listed with their licences in
[docs/third-party-packages.md](docs/third-party-packages.md) (`python3 tools/package-licenses.py`). Most are MIT or
Apache-2.0; most come through ppy's `osu.Game` and `osu.Framework`, which the pp calculation and replay reading use.
To watch for:

- **ppy.osu.Game.Resources** (osu!'s sounds, textures, fonts, through `osu.Game`): ppy publishes osu!resources under
  CC BY-NC 4.0 ([LICENCE.md](https://github.com/ppy/osu-resources/blob/master/LICENCE.md)), non-commercial. The server
  never shows or serves them, but the DLL is in the image; a server that charges money should check this with ppy, or
  keep the package out of what it ships.
- **SixLabors.ImageSharp** (through `osu.Framework`): Six Labors Split License; as a transitive dependency it's under
  Apache 2.0 terms.
- **LGPL**: OpenTabletDriver (LGPL-3.0-or-later, through `osu.Framework`) and TagLibSharp (LGPL-2.1, through
  `osu.Game`), as separate DLLs, unmodified: the licences are met by shipping them as they are, with this notice.
- **ppy.ManagedBass**: the .NET wrapper is MIT; the BASS audio library itself (un4seen, not free for commercial use)
  is not in the image.
- No licence stated in the package: HidSharpCore, ppy.osuTK.NS20, ppy.SDL2-CS, ppy.Vk (input, windowing and graphics
  bindings `osu.Framework` brings along; the server never loads them).
