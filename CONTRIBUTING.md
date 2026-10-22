# Contributing to MinePair

Thanks for taking the time to contribute! Bug reports, ideas and pull requests are all welcome.

## Reporting bugs and ideas

- Search the existing [issues](https://github.com/IlliaAnikushyn/MinePair/issues) first.
- Use the issue templates. For a bug, include your Windows version, whether you are the host or the friend, the step that failed and the relevant lines from the logs listed in the [README](README.md#troubleshooting). **Remove public IP addresses, nicknames and codes** before posting.
- Security problems go through [SECURITY.md](SECURITY.md), not public issues.

## Development setup

You only need Windows 10/11 x64 with .NET Framework 4.8 (built in). No Visual Studio or .NET SDK is required, although any C# editor works.

```powershell
git clone https://github.com/IlliaAnikushyn/MinePair.git
cd MinePair
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

`build.ps1` compiles every file in `src/` with `csc.exe` from .NET Framework and embeds `src/Network.ps1` as a resource. `-Test` runs `dist\MinePair.exe --self-test`; the report is `dist\artifacts\tests.txt` and screenshots of every screen are written next to it.

The self-tests must never change the system. Anything that touches services, the firewall, WireGuard or the router has to be tested by hand — describe what you checked in the pull request.

## Code guidelines

- The compiler is the C# 5 compiler shipped with .NET Framework: no `?.`, string interpolation, `nameof`, exception filters or other C# 6+ features.
- Follow the existing style: 4-space indentation, braces on the same line, short methods, comments that explain *why* rather than *what*.
- User-facing text is plain, friendly English and goes through `UserFacingException` when it is an expected error.
- Keep privileges minimal. The GUI and the server must never require administrator rights; new helper actions must be added to the fixed list in `Helper.cs` and validated again inside `Network.ps1`.
- Every download must be verified (hash or signature) before use.
- Add or update a self-test in `src/SelfTests.cs` for every behaviour change that can be tested without touching the system.

## Commits and pull requests

- Use [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `perf:`, `docs:`, `test:`, `ci:`, `chore:`, optionally with a scope such as `fix(helper): …`.
- Explain the problem and the reason for the change in the commit body, not just what changed.
- One logical change per pull request; make sure `build.ps1 -Test` passes and update the README, `docs/SPEC.md` and `CHANGELOG.md` when behaviour changes.

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE) and that you follow the [Code of Conduct](CODE_OF_CONDUCT.md).
