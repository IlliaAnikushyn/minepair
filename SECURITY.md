# Security policy

MinePair installs a service that runs as SYSTEM and changes firewall and network settings, so security reports are taken seriously.

## Supported versions

Only the latest release and the `main` branch receive fixes.

## Reporting a vulnerability

Please **do not open a public issue** for a vulnerability. Use GitHub's private reporting instead: *Security → Report a vulnerability* on this repository.

Include, if you can:

- the affected version or commit,
- what an attacker needs (for example: a standard user account on the same PC, control of the chat used for pairing, a position on the network),
- steps to reproduce and the impact.

You can expect an acknowledgement within a week. Once a fix is released, the report is credited in the changelog unless you prefer otherwise.

## Scope

In scope:

- privilege escalation through the `MinePairHelper` service, its pipe, its files in `C:\Program Files\MinePair` or `C:\ProgramData\MinePair`;
- access to the Minecraft server or to either computer from anywhere other than the paired device;
- weaknesses in pairing codes or the safety code, key handling, download verification or archive extraction;
- firewall or tunnel rules that are broader than documented, or that remain after *Stop* / uninstall.

Out of scope (documented limitations, see the [security model](README.md#security-model)):

- nickname spoofing by the paired device (the server runs in offline mode);
- attacks that already require administrator rights or code running as the same user;
- denial of service against a home internet connection.
