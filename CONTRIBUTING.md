# Contributing

Thanks for wanting to help out with Klip. This is a small project, so nothing
here is heavy. Just a few things worth knowing before you open a PR.

## Before you start

If you are planning something bigger than a small fix, open an issue first so we
can talk it through. It saves you from writing a lot of code that might go in a
direction I did not want to take.

Small fixes (typos, a clear bug, a rough edge) you can just send straight as a PR.

## Building

You need the .NET 9 SDK and Windows 11.

```powershell
git clone https://github.com/PoBruno/klip.git
cd klip

dotnet build Klip.sln
dotnet test Klip.sln
dotnet run --project src/Klip.App
```

The app is windows-only (WPF + a lot of Win32 interop), so it will not build on
linux or mac.

## How the code is split

- `Klip.Core` is pure domain: storage, clipboard engine, capture stitching,
  detectors. No WPF here, and it is the part that has tests.
- `Klip.Interop` is where all the P/Invoke lives. Keep new native calls here,
  do not scatter DllImport around the app.
- `Klip.App` is the WPF layer: windows, viewmodels, services, DI.

## A few conventions

- MVVM in the app: viewmodels do not reference controls.
- Capture geometry is always in physical pixels.
- UI strings go through the localization tables, not hardcoded.
- Add tests for logic that can be tested without UI (Core stuff).
- Run `dotnet test` before you push. Keep it green.

## Pull requests

- Keep them focused. One thing per PR is easier to review.
- Explain what changed and why in the description.
- Every change gets reviewed before it goes in. This matters because the repo is
  code-signed, so the source and the signed binary have to match.

## Reporting bugs and ideas

Use the issue templates. For bugs, the more you can tell me about how to
reproduce it (steps, your Windows version, what you expected), the faster it
gets fixed.

## Security

Found a security issue? Do not open a public issue. See [SECURITY.md](SECURITY.md)
for how to report it privately.
