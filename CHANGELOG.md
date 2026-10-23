# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed
- A router rule created through UPnP was removed on the first stop and never added again, so the friend could connect only once. Every start now adds it back; a refusal by the router shows a warning instead of blocking the game.
- The secure network stayed on when Java failed to start or exited while the server was being asked to stop.
- Retrying "Try automatically (UPnP)" after a failed attempt reported success without creating the router rule.

## [0.3.1] - 2026-10-23

### Fixed
- Setup failed with "Another program is pretending to be the MinePair helper": the pipe check could not open the SYSTEM service process as a standard user and rejected the real helper. The session is now read from the pipe itself.

## [0.3.0] - 2026-10-23

### Security
- The helper deletes a pre-created or linked `C:\ProgramData\MinePair` folder before installing and makes Administrators its owner.
- The GUI talks to the helper pipe only if its server runs as a service (session 0).
- The safety code is 48 bits (`XXXX-XXXX-XXXX`) instead of 32. Codes from earlier builds no longer match.
- The tunnel configuration in the user's data folder exists only while the helper starts the tunnel.

### Fixed
- The tunnel and firewall rules are removed when the server exits even if the GUI crashed.
- Folder moves retry while antivirus software briefly locks new files, and work across drives (legacy migration, Java install).
- The friend's app shows renamed nicknames after the host sends an updated invitation.
- Self-tests delete their scratch folder.

### Changed
- Folder ACLs are rewritten only when they differ, avoiding a walk over all worlds and backups on every settings load.
- Documentation rewritten for the public repository; the specification moved to `docs/SPEC.md`.

## [0.2.0] - 2026-10-21

### Added
- First-run wizard and a single Play/Connect screen.
- Pairing with short `MP-` text codes and a safety code instead of JSON files; new-address codes when the host's public IP changes.
- `MinePairHelper` service: one UAC prompt at setup instead of one per network operation.
- Multiple worlds with any official release, game mode, difficulty, PvP, hardcore and seed; matching Java downloaded automatically.
- Rotation of automatic backups (last 10) and crash recovery with a backup.
- Automatic migration of version 1 data from `.local` to `%LOCALAPPDATA%\MinePair`.

### Changed
- X25519 keys computed in-process (RFC 7748) instead of piping secrets through `wg.exe`.
- The WireGuard signature is pinned to the exact WireGuard LLC EV certificate.

### Security
- The network script is written from the executable's own resource into Program Files instead of running from a user-writable folder.

## [0.1.0] - 2026-10-20

### Added
- Native WinForms launcher, private pairing through invitation/reply files and a reversible WireGuard setup with interface-scoped firewall rules.
- Verified downloads of the vanilla server, Temurin Java and WireGuard.
- Backups, restore and rollback of all own network changes.

[Unreleased]: https://github.com/IlliaAnikushyn/MinePair/compare/v0.3.1...HEAD
[0.3.1]: https://github.com/IlliaAnikushyn/MinePair/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/IlliaAnikushyn/MinePair/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/IlliaAnikushyn/MinePair/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/IlliaAnikushyn/MinePair/releases/tag/v0.1.0
