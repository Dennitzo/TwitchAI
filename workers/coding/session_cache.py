"""Best-effort native KV snapshots. Conversation history remains authoritative.

Only opaque, hashed keys become filenames. llama.cpp validates restored state and
still compares prompt tokens before reusing it. No model is loaded by this module.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import time
import urllib.parse
import uuid


# A Stop arrives while llama is inside a prompt batch. Large models process a
# 2048-token batch in tens of seconds, so the slot reports is_processing long
# after the client disconnected. The gateway keeps its turn gate until this save
# returns; waiting here costs nothing the follow-up prompt would not wait for
# anyway (the single slot cannot start it earlier) and keeps the snapshot exact.
INTERRUPTED_SAVE_IDLE_SECONDS = 90.0
PREPARATION_IDLE_SECONDS = 300.0
PREPARATION_MAXIMUM_SECONDS = 2 * 60 * 60.0
# Native versions 3 and 4 retain the same leading token payload layout. Their
# KV tensors differ and remain bound to the native binary/model fingerprint;
# accepting this header for bounded inspection does not make KV interchangeable.
NATIVE_SEQUENCE_TOKEN_LAYOUT_VERSIONS = (3, 4)


class PreparationCancelledError(RuntimeError):
    pass


class PreparationTimeoutError(TimeoutError):
    pass


class PreparationProgress:
    """Only advancing native token counts extend the preparation idle budget."""
    def __init__(self, total, cancelled=None, clock=None):
        self.total, self.cancelled = total, cancelled
        self.clock = clock or time.monotonic
        self.started = self.updated = self.clock()
        self.processed = -1

    def check(self):
        if self.cancelled and self.cancelled():
            raise PreparationCancelledError("Subagent preparation was cancelled by its caller")
        now = self.clock()
        if now - self.started >= PREPARATION_MAXIMUM_SECONDS:
            raise PreparationTimeoutError("Subagent preparation exceeded its two-hour maximum")
        if now - self.updated >= PREPARATION_IDLE_SECONDS:
            raise PreparationTimeoutError("Subagent preparation made no token progress for five minutes")

    def observe(self, event):
        self.check()
        progress = event.get("prompt_progress")
        if not isinstance(progress, dict):
            return
        processed = progress.get("processed")
        if type(processed) is int and self.processed < processed <= self.total:
            self.processed = processed
            self.updated = self.clock()


class NativeSessionCache:
    def __init__(self, directory, router, fingerprint, maximum_bytes=64 * 1024**3,
                 free_reserve=4 * 1024**3, estimate_bytes=None, prepare_completion=None):
        self.directory = Path(directory).resolve()
        self.directory.mkdir(parents=True, exist_ok=True)
        self.router, self.fingerprint = router, fingerprint
        self.maximum_bytes, self.free_reserve = maximum_bytes, free_reserve
        self.estimate_bytes = estimate_bytes
        self.prepare_completion = prepare_completion
        self.resident = {}
        self.owners = {}
        self.deleted_keys = set()
        self.deleted_sessions = set()
        self.lifecycle_error = None
        self.save_progress = {}
        self._read_ownership()

    @staticmethod
    def _canonical_key(key):
        if isinstance(key, str) and re.fullmatch(r"twitchai-session-v2-[0-9a-f]{64}", key):
            return "go-session-v2-" + key[len("twitchai-session-v2-"):]
        return key

    def _read_ownership(self):
        path = self.directory / "ownership.json"
        if not path.exists():
            return
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(value, dict) or value.get("version") != 1 or not isinstance(value.get("entries"), dict):
                raise ValueError("Invalid native cache ownership ledger")
            self.owners = {identity: entry for identity, entry in value["entries"].items()
                           if re.fullmatch(r"[0-9a-f]{64}", identity)
                           and isinstance(entry, dict) and any(isinstance(entry.get(field), str)
                               for field in ("sessionKey", "sessionId", "parentSessionId"))}
            if not isinstance(value.get("deletedSessionKeys", []), list) or not isinstance(value.get("deletedSessionIds", []), list):
                raise ValueError("Invalid native cache deletion tombstones")
            self.deleted_keys = set(value.get("deletedSessionKeys", []))
            self.deleted_sessions = set(value.get("deletedSessionIds", []))
            if any(not isinstance(item, str) for item in self.deleted_keys | self.deleted_sessions):
                raise ValueError("Invalid native cache deletion tombstones")
        except (OSError, ValueError, TypeError) as error:
            # A damaged ledger must never revive deleted state. Inference can
            # continue cold, but snapshot reuse/writes wait for ledger repair.
            self.lifecycle_error = str(error)

    def _write_ownership(self):
        target = self.directory / "ownership.json"
        temporary = self.directory / ("ownership." + uuid.uuid4().hex + ".pending")
        try:
            with temporary.open("w", encoding="utf-8") as stream:
                json.dump(dict(version=1, entries=self.owners,
                               deletedSessionKeys=sorted(self.deleted_keys),
                               deletedSessionIds=sorted(self.deleted_sessions)), stream, sort_keys=True)
                stream.flush()
                os.fsync(stream.fileno())
            temporary.replace(target)
        finally:
            temporary.unlink(missing_ok=True)

    def _remember(self, model, key, identity, session_id=None, parent_session_id=None, parent_key=None):
        if self.lifecycle_error:
            raise RuntimeError("Native cache ownership is unavailable: " + self.lifecycle_error)
        if key is None:
            return
        key = self._canonical_key(key)
        if key in self.deleted_keys or session_id in self.deleted_sessions or parent_session_id in self.deleted_sessions:
            raise ValueError("Native cache session was deleted")
        entry = dict(self.owners.get(identity, {}), model=model, sessionKey=key)
        if session_id is not None and entry.get("sessionId") not in (None, session_id):
            raise ValueError("Native cache key is already bound to a different session owner")
        if session_id is not None and entry.get("sessionId") != session_id:
            if any(owner.get("sessionId") not in (None, session_id)
                   for owner in self.owners.values() if self._canonical_key(owner.get("sessionKey")) == key):
                raise ValueError("Native cache key is already bound to a different session owner")
        if (parent_session_id is not None and entry.get("parentSessionId") not in (None, parent_session_id)):
            raise ValueError("Native cache session is already bound to a different parent owner")
        for name, value in (("sessionId", session_id), ("parentSessionId", parent_session_id),
                            ("parentSessionKey", self._canonical_key(parent_key))):
            if value is not None:
                if not isinstance(value, str) or not 1 <= len(value) <= 2048:
                    raise ValueError("Invalid native cache owner")
                entry[name] = value
        if entry.get("sessionId") in self.deleted_sessions or entry.get("parentSessionId") in self.deleted_sessions:
            raise ValueError("Native cache session was deleted")
        if self.owners.get(identity) != entry:
            self.owners[identity] = entry
            self._write_ownership()

    def delete(self, session_ids=(), session_keys=(), known_models=()):
        """Erase only the selected logical owners, including all delegated forks.

        Tombstones commit before files or resident slots are touched. A delayed
        cancelled turn therefore cannot recreate its snapshot after deletion.
        Fingerprint/model revisions remain addressable through the owner ledger.
        """
        if self.lifecycle_error:
            raise RuntimeError("Native cache ownership is unavailable: " + self.lifecycle_error)
        if not isinstance(session_ids, (list, tuple)) or not isinstance(session_keys, (list, tuple)):
            raise ValueError("Cache deletion requires owner/key lists")
        if not 0 < len(session_ids) + len(session_keys) <= 4096:
            raise ValueError("Cache deletion requires 1..4096 owners/keys")
        if any(not isinstance(item, str) or not 1 <= len(item) <= 2048 for item in (*session_ids, *session_keys)):
            raise ValueError("Invalid native cache deletion owner")
        selected_sessions = set(session_ids)
        selected_keys = {self._canonical_key(key) for key in session_keys}
        # Metadata is a second ownership record, including snapshots recovered
        # after a ledger write failed. Never turn arbitrary JSON paths into files.
        for path in self.directory.glob("*.json"):
            if not re.fullmatch(r"[0-9a-f]{64}\.json", path.name):
                continue
            try:
                entry = json.loads(path.read_text(encoding="utf-8"))
                if isinstance(entry, dict) and any(isinstance(entry.get(field), str)
                        for field in ("sessionKey", "sessionId", "parentSessionId")):
                    self.owners.setdefault(path.stem, entry)
            except (OSError, ValueError):
                pass
        while True:
            previous = (len(selected_sessions), len(selected_keys))
            for entry in self.owners.values():
                if (entry.get("sessionId") in selected_sessions or entry.get("parentSessionId") in selected_sessions
                        or self._canonical_key(entry.get("sessionKey")) in selected_keys
                        or self._canonical_key(entry.get("parentSessionKey")) in selected_keys):
                    if isinstance(entry.get("sessionKey"), str):
                        selected_keys.add(self._canonical_key(entry["sessionKey"]))
                    if entry.get("sessionId"):
                        selected_sessions.add(entry["sessionId"])
            if previous == (len(selected_sessions), len(selected_keys)):
                break
        identities = {identity for identity, entry in self.owners.items()
                      if entry.get("sessionId") in selected_sessions or entry.get("parentSessionId") in selected_sessions
                      or self._canonical_key(entry.get("sessionKey")) in selected_keys}
        # Existing v2 snapshots predate owner metadata. The exact currently
        # installed fingerprint can still be removed without broad directory wipes.
        for model in known_models:
            try:
                fingerprint = self.fingerprint(model)
            except (OSError, ValueError):
                continue
            for key in selected_keys:
                identities.add(hashlib.sha256(json.dumps([model, self._canonical_key(key), fingerprint],
                                   sort_keys=True).encode()).hexdigest())
        self.deleted_sessions.update(selected_sessions)
        self.deleted_keys.update(selected_keys)
        self._write_ownership()
        pending_models, removed, removed_bytes = [], 0, 0
        for model, current in list(self.resident.items()):
            if current["identity"] not in identities and self._canonical_key(current["key"]) not in selected_keys:
                continue
            current["deleted"] = True
            try:
                self._idle_slot(model)
                self.router("slots/0?action=erase", dict(model=model))
                self.resident.pop(model, None)
            except Exception:
                # Do not kill another request/model to clear a busy slot. A
                # subsequent save or prepare drains it before any cache reuse.
                pending_models.append(model)
        for identity in identities:
            for path in (self.directory / (identity + ".bin"), self.directory / (identity + ".json"),
                         *self.directory.glob(identity + ".*.pending")):
                if path.exists():
                    size = path.stat().st_size
                    path.unlink()
                    removed_bytes += size
                    if path.suffix == ".bin":
                        removed += 1
        return self._record(None, "delete", dict(status="pending" if pending_models else "deleted",
                            removedSnapshots=removed, removedBytes=removed_bytes, pendingModels=pending_models,
                            sessionIds=sorted(selected_sessions), sessionKeys=sorted(selected_keys)))

    def invalidate(self, model):
        self.resident.pop(model, None)

    def _identity(self, model, key):
        if key is None:
            return None
        if not isinstance(key, str) or not 1 <= len(key) <= 2048:
            raise ValueError("Invalid native session cache key")
        # Legacy hash compatibility: only the wire prefix changed. Reuse the exact
        # existing opaque snapshot filenames, including cached interrupted turns.
        key = self._canonical_key(key)
        return hashlib.sha256(json.dumps([model, key, self.fingerprint(model)],
                            sort_keys=True).encode()).hexdigest()

    def _record(self, model, operation, result):
        try:
            log = self.directory / "events.jsonl"
            if log.exists() and log.stat().st_size > 10 * 1024**2:
                log.replace(self.directory / "events.previous.jsonl")
            with log.open("a", encoding="utf-8") as stream:
                stream.write(json.dumps(dict(time=time.time(), model=model,
                                  operation=operation, **result)) + "\n")
        except OSError:
            pass  # Cache diagnostics must not break inference on a full disk.
        return result

    def _slots(self, model):
        return self.router("slots?model=" + urllib.parse.quote(model, safe=""))

    def _idle_slot(self, model, timeout=2):
        # Closing a streaming HTTP request is acknowledged before llama.cpp has
        # necessarily finished cancelling its slot. Briefly drain that race;
        # never erase the session identity or restore an older snapshot over it.
        deadline = time.monotonic() + timeout
        while True:
            slot = next((item for item in self._slots(model) if item.get("id") == 0), None)
            if slot is None:
                raise RuntimeError("Native slot is not available")
            if not slot.get("is_processing"):
                return slot
            if time.monotonic() >= deadline:
                raise RuntimeError("Native slot is not idle")
            time.sleep(0.05)

    def prepare(self, model, key, session_id=None, parent_session_id=None):
        try:
            identity = self._identity(model, key)
            self._remember(model, key, identity, session_id, parent_session_id)
            if identity is not None:
                self.save_progress.setdefault(identity, dict(time=time.monotonic(), new_tokens=0))
            current = self.resident.get(model)
            # A model can also disappear through an external router unload. A
            # fresh empty slot must restore even when its logical key is equal.
            slot = self._idle_slot(model)
            if current and current.get("deleted"):
                self.router("slots/0?action=erase", dict(model=model))
                self.resident.pop(model, None)
                current = None
                slot = self._idle_slot(model)
            tokens = slot.get("n_prompt_tokens", slot.get("n_past", slot.get("n_tokens", 0)))
            if (current and current["identity"] == identity and tokens > 0
                    and not current.get("restore_preserved_snapshot")):
                current["dirty"] = True
                return self._record(model, "prepare", dict(status="resident"))
            if current and tokens > 0 and not current.get("restore_preserved_snapshot"):
                self._save(model)
            self.resident.pop(model, None)
            result = dict(status="bypass" if identity is None else "miss")
            path = self.directory / (identity + ".bin") if identity else None
            if path and path.is_file():
                try:
                    response = self.router("slots/0?action=restore",
                                 dict(model=model, filename=path.name), timeout=120)
                    restored = response.get("n_restored")
                    if not isinstance(restored, int) or restored <= 0:
                        raise RuntimeError("Native server did not restore reusable session tokens")
                    path.touch()
                    result = dict(status="restored", restoredTokens=restored)
                except Exception as error:
                    # A partial/incompatible cache is disposable; saved messages
                    # still permit a correct cold prompt, with a visible reason.
                    result = dict(status="unavailable", detail=str(error)[:500])
            if identity:
                self.resident[model] = dict(identity=identity, key=key, dirty=True)
                if (result["status"] == "unavailable" and current and current["identity"] == identity
                        and current.get("restore_preserved_snapshot")):
                    # A transient failed restore must not let a following
                    # shutdown save overwrite the still-preserved disk branch.
                    self.resident[model].update(restore_preserved_snapshot=True,
                        interrupted_prompt_tokens=current.get("interrupted_prompt_tokens"))
            return self._record(model, "prepare", result)
        except Exception as error:
            # An unavailable/busy slot does not invalidate its logical owner.
            # A later call must be allowed to reuse the interrupted prefix once
            # cancellation has drained. Actual unloads call invalidate().
            return self._record(model, "prepare", dict(status="unavailable", detail=str(error)[:500]))

    def _prune(self, required=0, protected=None, replacing=None):
        protected = set(protected or ()) | {
            value["identity"] + ".bin" for value in self.resident.values()}
        files = sorted((path for path in self.directory.glob("*.bin")
                        if re.fullmatch(r"[0-9a-f]{64}\.bin", path.name)),
                       key=lambda path: path.stat().st_mtime)
        pending_bytes = 0
        for pending in self.directory.glob("*.pending"):
            if not re.fullmatch(r"[0-9a-f]{64}\.[0-9a-f]{32}\.pending", pending.name):
                continue
            try:
                if time.time() - pending.stat().st_mtime > 300:
                    pending.unlink()
                else:
                    pending_bytes += pending.stat().st_size
            except OSError:
                pending_bytes += pending.stat().st_size if pending.exists() else 0
        total = pending_bytes + sum(path.stat().st_size for path in files)
        # The old snapshot survives until atomic replacement. It counts toward
        # peak filesystem use, but not toward the eventual snapshot allowance.
        replaced_bytes = replacing.stat().st_size if replacing and replacing.exists() else 0
        final_growth = required - replaced_bytes
        for path in files:
            if total + final_growth <= self.maximum_bytes and shutil.disk_usage(self.directory).free >= self.free_reserve + required:
                break
            if path.name in protected:
                continue
            total -= path.stat().st_size
            path.unlink()
            path.with_suffix(".json").unlink(missing_ok=True)
        if total + final_growth > self.maximum_bytes or shutil.disk_usage(self.directory).free < self.free_reserve + required:
            raise OSError("Insufficient native session cache space; conversation remains saved")

    @staticmethod
    def _snapshot_token_count(path):
        """Read a saved token count without loading its token vector or KV state."""
        with path.open("rb") as stream:
            magic, version, packed_count = struct.unpack("<III", stream.read(12))
            if (magic != 0x67677371 or version not in NATIVE_SEQUENCE_TOKEN_LAYOUT_VERSIONS
                    or not 0 < packed_count <= (path.stat().st_size - 12) // 4):
                raise ValueError("Unsupported native session token vector")
            marker = struct.unpack("<i", stream.read(4))[0]
            if marker == -1:
                server_version, token_count = struct.unpack("<II", stream.read(8))
                if server_version != 1 or not 0 < token_count <= packed_count - 4:
                    raise ValueError("Unsupported native server token vector")
                return token_count
            if marker < 0:
                raise ValueError("Invalid native plain token vector")
            return packed_count

    @staticmethod
    def _snapshot_tail(path, prompt_tokens, saved_tokens):
        # llama_state_seq_save_file v3/v4 store the same token vector before
        # their version-specific KV bytes. server_tokens::serialize v1 wraps it. Read
        # only that bounded vector tail, never the potentially huge KV state.
        with path.open("rb") as stream:
            header = stream.read(12)
            if len(header) != 12:
                raise ValueError("Incomplete native session header")
            magic, version, packed_count = struct.unpack("<III", header)
            if magic != 0x67677371 or version not in NATIVE_SEQUENCE_TOKEN_LAYOUT_VERSIONS:
                raise ValueError("Unsupported native session format")
            if packed_count < 1 or packed_count > (path.stat().st_size - 12) // 4:
                raise ValueError("Invalid native session token bounds")
            marker_bytes = stream.read(4)
            marker, = struct.unpack("<i", marker_bytes)
            if marker == -1:
                if packed_count < 4:
                    raise ValueError("Incomplete native server token header")
                server_version, token_count = struct.unpack("<II", stream.read(8))
                if server_version != 1 or token_count > packed_count - 4:
                    raise ValueError("Unsupported native server token vector")
                offset = 24
            else:
                if marker < 0:
                    raise ValueError("Invalid native plain token vector")
                token_count, offset = packed_count, 12  # Older plain token list.
            if token_count != saved_tokens or not 0 < prompt_tokens <= token_count:
                raise ValueError("Native snapshot and prompt token counts differ")
            tail_count = token_count - prompt_tokens
            if tail_count > 32768:
                raise ValueError("Native generated tail exceeds the recovery limit")
            stream.seek(offset + prompt_tokens * 4)
            encoded = stream.read(tail_count * 4)
            if len(encoded) != tail_count * 4:
                raise ValueError("Incomplete native generated token tail")
            tokens = list(struct.unpack("<" + "i" * tail_count, encoded))
            if any(token < 0 for token in tokens):
                raise ValueError("Native generated tail contains non-text tokens")
            return tokens

    def _save(self, model, prompt_tokens=None):
        current = self.resident.get(model)
        if not current or not current["dirty"]:
            return dict(status="unchanged")
        if (self.lifecycle_error or current.get("deleted")
                or self._canonical_key(current["key"]) in self.deleted_keys):
            if current.get("deleted"):
                try:
                    self._idle_slot(model)
                    self.router("slots/0?action=erase", dict(model=model))
                    self.resident.pop(model, None)
                except Exception:
                    pass
            return dict(status="deleted" if not self.lifecycle_error else "unavailable")
        identity = current["identity"]
        target = self.directory / (identity + ".bin")
        temporary = self.directory / (identity + "." + uuid.uuid4().hex + ".pending")
        try:
            slot = self._idle_slot(model, timeout=INTERRUPTED_SAVE_IDLE_SECONDS if prompt_tokens is not None else 2)
            token_count = slot.get("n_prompt_tokens", slot.get("n_past", slot.get("n_tokens", 0)))
            if token_count <= 0:
                raise RuntimeError("Native slot has no reusable tokens")
            metadata = target.with_suffix(".json")
            previous = None
            if metadata.exists():
                try:
                    previous = json.loads(metadata.read_text(encoding="utf-8"))
                except (OSError, ValueError, TypeError):
                    pass
            previous_tokens = None
            if target.is_file():
                try:
                    # The file is authoritative if a former process stopped
                    # between atomic cache replacement and metadata update.
                    previous_tokens = self._snapshot_token_count(target)
                except (OSError, ValueError, struct.error):
                    # Future native formats can still use the byte-bound
                    # metadata written alongside their successful snapshot.
                    if (isinstance(previous, dict) and type(previous.get("tokens")) is int
                            and previous["tokens"] > 0 and previous.get("bytes") == target.stat().st_size):
                        previous_tokens = previous["tokens"]
            interrupted_prompt = current.get("interrupted_prompt_tokens")

            def preserve_interrupted_prefill(saved_tokens):
                # Cancelling a replacement prompt can leave only its first few
                # batches in the slot, despite restoring a complete old turn
                # before inference. Never replace that durable, larger prefix
                # with the incomplete replacement. Keep the guard for shutdown,
                # unload and owner-switch saves as well as this explicit Stop.
                if (type(interrupted_prompt) is not int or saved_tokens >= interrupted_prompt
                        or previous_tokens is None or previous_tokens <= saved_tokens):
                    return None
                # On direct resume the partial slot is less useful than the
                # preserved durable branch, even if slot metadata still claims
                # the complete requested prompt. Restore that branch once.
                current["restore_preserved_snapshot"] = True
                return self._record(model, "save", dict(status="unchanged", reason="interruptedPrefill",
                    retainedTokens=previous_tokens, residentTokens=saved_tokens,
                    interruptedPromptTokens=interrupted_prompt,
                    detail="Incomplete interrupted prompt did not replace the larger saved session cache"))

            preserved = preserve_interrupted_prefill(token_count)
            if preserved is not None:
                return preserved
            # Reserve before the native writer starts. Subsequent snapshots use
            # measured bytes/token; the first uses GGUF cache dimensions when
            # available and an explicit conservative fallback otherwise.
            previous_bytes = target.stat().st_size if target.exists() else 0
            estimate = self.estimate_bytes(model, token_count) if self.estimate_bytes else None
            estimate = estimate if isinstance(estimate, int) and estimate > 0 else token_count * 256 * 1024
            if previous is not None:
                try:
                    estimate = int(max(1, previous["bytes"]) / max(1, previous["tokens"]) * token_count * 1.1)
                except (ValueError, TypeError, KeyError):
                    pass
            estimate = max(previous_bytes, estimate)
            self._prune(required=estimate, protected=[target.name], replacing=target)
            response = self.router("slots/0?action=save",
                         dict(model=model, filename=temporary.name), timeout=120)
            if not temporary.is_file() or type(response.get("n_saved")) is not int or response["n_saved"] <= 0:
                raise RuntimeError("Native server did not save a usable session cache")
            preserved = preserve_interrupted_prefill(response["n_saved"])
            if preserved is not None:
                return preserved
            if temporary.stat().st_size > self.maximum_bytes:
                raise OSError("Native session cache exceeds its disk allowance")
            temporary.replace(target)
            metadata.write_text(json.dumps(dict(self.owners.get(identity, {}), bytes=target.stat().st_size,
                                               tokens=response["n_saved"])), encoding="utf-8")
            current["dirty"] = False
            self.save_progress[identity] = dict(time=time.monotonic(), new_tokens=0)
            self._prune(protected=[target.name])
            result = self._record(model, "save", dict(status="saved", savedTokens=response["n_saved"],
                                             bytes=response.get("n_written", target.stat().st_size)))
            if prompt_tokens is not None:
                try:
                    tokens = self._snapshot_tail(target, prompt_tokens, response["n_saved"])
                    decoded = self.router("detokenize", dict(model=model, tokens=tokens)) if tokens else {"content": ""}
                    tail = decoded.get("content")
                    if not isinstance(tail, str):
                        raise ValueError("Native generated tail was not decoded as text")
                    tail.encode("utf-8", errors="strict")
                    result["generatedTail"] = tail
                except Exception as error:
                    # A valid KV snapshot remains saved even if exact text
                    # recovery is unavailable. Never label the SSE tail exact.
                    result["generatedTailDetail"] = str(error)[:500]
            return result
        except Exception as error:
            return self._record(model, "save", dict(status="unavailable", detail=str(error)[:500]))
        finally:
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass  # A timed-out native write may still hold this unique file.

    def save(self, model, key, prompt_tokens=None, force=True, new_tokens=None):
        try:
            if prompt_tokens is not None and (type(prompt_tokens) is not int or prompt_tokens <= 0 or not key):
                raise ValueError("promptTokens requires a positive integer and a session key")
            current = self.resident.get(model)
            if current is None or (key is not None and current["identity"] != self._identity(model, key)):
                return dict(status="unchanged")
            if prompt_tokens is not None:
                current["interrupted_prompt_tokens"] = prompt_tokens
            elif type(new_tokens) is int and new_tokens > 0:
                # This counter is supplied only after a completed model turn.
                # A shorter completed prompt can be intentional compaction and
                # must replace the old cache. Flush/unload/fork calls carry no
                # such completion evidence and cannot remove the Stop guard.
                current.pop("interrupted_prompt_tokens", None)
                current.pop("restore_preserved_snapshot", None)
            progress = self.save_progress.get(current["identity"])
            if progress is None:
                progress = dict(time=time.monotonic(), new_tokens=0)
                self.save_progress[current["identity"]] = progress
            if type(new_tokens) is int and new_tokens > 0:
                progress["new_tokens"] += new_tokens
            if not force and prompt_tokens is None:
                # AND, not OR: neither tiny frequent turns nor a large single
                # turn should rewrite gigabytes of disposable KV on every call.
                if time.monotonic() - progress["time"] < 120 or progress["new_tokens"] < 8192:
                    return self._record(model, "save", dict(status="deferred", newTokens=progress["new_tokens"]))
            return self._save(model, prompt_tokens)
        except Exception as error:
            return self._record(model, "save", dict(status="unavailable", detail=str(error)[:500]))

    @staticmethod
    def _snapshot_tokens(path):
        """Read only the bounded text-token vector, never the KV tensors."""
        with path.open("rb") as stream:
            magic, version, packed_count = struct.unpack("<III", stream.read(12))
            if (magic != 0x67677371 or version not in NATIVE_SEQUENCE_TOKEN_LAYOUT_VERSIONS
                    or not 0 < packed_count <= 1_048_580):
                raise ValueError("Unsupported native session token vector")
            marker = struct.unpack("<i", stream.read(4))[0]
            if marker == -1:
                server_version, token_count = struct.unpack("<II", stream.read(8))
                if server_version != 1 or not 0 < token_count <= packed_count - 4:
                    raise ValueError("Unsupported native server token vector")
            else:
                token_count = packed_count
                stream.seek(12)
            encoded = stream.read(token_count * 4)
            if len(encoded) != token_count * 4:
                raise ValueError("Incomplete native session token vector")
            tokens = list(struct.unpack("<" + "i" * token_count, encoded))
            if any(token < 0 for token in tokens):
                raise ValueError("Canonical cache preparation requires text tokens")
            return tokens

    @staticmethod
    def _check_preparation_cancelled(cancelled):
        if cancelled and cancelled():
            raise PreparationCancelledError("Subagent preparation was cancelled by its caller")

    def _prefill_fork(self, source_model, target, temporary, prefill, cancelled=None, preparation=None):
        """Evaluate a canonical branch on the source's live rollback checkpoints.

        Legacy llama slot files contain only the final recurrent state; newer
        files also persist earlier rollback checkpoints. Prepare the exact child
        prefix on the live source so re-rendered tool calls cannot silently make
        it incompatible. The sampled terminal token is never decoded into KV;
        verify the serialized vector rather than assuming n_predict=0 means no
        sampling (current native servers still sample one token with that value).
        """
        body = dict(prefill, model=source_model, add_generation_prompt=False)
        self._check_preparation_cancelled(cancelled)
        rendered = self.router("apply-template", body, timeout=120).get("prompt")
        if not isinstance(rendered, str) or not rendered:
            raise ValueError("Native template did not return a text prefix")
        tokens = self.router("tokenize", dict(model=source_model, content=rendered,
            add_special=True, parse_special=True), timeout=120).get("tokens")
        if (not isinstance(tokens, list) or not 1 <= len(tokens) <= 1_048_576
                or any(type(token) is not int or token < 0 for token in tokens)):
            raise ValueError("Native tokenizer did not return a bounded text prefix")
        first_prompt = self.router("apply-template", dict(body, add_generation_prompt=True), timeout=120).get("prompt")
        if not isinstance(first_prompt, str) or not first_prompt:
            raise ValueError("Native template did not return the first inference prompt")
        first_tokens = self.router("tokenize", dict(model=source_model, content=first_prompt,
            add_special=True, parse_special=True), timeout=120).get("tokens")
        if (not isinstance(first_tokens, list) or not len(tokens) < len(first_tokens) <= 1_048_576
                or first_tokens[:len(tokens)] != tokens):
            raise ValueError("The first inference prompt does not append to the canonical KV prefix")
        estimate = self.estimate_bytes(source_model, len(tokens)) if self.estimate_bytes else None
        estimate = estimate if type(estimate) is int and estimate > 0 else len(tokens) * 256 * 1024
        self._prune(required=max(target.stat().st_size if target.exists() else 0, estimate), protected=[target.name])
        self._check_preparation_cancelled(cancelled)
        completion = dict(model=source_model, prompt=tokens, n_predict=1,
            cache_prompt=True, id_slot=0, temperature=0)
        if preparation is not None:
            preparation["started"] = True
        response = (self.prepare_completion(completion, cancelled) if self.prepare_completion else
            self.router("completion", dict(completion, stream=False), timeout=PREPARATION_MAXIMUM_SECONDS))
        self._check_preparation_cancelled(cancelled)
        # Raw /completion's tokens_cached is the final slot length, not reuse.
        # Native timings separately count reused and newly evaluated tokens.
        processed = response.get("timings", {}).get("prompt_n")
        cached = response.get("timings", {}).get("cache_n")
        sampled = response.get("tokens_predicted")
        if (type(cached) is not int or cached < 0 or type(processed) is not int
                or processed < 0 or cached + processed != len(tokens)):
            raise RuntimeError("Canonical branch preparation returned inconsistent token accounting")
        if type(sampled) is not int or sampled != 1:
            raise RuntimeError("Native preparation did not honor its single sampled token limit")
        self._idle_slot(source_model)
        self._check_preparation_cancelled(cancelled)
        saved = self.router("slots/0?action=save", dict(model=source_model, filename=temporary.name), timeout=120)
        if not temporary.is_file() or saved.get("n_saved") != len(tokens) or self._snapshot_tokens(temporary) != tokens:
            raise RuntimeError("Prepared snapshot differs from the canonical prefix or contains generated tokens")
        if temporary.stat().st_size > self.maximum_bytes:
            raise OSError("Native session cache exceeds its disk allowance")
        temporary.replace(target)
        target.with_suffix(".json").write_text(json.dumps(dict(bytes=target.stat().st_size,
            tokens=len(tokens))), encoding="utf-8")
        return dict(sourceCachedTokens=cached, sourceEvaluatedTokens=processed,
                    preparationMode="reused" if cached else "replayed", preparationSampledTokens=sampled,
                    evaluatedGeneratedTokens=0, preparedPromptTokens=len(tokens), firstInferencePromptTokens=len(first_tokens))

    def _cold_fork(self, source_model, model, key, identity, target, prefill, cancelled=None):
        """Prepare a canonical child before its parent has evaluated any tokens.

        The complete caller-supplied history remains the canonical input. A
        different resident owner is saved before reusing this physical slot;
        no child state is ever written under the unevaluated parent's key.
        """
        temporary = self.directory / (identity + "." + uuid.uuid4().hex + ".pending")
        preparation = {"started": False}
        previous = self.resident.get(model)
        previous_path = None
        try:
            if not isinstance(prefill, dict) or not isinstance(prefill.get("messages"), list):
                raise ValueError("Canonical cache preparation requires chat messages")
            self._check_preparation_cancelled(cancelled)
            slot = self._idle_slot(model)
            slot_tokens = slot.get("n_prompt_tokens", slot.get("n_past", slot.get("n_tokens", 0)))
            if previous is not None and slot_tokens > 0:
                previous_path = self.directory / (previous["identity"] + ".bin")
                if not previous_path.is_file():
                    previous["dirty"] = True
                saved = self._save(model)
                if saved["status"] not in ("saved", "unchanged") or not previous_path.is_file():
                    raise RuntimeError(saved.get("detail", "Previous target session could not be saved"))
                previous = dict(self.resident[model])
            # Retain the clean previous owner during prefill so disk pruning
            # also protects its rollback snapshot. It must not own later work.
            metrics = self._prefill_fork(model, target, temporary, prefill, cancelled, preparation)
            self._check_preparation_cancelled(cancelled)
            self.resident[model] = dict(identity=identity, key=key, dirty=False)
            self.save_progress[identity] = dict(time=time.monotonic(), new_tokens=0)
            return self._record(model, "fork", dict(status="forked", sourceModel=source_model,
                restoredTokens=metrics["preparedPromptTokens"], **dict(metrics,
                    preparationCachedTokens=metrics["sourceCachedTokens"], sourceCachedTokens=0,
                    preparationMode="coldCanonical")))
        except Exception as error:
            detail = str(error)
            if preparation["started"]:
                # Cancellation closes its inference stream before this method
                # drains it. Restore the actual previous owner, never an empty
                # parent key or partially generated child prefix.
                self.resident.pop(model, None)
                try:
                    self._idle_slot(model, timeout=INTERRUPTED_SAVE_IDLE_SECONDS)
                    if previous_path is not None and previous_path.is_file():
                        restored = self.router("slots/0?action=restore",
                            dict(model=model, filename=previous_path.name), timeout=120)
                        if restored.get("n_restored") != len(self._snapshot_tokens(previous_path)):
                            raise RuntimeError("Previous target snapshot was not restored completely")
                        self.resident[model] = previous
                    else:
                        self.router("slots/0?action=erase", dict(model=model))
                except Exception as restore_error:
                    self.resident.pop(model, None)
                    detail += f"; previous target cache recovery required: {restore_error}"
            for failed_path in (target, target.with_suffix(".json")):
                try:
                    failed_path.unlink(missing_ok=True)
                except OSError as cleanup_error:
                    detail += f"; failed branch cleanup: {cleanup_error}"
            reason = ("subagent.preparation_timeout" if isinstance(error, PreparationTimeoutError) else
                      "subagent.preparation_cancelled" if isinstance(error, PreparationCancelledError) else
                      "subagent.preparation_failed")
            return self._record(model, "fork", dict(status="unavailable", reason=reason, detail=detail[:500]))
        finally:
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass  # An interrupted native writer can briefly retain it.

    def fork(self, source_model, source_key, model, key, prefill=None, session_id=None, parent_session_id=None, cancelled=None):
        """Copy a compatible prefix into an independent, atomic child snapshot.

        CUDA placement is not serialized model/KV geometry. Every other
        fingerprint component must match. Native restore and prompt comparison
        remain authoritative; a failed fork falls back to the saved messages.
        The caller owns the manager lock and both instance turn leases.
        """
        temporary = None
        preparation = {"started": False}
        new_target = None
        try:
            self._check_preparation_cancelled(cancelled)
            if not source_key or not key:
                raise ValueError("Cache fork requires source and child session keys")
            if source_model.removesuffix("@subagent") != model.removesuffix("@subagent"):
                raise ValueError("Cache fork requires the exact same base model")
            source_fingerprint = dict(self.fingerprint(source_model))
            target_fingerprint = dict(self.fingerprint(model))
            source_fingerprint.pop("placement", None)
            target_fingerprint.pop("placement", None)
            if source_fingerprint != target_fingerprint:
                raise ValueError("Native session cache fingerprints are incompatible")
            source_identity = self._identity(source_model, source_key)
            target_identity = self._identity(model, key)
            self._remember(source_model, source_key, source_identity, session_id=parent_session_id)
            self._remember(model, key, target_identity, session_id, parent_session_id, parent_key=source_key)
            if source_identity == target_identity:
                raise ValueError("Cache fork requires an independent child snapshot")
            source = self.directory / (source_identity + ".bin")
            target = self.directory / (target_identity + ".bin")
            # Restart/retry must restore the child's own progressed state.
            if target.is_file():
                return self.prepare(model, key)
            new_target = target
            source_owner = self.resident.get(source_model)
            if prefill is not None and not source.is_file():
                source_slot = (self._idle_slot(source_model)
                               if source_owner is not None and source_owner["identity"] == source_identity else {})
                source_has_tokens = source_slot.get("n_prompt_tokens", source_slot.get("n_past", source_slot.get("n_tokens", 0))) > 0
                if not source_has_tokens:
                    return self._cold_fork(source_model, model, key, target_identity, target, prefill,
                                           cancelled)
            if source_owner is None or source_owner["identity"] != source_identity:
                if source_owner is not None:
                    # prepare normally saves the previous owner, but cache
                    # failures are best-effort there. A fork must not proceed
                    # past a failed save and overwrite another session's slot.
                    previous = self._save(source_model)
                    if previous["status"] not in ("saved", "unchanged"):
                        raise RuntimeError(previous.get("detail", "Previous source session could not be saved"))
                prepared = self.prepare(source_model, source_key, session_id=parent_session_id)
                source_owner = self.resident.get(source_model)
                if (prepared["status"] not in ("restored", "resident") or source_owner is None
                        or source_owner["identity"] != source_identity):
                    if source_owner and source_owner["identity"] == source_identity:
                        self.resident.pop(source_model, None)
                    raise RuntimeError(prepared.get("detail", "Parent source session could not be prepared"))
            saved = self.save(source_model, source_key)
            if saved["status"] not in ("saved", "unchanged") or not source.is_file():
                raise RuntimeError(saved.get("detail", "Parent KV snapshot is not available"))
            self._prune(required=source.stat().st_size, protected=[source.name, target.name])
            temporary = self.directory / (target_identity + "." + uuid.uuid4().hex + ".pending")
            metrics = {}
            if prefill is not None:
                if not isinstance(prefill, dict) or not isinstance(prefill.get("messages"), list):
                    raise ValueError("Canonical cache preparation requires chat messages")
                # The source's durable snapshot stays unchanged. Keep its live
                # checkpoints for the main agent's next turn; restoring the old
                # file here would erase those checkpoints again.
                metrics = self._prefill_fork(source_model, target, temporary, prefill, cancelled, preparation)
            else:
                shutil.copyfile(source, temporary)
                temporary.replace(target)
                source_metadata = source.with_suffix(".json")
                if source_metadata.exists():
                    metadata = json.loads(source_metadata.read_text(encoding="utf-8"))
                    target.with_suffix(".json").write_text(json.dumps(dict(metadata,
                        **self.owners[target_identity])), encoding="utf-8")
            self._check_preparation_cancelled(cancelled)
            result = self.prepare(model, key)
            if result["status"] not in ("restored", "resident"):
                raise RuntimeError(result.get("detail", "Canonical child cache could not be restored"))
            if result["status"] == "restored":
                result = dict(result, status="forked", sourceModel=source_model, **metrics)
            return self._record(model, "fork", result)
        except Exception as error:
            detail = str(error)
            if preparation["started"]:
                # The transport has closed its SSE stream before returning an
                # error. Retain the manager/turn lease until cancellation drains,
                # then recover the exact durable parent, never its branch state.
                try:
                    self._idle_slot(source_model, timeout=INTERRUPTED_SAVE_IDLE_SECONDS)
                    restored = self.router("slots/0?action=restore",
                        dict(model=source_model, filename=source.name), timeout=120)
                    if restored.get("n_restored") != len(self._snapshot_tokens(source)):
                        raise RuntimeError("Parent snapshot was not restored completely")
                    self.resident[source_model] = dict(identity=source_identity, key=source_key, dirty=False)
                except Exception as restore_error:
                    # An unowned slot cannot be saved over the parent. Its next
                    # prepare must wait for idle and restore the durable file.
                    self.resident.pop(source_model, None)
                    detail += f"; parent cache recovery required: {restore_error}"
            if new_target is not None:
                child_owner = self.resident.get(model)
                if child_owner and child_owner["identity"] == target_identity:
                    self.resident.pop(model, None)
                for failed_path in (new_target, new_target.with_suffix(".json")):
                    try:
                        failed_path.unlink(missing_ok=True)
                    except OSError as cleanup_error:
                        detail += f"; failed branch cleanup: {cleanup_error}"
            reason = ("subagent.preparation_timeout" if isinstance(error, PreparationTimeoutError) else
                      "subagent.preparation_cancelled" if isinstance(error, PreparationCancelledError) else
                      "subagent.preparation_failed")
            return self._record(model, "fork", dict(status="unavailable", reason=reason, detail=detail[:500]))
        finally:
            if temporary:
                try:
                    temporary.unlink(missing_ok=True)
                except OSError:
                    pass  # A timed-out native save may briefly retain its file.

    def reset_epoch(self, epoch, loaded_models=None):
        """Discard optimization files while retaining real session tombstones/history."""
        if not isinstance(epoch, str) or not re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", epoch):
            raise ValueError("Invalid native cache epoch")
        target = self.directory / "epoch.json"
        if target.is_file() and json.loads(target.read_text(encoding="utf-8")).get("epoch") == epoch:
            return dict(status="unchanged", epoch=epoch)
        models = set(self.resident) if loaded_models is None else set(loaded_models)
        for model in models:
            if any(slot.get("is_processing") for slot in self._slots(model)):
                return dict(status="busy", epoch=epoch)
        for model in models:
            self.router("slots/0?action=erase", dict(model=model))
        removed = 0
        for path in self.directory.iterdir():
            if re.fullmatch(r"[0-9a-f]{64}\.(bin|json)", path.name):
                path.unlink()
                removed += 1
        self.resident.clear()
        self.owners.clear()
        self.save_progress.clear()
        self._write_ownership()
        temporary = target.with_suffix(".pending")
        temporary.write_text(json.dumps(dict(epoch=epoch)), encoding="utf-8")
        temporary.replace(target)
        return self._record(None, "reset-epoch", dict(status="reset", epoch=epoch, removedFiles=removed))

    def save_all(self):
        for model in list(self.resident):
            self._save(model)
