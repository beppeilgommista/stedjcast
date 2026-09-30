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
| [Vst3HostSharp](https://github.com/cuikp/Vst3HostSharp) (managed library and native `Vst3Pont.dll`) | VST3 plugin hosting | MIT | cuip |
| [Steinberg VST 3 SDK](https://github.com/steinbergmedia/vst3sdk), compiled into `Vst3Pont.dll` | VST3 plugin interfaces | `pluginterfaces`: GPLv3 or Steinberg VST3 License (used here under GPLv3); `base`, `public.sdk`: BSD 3-Clause | Steinberg Media Technologies GmbH |
| Microsoft Visual C++ Runtime (`msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`) | Runtime required by `Vst3Pont.dll` | Microsoft Visual Studio redistributable license terms | Microsoft Corporation |

VST is a registered trademark of Steinberg Media Technologies GmbH.

LAME is distributed as a separate, dynamically loaded library. Its source code is
available at <https://lame.sourceforge.io/>; the LGPL text is available at
<https://www.gnu.org/licenses/old-licenses/lgpl-2.0.html>.

---

## MIT License

Applies to .NET, NAudio, NAudio.Lame and Vst3HostSharp, with the copyright holders
listed above.

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

## BSD 3-Clause License (Steinberg VST 3 SDK: `base`, `public.sdk`)

```
(c) 2023, Steinberg Media Technologies GmbH, All Rights Reserved

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

  * Redistributions of source code must retain the above copyright notice,
    this list of conditions and the following disclaimer.
  * Redistributions in binary form must reproduce the above copyright notice,
    this list of conditions and the following disclaimer in the documentation
    and/or other materials provided with the distribution.
  * Neither the name of the Steinberg Media Technologies nor the names of its
    contributors may be used to endorse or promote products derived from this
    software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE
OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
OF THE POSSIBILITY OF SUCH DAMAGE.
```
