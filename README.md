# TwitchAI

TwitchAI ist eine lokale WinUI-3-Anwendung für assistierte Twitch-Chatmoderation.
Sie bündelt Chatnachrichten, bewertet sie mit einem lokal in LM Studio geladenen
`openai/gpt-oss-20b` und legt ausschließlich überprüfbare Vorschläge vor. Eine
Moderationsaktion wird niemals automatisch ausgeführt.

## Sicherheitsmodell

- Twitch-User-IDs und Message-IDs kommen nur aus dem unveränderlichen lokalen
  EventSub-Snapshot. Das Modell sieht temporäre IDs (`m1`, `m2`, …) und kann kein
  Twitch-Ziel bestimmen.
- Vor jeder Freigabe werden `CaseId`, Row-Version, Identitätsfingerprint,
  Regelrevision und die lokale EventSub-Verfügbarkeit erneut geprüft.
- Unklare Verfügbarkeit oder eine EventSub-Lücke blockiert die Aktion.
- Ein persistierter Outbound-Intent verhindert automatische Wiederholungen bei
  mehrdeutigen Transportfehlern.
- Vor dem Twitch-Aufruf wird der EventSub-Verarbeitungspuffer geleert. Nach
  einem Absturz werden begonnene, nicht eindeutig abgeschlossene Aktionen als
  `OutcomeUnknown` wiederhergestellt und niemals automatisch erneut gesendet.
- Tokens liegen im Windows Credential Locker, nicht in SQLite.
- Chattext wird fachlich in Lowercase verarbeitet; der Originaltext bleibt für
  Anzeige, Identität und Audit unverändert erhalten.

## Voraussetzungen

- Windows 10 ab Version 2004 (Build 19041), x64
- .NET SDK `10.0.302` (siehe `global.json`)
- Visual Studio 2026 oder eine Installation mit WinUI-/MSIX-Buildtools
- LM Studio mit lokal geladenem Modell `openai/gpt-oss-20b`
- eine Twitch-Anwendung vom Typ Public Client mit aktivem Device Code Flow

## Erster Start

1. LM Studio starten, das Modell laden und den lokalen Server auf
   `http://127.0.0.1:1234` bereitstellen.
2. TwitchAI öffnen und unter **Einstellungen** die Twitch Client-ID eintragen.
3. Optional den zu moderierenden Kanal eintragen. Ohne Kanalangabe wird der
   eigene Kanal verwendet; andere Kanäle müssen in der Moderatorliste des
   angemeldeten Kontos enthalten sein.
4. **Mit Twitch verbinden** wählen, den Device-Code im Browser bestätigen und
   die Verbindung prüfen.
5. LM Studio prüfen und anschließend im Header **Analyse starten** wählen.

Die Anwendung verlangt diese Twitch-Scopes:

- `user:read:chat`
- `user:read:moderated_channels`
- `moderator:manage:chat_messages`
- `moderator:manage:banned_users`

Die SQLite-Datei und die nicht geheimen Einstellungen liegen paketunabhängig
unter `%LOCALAPPDATA%\TwitchAI`. Der Datenbankpfad ist
`%LOCALAPPDATA%\TwitchAI\TwitchAI.db`.

## Moderationsablauf

1. EventSub liefert Chat, Lösch-, Clear- und Kategorieereignisse.
2. Die exakte Lowercase-Whitelist wird vor jeder weiteren Verarbeitung geprüft.
   Der Seed `KEKW` wird daher in beliebiger Groß-/Kleinschreibung übersprungen,
   ` KEKW` oder `KEKW!` dagegen nicht.
3. Analysierbare Nachrichten werden nach spätestens 10 Sekunden oder bei 20
   Nachrichten gebündelt. Kategorie- oder Regelwechsel schließen ein Batch
   sofort.
4. General-Regeln gelten immer. Backseatgaming gilt für bekannte Kategorien
   außer Just Chatting; bei unbekannter Kategorie wird konservativ nur General
   verwendet.
5. LM Studio liefert ein streng validiertes
   `twitchai.moderation.response.v3`. Ein ungültiges Envelope erhält höchstens
   einen Reparaturversuch und erzeugt danach keinen Fall.
6. Ein Mensch kann Aktion, Timeoutdauer, Verstoßkategorie, Begründung und
   ausgewählte Triggerphrasen ändern und danach **Ausführen** oder
   **Verwerfen** wählen. General unterscheidet dabei 17 globale Kategorien;
   `BACKSEATGAMING` bleibt das kategorieabhängige Regelset.
7. Extern bereits erledigte Fälle senden keinen Twitch-Aufruf. Sie können als
   positives Feedback bestätigt oder ohne Lernen abgeschlossen werden.

## Datenbankeditor

Der Reiter **Datenbank** zeigt alle anwendungseigenen Tabellen. Schreibzugriff
ist allowlist-basiert:

- Whitelist-, Blacklist-, Policy- und Kategorie-Konfiguration sind editierbar.
- Workflow-, Audit- und Systemtabellen sind geschützt.
- Änderungen werden als Draft gesammelt, vor dem Commit validiert und atomar
  gespeichert; eine manuelle Eingabe eines Änderungsgrunds ist nicht nötig.
- Konfigurationszeilen werden historienerhaltend archiviert und können unter
  derselben ID wiederhergestellt werden.
- Abgeschlossene Moderationsfallpakete und sichere alte Betriebslogs können
  nach Vorschau als Löschentwurf markiert und erst über **Speichern** physisch
  gelöscht werden. Offene oder mehrdeutige Fälle bleiben geschützt.
- Jeder Commit erzeugt einen append-only Audit-Datensatz.
- Suche, Sortierung, Zählung und Pagination laufen in SQLite; Standardgröße ist
  50, wählbar sind 25, 50 und 100 Zeilen.
- Vorhandene Auditdaten bleiben unveränderlich; Korrekturen werden ausschließlich
  als neue `record_annotations` erfasst.
- Die aktuell gefilterte Tabellen- oder Auditansicht kann kontrolliert als
  JSON beziehungsweise CSV exportiert werden. Der Export wird bestätigt und
  selbst im Konfigurationsaudit protokolliert.

## Build und Tests

```powershell
dotnet restore TwitchAI.slnx
dotnet build TwitchAI.slnx -p:Platform=x64
dotnet test tests/TwitchAI.Tests/TwitchAI.Tests.csproj

# Direkt startbaren x64-Build am festen Zielpfad erstellen
.\windows\Build-Portable.ps1

# Erstellen und anschließend direkt starten
.\windows\Build-Portable.ps1 -Run
```

Der portable Build besteht aus
`artifacts\TwitchAI-Portable-x64\TwitchAI.App.exe`. Er enthält
die .NET- und Windows-App-SDK-Laufzeit sowie die kompilierten XAML-Ressourcen
und kann ohne MSIX-Installation gestartet werden. Beim ersten Start entpackt
die EXE ihre Abhängigkeiten in den lokalen Windows-Tempordner; das kann einige
Sekunden dauern.

Die EXE aus einem älteren MSIX-Build unter `bin` ist nicht eigenständig
startbar. Ein MSIX kann weiterhin explizit mit
`-p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true` gebaut werden;
für eine verteilbare Version ist ein vertrauenswürdiges Signaturzertifikat
erforderlich.

Weitere technische Details stehen in
[docs/architecture.md](docs/architecture.md).
