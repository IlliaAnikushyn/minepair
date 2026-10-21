# MinePair — context and specification

## Goal

A small Windows application with a minimal interface: vanilla Minecraft Java 26.3 for the owner of a laptop and one friend who lives elsewhere. New world. The server runs while the laptop is on and the server is started; the owner quitting the game client does not stop the server. Hosted services such as Aternos are ruled out because of the latency observed with them.

## Target environment and verified facts

- Host: Windows x64 laptop, 4 cores / 8 threads, 16 GB RAM, NVMe SSD with plenty of free space.
- Friend: Windows 11. The application checks the architecture; the first version targets x64.
- Router: consumer Wi-Fi router on home broadband. The router's WAN IPv4 matched the address seen from the internet, so the host has a public IPv4. Whether the address is stable and whether inbound traffic reaches the host is not verified yet.
- The host is on Wi-Fi. A speed test to a nearby server says nothing about the route to the friend. Ethernet is preferred for stability.
- Players join by nickname without verified Mojang accounts. The server therefore needs offline mode; a whitelist is not authentication. Players are not given OP by default.
- Version 26.3 is confirmed by the official Mojang version manifest. The required Java version is read from the version metadata, never assumed.

## Constraints

Development may download the required components, request UAC and make reversible router changes. The friend agrees to install a network component. Secrets must never be stored in Git.

Do not promise the absence of vulnerabilities, latency or packet loss. Keep FPS, network latency and server performance apart. Do not claim better quality than Aternos before a joint test.

## Architecture

- Native WinForms interface compiled with the .NET Framework C# compiler that ships with Windows; no SDK, Node or Electron.
- Official vanilla server.jar, SHA-1 verified against the official metadata. Portable Java in the data folder; system PATH/JAVA_HOME are not touched.
- Official WireGuard for Windows; the installer's Authenticode signature is checked before UAC. No home-made cryptography.
- A dedicated MinePair tunnel; keys are generated locally on each computer. Invitation/reply files carry only public keys and connection details. Private keys are encrypted with Windows DPAPI; access to the data is restricted with ACLs.
- Only routes to the two /32 tunnel addresses. DNS, the default route, Internet Connection Sharing and IP forwarding are not changed.
- Minecraft listens on the tunnel IP only; the game TCP port is never forwarded on the router.
- On the host the firewall allows Minecraft TCP only from the friend's tunnel IP, for the specific java.exe and interface. If broad Java rules already exist, verify the protection still holds; never delete other rules silently.
- The router needs a single WireGuard UDP port. Automatic UPnP is acceptable only if it is already enabled and the port is free. Never enable UPnP, never use DMZ, never change WAN/Wi-Fi/DHCP. Without UPnP, give exact manual instructions and record the change.
- Network setup and rollback are performed by the MinePair helper service (see version 2). The server and the regular GUI never run as administrator.

## Interface and behaviour

1. Mode: "My server" or "Join a friend".
2. Component preparation with progress; explicit Minecraft EULA consent before the first start.
3. Owner and friend nicknames, creating a public invitation, importing the public reply.
4. Start / clean stop, connection address, process status, age of the WireGuard handshake and TCP reachability; TCP reachability is never presented as proof that there is no packet loss.
5. Latency check over a real game TCP connection. Show the limits of the measurement; never promise TPS without measuring it.
6. Automatic backup before a start that follows a clean stop; manual backups only while the server is stopped. Restore only while stopped, validate the archive and take a safety backup before replacing the world.
7. Closing the GUI while running must offer a clean stop. Never leave the tunnel or the server running unnoticed.
8. If the server exits unexpectedly, remove the tunnel and the rules. On the next start, detect a leftover service and offer or perform its cleanup.

## Rollback

"Stop": save the world through `stop` on stdin, wait for Java to exit, remove only our own tunnel service, only our own firewall rules and our own UPnP mapping. No forced termination without separate consent.

"Remove network setup": the same steps; remove WireGuard only if this application installed it and no other tunnels exist. Saves, backups and keys are not deleted without a separate explicit action. Full removal of the application is described in the README. Our own changes are journaled before and after each operation, and rollback is safe to repeat. Git rolls back code, not OS or router settings.

## Verification and delivery

- Commits: specification; implementation; fixes after review.
- Isolated checks: parsing pairing files, validation of addresses/keys/nicknames, no default route or DNS in the configuration, DPAPI, safe extraction, backup/restore, own-only cleanup policy, GUI compilation, offscreen preview.
- On the host: signature checks, server binding, start/stop and restart, no own service/rules left after stopping.
- A full handshake, playing together and real latency need the friend's computer and working UDP forwarding. These checks are not reported as done without the friend.
- Release: EXE + runtime scripts + README, plus a clean ZIP for the friend without `.local`, keys, worlds or logs.

## Status — 2026-10-20

- Git initialised on `main`. Commits: the initial specification and the main implementation; follow-up fixes go into a separate commit. No remote is configured.
- Built the native EXE and the PowerShell network worker. Working location: `dist\MinePair.exe`; data: `dist\.local`. Deleting `dist` also deletes user data — the build does not clean it.
- Downloaded and verified Minecraft 26.3 (Mojang SHA-1), a portable Temurin Java 25 (Adoptium SHA-256) and the official signed WireGuard. WireGuard was installed with its package ownership recorded. PATH/JAVA_HOME were not changed.
- Local self-tests pass, including a regression test for importing an invitation after the client setup and preserving component ownership for rollback; the real interface is rendered offscreen.
- A real vanilla server was started twice with a temporary public test key (the private test key was never stored or shared). A new world was created; Minecraft status/ping/pong answered 26.3, 0/2 players, local ping 0–1 ms. This is not a measurement of the friend's connection.
- Verified: a clean `stop`, saving of all dimensions, restart and backup creation. After stopping, no own tunnel service and zero rules in our firewall group remain. No unclean-session marker.
- Verified a full removal of the WireGuard we installed while keeping the world and keys, then reinstalled the signed component for normal use. Tunnel and server are left off.
- One manual UDP 51820 forwarding rule to the laptop's local IPv4 was added in the router UI. Its name, address and rollback instructions are recorded in the private, ignored `router-manual.json`. TCP 25565 is not forwarded. DMZ and UPnP are off and were not changed.
- No DHCP reservation was saved yet. If the laptop's address changes, the program compares it with the recorded value and asks to update the manual UDP rule.
- Limitation: with UPnP off, a manual router rule cannot honestly be removed automatically without access to the router's admin page. The program and the README say so; a full rollback means deleting exactly that rule by hand. No router password or session token is stored anywhere in the project.
- The test friend key was revoked. A clean friend ZIP contains exactly four files: EXE, Network.ps1, README.md and the public invitation — no private keys, `.local`, worlds or logs.
- Final OS check: no own service, zero rules in our group, UDP 51820 and TCP 25565 not listening. Two local backups kept.
- Not verified yet: a real handshake, playing together, port exposure on the friend's computer, real packet loss/jitter and latency over the internet.

## Version 2 — 2026-10-21 (after review and user interviews)

First-time users could not figure out how to play together (file exchange, 13 buttons, router). Decisions:

- The owner always hosts the server; both players use offline-mode nicknames.
- A first-run wizard (role → nicknames + EULA → setup → code exchange → router), then a single "Play / Stop" screen with the friend's status, the address and a copy button. Everything else lives in "Settings".
- Short text codes `MP-…` (base64url + 4 bytes of SHA-256 to catch copy/paste damage) replace the JSON files. A safety code derived from both public keys is compared by voice; the host confirms it before the friend's key is added.
- Public IP changes: the host detects them and shows a short new-address code; the friend pastes it without re-pairing (the host key stays pinned, so such a code cannot redirect the friend to another server).
- One UAC prompt: the `MinePairHelper` service (SYSTEM, manual start, start right and pipe access only for the installing user, stops itself after 10 idle minutes). The exe is copied to Program Files and the network script is written from the exe's own resource. The network journal, the protected copy of the tunnel configuration and the results live in `ProgramData\MinePair` (SYSTEM/Administrators only). This removes the privilege escalation through a replaced `Network.ps1` in a user-writable folder.
- One exe; data in `%LOCALAPPDATA%\MinePair`, automatic migration from the first version's `.local`.
- Multiple worlds (version from the official releases, game mode, difficulty, PvP, hardcore, seed), with the matching Java version from Mojang's metadata.
- Review fixes: an abandoned Java process blocks backups and starts and is offered for termination; key recovery after an interrupted generation; the supervisor holds a handle to the owner process; a clear message when UAC is cancelled; rotation of automatic backups (10); the crash marker is cleared by a confirmation in the program together with a world backup; exact WireGuard LLC signature (EV, SERIALNUMBER=4227913); the MSI is downloaded and verified by the service in an admin-only folder; virtual adapters (Radmin and similar) are not mistaken for the local IP; WireGuard is removed only if the product code in the registry belongs to WireGuard; the script is written with a BOM for PowerShell 5.1.
- Router: a DHCP reservation for the laptop was added; the UDP 51820 forwarding already existed; UPnP and DMZ stay off and unchanged. The router's WAN IP matches the address seen from the internet (no CGNAT), but the ISP assigns it over DHCP, so it may change.
- Live check 2026-10-21 (temporary public "friend" key, private half never stored, key revoked afterwards): the service was started by a regular user through the pipe, the tunnel and 5 firewall rules were created, Minecraft 26.3 listened only on its tunnel address port 25565 and answered after 30 s (local ping 2 ms — not a measurement of the friend's route). After stopping: no tunnel, 0 rules, port 25565 not listening, no crash marker. A handshake with a real friend and playing together are NOT verified yet.
- Found during the check: (1) .NET Framework writes a BOM into child process stdin when the console is UTF-8 — keys are now computed in the program (X25519 per RFC 7748, verified with the test vectors), so wg.exe is not needed for keys; (2) applications started from an MSIX-packaged app write AppData into the package's virtualised folder, which the SYSTEM service cannot see. A normal double-click launch is not affected.
