# HDD Spindown 1.0

Stand: 12. November 2026 · Version 1.0.0 (Release-Kandidat) · **Noch keine Produktivfreigabe:** Hardwarequalifikation und Pilotbetrieb stehen aus (siehe unten).

HDD Spindown sperrt interne SATA-Daten-HDDs, entfernt ihre Laufwerksbuchstaben aus „Dieser PC“ und fordert Standby an. Ab 1.0 hält ein **Windows-Dienst** die Sperren. Die Oberfläche kann geschlossen werden, abstürzen oder der Benutzer kann sich abmelden – pausierte HDDs bleiben pausiert. Nach Dienstabsturz, Neustart oder Stromausfall stellt der Dienst beim nächsten Start die ursprüngliche Verfügbarkeit wieder her.

Grundlage ist das [Produktionskonzept](PRODUKTIONSKONZEPT.md); die Sicherheitsregeln aus Abschnitt 3 sind unverändert umgesetzt.

## Installation

1. Alte Version 0.3.x beenden („Freigeben und beenden“ im Infobereich).
2. `out\HDD-Spindown-1.0.0-x64.msi` ausführen und die Administratorabfrage bestätigen.
3. „HDD Spindown“ aus dem Startmenü öffnen. Die App selbst läuft **ohne** Administratorrechte; steuern dürfen Konten der lokalen Administratorengruppe.
4. Falls die alte Version noch Laufwerksbuchstaben entfernt hatte, bietet die App „Zuordnungen aus Version 0.3 übernehmen“ an (Menü „…“).

Installiert werden:

| Pfad | Inhalt |
|---|---|
| `C:\Program Files\HDD Spindown\Service\` | Dienst `HddSpindown` (LocalSystem, Autostart) und `HddSpindown.Recovery.exe` |
| `C:\Program Files\HDD Spindown\App\` | WinUI-3-Oberfläche |
| `C:\ProgramData\HDD Spindown\state\` | Zustandsspeicher (2 Kopien, SHA-256), nur SYSTEM/Administratoren |
| `C:\ProgramData\HDD Spindown\logs\` | Begrenztes Protokoll (5 × 1 MiB); Störungen zusätzlich im Ereignisprotokoll „Anwendung“, Quelle „HDD Spindown“ |

Deinstallation und Update prüfen vorher alle Vorgänge: Der Installer gibt pausierte HDDs frei und **bricht ab**, wenn ein Vorgang nicht wiederhergestellt werden kann. Der Zustandsspeicher und die Protokolle unter ProgramData bleiben bei der Deinstallation erhalten.

## Bedienung

Jede HDD erscheint als Karte mit drei getrennten Angaben:

| Angabe | Bedeutung |
|---|---|
| **Zugriff** | Freigegeben / Volumes gesperrt / Sperrstatus unklar |
| **Explorer** | Sichtbar / Ausgeblendet / Wiederherstellung offen |
| **Hardware** | Nicht überwacht / Standby angefordert / Unbekannt (nach Neustart oder Energiesparmodus) |

„Standby angefordert“ heißt: Die HDD hat den Befehl bestätigt. Der Motorzustand wird **nicht gemessen**, damit die HDD nicht aufgeweckt wird.

- **Schlafen**: sperrt alle Volumes, entfernt die Laufwerksbuchstaben, sendet ATA STANDBY IMMEDIATE. Dateien und Programme auf der HDD müssen geschlossen sein – es wird nichts zwangsweise geschlossen oder ausgehängt.
- **Freigeben**: löst die Sperren und ordnet dieselben Buchstaben denselben Volumes zu. Belegte Buchstaben werden nie überschrieben.
- **Alle freigeben**: nacheinander mit 5 s Abstand, damit nicht alle HDDs gleichzeitig anlaufen.
- **Schonregeln** (Entwurfswerte aus Konzept Abschnitt 9, keine Herstellergarantie): mindestens 30 Minuten Laufzeit nach einer Freigabe, höchstens 4 Standby-Anforderungen je HDD in 24 Stunden. Die Restzeit wird an der Karte angezeigt.
- **Diagnose** (Menü „…“): Zustand des Dienstes, aller HDDs und offener Vorgänge; Seriennummern gekürzt.

Die Oberfläche folgt dem Windows-App-Modus (hell/dunkel), nutzt Mica, die Systemakzentfarbe und Segoe Fluent Icons. `HDD Spindown.exe --demo` zeigt eine Vorschau mit simulierten HDDs ohne Dienst und ohne Hardwarezugriff.

## Zugelassene Geräte

Nur interne SATA-HDDs (Seek-Penalty bestätigt) mit GPT und einfachen, fehlerfreien NTFS-Datenvolumes. Abgelehnt mit Begründung an der Karte: SSD/NVMe (ausgeblendet), USB/RAID-Controller, MBR, dynamische Datenträger/Storage Spaces, EFI-/System-/Programm-/Auslagerungs- und Dienstdaten-Datenträger, BitLocker oder nicht prüfbarer Verschlüsselungsstatus, gesetztes Dirty-Bit, über mehrere Bereiche verteilte Volumes.

## Recovery ohne Oberfläche

Als Administrator in einer Konsole:

```text
"C:\Program Files\HDD Spindown\Service\HddSpindown.Recovery.exe" status
"C:\Program Files\HDD Spindown\Service\HddSpindown.Recovery.exe" list
sc stop HddSpindown          (Dienst stellt beim Beenden alle Buchstaben wieder her)
"C:\Program Files\HDD Spindown\Service\HddSpindown.Recovery.exe" recover
"C:\Program Files\HDD Spindown\Service\HddSpindown.Recovery.exe" import-legacy
"C:\Program Files\HDD Spindown\Service\HddSpindown.Recovery.exe" export
```

`recover` verwendet denselben geprüften Kern wie der Dienst und arbeitet nur bei gestopptem Dienst. Es gibt keine geratenen Zuordnungen, keine Ersatzbuchstaben und keine CHKDSK-Schreibzugriffe.

## Verhalten bei Windows-Ereignissen

| Ereignis | Verhalten |
|---|---|
| App geschlossen / abgestürzt, Abmeldung | Dienst hält die Sperren |
| Dienst beendet (`sc stop`), Herunterfahren | Alle Buchstaben werden wiederhergestellt |
| Dienstabsturz | Neustart nach 5 s, 30 s, 120 s, danach keine weitere Aktion; beim Start Wiederherstellung |
| Stromausfall | Wiederherstellung beim nächsten Dienststart; kein erneuter Standby |
| Energiesparmodus | Keine neuen Vorgänge; nach Resume Abgleich der Sperren, Hardwarezustand „Unbekannt“; nicht mehr prüfbare Sperren → ursprüngliche Verfügbarkeit |
| HDD fehlt beim Start | Vorgang bleibt offen; der Dienst versucht alle 2 Minuten erneut (ohne pausierte HDDs anzufassen) |
| Schreibfehler im Zustandsspeicher | Keine weiteren Geräteänderungen; Dienst startet zum Abgleich neu |

## Aufbau

```text
src/HddSpindown.Core       Zustandsautomat, Transaktionsspeicher, Schonregeln, IPC-Protokoll (aus M1)
src/HddSpindown.Windows    Windows-Adapter (Identität, GPT, Volumes, Sperren, Mount-Manager, ATA), Pipe-Client
src/HddSpindown.Service    Windows-Dienst, Named Pipe mit ACL, Ereignisse, Protokoll
src/HddSpindown.Recovery   Recovery-Werkzeug (Konsole)
src/HddSpindown.App        WinUI-3-Oberfläche
tests/                     Kern-Tests (simuliert) und Integrationstests (Wegwerf-VHDs)
installer/                 WiX-5-Paket
```

Sicherheit der Steuerung: Die Named Pipe ist lokal (Netzwerk verweigert), nur SYSTEM, Administratoren und interaktive Benutzer dürfen verbinden; steuernde Aufträge verlangen ein Konto der Administratorengruppe (auch mit UAC-gefiltertem Token). Der Client prüft, dass der Pipe-Server der registrierte Dienst ist. Aufträge sind auf `pause`/`release`/`releaseAll`/`importLegacy` mit Gerätekennung begrenzt – keine Pfade, ATA-Befehle oder Skripte.

## Build

Voraussetzung: .NET 8 SDK. Pakete (Windows App SDK, WiX) werden beim ersten Build von nuget.org geladen.

```powershell
powershell -ExecutionPolicy Bypass -File .\Build.ps1                    # Kern-Tests, Veröffentlichung, MSI
powershell -ExecutionPolicy Bypass -File .\Build.ps1 -IntegrationTests  # zusätzlich VHD-Tests (UAC)
```

Ergebnis: `out\HDD-Spindown-1.0.0-x64.msi` und `out\SHA256SUMS.txt`. Details zu den Tests: [TEST-REPORT.md](TEST-REPORT.md).

## Offen bis zur Produktivfreigabe

- **Hardwarequalifikation (P1.1):** ATA-Standby, Freigabe und Aufwachverhalten an den echten HDD-Modellen und Controllern prüfen. Die Schonregeln sind generische Entwurfswerte; modellbezogene Profile fehlen.
- **Pilot (P1.3):** beaufsichtigter Betrieb, danach mindestens sieben Tage unter realistischer Last.
- **Signatur:** MSI und EXE sind unsigniert; dafür wird ein eigenes Code-Signing-Zertifikat benötigt.
- **Wartungssperren** für laufende Backups/SMART-Tests (Konzept Abschnitt 9) und eine Erkennung unerwarteten Aufwachens sind nicht umgesetzt.
- **Kein Infobereich-Symbol:** Der Dienst meldet Störungen im Ereignisprotokoll; die App zeigt sie beim Öffnen.
- Unabhängige Prüfung der Gerätezugriffe und der Recovery-Logik.

Das Tool ersetzt kein Backup, verhindert keine mechanischen Defekte und sagt keine Lebensdauer zu.
