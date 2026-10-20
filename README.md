# MinePair

A private vanilla Minecraft Java 26.3 server for two Windows x64 computers. Requirements and context: [SPEC.md](SPEC.md).

The program is under development. Having an interface does not mean the server or the network is set up yet. Verification results are recorded in SPEC.md.

## Build

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`. It uses the .NET Framework compiler that ships with Windows; no separate SDK is needed.

## Data and Git

All user data lives in `.local` next to the program and is excluded from Git. Never publish that folder: it contains keys, settings, the world and backups. The `dist` folder holds the build, not user data.

Commits keep the code. Reverting a commit does not undo firewall rules, installed components or router changes: those have their own rollback.
