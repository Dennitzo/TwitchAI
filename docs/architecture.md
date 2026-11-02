# Architektur

## Projektgrenzen

- `TwitchAI.Core`: unveränderliche Domänenmodelle, Normalisierung, Whitelist,
  Policyauflösung, Batching, Envelope-Validierung und Identitätsschutz.
- `TwitchAI.Infrastructure`: SQLite, Migrationen, Twitch OAuth/Helix/EventSub,
  lokaler Gateway-Client, Retrieval, Moderationsausführung und Datenbankeditor.
- `TwitchAI.App`: WinUI-Shell, MVVM-Zustand, Moderationskarten,
  Regel-/Whitelistansichten, Datenbankraster, Audit und Einstellungen.
- `TwitchAI.Tests`: Unit- und SQLite-Integrationstests.

## Ingress und Batch

`ModerationCoordinator` konsumiert den EventSub-Strom sequenziell. Chattext wird
in drei Formen aufgeteilt:

- `OriginalText`: unverändert;
- `LowerText`: ausschließlich `ToLowerInvariant`;
- `RuleNormalizedText`: NFKC, entfernte unsichtbare Zeichen,
  zusammengefasster Whitespace und invariantes Lowercase.

Der EventSub-WebSocket abonniert pro Kanal nur die autorisierten Chat-Ereignisse.
`channel.update` wird bewusst nicht abonniert, weil Fremdkanäle dafür jeweils
EventSub-Kosten erzeugen und Twitch insgesamt nur zehn Kostenpunkte zulässt.
Kategorie-Snapshots werden vor dem Start sowie periodisch über Helix abgeglichen.

Whitelist-Treffer verlassen den Ingress an dieser Stelle. Alle übrigen
Nachrichten erhalten einen unveränderlichen Kategorie- und Policy-Snapshot.
`ModerationBatcher` trennt Kategorien sowie Revisionen und versiegelt nach dem
ersten Grenzwert: 20 Nachrichten, 6.000 Nachrichtenzeichen oder zehn Sekunden
seit der ersten Nachricht. Verschiedene Channels können bis zur globalen
Vier-Slot-Grenze parallel inferieren; je Channel bleibt die Verarbeitung FIFO
und auf einen aktiven Request begrenzt.

## Modellgrenze

Die kompakten Stage-Envelopes enthalten keine Twitch-Identitäten. Die
Klassifikation v1 wählt nur Regel, Aktion, Evidence und optional eine
Chat-Antwort. Exakte Allow-Entscheidungen sperren Moderation; exakte Moderate-
und aktive Phrasentreffer werden direkt zu Bestätigungskandidaten. Die
Bestätigung v1 prüft alle Kandidaten eines Batches gemeinsam unter fest
vorgegebener Regel und Aktion und erzeugt erst dann Triggerbeleg, Begründung und
Hinweis. Ein lokales Mapping verbindet die temporären Schlüssel mit den
Snapshots.

Bei einem Klassifikationsfehler mit sicher zuordenbarer Message-ID bleiben
gültige Entscheidungen erhalten und nur die betroffene Nachricht wird einmal
kompakt repariert. Bestätigungen werden nie repariert: ungültige Kandidaten
fallen einzeln auf `noAction`, gültige Kandidaten desselben Responses bleiben
verwendbar. Es gibt keinen separaten Reason-Request.

Retrieval ist auf sechs Beispiele und ungefähr 800 Tokens begrenzt. Lokale
Blacklistbestände werden nicht in den Prompt kopiert; nur gefundene Regelcodes
werden übertragen.

`ai_validation_attempts` protokolliert Initial-, Repair- und lokale
Fallback-Prüfungen ausschließlich mit Fehlercodes, Zeichenzählern und einem
SHA-256-Fingerprint der Antwort. Chattexte, Modellantworten und lesbare
Fehlertexte werden dort nicht gespeichert.

## Freigabe und Idempotenz

Die UI übergibt an die Ausführung nur:

- `CaseId` und erwartete Row-Version;
- die endgültig editierte Aktion;
- Timeoutdauer und Begründung;
- ausgewähltes, aktuell aktives Regelset;
- eine Teilmenge der zum Fall gehörenden Triggerphrasen.

Die Ziel-User-ID und Message-ID werden erst im Executor aus dem persistierten
Snapshot geladen. Vor dem HTTP-Aufruf wird ein Intent in derselben lokalen
Transaktion angelegt, in der der Case von `pending` nach `executing` wechselt.
Ein eindeutiger Index verhindert einen zweiten Versuch. Netzwerkfehler nach
diesem Punkt werden als `OutcomeUnknown` behandelt und nicht wiederholt.

Die lokale Verfügbarkeitssicht kombiniert exakte Delete-Ereignisse,
User-/Room-Clear-Watermarks, das Sechs-Stunden-Limit sowie EventSub-Lücken. Nur
`ObservedActive` darf Twitch erreichen. Bei Timeouts wird zusätzlich der
aktuelle Ban-/Timeoutstatus per Helix geprüft.

Unmittelbar vor der Freigabe wartet der Executor auf die sequenzielle
Verarbeitung bereits empfangener EventSub-Ereignisse. Kann diese Barriere nicht
sicher erreicht werden, bleibt der Fall lokal blockiert. Ein EventSub-Ereignis
wird erst nach erfolgreicher Weitergabe dauerhaft dedupliziert; schlägt die
Weiterverarbeitung fehl, wird die Markierung freigegeben, damit ein Twitch-Retry
erneut verarbeitet werden kann.

## Persistenz

SQLite läuft mit WAL, Foreign Keys, `busy_timeout`, parametrisierten Statements
und einem prozessweiten asynchronen Write-Lock. Migrationen sind versioniert.
Konfigurierbare Datensätze besitzen `row_version` und `archived_utc`; Audit- und
Workflowdaten bleiben append-only beziehungsweise workflowgeschützt.

Der Write-Lock umfasst vollständige fachliche Schreibeinheiten einschließlich
Transaktionen, EventSub-Deduplizierung, LM-Metriken und UI-Exporte; reine
Lesezugriffe bleiben parallel. Beim Start werden zurückgelassene
`intent`-/`executing`-Datensätze atomar und idempotent in `OutcomeUnknown`
überführt.

Die Phrasenlisten sind ausschließlich manuell gepflegte Konfiguration. Live-,
Read-Only-, externe und manuelle Lernentscheidungen verändern weder
`phrase_rules` noch `whitelist_phrases` oder deren Bestätigungszähler. Sie
werden als vollständige Kontextbeispiele in der separaten Learning-Projektion
gespeichert. Optionale Trigger dienen nur als Metadaten und benötigen mindestens
zwei lexikalische Wörter; Retrieval und Klassifikation verwenden immer die
vollständige Nachricht.

## Datenschutz

Normale Chats verbleiben im begrenzten RAM-Ringpuffer. Nur Nachrichten, für die
ein Moderationsfall erzeugt wurde, werden mit Originaltext dauerhaft
gespeichert. EventSub-Verfügbarkeitsevents ohne Case enthalten keinen Chattext
und werden zeitlich begrenzt vorgehalten.

## Lokaler AI-Prozesslebenszyklus

`LocalAiRuntime` startet beim Host-Start den eigenen TwitchAI-Stack im Hintergrund.
AI-Anfragen warten auf denselben Startpfad. Beim Schließen der nativen Anwendung
wartet der UI-Thread auf den Host-Shutdown, damit Gateway, Modell-Unload und native
Prozessbeendigung vor dem Prozessende abgeschlossen werden können.
`ModerationCoordinator.StartAsync` ruft nach der Kanal-/Policyprüfung
`ILocalModerationClient.PrepareAsync` auf. Der Gateway bestätigt das ausgewählte
Modell über `POST /v1/models/load`, bevor EventSub und Inferenzworker starten.
Die Runtime-Skripte, der Katalog und die Gateway-Konfiguration sind als Ressourcen
in der Infrastructure-Assembly enthalten. `LocalAiRuntimeSupport` stellt sie in
einem versionsgebundenen Verzeichnis unter `.twitchai/runtime-support` bereit und
ersetzt fehlende oder abweichende Dateien atomar. Der Startpfad hängt dadurch
nicht vom temporären Entpackverzeichnis der portablen EXE ab.

Der Gateway übernimmt Modellauflösung und die Übersetzung der ausgewählten,
modellabhängigen Reasoning-Stufe. Er lädt Modelle über die GPU-Steuerung des aus
Missum übernommenen nativen Supervisors. Der Supervisor prüft GGUF-Metadaten,
vollständige Shards und verfügbaren GPU-Speicher. Er beendet nur eigene Prozesse,
deren Prozessidentität und Startzeit zur gespeicherten Eigentümerdatei passen.
Compose-Projekt, Container, Modell-Tags, Umgebungsvariablen, Status-/Cacheordner
und Ports sind für TwitchAI getrennt. Die Modellgewichte werden ausschließlich
lesend aus dem vorgegebenen Hugging-Face-Cache geöffnet.
Laden und Inferenz sind im Gateway serialisiert; ein Modellwechsel entlädt keine
laufende Anfrage. Die TwitchAI-Runtime lädt keine ungenutzte Subagenteninstanz vor.
HTTP-JSON-Bodies werden mit begrenzter Größe sowohl über Content-Length als auch
Chunked gelesen. SSE-Antworten werden fortlaufend weitergeleitet. Native
Reasoning-Stufen steuern zusätzlich die passenden Chat-Template-Argumente.
