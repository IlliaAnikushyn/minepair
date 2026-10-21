# MinePair

Your own Minecraft Java server for two Windows x64 computers: yours and a friend's. A private WireGuard network between just your two computers — a replacement for Radmin VPN and Aternos. Requirements and context: [SPEC.md](SPEC.md).

The program is a single file, `MinePair.exe`. Start it with a normal double-click (not "Run as administrator").

## Playing together

**Whoever hosts the server:**

1. Start MinePair → "I host the server".
2. Enter your nickname and your friend's exactly as in the launcher and accept the Minecraft EULA.
3. "Start setup". Windows asks for permission once — click "Yes". The program downloads WireGuard, Java and the Minecraft server (latest release) by itself.
4. Copy the invitation code (it starts with `MP-`) and send it to your friend in any messenger, together with `MinePair.exe`.
5. Your friend sends a reply code back — paste it and compare the safety code (like `2D51-3B2F`) with your friend.
6. If the router is not configured yet, the program shows the one port to open (UDP 51820 → this computer).
7. From then on: "Play". In Minecraft: Multiplayer → Direct Connection → the address from the program ("Copy" button).

**The friend:**

1. Start `MinePair.exe` → "I join a friend".
2. "Start setup" (click "Yes" once in the Windows prompt) — only WireGuard is installed.
3. Paste the host's code, send the reply code back and compare the safety code.
4. From then on: "Connect", then the same address in Minecraft. Join with the nickname the program shows.

The friend's router needs no changes.

**If the host's public IP changes** (common with home internet), the program notices and shows a short code. The friend pastes it with "Paste code from friend" — no need to pair again.

## Where things live

- Data: `%LOCALAPPDATA%\MinePair` — settings, keys, worlds (`worlds`), backups (`backups`), Java, the Minecraft server, logs. "Settings → Data folder".
- Helper: the `MinePairHelper` service in `C:\Program Files\MinePair`. It runs as SYSTEM, starts only on request from the program and stops itself after 10 idle minutes. It can perform only a fixed set of actions: install WireGuard, turn MinePair's own tunnel and firewall rules on and off. It accepts commands only from your user account.
- Helper network state: `C:\ProgramData\MinePair` (Administrators and SYSTEM only).

The first version kept its data in `.local` next to the exe. On first start the new version moves it to `%LOCALAPPDATA%\MinePair` automatically: keys, world, backups and the router rule are preserved.

## Worlds and backups

"Settings → Worlds": several worlds, each with its own Minecraft version (official releases), game mode, difficulty, PvP, hardcore and seed. The matching Java is downloaded automatically. Switch worlds with "Switch world" on the main screen while the server is stopped.

The current world is backed up before every start. The last 10 automatic backups are kept; manual backups are never deleted. "Restore from backup" first saves the current state as a separate backup.

If the computer turned off during a game, the next "Play" warns you, makes a backup and continues.

## Security

- The friend connects only through WireGuard. One UDP port is open on the router; WireGuard does not answer anyone without the friend's key, so to scanners the port looks closed. TCP 25565 is never exposed.
- The tunnel carries only two `/32` addresses; DNS, the default route and all other traffic are untouched.
- The server listens only on the tunnel address. The firewall lets only the friend's tunnel IP reach the Minecraft port, and only for MinePair's Java. RCON, query and management are off, nobody is OP.
- The server runs in offline mode. The nickname whitelist does not verify identity; the private network is what keeps strangers out.
- Invitation codes carry only public data. The safety code protects against a code being swapped in the chat.
- The private key is stored with the current user's DPAPI. The network script comes from the program itself when the helper is installed, not from a folder the user can write to. The WireGuard signature is checked against the exact WireGuard LLC certificate.
- No program protects against malware that already runs under your account or as administrator.

## Uninstalling

"Settings → Remove MinePair from Windows": stops the server and the network, removes the helper, the tunnel and the firewall rules, and WireGuard — if MinePair installed it and there are no other tunnels. Worlds, backups and keys stay in `%LOCALAPPDATA%\MinePair`; you can delete that folder by hand. The router forwarding rule has to be removed in the router settings.

## Checking the connection

"Settings → Check connection" shows whether the network is on, when the last handshake with the friend happened, whether Minecraft answers and with what ping, and whether the public or internal IP changed. A TCP ping does not measure packet loss or server TPS.

## Build

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

Uses the .NET Framework 4.8 compiler that ships with Windows; no SDK needed. `-Test` runs the self-tests (pairing codes, validation, worlds, backups, migration of old data, rendering of every screen) without changing anything on the system.
