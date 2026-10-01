# Third-party notices

Stedjcast is licensed under the GNU General Public License v3.0 (see `LICENSE`).
It uses and redistributes the following third-party components, each under its own
license.

| Component | Used for | License | Copyright |
|---|---|---|---|
| [.NET runtime and WPF](https://github.com/dotnet/runtime) | Runtime (bundled in the self-contained build) | MIT | .NET Foundation and Contributors |
| [NAudio](https://github.com/naudio/NAudio) (NAudio, NAudio.Core, NAudio.Wasapi) | WASAPI capture, resampling, system mute, WAV writing | MIT | Mark Heath & Contributors |
| [NAudio.Lame](https://github.com/Corey-M/NAudio.Lame) | MP3 encoder wrapper | MIT | Corey Murtagh |
| [LAME](https://lame.sourceforge.io/) (`libmp3lame.64.dll`, shipped by NAudio.Lame) | MP3 encoding | LGPL | The LAME project |
| [Steinberg VST 3 interfaces](https://github.com/steinbergmedia/vst3_pluginterfaces) (`pluginterfaces`), translated to C# in `Services/Vst3Interop.cs` and `Services/Vst3HostObjects.cs` | VST3 plugin hosting | MIT | Steinberg Media Technologies GmbH |
| Microsoft Visual C++ Runtime (`msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`) | Runtime for plugins built against the dynamic VC++ runtime | Microsoft Visual Studio redistributable license terms | Microsoft Corporation |

VST is a registered trademark of Steinberg Media Technologies GmbH.

LAME is distributed as a separate, dynamically loaded library. Its source code is
available at <https://lame.sourceforge.io/>; the LGPL text is available at
<https://www.gnu.org/licenses/old-licenses/lgpl-2.0.html>.

---

## MIT License

Applies to .NET, NAudio, NAudio.Lame and the Steinberg VST 3 interfaces, with the
copyright holders listed above (VST 3 interfaces: Copyright (c) 2026, Steinberg Media
Technologies GmbH).

```
Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

