# Third-Party Notices & License Compatibility

NaSeqMen's own source code is licensed under the **MIT License** (see [LICENSE](LICENSE)).

Below are the licenses and notices for third-party libraries, datasets, and optional external runtime integrations used by NaSeqMen.

---

## 1. Microsoft .NET & Roslyn Scripting (`Microsoft.CodeAnalysis.CSharp.Scripting`)

- **Source:** https://github.com/dotnet/roslyn
- **License:** MIT License
- **Compatibility with NaSeqMen (MIT):** Fully compatible (permissive).

---

## 2. USB IDs Repository (`usb.ids`)

The friendly USB vendor name lookup table in `src/NaSeqMen.Nodes.Interception/UsbVendorTable.cs` is derived from the **USB IDs Repository** (`https://usb-ids.gowdy.us` / `http://www.linux-usb.org/usb.ids`), which is dual-licensed under **GPL-2.0-or-later OR BSD-3-Clause**.

NaSeqMen uses this dataset under the **3-Clause BSD License** (fully compatible with MIT):

```text
Copyright (c) Stephen J. Gowdy and contributors (USB IDs Repository).
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors
   may be used to endorse or promote products derived from this software
   without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

---

## 3. Optional External Runtime Integration: `oblitum/Interception`

- **Source:** https://github.com/oblitum/Interception
- **Bundled with NaSeqMen:** **No.** Neither `interception.dll`, nor `install-interception.exe`, nor the Windows kernel drivers (`.sys`), nor any source code from `oblitum/Interception` are included in this repository or its build outputs.
- **How NaSeqMen interacts with it:** The optional `NaSeqMen.Nodes.Interception` plugin dynamically loads a user-supplied `interception.dll` at runtime via `NativeLibrary.TryLoad` using clean-room C ABI declarations. If `interception.dll` is not installed, NaSeqMen runs normally with all other built-in modules and plugins.
- **Upstream License Notice:** According to `oblitum/Interception`, the precompiled library and drivers are free for **non-commercial use under LGPL-3.0**, while **commercial distribution or commercial use requires a commercial license** from the author (Francisco Lopes / oblitum), as the kernel driver source code is closed-source. Users who choose to install Interception are responsible for complying with its license terms.

---

## 4. OBS WebSocket v5 Protocol

- `NaSeqMen.Nodes.Obs` communicates over standard WebSockets (`System.Net.WebSockets.ClientWebSocket`) with a user's running **OBS Studio** instance using the public OBS WebSocket v5 protocol specification. No OBS source code or libraries are bundled.
