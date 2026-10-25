# Third-Party Notices

This package embeds third-party software. Respect their licenses.

## QuickJS

- **Version:** 2025-04-26 (locked; see `Native/source-manifest.json` for the
  download URL and SHA256 of the upstream tarball)
- **Upstream:** https://bellard.org/quickjs/
- **License:** MIT — full text in `Native/quickjs/LICENSE`

```
Copyright (c) 2017-2021 Fabrice Bellard
Copyright (c) 2017-2021 Charlie Gordon

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

The vendored copy carries documented MSVC portability patches and disables
`Atomics`/`SharedArrayBuffer` on every platform for a uniform JS surface;
both are enumerated in `Native/source-manifest.json`.
