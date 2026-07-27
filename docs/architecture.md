# Architektur

## Projektgrenzen

- `TwitchAI.Core`: unveränderliche Domänenmodelle, Normalisierung, Whitelist,
  Policyauflösung, Batching, Envelope-Validierung und Identitätsschutz.
- `TwitchAI.Infrastructure`: SQLite, Migrationen, Twitch OAuth/Helix/EventSub,
  LM-Studio-Client, Retrieval, Moderationsausführung und Datenbankeditor.
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

Whitelist-Treffer verlassen den Ingress an dieser Stelle. Alle übrigen
Nachrichten erhalten einen unveränderlichen Kategorie- und Policy-Snapshot.
`ModerationBatcher` trennt Kategorien sowie Revisionen und versiegelt nach dem
ersten Grenzwert: 20 Nachrichten, 6.000 Nachrichtenzeichen oder zehn Sekunden
seit der ersten Nachricht. Ein zweiter Batch kann während einer laufenden
Inferenz gesammelt werden; der Inferenz-Worker bleibt seriell.

## Modellgrenze

Das Request-Envelope enthält keine Twitch-Identitäten. Ein lokales Mapping
verbindet temporäre IDs mit den Snapshots. Die Antwort wird als Ganzes
verworfen, wenn unter anderem eine ID fehlt/doppelt/unbekannt ist, ein Regelset
inaktiv ist, Timeoutwerte ungültig sind oder eine Triggerphrase nicht im
Lowercase-Text vorkommt.

Retrieval ist auf sechs Beispiele und ungefähr 800 Tokens begrenzt. Lokale
Blacklistbestände werden nicht in den Prompt kopiert; nur gefundene Regelcodes
werden übertragen.

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

Automatische Phrasenaktivierung wird aus positivem und negativem Feedback
abgeleitet. Eine Phrase benötigt mindestens eine positive Bestätigung und eine
Positivquote von mindestens 60 Prozent, sofern kein manueller Override greift.

## Datenschutz

Normale Chats verbleiben im begrenzten RAM-Ringpuffer. Nur Nachrichten, für die
ein Moderationsfall erzeugt wurde, werden mit Originaltext dauerhaft
gespeichert. EventSub-Verfügbarkeitsevents ohne Case enthalten keinen Chattext
und werden zeitlich begrenzt vorgehalten.
