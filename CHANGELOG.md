# Changelog

## 1.1.0 – 2026-11-12

- German and English throughout: app, built-in help, tray, service messages, recovery tool (`--lang de|en`). The language can be switched at runtime under `…` → *Language*; default follows Windows or the installer choice.
- New `Setup.exe` with language selection (Windows 11 style); bilingual MSI (German base, embedded English transform), license page and “Launch HDD Spindown” option.
- Built-in help (F1) with status legend, protection rules, special situations and copyable recovery commands.
- Notification area icon: Sleep/Release per HDD, starts at sign-in, notifications only for faults.
- Fixed: broken umlauts in the recovery tool output.
- Build checks for missing or duplicate translations.

## 1.0.1 – 2026-11-12

- Built-in help; version bump so that the MSI upgrades 1.0.0 in place.

## 1.0.0 – 2026-11-12

- Production architecture per the concept: Windows service holds the locks, WinUI 3 app without admin rights, local named pipe with ACL and caller check.
- Windows adapter: device identity, GPT layout, volume mapping, exclusion of system/boot/page-file/encrypted/SSD disks, volume locks, mount manager, ATA STANDBY IMMEDIATE.
- Durable journal with two checksummed copies, recovery at service start, retry for drives that were missing, resume revalidation, protection rules (minimum runtime, daily standby limit).
- Recovery tool, import of 0.3.x journals, WiX installer with uninstall/upgrade gate.
- VHD integration tests against the real Windows storage stack.
