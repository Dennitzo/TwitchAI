# TwitchAI

TwitchAI ist eine lokale WinUI-3-Anwendung für assistierte Twitch-Chatmoderation.
Sie bündelt Chatnachrichten, bewertet sie mit einem lokalen GGUF-Modell über den eigenen TwitchAI-Gateway und llama.cpp und legt ausschließlich überprüfbare Vorschläge vor. Eine
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
- Docker Desktop, Python 3 und ein vollständiges GGUF-Modell unter `C:\Users\AMD\.cache\huggingface`
- eine registrierte Twitch Developer Application. TwitchAI verwendet den
  Device Code Flow und benötigt in den Einstellungen ausschließlich deren
  Client-ID, keinen Client Secret.

## Erster Start

1. Einmal `windows/build-ai-stack.ps1` ausführen. Gateway und llama werden beim App-Start automatisch gestartet und beim Beenden gestoppt.
2. TwitchAI öffnen und unter **Einstellungen** die Twitch Client-ID eintragen.
3. Optional die zu moderierenden Kanäle kommagetrennt eintragen, zum Beispiel
   `channel1, channel2, channel3`. Ohne Kanalangabe wird der eigene Kanal
   verwendet; andere Kanäle müssen in der Moderatorliste des angemeldeten
   Kontos enthalten sein.
4. **Mit Twitch verbinden** wählen, den Device-Code im Browser bestätigen und
   die Verbindung prüfen.
5. Unter Einstellungen das lokale Modell und dessen unterstütztes Reasoning wählen, die lokale AI prüfen und anschließend im Header **Analyse starten** wählen.

Zeigt Twitch beim Aktivieren **„Falscher Code!“**, den alten Browser-Tab
schließen und die Verbindung in TwitchAI erneut starten. Die Anwendung öffnet
daraufhin die exakt von Twitch gelieferte URL mit einem frischen Gerätecode.
Ein fehlgeschlagener Refresh einer älteren Sitzung blockiert diesen neuen
Autorisierungsablauf nicht.

Die Anwendung verlangt diese Twitch-Scopes:

- `user:read:chat`
- `user:read:moderated_channels`
- `moderator:manage:chat_messages`
- `moderator:manage:banned_users`

Die SQLite-Datei und die nicht geheimen Einstellungen liegen paketunabhängig
unter `%LOCALAPPDATA%\TwitchAI`. Der Datenbankpfad ist
`%LOCALAPPDATA%\TwitchAI\TwitchAI.db`.

## Moderationsablauf

1. EventSub liefert Chat sowie im Moderationsmodus Lösch- und Clear-Ereignisse.
   Kategorien werden beim Start und regelmäßig per Helix abgeglichen; dadurch
   verbrauchen viele überwachte Fremdkanäle keine `channel.update`-EventSub-Kosten.
2. Die exakte Lowercase-Whitelist wird vor jeder weiteren Verarbeitung geprüft.
   Der Seed `KEKW` wird daher in beliebiger Groß-/Kleinschreibung übersprungen,
   ` KEKW` oder `KEKW!` dagegen nicht.
3. Analysierbare Nachrichten werden nach spätestens 10 Sekunden oder bei 20
   Nachrichten gebündelt. Kategorie- oder Regelwechsel schließen ein Batch
   sofort.
4. General-Regeln gelten immer. Backseatgaming kann unter **Einstellungen →
   Moderation → Backseatgaming** ein- oder ausgeschaltet werden; der Schalter ist
   standardmäßig aktiviert. **Einstellungen speichern** im Seitenkopf übernimmt
   die Änderung und initialisiert den Regelkontext ohne App-Neustart neu.
   Bei aktiviertem Schalter gilt Backseatgaming für bekannte Kategorien außer
   Just Chatting und IRL; bei unbekannter Kategorie wird konservativ nur General
   verwendet.
5. Die lokale AI klassifiziert zunächst mit
   `twitchai.moderation.classification.response.v1` ohne Begründungstext.
   Erkannte sowie durch exakte Lerndaten oder aktive Phrasen vorgegebene
   Kandidaten werden anschließend gemeinsam mit
   `twitchai.moderation.confirmation.response.v1` bestätigt; erst dort entstehen
   Begründung und Hinweis. Reines `noAction` benötigt einen Request, ein
   semantischer Treffer höchstens zwei und eine ungültige Bestätigung niemals
   einen zusätzlichen Reason-Run.
6. Ein Mensch kann Aktion, Timeoutdauer, Verstoßkategorie, Begründung und
   ausgewählte Triggerphrasen ändern und danach **Ausführen** oder
   **Verwerfen** wählen. General unterscheidet dabei 19 globale Kategorien;
   `BACKSEATGAMING` bleibt das kategorieabhängige Regelset. Bestätigungen
   werden als vollständige Kontextbeispiele ausschließlich unter **Learning**
   gespeichert; die manuellen **Phrasen**-Listen werden dadurch nie erweitert.
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

Die zweistufigen keyed Moderationsverträge sind standardmäßig aktiv. Für einen internen
Rollback kann bei geschlossener App in
`%LOCALAPPDATA%\TwitchAI\settings.json` der Stringwert
`"UseCompactModerationContractV6": "false"` gesetzt werden; der historische
Einstellungsname bleibt aus Kompatibilitätsgründen erhalten. Klassifikations-,
Bestätigungs- und Reparaturdiagnosen werden ohne Chattexte oder Modellantworten in
`ai_validation_attempts` gespeichert.

Weitere technische Details stehen in
[docs/architecture.md](docs/architecture.md).

## Eigene lokale AI-Laufzeit

Der Docker-Gateway (`twitchai-gateway`, Compose-Projekt `twitchai-ai-stack`) hört auf
`127.0.0.1:8180`. Der native llama-Router verwendet Port 8181, seine Modellsteuerung
Port 8182. Missums Dienste, Ports und Laufzeitverzeichnisse werden nicht verwendet.
Die portable EXE enthält den Runtime-Support und stellt ihn beim Start unter
`%USERPROFILE%\.twitchai\runtime-support` passend zur App-Version bereit.
GGUF-Dateien werden ausschließlich aus `C:\Users\AMD\.cache\huggingface` gelesen.
Unvollständige Shards, reine Projektoren und ungeeignete Modelltypen erscheinen
nicht in der nativen Chat-Modellauswahl. Es werden keine Modelle heruntergeladen.

- `windows/build-ai-stack.ps1`: Docker-Image bauen und offizielle llama.cpp-CUDA-Binärdateien mit SHA-256-Prüfung installieren.
- `windows/start-ai-stack.ps1`: Docker Desktop bei Bedarf starten, eigene native Runtime und Gateway starten.
- `windows/stop-ai-stack.ps1`: eigenen Gateway stoppen, Modelle entladen und eigene native Prozesse beenden.
- `windows/Build-Portable.ps1`: App einschließlich Runtime-Support erstellen.

**Einstellungen speichern** im Seitenkopf übernimmt Modell, Reasoning und die
Moderationsschalter gemeinsam. Eine laufende Analyse wird dafür angehalten und
mit frischen Kanalinformationen neu gestartet; eine pausierte Analyse bleibt
pausiert. Ein App-Neustart ist nicht erforderlich. `auto` verwendet
die nativen Modellvorgaben; weitere Stufen stammen aus den GGUF-Template-Metadaten.
**Analyse starten** wartet auf die bestätigte Ladung des ausgewählten Modells,
bevor Nachrichtenempfang und AI-Verarbeitung beginnen. Währenddessen zeigt die
App **Modell wird geladen**. Der Gateway nimmt JSON sowohl mit `Content-Length`
als auch über HTTP/1.1 Chunked entgegen und beantwortet ungültige Requests mit
strukturierten JSON-Fehlern. Modellwechsel warten auf laufende Inferenzanfragen.
Ein Modellwechsel wird bei der nächsten Anfrage durch die native GPU-Verwaltung
angewendet. Die bereits vorhandenen Moderationsschemas, Freigaben, AI-Metriken
und Learning-Abläufe verwenden denselben lokalen Gateway.

Die native Verwaltung in `workers/coding/catalog.py`, `session_cache.py` und den
beiden `manage-*.ps1`-Skripten wurde aus dem lokalen Missum-Projekt übernommen und
für TwitchAI isoliert. Der auf Chatmoderation beschränkte Gateway ist eigenständig;
er benötigt weder eine laufende Missum-App noch Missums Server oder Datenordner.
Betriebslogs und Modell-/Sitzungszustand liegen unter `%USERPROFILE%\.twitchai\native-runtime`,
die separaten llama-Binärdateien unter `%USERPROFILE%\.twitchai\llama-server`.
Dieser paketunabhängige Pfad gilt auch beim Start aus Codex. Startfehler werden
unter `%USERPROFILE%\.twitchai\logs\start-ai-stack.log` protokolliert.

Für die Übernahme bereits vorhandener llama-Binärdateien unterstützt der Build
`-BinaryDirectory <Ordner mit llama-server.exe und DLLs>`. Dabei werden alle
Binärdateien in den eigenen TwitchAI-Installationsordner kopiert und ihre Hashes
verglichen. Der Quellordner wird danach für den Betrieb nicht benötigt.

Der reproduzierbare lokale Gateway-Test steht unter
[tools/TwitchAI.Gateway.Smoke](tools/TwitchAI.Gateway.Smoke/README.md).
Er prüft feste Beispielnachrichten mit den produktiven Klassifikations- und
Bestätigungsschemas, ohne Twitch-Anfragen oder Änderungen an der App-Datenbank.
Protokollregressionen: `python -m unittest discover -s tests/gateway -v`.
