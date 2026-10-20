# MinePair

A private vanilla Minecraft Java 26.3 server for two Windows x64 computers. Requirements and context: [SPEC.md](SPEC.md).

First version. Official vanilla server.jar, Temurin Java and WireGuard. The server runs without administrator rights; individual network operations ask for UAC. Neither the interface nor a running service proves that the friend is connected. Verification results are recorded in SPEC.md.

## Server owner

1. Unpack the application into a permanent folder. Start `MinePair.exe` with a normal double-click, not as administrator.
2. Choose "My server", enter the exact nicknames, the public IPv4 and the memory. The default of 3072 MB targets a 16 GB laptop; it does not guarantee a lag-free game. "Detect IPv4" queries api.ipify.org. Compare the address with the router's WAN address; if it changes, update it and send your friend a new invitation.
3. Press "Prepare": official downloads only. The Minecraft SHA-1 is checked against the Mojang manifest, the Java SHA-256 against the Adoptium API and the WireGuard Authenticode signature before installation. An existing WireGuard is not reinstalled.
4. Read and accept the Minecraft EULA: https://www.minecraft.net/eula. The program never accepts it on your behalf.
5. Create an "Invitation" and send the JSON to your friend. Your friend sends a reply back; import it. Compare the full public keys in a trusted conversation before confirming. These files contain no private keys, but the address and nicknames are still your data; do not publish them.
6. Press "Start". One tunnel and narrow firewall rules are created, then the server starts. Starting Java and generating the world take a while. Open Minecraft with the matching nickname and use the address shown in the program.
7. To finish, press "Stop / remove network" and confirm UAC. Wait for the success message. Do not end Java in Task Manager as a normal way to quit.

## Friend

Unpack a separate clean copy of the program without the owner's `.local` folder. If the package contains `MinePair.invite.json`, the "I am the friend" mode is selected automatically. Press "Prepare" (only WireGuard is installed), import the invitation, compare the host's public key, create a "Reply to host" and send the JSON to the owner. Once the owner accepts the reply, press "Start" and connect to the address shown with the nickname shown. The friend's router does not need any changes.

Never share private keys or your `.local` folder. To revoke the friend's device, the owner stops the network and presses "Revoke friend". In offline mode a nickname/whitelist does not prove identity: the private network is what keeps strangers out.

## Router and the limits of automation

One forwarding rule is needed: **UDP 51820** to the laptop's local IPv4, same internal port. **Do not open TCP 25565 and do not enable DMZ.** Check the laptop address with `ipconfig`; it may change when switching from Wi-Fi to cable. It helps to reserve the current address for the laptop in the router's DHCP settings, noting the previous value.

The "Router" button can create, and later remove, its own UPnP rule **only if UPnP is already enabled**. If it is off, the program does not turn it on. A manual rule is created in the router's admin page; it stays after the application stops and has to be removed there by hand. The program never stores the router password. An automatic rollback of a manual rule cannot honestly be promised without access to the specific router's management.

Typical path in a router UI: Advanced settings → Port forwarding → Add rule. Rule name: `MinePair_<profile id>`, protocol UDP, external/internal port 51820, the laptop's local IP. To undo, delete only the row with that name. Leave UPnP, DMZ, WAN, DNS and Wi-Fi as they were.

## Checking the connection

"Check connection" asks for the age of the WireGuard handshake (UAC), then performs a real Minecraft status/ping/pong. On the host itself this is a local check and does not measure the friend's route; the friend has to run it on their computer. The value is rounded to milliseconds: a local `0 ms` does not mean zero latency over the internet. The test does not measure packet loss, jitter, FPS or TPS and does not prove their absence. TCP can hide loss behind retransmissions.

## World and backups

World: `.local\server\world`. Backups: `.local\backups`. A backup is made automatically before restarting a cleanly stopped world. "Back up world" works only while the server is stopped. "Restore" accepts a ZIP with `level.dat` at its root; it validates the paths, extracts into a separate folder, makes a safety backup of the current world and then replaces it. Old backups are not deleted automatically: keep an eye on free space.

Do not edit `server.properties` by hand to publish the server: safe settings are rewritten on every start. Players are not given OP. The server accepts only the two agreed nicknames, listens only on the tunnel IPv4, and RCON/query/management are off.

## Stopping and removal

- "Stop / remove network": sends `stop`, waits for Java to save, removes only our tunnel service and our own rule group. Our UPnP mapping is removed only when the port, target and name match; other rules are never overwritten.
- "Remove setup": additionally removes WireGuard if MinePair installed it and no other tunnels/configurations exist. Someone else's WireGuard is left alone. The world and keys are kept. A manual router rule must be removed separately.
- Full removal: run "Remove setup", delete our manual router rule, keep the backups you need, then delete the application folder. Git cannot bring deleted backups back.
- If UAC is cancelled, nothing was rolled back: an error message is not a successful stop. Run "Stop / remove network" again.

## After a crash

The supervisor tries to stop Java cleanly when the GUI disappears. A sudden power loss can never guarantee the last session was saved. On the next start a leftover tunnel is shown in the interface: press "Stop / remove network". Change log: `.local\network-actions.log`; component ownership: `.local\network-journal.json`; server console: `.local\logs\server-console.log`.

The `.local\unclean-session` marker blocks a new start after an unclean shutdown so the problem is not hidden. First make sure Java and the supervisor have exited, remove the network, copy `.local\server\world` aside, read the log or restore the last good backup. Only after that delete `unclean-session` and start again. Never delete the marker while the server is running.

## Security and limitations

The device private key is stored with Windows DPAPI CurrentUser. The service's working configuration uses DPAPI LocalMachine with an ACL that allows only the owner, Administrators and SYSTEM; after the tunnel is removed the file stays encrypted. The data is not protected against an administrator or against malware already running as the owner. The program does not replace Windows, Java or Minecraft updates. The unsigned EXE of this first build may trigger Windows SmartScreen: the source code and a local build are available. Never disable your antivirus or firewall to run it.

The built-in tests are not a security audit. Playing together, real latency and a handshake with the friend's computer need a separate check. No connection method can guarantee the absence of packet loss, laptop overheating or ISP latency.

Official sources: https://www.minecraft.net/download/server · https://adoptium.net/ · https://www.wireguard.com/ · https://github.com/WireGuard/wireguard-windows/blob/master/docs/enterprise.md

## Build

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`. It uses the .NET Framework compiler that ships with Windows; no separate SDK is needed.

## Data and Git

All user data lives in `.local` next to the program and is excluded from Git. Never publish that folder: it contains keys, settings, the world and backups. The `dist` folder holds the build, not user data.

Commits keep the code. Reverting a commit does not undo firewall rules, installed components or router changes: those have their own rollback.
