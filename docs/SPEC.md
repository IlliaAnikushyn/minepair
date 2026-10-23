# MinePair — requirements and design

This document records what MinePair must do, the constraints it works under, the design decisions taken and what has been verified. The user-facing guide is the [README](../README.md).

## 1. Goal

A small Windows application that lets one person host a vanilla Minecraft: Java Edition server for exactly one friend living elsewhere. The server runs on the host's computer while the host has it started; quitting the game client does not stop it. Hosted services were ruled out because of the latency observed with them, and general-purpose VPNs because they connect far more than two game endpoints.

## 2. Target environment

- Host: Windows 10/11 x64 laptop or desktop (reference machine: 4 cores / 8 threads, 16 GB RAM, NVMe SSD) on home broadband with a public IPv4 address and a consumer router.
- Friend: Windows 10/11 x64; their router is never changed.
- Players use offline-mode nicknames without verified Mojang accounts, so the server must run with `online-mode=false`. A whitelist is therefore not authentication, and no player gets OP.
- The required Java version is read from Mojang's version metadata, never assumed.

## 3. Constraints

- No secrets in Git. Codes exchanged between players carry public data only.
- Do not promise the absence of vulnerabilities, latency or packet loss; keep FPS, network latency and server performance (TPS) apart; never present a TCP ping as proof of a loss-free connection.
- Every change to the operating system or router must be reversible, and only MinePair's own changes may be rolled back.

## 4. Architecture

- Native WinForms GUI compiled with the C# compiler that ships with .NET Framework 4.8: no SDK, Node or Electron. One executable.
- Official vanilla `server.jar` (SHA-1 from Mojang's manifest) and portable Temurin Java (SHA-256 from the Adoptium API) in the user's data folder; PATH/JAVA_HOME are not changed.
- Official WireGuard for Windows; the installer's Authenticode signature must chain to the WireGuard LLC EV certificate (subject pinned including `SERIALNUMBER=4227913`). No home-made cryptography: X25519 key derivation follows RFC 7748 and is checked against its test vectors.
- A dedicated tunnel per profile with routes to the two `/32` tunnel addresses only. DNS, the default route, Internet Connection Sharing and IP forwarding are not changed; IPv6 is unbound from the tunnel adapter.
- Minecraft listens on the host's tunnel address only; the game port is never forwarded on the router.
- Host firewall (rule group `MinePair <id>`, scoped to the tunnel interface): allow TCP 25565 only from the friend's tunnel IP and only for MinePair's `java.exe`; block every other inbound TCP/UDP/ICMP to the tunnel address; allow WireGuard's UDP port. Friend firewall: block all unsolicited inbound traffic to the tunnel address. Other applications' rules are never removed.
- Router: one WireGuard UDP port. UPnP is used only if it is already enabled and the port is free; MinePair never enables UPnP or DMZ and never changes WAN, Wi-Fi or DHCP settings. Otherwise the user gets exact manual instructions.
- Privilege separation:
  - The GUI and the server supervisor run as the standard user and refuse to run elevated.
  - `MinePairHelper` is a demand-start service running as SYSTEM, installed once with UAC. It executes a fixed list of actions (`version`, `status`, `install-wireguard`, `start`, `stop`, `router-add`, `router-remove`) received over a named pipe that only the installing user can open. Only that user may start the service. It stops itself after ten idle minutes.
  - The helper runs `Network.ps1` written from the executable's embedded resource into Program Files, never a script from a user-writable folder.
  - Helper state (journal, tunnel configuration, logs, downloaded MSI) lives in `C:\ProgramData\MinePair`, owned by Administrators and accessible only to SYSTEM and Administrators. A folder planted there before installation, or a junction, is removed instead of reused.
  - The GUI talks to the pipe only if its server process runs in session 0, i.e. is a service.
- Keys: the device private key is encrypted with per-user DPAPI. The tunnel configuration is encrypted with machine-wide DPAPI because the SYSTEM WireGuard service must read it; the helper validates it byte-for-byte against the expected template before use, and the user-side copy exists only while the helper is starting the tunnel.

## 5. Pairing

- Codes are `MP-` + base64url(kind | body | first 4 bytes of SHA-256(kind | body)). The checksum catches copy/paste damage; it is not a security feature.
- Invitation (host → friend): profile id, tunnel address, host public key, public IPv4 and port, both nicknames. Reply (friend → host): profile id and friend public key. New address (host → friend): profile id, public IPv4 and port.
- The safety code is 48 bits of SHA-256("MinePair-v2" | host key | friend key), shown as `XXXX-XXXX-XXXX` on both screens and compared by voice; the host confirms it before the friend's key is stored. The host key is pinned on the friend's side, so a new-address code cannot redirect the friend to another server.

## 6. Behaviour

1. First-run wizard: role → nicknames and EULA (host) → setup with progress → code exchange → router instructions (host). Afterwards a single *Play/Stop* (host) or *Connect/Disconnect* (friend) screen; everything else is in *Settings*.
2. Status shows the server process, the age of the last WireGuard handshake and a real Minecraft status/ping exchange, with the limits of that measurement.
3. Several worlds, each with its own version, game mode, difficulty, PvP, hardcore and seed.
4. Automatic backup before every start (last 10 kept); manual backups only while the server is stopped; restore only while stopped, after validating the archive and taking a safety backup.
5. Closing the GUI while running offers a clean stop. If the server exits — including after a GUI crash — the supervisor asks the helper to remove the tunnel and rules. A Java process left without a supervisor blocks starts and backups and is offered for termination.
6. After an unclean shutdown the next start warns, takes a backup and continues.
7. The host detects public-IP and internal-IP changes and tells the user what to do.

## 7. Rollback

- *Stop*: send `stop` to the server's stdin, wait for Java to exit, remove only our tunnel service, our firewall rule group and our own UPnP mapping. No forced termination without separate consent.
- *Play* with a UPnP router rule: once the host chose UPnP, every start adds the mapping again and every stop removes it, so the port is open only while the server runs. If the router refuses at start, the server still starts and the host sees a warning with the router instructions. Confirming a manual rule turns UPnP off.
- *Remove MinePair from Windows*: the same, plus removal of the helper service, its files and its state; WireGuard is removed only if MinePair installed it (product code recorded and confirmed in the registry) and no other tunnels exist. Worlds, backups and keys are kept. The helper journals its own changes before and after each operation, and every operation is safe to repeat. A failure in one step (for example an unreachable router) does not skip the others; while the journal still lists something MinePair owns, it is kept in `C:\ProgramData\MinePair` so a later install and removal can finish.
- *Damaged profile*: if `settings.json` cannot be read, MinePair offers to rename it to `settings.json.damaged-<time>` and run the wizard again. The private key, worlds and backups are separate files and are kept; only the code exchange is repeated.
- Git rolls back code, not OS or router settings. A manually created router rule must be deleted by hand.

## 8. Verification

Automated (`build.ps1 -Test`, no system changes): RFC 7748 vectors, pairing codes and their validation, tunnel configuration content (no default route, DNS or scripts), DPAPI, folder ACLs, pipe-server check, worlds and `server.properties`, backup rotation and restore, archive path traversal and alternate streams, migration of the first version's data, locked-file retries, offscreen rendering of every screen, parse check of the embedded script.

Manual on the developer's machine (2026-10-20/21, with a temporary public test key whose private half was never stored):

- Signature checks; WireGuard install, removal with data preserved and reinstall.
- The helper started by a standard user through the pipe; tunnel and five firewall rules created; Minecraft listening only on the tunnel address and answering a status/ping; after stopping no tunnel, no rules, nothing listening on 25565 and no crash marker.
- Clean stop with all dimensions saved, restart, backup creation.

Not verified yet: a WireGuard handshake and a game session with a real second computer over the internet, real latency/jitter/packet loss, and the friend-side firewall under real traffic.

## 9. History

- **Version 1** (2026-10-20): one window with buttons, JSON invitation/reply files, a short-lived elevated PowerShell worker per network operation, data in `.local` next to the exe, fixed Minecraft 26.3.
- **Version 2** (2026-10-21), after watching a first-time user fail to set it up: setup wizard, short chat codes with a safety code, a one-time UAC helper service instead of repeated UAC prompts (which also removed an escalation path through a user-writable script), data in `%LOCALAPPDATA%`, multiple worlds with any official release, automatic migration from version 1.
- **Post-review hardening** (2026-10-22): ProgramData folder ownership, pipe-server check, 48-bit safety code, short-lived user-side tunnel configuration, tunnel removal by the supervisor, ACL write avoidance, robust folder moves, test clean-up, nickname updates after pairing. See [CHANGELOG.md](../CHANGELOG.md).
