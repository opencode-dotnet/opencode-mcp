# Third-Party Notices

This repository's own code is licensed under the MIT License in [`LICENSE`](LICENSE). It also
carries content derived from other projects, reproduced here with each project's notice as its
license requires.

## OpenCode .NET SDK

Source project: [opencode-dotnet/opencode-sdk-dotnet](https://github.com/opencode-dotnet/opencode-sdk-dotnet)

Derived content in this repository: the engineering conventions, adapted to this product. That
covers the agent entry point (`AGENTS.md`), the engineering canon under `docs/engineering/`, the
ADR policy in `docs/adr/README.md`, and the build and analyzer configuration (`.editorconfig`,
`BannedSymbols.txt`, `Directory.Build.props`, `Directory.Packages.props`). No source code of that
project is vendored here. Its notice follows.

```
MIT License

Copyright (c) 2026 Deniz İrgin

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

## Aspire ServiceDefaults template

Source project: [microsoft/aspire](https://github.com/microsoft/aspire), tag `v13.6.4`, path
`src/Aspire.ProjectTemplates/templates/aspire-servicedefaults/`.

Derived content in this repository: `src/OpenCode.Mcp.ServiceDefaults/`, adapted from the standard
ServiceDefaults template. The source repository's `LICENSE.TXT` notice follows.

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

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
