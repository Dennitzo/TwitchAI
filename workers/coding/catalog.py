"""Discover complete local GGUF models and supervise the native llama.cpp router.

Model files are only read. Generated presets and logs use a separate state directory.
No network access, downloads, model conversion, or Unsloth code is used here.
"""
from __future__ import annotations

import argparse
import ctypes
import csv
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import select
import signal
import socket
import struct
import subprocess
import sys
import threading
import time
import urllib.request
import urllib.error
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from session_cache import NativeSessionCache, PreparationCancelledError, PreparationProgress, PreparationTimeoutError

SHARD = re.compile(r"^(.*)-([0-9]{5})-of-([0-9]{5})\.gguf$", re.IGNORECASE)
NON_TEXT = re.compile(r"(?:^|[-_./])(?:mmproj|ggml-vocab|embedding|embed|asr|whisper|clip|vae|diffusion|mtp)(?:[-_./]|$)", re.IGNORECASE)
NON_TEXT_ARCHITECTURES = ("bert", "nomic-bert", "jina-bert", "embedding", "t5", "clip", "mclip", "wavtokenizer", "whisper", "dflash")
EMBEDDING_ARCHITECTURES = ("bert", "nomic-bert", "jina-bert", "embedding")
_METADATA_CACHE = {}
SCALAR_SIZES = {0: 1, 1: 1, 2: 2, 3: 2, 4: 4, 5: 4, 6: 4, 7: 1, 10: 8, 11: 8, 12: 8}


def read_exact(stream, length):
    data = stream.read(length)
    if len(data) != length:
        raise ValueError("Truncated GGUF metadata")
    return data


def u64(stream):
    return struct.unpack("<Q", read_exact(stream, 8))[0]


def read_string(stream):
    size = u64(stream)
    if size > 16 * 1024 * 1024:
        raise ValueError("GGUF metadata string exceeds bound")
    return read_exact(stream, size).decode("utf-8", errors="replace")


def skip_bytes(stream, length):
    # BufferedReader.seek(…, SEEK_CUR) can query the underlying remote file
    # pointer for every token string. Consume small metadata values from the
    # read buffer instead, avoiding hundreds of thousands of SMB seeks.
    if length <= 64 * 1024:
        read_exact(stream, length)
    else:
        stream.seek(length, 1)


def skip_value(stream, kind):
    if kind == 8:
        length = u64(stream)
        if length > 16 * 1024 * 1024:
            raise ValueError("GGUF metadata string exceeds bound")
        skip_bytes(stream, length)
    elif kind == 9:
        element = struct.unpack("<I", read_exact(stream, 4))[0]
        count = u64(stream)
        if count > 10_000_000 or element == 9:
            raise ValueError("Invalid GGUF array")
        if element in SCALAR_SIZES:
            skip_bytes(stream, SCALAR_SIZES[element] * count)
        else:
            for _ in range(count):
                skip_value(stream, element)
    elif kind in SCALAR_SIZES:
        skip_bytes(stream, SCALAR_SIZES[kind])
    else:
        raise ValueError("Unknown GGUF value kind")


def model_metadata(path, allow_metadata_only=False):
    stat = path.stat()
    if stat.st_size < (24 if allow_metadata_only else 1024 * 1024):
        return None
    cache_key = (str(path), stat.st_size, stat.st_mtime_ns, allow_metadata_only)
    if cache_key in _METADATA_CACHE:
        return _METADATA_CACHE[cache_key]
    with path.open("rb", buffering=64 * 1024) as stream:
        magic, version, tensors, count = struct.unpack("<4sIQQ", read_exact(stream, 24))
        if magic != b"GGUF" or version not in (2, 3) or (tensors == 0 and not allow_metadata_only) or count > 100_000:
            return None
        metadata = {"general.type": "model", "tensor_count": tensors}
        for _ in range(count):
            key = read_string(stream)
            kind = struct.unpack("<I", read_exact(stream, 4))[0]
            if key in ("general.architecture", "general.type", "general.name", "tokenizer.chat_template") and kind == 8:
                metadata[key] = read_string(stream)
            elif key.endswith((".context_length", ".pooling_type", ".block_count", ".embedding_length",
                               ".attention.head_count", ".attention.head_count_kv", ".attention.key_length",
                               ".attention.value_length", ".attention.indexer.key_length", ".ssm.state_size", ".ssm.inner_size",
                               ".ssm.group_count", ".ssm.conv_kernel", ".full_attention_interval",
                               ".nextn_predict_layers")) and kind == 4:
                metadata[key] = struct.unpack("<I", read_exact(stream, 4))[0]
            else:
                skip_value(stream, kind)
        # A zero-tensor first shard can end exactly at its metadata boundary.
        # Seeking beyond EOF still indicates truncated metadata.
        end = stream.tell()
        if end > stat.st_size or (end == stat.st_size and not (allow_metadata_only and tensors == 0)):
            return None
        if len(_METADATA_CACHE) > 512:
            _METADATA_CACHE.clear()
        _METADATA_CACHE[cache_key] = metadata
        return metadata


def is_text_model(path, allow_metadata_only=False):
    if NON_TEXT.search(path.name):
        return False
    metadata = model_metadata(path, allow_metadata_only)
    if metadata is None:
        return False
    architecture = metadata.get("general.architecture", "").lower()
    return bool(architecture) and metadata["general.type"] == "model" and not NON_TEXT.search(architecture) and not architecture.startswith(NON_TEXT_ARCHITECTURES)


def is_tensor_shard(path):
    # Unsloth may place tokenizer/architecture metadata in a zero-tensor first
    # shard. Subsequent shards carry tensors and only split metadata.
    if path.stat().st_size < 1024 * 1024:
        return False
    with path.open("rb") as stream:
        magic, version, tensors, count = struct.unpack("<4sIQQ", read_exact(stream, 24))
        return magic == b"GGUF" and version in (2, 3) and tensors > 0 and count <= 100_000


def matching_projector(path, root):
    # A quantization subfolder may use the projector beside it in the same HF
    # snapshot. Never borrow a projector from another snapshot or model repo.
    stop = path.parent
    for parent in path.parents:
        if parent.parent.name == "snapshots":
            stop = parent
            break
        if parent == root:
            break
    current = path.parent
    while current.is_relative_to(root):
        candidates = []
        for projector in current.glob("mmproj*.gguf"):
            if projector.is_file() and projector.resolve().is_relative_to(root):
                metadata = model_metadata(projector)
                if metadata and metadata.get("general.architecture") in ("clip", "mclip"):
                    candidates.append(projector)
        if candidates:
            return candidates[0] if len(candidates) == 1 else None
        if current == stop:
            break
        current = current.parent
    return None


def _jinja_thinking_controls(template):
    """Read boolean inputs from executable Jinja conditions, not prose.

    Skip comments/raw sections and consume quoted values before their closing
    delimiter. An example like ``{{ '{% if thinking %}' }}`` is not a toggle;
    neither are attributes of message data.
    """
    controls = set()
    position = 0
    opening = re.compile(r"\{([%#{])")
    quoted = re.compile(r"'(?:\\.|[^'\\])*'|\"(?:\\.|[^\"\\])*\"", re.DOTALL)
    while match := opening.search(template, position):
        kind = match.group(1)
        end_token = {"%": "%}", "#": "#}", "{": "}}"}[kind]
        start = match.end()
        if kind == "#":
            end = template.find(end_token, start)
        else:
            cursor = start
            end = -1
            while cursor < len(template):
                if template[cursor] in "'\"":
                    literal = quoted.match(template, cursor)
                    if literal is None:
                        break  # Malformed template: do not infer capabilities.
                    cursor = literal.end()
                elif template.startswith(end_token, cursor):
                    end = cursor
                    break
                else:
                    cursor += 1
        if end < 0:
            break
        position = end + len(end_token)
        if kind != "%":
            continue
        code = template[start:end].strip().strip("-+").strip()
        if code == "raw":
            raw_end = re.search(r"\{%[-+]?\s*endraw\s*[-+]?%\}", template[position:])
            if raw_end is None:
                break
            position += raw_end.end()
            continue
        condition = re.match(r"(?:if|elif)\s+(.+)", code, re.DOTALL)
        if condition is None:
            continue
        expression = quoted.sub(" ", condition.group(1))
        tokens = re.findall(r"[A-Za-z_][A-Za-z0-9_]*|[^\s]", expression)
        for index, token in enumerate(tokens):
            if token not in ("thinking", "enable_thinking"):
                continue
            previous = tokens[index - 1] if index else None
            following = tokens[index + 1] if index + 1 < len(tokens) else None
            if previous != "." and following not in (".", "("):
                controls.add(token)
    return controls


def reasoning_profile(metadata):
    """Only select effort values supported by this model's actual template."""
    template = metadata.get("tokenizer.chat_template", "")
    architecture = metadata.get("general.architecture", "")
    thinking_controls = _jinja_thinking_controls(template)
    levels = []
    for match in re.finditer(r"(?:resolved_)?reasoning_(?:effort|strength)\s+(?:not\s+)?in\s*[\[(]([^\])]{1,512})[\])]", template):
        levels.extend(re.findall(r"['\"]([a-z][a-z0-9_-]{0,31})['\"]", match.group(1)))
    levels = list(dict.fromkeys(levels))
    if not levels and (architecture == "gpt-oss" or "gpt-oss" in metadata.get("general.name", "").lower()) and "reasoning_effort" in template:
        levels = ["low", "medium", "high"]
    if levels:
        if "enable_thinking" in thinking_controls and "none" not in levels:
            levels.insert(0, "none")
        highest = next((level for level in ("max", "ultra", "xhigh", "high", "medium", "low", "minimal") if level in levels), None)
        return {"enabled": True, "effort": highest, "budget": -1, "levels": levels, "mode": "llama-native"}
    if "enable_thinking" in thinking_controls:
        return {"enabled": True, "effort": None, "budget": -1, "levels": ["none", "on"], "mode": "llama-toggle"}
    if "thinking" in thinking_controls:
        return {"enabled": True, "effort": None, "budget": -1, "levels": ["none", "on"], "mode": "llama-thinking-toggle"}
    if "<think>" in template or "<|channel>analysis" in template:
        return {"enabled": None, "effort": None, "budget": -1, "levels": ["on"], "mode": "llama-fixed"}
    return {"enabled": None, "effort": None, "budget": -1, "levels": [], "mode": "automatic"}


def resolve_model_catalog_root(root):
    """Accept either a GGUF/HF hub directory or its Hugging Face cache parent.

    Keep IDs relative to the actual hub, so selecting ``Huggingface`` instead
    of ``Huggingface/hub`` does not rename the same installed models. This also
    avoids walking datasets/xet caches on a network share.
    """
    root = Path(root).expanduser().resolve(strict=True)
    if not root.is_dir():
        raise NotADirectoryError(f"AI model directory is not a directory: {root}")
    if root.name.lower() != "hub":
        hub = root / "hub"
        try:
            hub.stat()
        except (FileNotFoundError, NotADirectoryError):
            return root
        if hub.is_dir() and any(child.name.startswith("models--") and child.is_dir() for child in hub.iterdir()):
            return hub.resolve(strict=True)
    return root


def discover_models(root):
    root = resolve_model_catalog_root(root)
    found = []
    for path in sorted(root.rglob("*.gguf")):
        if not path.resolve().is_relative_to(Path(r"C:\Users\AMD\.cache\huggingface").resolve()):
            continue
        try:
            if re.match(r"mtp(?:[-_.]|$)", path.name, re.IGNORECASE):
                # Prediction-only companions may contain the main tokenizer
                # metadata, but cannot act as independently selected chats.
                continue
            # HF symlinks may target blobs within this mount, never other host paths.
            if not path.resolve().is_relative_to(root):
                continue
            match = SHARD.match(path.name)
            if match:
                stem, index, total = match.groups()
                if int(index) != 1 or not 1 <= int(total) <= 128:
                    continue
                shards = [path.with_name(f"{stem}-{part:05d}-of-{int(total):05d}.gguf") for part in range(1, int(total) + 1)]
                if not all(part.is_file() and part.resolve().is_relative_to(root) for part in shards):
                    continue
                metadata = model_metadata(shards[0], allow_metadata_only=True)
                if any(not part.resolve().is_relative_to(Path(r"C:\Users\AMD\.cache\huggingface").resolve()) for part in shards):
                    continue
                if not metadata or (metadata["tensor_count"] == 0 and len(shards) == 1) or not all(is_tensor_shard(part) for part in shards[1:]):
                    continue
                name = stem
            else:
                metadata = model_metadata(path)
                if not metadata:
                    continue
                name = path.stem
            safe_name = re.sub(r"[^A-Za-z0-9_.-]", "_", name)[:160]
            digest = hashlib.sha256(path.relative_to(root).as_posix().encode()).hexdigest()[:12]
            architecture = metadata.get("general.architecture", "").lower()
            if not architecture or metadata["general.type"] != "model":
                continue
            context = metadata.get(architecture + ".context_length")
            # Do not invent a supported context for incomplete/unknown metadata.
            if not isinstance(context, int) or not 1 <= context <= 2_147_483_647:
                continue
            is_embedding = architecture.startswith(EMBEDDING_ARCHITECTURES) or re.search(r"(?:^|[-_])(?:embedding|embed)(?:[-_]|$)", name, re.IGNORECASE)
            if is_embedding:
                pooling = {1: "mean", 2: "cls", 3: "last"}.get(metadata.get(architecture + ".pooling_type"), "cls" if architecture == "bert" else "last")
                found.append({"id": f"embedding/{safe_name}~{digest}", "path": path, "role": "embedding",
                              "context": context, "contextPolicy": "model-maximum", "pooling": pooling})
                continue
            if NON_TEXT.search(path.name) or NON_TEXT.search(architecture) or architecture.startswith(NON_TEXT_ARCHITECTURES):
                continue
            reasoning = reasoning_profile(metadata)
            found.append({"id": f"coding/{safe_name}~{digest}", "path": path, "role": "general", "context": context,
                          "contextPolicy": "max-fit", "reasoning": reasoning})
            projector = matching_projector(path, root)
            if projector and not projector.resolve().is_relative_to(Path(r"C:\Users\AMD\.cache\huggingface").resolve()):
                projector = None
            if projector:
                # A matching projector makes this exact text/tool model vision
                # capable. Keep media requests on the same agent GPU/instance
                # instead of switching its weights to a second model role.
                found[-1]["projector"] = projector
            if projector:
                found.append({"id": f"vision/{safe_name}~{digest}", "path": path, "role": "vision",
                              "context": context, "contextPolicy": "max-fit", "reasoning": reasoning, "projector": projector})
        except (OSError, ValueError, struct.error):
            continue
    return found


def discover(root):
    """Compatibility view for callers needing only Coding text models."""
    return [(model["id"], model["path"]) for model in discover_models(root) if model["role"] == "general"]


def is_network_model_path(path):
    value = str(path).replace("\\", "/")
    return value.lower().startswith("//?/unc/") or (value.startswith("//") and not value.startswith("//?/"))


def model_load_deadline(model):
    # NAS transfers can legitimately exceed a local allocation trial by many
    # minutes. Native failures and the owning supervisor's explicit stop remain
    # authoritative; storage speed is not a model error.
    return float("inf") if is_network_model_path(model["path"]) else time.monotonic() + 280


DEEPSEEK_HISTORY_BY_FLAG = "{%- if keep_reasoning and thinking -%}"
DEEPSEEK_HISTORY_BY_CONTENT = ("{%- if keep_reasoning and message['reasoning_content'] is defined "
                               "and message['reasoning_content'] -%}")


def native_history_template(template):
    """Preserve exact DeepSeek history without changing generation instructions.

    All other templates use the model's original reasoning defaults.
    """
    deepseek_history = "{%- set keep_reasoning = tp.has or (loop.index0 > last_user_idx.value) -%}"
    if "dsml_token" in template and deepseek_history in template:
        # The stock DeepSeek-V4 template drops old reasoning without tools.
        # JSON history can therefore be identical while its rendered prompt
        # changes. In-memory rollback checkpoints mask this until /slots/restore
        # (which restores final KV+tokens, not those checkpoints). Keep the
        # provider's saved reasoning so the next user turn appends to the exact
        # generated prefix, including in General chat after a process restart.
        adapted = template.replace(deepseek_history, "{%- set keep_reasoning = true -%}")
        # Historical assistant turns must replay exactly what was generated. The
        # stock template renders them by the CURRENT thinking flag, so switching
        # reasoning between runs of one session ("none" -> "on") rewrites every
        # earlier turn and discards the whole native KV prefix (observed: 0 of
        # 158k cached tokens after Stop + new prompt). Render by the stored
        # reasoning of each turn instead; only the generation prompt follows the
        # current flag.
        return adapted.replace(DEEPSEEK_HISTORY_BY_FLAG, DEEPSEEK_HISTORY_BY_CONTENT)
    return None


def multi_gpu_split_mode():
    """llama split mode for models that need both GPUs; TWITCHAI_NATIVE_MULTI_GPU_SPLIT overrides."""
    value = os.environ.get("TWITCHAI_NATIVE_MULTI_GPU_SPLIT", "layer").strip().lower()
    return value if value in ("layer", "row", "tensor") else "layer"


def vision_batch_policy(model, policy):
    """Keep every non-causal image decode inside the physical microbatch.

    llama/mtmd passes up to n_batch image tokens to llama_decode, which aborts
    for non-causal attention when n_ubatch is smaller. A projector can use this
    path even on a coding/general alias. Lower the logical batch instead of
    increasing the measured GPU allocation; text-only and embedding models
    retain their existing policies. Defaults are those of the native runtime.
    """
    if not model.get("projector") or model["role"] == "embedding":
        return policy
    safe = dict(policy)
    size = min(policy.get("batchSize", 2048), policy.get("ubatchSize", 512))
    safe.update(batchSize=size, ubatchSize=size)
    return safe


def limit_context_policy(model, policy):
    # Each model advertises its own GGUF maximum. Keep a resolved smaller
    # policy (including a resident legacy load) unchanged; new loads may use
    # the full model window and llama's actual fitted n_ctx is authoritative.
    maximum = min(model["context"], policy.get("contextMaximum") or model["context"])
    context = min(policy.get("context") or maximum, maximum)
    return dict(policy, context=context, contextMaximum=maximum)


def model_runtime_policy(model, placement=None, *, split_mode="layer", fit_target="2048", gpu_layers="auto"):
    """Resolve once before a load; active KV must not follow later environment changes."""
    embedding = model["role"] == "embedding"
    policy = dict(split="none" if placement else "layer" if embedding else split_mode,
                  fit="off" if placement else "on", fitTargetMiB=int(fit_target),
                  gpuLayers="999" if placement else str(gpu_layers),
                  context=model["context"] if placement or embedding else None,
                  contextMaximum=model["context"], contextPolicy="model-maximum" if embedding or placement else "max-fit",
                  cacheK="f16" if embedding else "q8_0", cacheV="f16" if embedding else "q8_0",
                  flashAttention="off" if embedding else "on")
    if policy["split"] == "tensor":
        # llama's automatic fitting is unsupported with tensor parallelism.
        # Never substitute an invented smaller context to make it start.
        value = os.environ.get("TWITCHAI_NATIVE_MULTI_GPU_CONTEXT_LIMIT", "").strip()
        if not re.fullmatch(r"[0-9]{1,10}", value) or not 4096 <= int(value) <= 2_147_483_647:
            raise ValueError("TWITCHAI_NATIVE_MULTI_GPU_SPLIT=tensor requires an explicit "
                             "TWITCHAI_NATIVE_MULTI_GPU_CONTEXT_LIMIT between 4096 and 2147483647 tokens. "
                             "Choose a measured VRAM-safe limit, or use split mode layer for automatic fitting.")
        policy.update(fit="off", gpuLayers="999", context=min(model["context"], int(value)),
                      contextPolicy="explicit-limit")
    return vision_batch_policy(model, limit_context_policy(model, policy))


def write_presets(root, target, placements=None, managed_gpu=False, fit_target="2048", gpu_layers="auto",
                  resolved_policies=None, agent_instances=False):
    models = discover_models(root)
    if agent_instances:
        models += [dict(model, id=model["id"] + "@subagent", baseModel=model["id"], instance="subagent")
                   for model in list(models) if model["role"] != "embedding"]
    session_directory = Path(target).resolve().parent / "session-cache"
    session_directory.mkdir(parents=True, exist_ok=True)
    # Session persistence addresses slot 0. Omitting this internal limit lets
    # llama default to four slots and send chat turns to an unsaved LRU slot.
    # This is a single execution slot, not a user-facing parallel-agent option.
    lines = ["version = 1", "", "[*]", "load-on-startup = false", "stop-timeout = 10", "sleep-idle-seconds = -1", "parallel = 1",
             f"slot-save-path = {session_directory.as_posix()}/",
             "fit = on", f"fit-target = {fit_target}", "fit-ctx = 4096", f"n-gpu-layers = {gpu_layers}", ""]
    for model in models:
        model_id, path = model["id"], model["path"]
        if any(char in str(path) for char in "\r\n"):
            continue
        tags = f"twitchai-context-train:{model['context']},twitchai-context-policy:{model['contextPolicy']}"
        if is_network_model_path(path):
            tags += ",twitchai-model-storage:network"
        if model.get("projector"):
            tags += ",twitchai-vision:projector"
        if managed_gpu:
            tags += ",twitchai-gpu-policy:single-preferred-v1"
        if model.get("instance"):
            tags += f",twitchai-agent-instance:{model['instance']},twitchai-base-model:{model['baseModel']}"
        if model["role"] != "embedding":
            profile = model["reasoning"]
            tags += f",twitchai-reasoning-mode:{profile['mode']},twitchai-reasoning-levels:{'|'.join(profile['levels'])}"
            default = profile["effort"] or ("on" if "on" in profile["levels"] else "none" if profile["levels"] == ["none"] else "auto")
            tags += f",twitchai-reasoning-default:{default}"
        lines += [f"[{model_id}]", f"model = {path.as_posix()}", f"tags = {tags}"]
        if model["role"] != "embedding":
            metadata = model_metadata(path, allow_metadata_only=True) or {}
            localized = native_history_template(metadata.get("tokenizer.chat_template", ""))
            if localized:
                template_dir = Path(target).parent / "templates"
                template_dir.mkdir(parents=True, exist_ok=True)
                template_path = template_dir / (hashlib.sha256(localized.encode()).hexdigest()[:24] + ".jinja")
                if not template_path.exists():
                    template_path.write_text(localized, encoding="utf-8")
                lines += [f"chat-template-file = {template_path.as_posix()}"]
        placement = (placements or {}).get(model_id)
        policy = (resolved_policies or {}).get(model_id)
        if policy is None:
            # Managed catalogs use a neutral unloaded preset. The environment
            # override is validated/applied only at the next actual load.
            policy = model_runtime_policy(model, placement, split_mode="layer" if managed_gpu else multi_gpu_split_mode(),
                                          fit_target=fit_target, gpu_layers=gpu_layers)
        policy = vision_batch_policy(model, limit_context_policy(model, policy))
        if placement:
            lines += [f"device = {placement}", "main-gpu = 0"]
            if model.get("projector"):
                lines += [f"mmproj-device = {placement}"]
        elif policy.get("profileId"):
            # Match the measured two-device allocation exactly; do not let
            # free-memory-dependent defaults change the validated split ratio.
            lines += [f"device = {policy['devices']}", f"tensor-split = {policy['tensorSplit']}"]
        lines += [f"split-mode = {policy['split']}", f"fit = {policy['fit']}", f"n-gpu-layers = {policy['gpuLayers']}"]
        if policy["context"] is not None:
            lines += [f"ctx-size = {policy['context']}"]
        if policy.get("fitContext"):
            lines += [f"fit-ctx = {policy['fitContext']}"]
        if policy.get("mmprojDevice") and model.get("projector") and not placement:
            lines += [f"mmproj-device = {policy['mmprojDevice']}"]
        for setting, argument in (("batchSize", "batch-size"), ("ubatchSize", "ubatch-size")):
            if setting in policy:
                lines += [f"{argument} = {policy[setting]}"]
        if model["role"] == "embedding":
            lines += ["embedding = true", f"pooling = {model['pooling']}", f"batch-size = {model['context']}",
                      f"ubatch-size = {model['context']}", "cache-type-k = f16", "cache-type-v = f16", "flash-attn = off"]
        else:
            # An explicit product ceiling applies to every current/future model,
            # including supplied measured policies and delegated model instances.
            # Training metadata stays unchanged; it cannot expand the usable window.
            lines += ["cache-type-k = q8_0", "cache-type-v = q8_0", "flash-attn = on", "predict = -1",
                      "reasoning-budget = -1", "reasoning = on" if model["reasoning"]["enabled"] else "reasoning = auto"]
            if model["reasoning"]["effort"]:
                lines += [f"reasoning-effort = {model['reasoning']['effort']}"]
            if model.get("projector"):
                lines += [f"mmproj = {model['projector'].as_posix()}"]
        lines.append("")
    text = "\n".join(lines)
    target = Path(target)
    if not target.exists() or target.read_text(encoding="utf-8") != text:
        temporary = target.with_suffix(".new")
        temporary.write_text(text, encoding="utf-8")
        temporary.replace(target)
        print(f"Native catalog: {len(models)} local text, vision and embedding preset(s) in {root}", flush=True)
    return models


def create_windows_job(process):
    """Ensure only this supervisor's native server tree ends with the supervisor."""
    if os.name != "nt":
        return None
    from ctypes import wintypes

    class BasicLimits(ctypes.Structure):
        _fields_ = [("process_time", ctypes.c_longlong), ("job_time", ctypes.c_longlong),
                    ("flags", wintypes.DWORD), ("min_working_set", ctypes.c_size_t),
                    ("max_working_set", ctypes.c_size_t), ("active_limit", wintypes.DWORD),
                    ("affinity", ctypes.c_size_t), ("priority", wintypes.DWORD), ("scheduling", wintypes.DWORD)]

    class IoCounters(ctypes.Structure):
        _fields_ = [(name, ctypes.c_ulonglong) for name in ("read_ops", "write_ops", "other_ops", "read_bytes", "write_bytes", "other_bytes")]

    class ExtendedLimits(ctypes.Structure):
        _fields_ = [("basic", BasicLimits), ("io", IoCounters), ("process_memory", ctypes.c_size_t),
                    ("job_memory", ctypes.c_size_t), ("peak_process_memory", ctypes.c_size_t), ("peak_job_memory", ctypes.c_size_t)]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel.CreateJobObjectW.restype = wintypes.HANDLE
    kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    job = kernel.CreateJobObjectW(None, None)
    limits = ExtendedLimits()
    limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    if not job or not kernel.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)) or not kernel.AssignProcessToJobObject(job, process._handle):
        error = ctypes.get_last_error()
        if job:
            kernel.CloseHandle(job)
        process.kill()
        raise OSError(error, "Could not isolate the managed native llama process tree")
    return kernel, job


def gpu_inventory():
    """Match CUDA's PCI_BUS_ID ordering to physical nvidia-smi indices."""
    try:
        result = subprocess.run(["nvidia-smi", "--query-gpu=index,pci.bus_id,memory.free", "--format=csv,noheader,nounits"],
                                capture_output=True, text=True, timeout=10,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0, check=True)
        rows = sorted(csv.reader(result.stdout.splitlines()), key=lambda row: row[1].strip())
        return [{"index": int(row[0]), "device": f"CUDA{position}", "free": int(row[2]) * 1024 * 1024}
                for position, row in enumerate(rows)]
    except (OSError, ValueError, IndexError, subprocess.SubprocessError):
        return []


def available_host_memory():
    """Available physical RAM, without optional packages or a paging-file budget."""
    try:
        if os.name == "nt":
            class MemoryStatus(ctypes.Structure):
                _fields_ = [("length", ctypes.c_ulong), ("load", ctypes.c_ulong),
                            *[(name, ctypes.c_ulonglong) for name in (
                                "totalPhysical", "availablePhysical", "totalPageFile", "availablePageFile",
                                "totalVirtual", "availableVirtual", "availableExtendedVirtual")]]
            status = MemoryStatus()
            status.length = ctypes.sizeof(status)
            if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
                return None
            return status.availablePhysical
        # MemAvailable includes reclaimable filesystem pages, unlike MemFree.
        for line in Path("/proc/meminfo").read_text().splitlines():
            if line.startswith("MemAvailable:"):
                return int(line.split()[1]) * 1024
    except (AttributeError, OSError, ValueError, IndexError):
        pass
    return None


def model_file_bytes(model):
    path = model["path"]
    match = SHARD.match(path.name)
    paths = [path] if not match else [path.with_name(f"{match[1]}-{part:05d}-of-{int(match[3]):05d}.gguf")
                                    for part in range(1, int(match[3]) + 1)]
    if model.get("projector"):
        paths.append(model["projector"])
    return sum(item.stat().st_size for item in paths)


def file_identity(paths):
    return [[str(item.resolve()), item.stat().st_size, item.stat().st_mtime_ns]
            for item in sorted(paths, key=lambda path: str(path.resolve()))]


def model_file_identity(model):
    path = model["path"]
    match = SHARD.match(path.name)
    paths = [path] if not match else [path.with_name(f"{match[1]}-{part:05d}-of-{int(match[3]):05d}.gguf")
                                    for part in range(1, int(match[3]) + 1)]
    if model.get("projector"):
        paths.append(model["projector"])
    return file_identity(paths)


def runtime_file_identity(binary):
    return file_identity([Path(binary), *Path(binary).parent.glob("*.dll")]) if binary else []


def gpu_profile_inventory():
    """Hardware identity for measured profiles, separate from free-memory admission."""
    try:
        result = subprocess.run(["nvidia-smi", "--query-gpu=pci.bus_id,uuid,name,memory.total", "--format=csv,noheader,nounits"],
                                capture_output=True, text=True, timeout=10,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0, check=True)
        rows = sorted(csv.reader(result.stdout.splitlines()), key=lambda row: row[0].strip().lower())
        return [dict(pciBusId=row[0].strip().lower(), uuid=row[1].strip(), name=row[2].strip(),
                     memoryTotalMiB=int(row[3])) for row in rows]
    except (OSError, ValueError, IndexError, subprocess.SubprocessError):
        return []


def read_model_load_profiles(state):
    path = Path(state) / "model-load-policy.json"
    try:
        if not path.exists():
            return [], None
        if path.stat().st_size > 256 * 1024:
            raise ValueError("profile file exceeds 256 KiB")
        data = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(data, dict) or type(data.get("version")) is not int or data["version"] != 1:
            raise ValueError("expected schema version 1")
        profiles = data.get("profiles")
        if not isinstance(profiles, list) or len(profiles) > 16:
            raise ValueError("expected at most 16 profiles")
        for profile in profiles:
            if (not isinstance(profile, dict) or not isinstance(profile.get("id"), str)
                    or not re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", profile["id"])):
                raise ValueError("each profile needs a short id")
            if (profile.get("architecture") != "deepseek4" or profile.get("split") not in ("layer", "tensor")
                    or profile.get("context") != "model-maximum" or profile.get("cacheK") != "q8_0"
                    or profile.get("cacheV") != "q8_0"):
                raise ValueError("only validated DeepSeek4 layer/tensor/full-context/q8_0 profiles are supported")
            if ((profile["split"] == "tensor" and profile.get("allReduce") != "none")
                    or (profile["split"] == "layer" and profile.get("allReduce") is not None)):
                raise ValueError("tensor profiles require allReduce=none; layer profiles leave AllReduce unset")
            for key in ("modelFiles", "runtimeFiles"):
                entries = profile.get(key)
                if not isinstance(entries, list) or not entries or len(entries) > 256:
                    raise ValueError(f"{key} must contain exact file identities")
                for entry in entries:
                    if (not isinstance(entry, list) or len(entry) != 3 or not isinstance(entry[0], str)
                            or not Path(entry[0]).is_absolute() or type(entry[1]) is not int or entry[1] < 0
                            or type(entry[2]) is not int or entry[2] <= 0):
                        raise ValueError(f"invalid {key} identity")
            gpus = profile.get("gpuIdentities")
            if not isinstance(gpus, list) or len(gpus) != 2:
                raise ValueError("exactly two GPU identities are required")
            for gpu in gpus:
                if (not isinstance(gpu, dict) or not isinstance(gpu.get("pciBusId"), str)
                        or not re.fullmatch(r"[0-9a-f]{4,8}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]", gpu["pciBusId"])
                        or not isinstance(gpu.get("uuid"), str) or not gpu["uuid"].startswith("GPU-")
                        or not isinstance(gpu.get("name"), str) or not gpu["name"]
                        or type(gpu.get("memoryTotalMiB")) is not int or gpu["memoryTotalMiB"] <= 0):
                    raise ValueError("invalid GPU identity")
            if profile.get("gpuPciBusIds") != [gpu["pciBusId"] for gpu in gpus]:
                raise ValueError("GPU PCI list must match the ordered full identities")
            if profile.get("mmprojDevice", "CUDA0") not in ("CUDA0", "CUDA1"):
                raise ValueError("mmprojDevice must be CUDA0 or CUDA1")
            for key in ("batchSize", "ubatchSize"):
                if key in profile and (type(profile[key]) is not int or not 1 <= profile[key] <= 8192):
                    raise ValueError(f"{key} must be between 1 and 8192")
            if "ubatchSize" in profile and profile["ubatchSize"] > profile.get("batchSize", 2048):
                raise ValueError("ubatchSize exceeds batchSize")
        return profiles, None
    except (OSError, UnicodeError, ValueError) as error:
        return [], f"Local model-load-policy.json ignored: {error}"


def matching_model_load_profile(profiles, model, binary, gpus):
    metadata = model_metadata(model["path"], allow_metadata_only=True) or {}
    if metadata.get("general.architecture") != "deepseek4" or model["role"] == "embedding":
        return None
    try:
        model_identity, runtime_identity = model_file_identity(model), runtime_file_identity(binary)
        for profile in profiles:
            if (profile["modelFiles"] == model_identity and profile["runtimeFiles"] == runtime_identity
                    and profile["gpuIdentities"] == gpus):
                return profile
    except OSError:
        pass
    return None


def measured_model_policy(model, profile, fit_target):
    # Reuse measured GPU placement and its full model-specific context.
    policy = model_runtime_policy(model, fit_target=fit_target)
    policy.update(split=profile["split"], fit="off", gpuLayers="999", context=model["context"],
                  contextPolicy="model-maximum", profileId=profile["id"],
                  devices="CUDA0,CUDA1", tensorSplit="1,1")
    if profile["split"] == "tensor":
        policy["allReduce"] = "none"
    for key in ("mmprojDevice", "batchSize", "ubatchSize"):
        if key in profile:
            policy[key] = profile[key]
    return vision_batch_policy(model, limit_context_policy(model, policy))


def choose_single_gpu(model, devices, reserve_mib=2048):
    # This is only admission to a real full-context allocation trial, not a KV
    # memory estimate. Unknown architectures are validated by llama itself.
    minimum = model_file_bytes(model) + int(reserve_mib) * 1024 ** 2
    candidates = [gpu for gpu in devices if gpu["index"] == 0 and gpu["free"] >= minimum]
    # The primary agent always owns physical GPU0 when a single device can
    # accommodate it. Secondary placement is explicit and never model-specific.
    candidates.sort(key=lambda gpu: (gpu["index"] == 0, gpu["free"]), reverse=True)
    return candidates[0]["device"] if candidates else None


def estimate_replica_bytes(model, context):
    """Admission estimate from GGUF dimensions; native allocation is final authority.

    Hybrid attention intervals and prediction-only layers are explicit GGUF
    metadata, so a full-context replica does not reserve dense KV for recurrent
    layers. This policy works for future models carrying the same dimensions.
    """
    metadata = dict(model_metadata(Path(model["path"]), allow_metadata_only=True) or {})
    architecture = metadata.get("general.architecture", "")
    layers = metadata.get(architecture + ".block_count")
    interval = metadata.get(architecture + ".full_attention_interval")
    recurrent_extra = 0
    if isinstance(layers, int) and isinstance(interval, int) and interval > 1:
        prediction = metadata.get(architecture + ".nextn_predict_layers", 0)
        prediction = prediction if type(prediction) is int and 0 <= prediction < layers else 0
        dense_layers = max(1, (layers - prediction + interval - 1) // interval)
        metadata[architecture + ".block_count"] = dense_layers
        # Only dense attention gets token-sized KV. Recurrent state buffers
        # still exist in the other layers; do not shrink them with the mask.
        recurrent_extra = recurrent_state_bytes(metadata, layers - dense_layers)
    kv = estimate_q8_session_bytes(metadata, context)
    # Admission must not treat an unknown KV layout as a zero-byte cache.
    # Additional runtime buffers and the separate per-device reserve cover
    # CUDA graphs and batching before a native allocation is attempted.
    return (int(model_file_bytes(model) * 1.05) + kv + int(recurrent_extra * 1.1) + 512 * 1024**2
            if kv is not None else None)


def recurrent_state_bytes(metadata, layers):
    architecture = metadata.get("general.architecture", "")
    def number(suffix):
        value = metadata.get(architecture + "." + suffix)
        return value if type(value) is int and value > 0 else 0
    state, inner = number("ssm.state_size"), number("ssm.inner_size")
    if not state or not inner:
        return 0
    convolution = max(0, (number("ssm.conv_kernel") or 1) - 1) * (inner + 2 * (number("ssm.group_count") or 1) * state)
    return layers * (state * inner + convolution) * 4 * 2


def estimate_q8_session_bytes(metadata, tokens):
    """Conservative q8_0 KV estimate for standard/GQA and DeepSeek-V4 caches.

    Count every layer as attention, including hybrid layers, so this remains an
    upper estimate without guessing an architecture's recurrent-layer mask.
    Unknown/variable dimensions use the cache manager's explicit fallback.
    """
    architecture = metadata.get("general.architecture", "")
    if not architecture:
        return None
    def number(suffix):
        value = metadata.get(architecture + "." + suffix)
        return value if isinstance(value, int) and value > 0 else None
    layers = number("block_count")
    if architecture == "deepseek4":
        key = number("attention.key_length")
        indexer_key = number("attention.indexer.key_length")
        if not all((layers, key, indexer_key)):
            return None
        # llama-kv-cache-dsv4.cpp stores K-only MLA rows, not a dense K+V
        # tensor for all query heads. Include raw K for every token/layer,
        # CSA and lightning-indexer at 1/4, and HCA at 1/128. Counting every
        # layer in both compressed groups deliberately overestimates their
        # disjoint layer masks. Round rows upward and reserve compressor state,
        # slot metadata and 25% margin; real per-session measurements supersede
        # this initial estimate after the first successful snapshot.
        key_row = ((key + 31) // 32) * 34
        indexer_row = ((indexer_key + 31) // 32) * 34
        quarter = (tokens + 3) // 4 + 1
        compressed = (tokens + 127) // 128 + 1
        rows = layers * (tokens * key_row + quarter * (key_row + indexer_row) + compressed * key_row)
        return int((rows + tokens * 64) * 1.25) + 128 * 1024**2
    heads = number("attention.head_count")
    embedding = number("embedding_length")
    kv_heads = number("attention.head_count_kv") or heads
    inferred = embedding // heads if embedding and heads and embedding % heads == 0 else None
    key = number("attention.key_length") or inferred
    value = number("attention.value_length") or key
    if not all((layers, kv_heads, key, value)):
        return None
    # GGML q8_0 stores 32 values and a 2-byte scale in each 34-byte block.
    per_token = layers * (((key * kv_heads + 31) // 32) * 34 + ((value * kv_heads + 31) // 32) * 34)
    recurrent = recurrent_state_bytes(metadata, layers)
    return int((tokens * (per_token + 24) + recurrent) * 1.1) + 16 * 1024**2


class GpuLoadManager:
    """Only changes presets while their model is unloaded; never on live refresh."""
    def __init__(self, root, preset, state, port, fit_target="2048", gpu_layers="auto", binary=None):
        self.model_root = Path(root).expanduser().resolve(strict=True)
        self.root, self.preset, self.state, self.port = resolve_model_catalog_root(root), preset, state, port
        self.fit_target, self.gpu_layers = fit_target, gpu_layers
        self.placements = {}
        self.policies = {}
        self.allocations = {}
        self.agent_failures = {}
        self.router_allreduce = os.environ.get("GGML_CUDA_ALLREDUCE", "").strip().lower() or None
        self.router_cuda_graphs_disabled = "GGML_CUDA_DISABLE_GRAPHS" in os.environ
        self.lock = threading.RLock()
        self.binary = Path(binary) if binary else None
        self.sessions = NativeSessionCache(Path(state) / "session-cache", self.router, self.session_fingerprint,
                                           estimate_bytes=self.session_size_estimate,
                                           prepare_completion=self.prepare_completion)

    def session_model(self, model_id):
        # This lookup is read-only: fingerprint checks do not rewrite/reload
        # active presets on each inference round.
        base_id = model_id.removesuffix("@subagent")
        model = next((item for item in discover_models(self.root) if item["id"] == base_id), None)
        if model is None:
            raise ValueError("Model is not in the local catalog")
        return model

    def session_size_estimate(self, model_id, tokens):
        metadata = model_metadata(Path(self.session_model(model_id)["path"]), allow_metadata_only=True) or {}
        return estimate_q8_session_bytes(metadata, tokens)

    def session_fingerprint(self, model_id):
        model = self.session_model(model_id)
        path = Path(model["path"])
        shard = SHARD.match(path.name)
        paths = sorted(path.parent.glob(shard.group(1) + "-*.gguf")) if shard else [path]
        if model.get("projector"):
            paths += [Path(model["projector"])]
        if self.binary:
            paths += [self.binary] + sorted(self.binary.parent.glob("*.dll"))
        identity = [(str(item.resolve()), item.stat().st_size, item.stat().st_mtime_ns) for item in paths]
        metadata = model_metadata(path, allow_metadata_only=True) or {}
        policy = self.policies.get(model_id) or model_runtime_policy(
            model, self.placements.get(model_id), fit_target=self.fit_target, gpu_layers=self.gpu_layers)
        policy = vision_batch_policy(model, limit_context_policy(model, policy))
        # Preserve the existing default layer/single-GPU snapshot identity:
        # the same loaded configuration must not force a large prefix re-eval.
        fingerprint = dict(version=1, files=identity, context=policy["context"], cache="q8_0", slots=1,
                           template=native_history_template(metadata.get("tokenizer.chat_template", "")),
                           placement=self.placements.get(model_id), fit=self.fit_target, gpuLayers=self.gpu_layers,
                           split="layer" if policy["split"] == "none" else policy["split"])
        if model.get("projector") and model["role"] != "embedding":
            # Default/fitted vision loads also changed their physical batching.
            # A snapshot must describe the effective decoder, not the unsafe
            # logical batch recorded by an older measured profile.
            fingerprint["visionBatch"] = {key: policy[key] for key in ("batchSize", "ubatchSize")}
        if policy["split"] == "tensor" or policy.get("profileId") or policy.get("contextPolicy") == "profile-fallback-full-context":
            # Explicit tensor/profiled loads have distinct fitting, context,
            # projector and batch settings; include their effective config.
            fingerprint.update(context=policy["context"], fit="off", gpuLayers=policy["gpuLayers"],
                               runtimePolicy={key: value for key, value in policy.items()
                                              if key not in ("profileId", "fallbackReason")})
        return fingerprint

    def session_prepare(self, model, key, session_id=None, parent_session_id=None):
        with self.lock:
            return self.sessions.prepare(model, key, session_id, parent_session_id)

    def session_delete(self, session_ids, session_keys):
        with self.lock:
            models = [instance for item in discover_models(self.root)
                      for instance in (item["id"], item["id"] + "@subagent")]
            return self.sessions.delete(session_ids, session_keys, models)

    def session_save(self, model, key, prompt_tokens=None, force=True, new_tokens=None):
        with self.lock:
            return self.sessions.save(model, key, prompt_tokens, force, new_tokens)

    def session_reset_epoch(self, epoch):
        with self.lock:
            # Resident unowned/stateless native requests also make a reset busy.
            current = self.router("v1/models").get("data", [])
            loaded = []
            for item in current:
                state = item.get("status", {}).get("value")
                if state == "loading":
                    return dict(status="busy", epoch=epoch)
                if state in ("loaded", "sleeping"):
                    loaded.append(item["id"])
                    if any(slot.get("is_processing") for slot in self.sessions._slots(item["id"])):
                        return dict(status="busy", epoch=epoch)
            return self.sessions.reset_epoch(epoch, loaded_models=loaded)

    def session_fork(self, source_model, source_key, model, key):
        with self.lock:
            return self.sessions.fork(source_model, source_key, model, key)

    def refresh(self):
        with self.lock:
            return write_presets(self.root, self.preset, self.placements, managed_gpu=True,
                                 fit_target=self.fit_target, gpu_layers=self.gpu_layers,
                                 resolved_policies=self.policies, agent_instances=False)

    def router(self, path, body=None, timeout=15):
        request = urllib.request.Request(f"http://127.0.0.1:{self.port}/{path}",
                  data=json.dumps(body).encode() if body is not None else None,
                  headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.load(response)

    def prepare_completion(self, body, cancelled=None):
        """Keep long prefills alive only while their native token count advances.

        A watchdog also closes the actual inference socket on caller disconnect;
        abandoning the outer control request alone does not cancel llama's task.
        The session cache drains the slot before releasing the preparation lease.
        """
        progress = PreparationProgress(len(body["prompt"]), cancelled)
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=300)
        finished, failures = threading.Event(), []
        response = None

        def disconnect():
            active_socket = connection.sock
            if active_socket is None and response is not None:
                active_socket = getattr(getattr(getattr(response, "fp", None), "raw", None), "_sock", None)
            if active_socket is not None:
                try:
                    active_socket.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass

        def watch():
            while not finished.wait(0.1):
                if not failures:
                    try:
                        progress.check()
                    except Exception as error:
                        failures.append(error)
                if failures:
                    disconnect()

        watchdog = threading.Thread(target=watch, daemon=True)
        watchdog.start()
        try:
            progress.check()
            connection.request("POST", "/completion", json.dumps(dict(body, stream=True, return_progress=True)),
                {"Content-Type": "application/json", "Accept": "text/event-stream"})
            progress.check()
            response = connection.getresponse()
            if response.status != 200:
                raise RuntimeError(f"Native preparation returned HTTP {response.status}: " +
                    response.read(4096).decode("utf-8", errors="replace"))
            while True:
                progress.check()
                line = response.readline(16 * 1024**2 + 1)
                if not line:
                    break
                if len(line) > 16 * 1024**2:
                    raise RuntimeError("Native preparation event exceeds its size limit")
                if not line.startswith(b"data:"):
                    continue
                data = line[5:].strip()
                if data == b"[DONE]":
                    break
                event = json.loads(data)
                if not isinstance(event, dict):
                    raise RuntimeError("Native preparation returned an invalid event")
                if "error" in event:
                    raise RuntimeError("Native preparation failed: " + str(event["error"])[:500])
                progress.observe(event)
                if event.get("stop") is True:
                    return event
            raise RuntimeError("Native preparation ended without final token accounting")
        except Exception as error:
            if failures:
                raise failures[0] from error
            if isinstance(error, PreparationTimeoutError):
                raise
            if isinstance(error, TimeoutError):
                raise PreparationTimeoutError("Native preparation made no progress before its socket deadline") from error
            raise
        finally:
            finished.set()
            disconnect()
            if response is not None:
                response.close()
            connection.close()
            watchdog.join(timeout=1)

    def record(self, model, device, outcome, detail=None):
        with (self.state / "gpu-placement.jsonl").open("a", encoding="utf-8") as stream:
            stream.write(json.dumps({"time": time.time(), "model": model, "device": device or "multi-gpu-auto",
                                     "outcome": outcome, "settings": self.policies.get(model), "detail": detail}) + "\n")

    def configure_router_environment(self, source):
        environment = runtime_environment(source, self.state)
        self.router_allreduce = environment.get("GGML_CUDA_ALLREDUCE", "").strip().lower() or None
        self.router_cuda_graphs_disabled = "GGML_CUDA_DISABLE_GRAPHS" in environment
        profiles, diagnostic = read_model_load_profiles(self.state)
        if diagnostic:
            self.record("catalog", None, "profile-ignored", diagnostic)
        if not profiles:
            return environment
        gpus = gpu_profile_inventory()
        matched = False
        for model in discover_models(self.root):
            profile = matching_model_load_profile(profiles, model, self.binary, gpus)
            if profile:
                matched = True
                if profile["split"] == "layer":
                    self.record(model["id"], None, "profile-environment", {
                        "profileId": profile["id"], "split": "layer", "environment": "unchanged"})
                    continue
                if self.router_cuda_graphs_disabled:
                    self.record(model["id"], None, "profile-ignored", "Human CUDA graphs-disable override retained; automatic tensor profile skipped.")
                    break
                if "GGML_CUDA_ALLREDUCE" not in source:
                    environment["GGML_CUDA_ALLREDUCE"] = profile["allReduce"]
                    self.router_allreduce = profile["allReduce"]
                self.record(model["id"], None, "profile-environment", {
                    "profileId": profile["id"], "allReduce": self.router_allreduce,
                    "source": "human-environment" if "GGML_CUDA_ALLREDUCE" in source else "local-profile"})
                break
        if not matched:
            self.record("catalog", None, "profile-ignored", "No exact model/runtime/GPU identity match at startup.")
        return environment

    def resolve_load_policy(self, model, device):
        if device or "TWITCHAI_NATIVE_MULTI_GPU_SPLIT" in os.environ:
            return model_runtime_policy(model, device, split_mode=multi_gpu_split_mode(),
                                        fit_target=self.fit_target, gpu_layers=self.gpu_layers)
        profiles, diagnostic = read_model_load_profiles(self.state)
        if diagnostic:
            self.record(model["id"], device, "profile-ignored", diagnostic)
        if profiles:
            profile = matching_model_load_profile(profiles, model, self.binary, gpu_profile_inventory())
            if profile and (profile["split"] == "layer" or
                            (not self.router_cuda_graphs_disabled and self.router_allreduce == profile["allReduce"])):
                policy = measured_model_policy(model, profile, self.fit_target)
                self.record(model["id"], device, "profile-selected", {"profileId": profile["id"]})
                return policy
            if profile and self.router_cuda_graphs_disabled:
                reason = "Human CUDA graphs-disable override retained; restart without it to apply the validated tensor profile."
            else:
                reason = ("Matching profile requires a native supervisor restart with its validated AllReduce setting."
                          if profile else "No exact model/runtime/GPU identity match; default placement retained.")
            self.record(model["id"], device, "profile-ignored", reason)
        return model_runtime_policy(model, device, fit_target=self.fit_target, gpu_layers=self.gpu_layers)

    @staticmethod
    def _argument(item, name):
        arguments = item.get("status", {}).get("args", [])
        try:
            return arguments[arguments.index(name) + 1]
        except (ValueError, IndexError):
            return None

    def agent_status(self, model_id, context_length=None):
        """Only admit an independently budgeted GPU1 slot; never share the primary."""
        with self.lock:
            secondary = self._secondary_agent_status(model_id, context_length)
            return dict(secondary, executionMode="parallel" if secondary["allowed"] else "unavailable")

    def _full_gpu_residency(self, item, model, instance):
        """Require a fixed full-offload load, not an auto-fit or CPU-backed instance."""
        policy = self.policies.get(instance, {})
        fit = self._argument(item, "--fit") or policy.get("fit")
        layers = self._argument(item, "--n-gpu-layers") or self._argument(item, "-ngl") or policy.get("gpuLayers")
        metadata = model_metadata(Path(model["path"]), allow_metadata_only=True) or {}
        blocks = metadata.get(metadata.get("general.architecture", "") + ".block_count")
        if fit != "off" or layers is None or not re.fullmatch(r"[0-9]+", str(layers)):
            return "subagent.primary_offload_unknown"
        layers = int(layers)
        if layers < 999 and (type(blocks) is not int or layers <= blocks):
            return "subagent.primary_cpu_offload"
        # Explicit tensor overrides may put individual tensors on CPU even
        # when n-gpu-layers requests all layers.
        arguments = item.get("status", {}).get("args", [])
        if (self._argument(item, "--override-tensor") or self._argument(item, "-ot")
                or "--no-kv-offload" in arguments or "-nkvo" in arguments
                or self._argument(item, "--mmproj-device") == "CPU"):
            return "subagent.primary_cpu_offload"
        return None

    def _secondary_agent_status(self, model_id, context_length=None):
        with self.lock:
            model = self.session_model(model_id)
            base = model["id"]
            replica = base + "@subagent"
            result = dict(allowed=False, reason=None, modelId=base, instanceId=replica, gpuIndex=1)
            if model["role"] == "embedding":
                return dict(result, reason="subagent.model_not_language")
            current = self.router("v1/models")["data"]
            active = [item for item in current if item.get("status", {}).get("value") in ("loaded", "loading", "sleeping")]
            parent = next((item for item in active if item["id"] == base
                           and item.get("status", {}).get("value") in ("loaded", "sleeping")), None)
            if parent is None:
                return dict(result, reason="subagent.primary_not_loaded")
            devices = gpu_inventory()
            primary_gpu = next((item for item in devices if item["index"] == 0), None)
            secondary_gpu = next((item for item in devices if item["index"] == 1), None)
            if not primary_gpu or not secondary_gpu:
                return dict(result, reason="subagent.second_gpu_unavailable")
            parent_device = self._argument(parent, "--device") or self.placements.get(base)
            parent_split = self._argument(parent, "--split-mode") or self.policies.get(base, {}).get("split")
            if parent_split != "none" or parent_device != primary_gpu["device"]:
                return dict(result, reason="subagent.primary_uses_multiple_gpus" if parent_split != "none"
                            or parent_device and "," in parent_device else "subagent.primary_not_gpu0")
            residency_error = self._full_gpu_residency(parent, model, base)
            if residency_error:
                return dict(result, reason=residency_error)
            if any(item["id"] not in (base, replica) for item in active):
                return dict(result, reason="subagent.other_model_resident")
            if base in self.agent_failures:
                return dict(result, reason=self.agent_failures[base])
            existing = next((item for item in active if item["id"] == replica), None)
            props = self.router("props?model=" + urllib.parse.quote(base, safe=""))
            context = props.get("default_generation_settings", {}).get("n_ctx")
            if type(context) is not int or context < 2048:
                return dict(result, reason="subagent.context_unavailable")
            if context_length is not None and context_length > context:
                return dict(result, reason="subagent.context_too_large")
            reserve = max(2048, int(self.fit_target)) * 1024**2
            if primary_gpu["free"] < reserve:
                return dict(result, reason="subagent.primary_vram_insufficient", contextLength=context)
            host_free = available_host_memory()
            # Full GPU offload still needs host token/batch buffers. Preserve
            # physical headroom; swap capacity is not evidence of free RAM.
            host_required = reserve + 512 * 1024**2 + context * 64
            result.update(contextLength=context, requiredHostBytes=host_required, freeHostBytes=host_free)
            if host_free is None:
                return dict(result, reason="subagent.ram_unavailable")
            if host_free < host_required:
                return dict(result, reason="subagent.ram_insufficient")
            if existing and existing.get("status", {}).get("value") in ("loaded", "sleeping"):
                # Its model/KV/work buffers are already committed. Admission
                # still requires *additional* free safety headroom, not enough
                # free memory to allocate the same replica a second time.
                result.update(requiredBytes=reserve, freeBytes=secondary_gpu["free"])
                child_device = self._argument(existing, "--device") or self.placements.get(replica)
                child_split = self._argument(existing, "--split-mode") or self.policies.get(replica, {}).get("split")
                if child_device != secondary_gpu["device"] or child_split != "none":
                    return dict(result, reason="subagent.secondary_not_gpu1")
                child_props = self.router("props?model=" + urllib.parse.quote(replica, safe=""))
                child_context = child_props.get("default_generation_settings", {}).get("n_ctx")
                if type(child_context) is not int or child_context < 2048:
                    return dict(result, reason="subagent.context_unavailable")
                if child_context != context:
                    return dict(result, reason="subagent.context_mismatch", contextLength=context,
                                replicaContextLength=child_context)
                if self._full_gpu_residency(existing, model, replica):
                    return dict(result, reason="subagent.secondary_offload_unknown")
                if secondary_gpu["free"] < reserve:
                    return dict(result, reason="subagent.vram_insufficient", requiredBytes=reserve,
                                freeBytes=secondary_gpu["free"])
                return dict(result, allowed=True, contextLength=context)
            estimate = estimate_replica_bytes(model, context)
            if estimate is None:
                return dict(result, reason="subagent.resource_estimate_unavailable")
            required = max(self.allocations.get(base, 0), estimate) + reserve
            result.update(contextLength=context, requiredBytes=required, freeBytes=secondary_gpu["free"])
            if secondary_gpu["free"] < required:
                return dict(result, reason="subagent.vram_insufficient")
            return dict(result, allowed=True)

    def agent_prepare(self, model_id, source_key=None, key=None, prefill=None, session_id=None, parent_session_id=None, cancelled=None):
        with self.lock:
            self.sessions._check_preparation_cancelled(cancelled)
            availability = self.agent_status(model_id)
            if not availability["allowed"]:
                return availability
            base, replica = availability["modelId"], availability["instanceId"]
            if replica != base + "@subagent":
                return dict(availability, allowed=False, reason="subagent.shared_primary_disabled", executionMode="unavailable")
            try:
                self.load(replica)
            except (OSError, RuntimeError, ValueError) as error:
                # A competing allocation can invalidate an earlier budget.
                # Keep the primary and its KV unchanged; never fork onto it.
                reason = self.agent_failures.get(base) or (
                    str(error) if re.fullmatch(r"subagent\.[a-z_]+", str(error)) else "subagent.runtime_load_failed")
                return dict(availability, allowed=False, reason=reason, executionMode="unavailable", detail=str(error)[-4000:])
            availability = self.agent_status(base)
            if not availability["allowed"]:
                return availability
            self.sessions._check_preparation_cancelled(cancelled)
            cache = (self.sessions.fork(base, source_key, replica, key, prefill, session_id, parent_session_id, cancelled)
                     if source_key and key else self.sessions.prepare(replica, key, session_id, parent_session_id))
            result = dict(availability, cacheStatus=cache["status"], cachedTokens=cache.get("restoredTokens", 0),
                **{name: cache[name] for name in ("sourceCachedTokens", "preparationSampledTokens",
                    "sourceEvaluatedTokens", "preparationMode", "evaluatedGeneratedTokens", "preparedPromptTokens", "detail") if name in cache})
            if cache.get("reason") in ("subagent.preparation_timeout", "subagent.preparation_cancelled"):
                result.update(allowed=False, reason=cache["reason"])
            return result

    def _preload_subagent(self, model_id):
        """Finish an ordinary primary load with its eligible GPU1 replica.

        Admission failures do not invalidate a usable primary. A native replica
        allocation failure is sticky until the primary is actually reloaded;
        ordinary free-memory rejection is rechecked on the next primary reuse.
        """
        replica = model_id + "@subagent"
        try:
            availability = self._secondary_agent_status(model_id)
            if not availability["allowed"]:
                return dict(availability, loaded=False, reused=False)
            result = self.load(replica)
            return dict(availability, loaded=True, reused=result.get("reused", False))
        except (OSError, RuntimeError, ValueError) as error:
            reason = self.agent_failures.get(model_id) or "subagent.runtime_load_failed"
            self.agent_failures[model_id] = reason
            self.record(replica, self.placements.get(replica), "preload-failed", str(error)[-4000:])
            return dict(allowed=False, reason=reason, modelId=model_id, instanceId=replica,
                        gpuIndex=1, loaded=False, reused=False)

    def load(self, model_id, context_length=None):
        with self.lock:
            self._check_model_load_stopped()
            models = self.refresh()
            model = next((item for item in models if item["id"] == model_id), None)
            if model is None:
                raise ValueError("Model is not in the local catalog")
            if context_length is not None and (type(context_length) is not int or not 2048 <= context_length <= model["context"]):
                raise ValueError("Context limit must be between 2048 and the GGUF model maximum")
            current = self.router("v1/models")["data"]
            active = [item for item in current if item.get("status", {}).get("value") in ("loaded", "loading", "sleeping")]
            replica = model.get("instance") == "subagent"
            base = model.get("baseModel", model_id)
            if any(item["id"] not in (base, base + "@subagent") for item in active):
                raise ValueError("Unload the previous model before selecting GPU placement")
            existing = next((item for item in active if item["id"] == model_id), None)
            if existing and context_length is not None and not replica and existing.get("status", {}).get("value") in ("loaded", "sleeping"):
                props = self.router("props?model=" + urllib.parse.quote(model_id, safe=""))
                actual_context = props.get("default_generation_settings", {}).get("n_ctx", 0)
                requested_context = self.policies.get(model_id, {}).get("context")
                # llama may pad n_ctx to an attention block (258000 -> 258048).
                # Repeated turns must retain the same allocation and KV prefix.
                if requested_context != context_length or actual_context < context_length:
                    if any(slot.get("is_processing") for slot in self.sessions._slots(model_id)):
                        raise ValueError("Cannot change context while the model is processing")
                    self.sessions.save(model_id, None)
                    for item in active:
                        if item["id"] in (base, base + "@subagent"):
                            self.router("models/unload", {"model": item["id"]})
                    deadline = time.monotonic() + 60
                    while any(item.get("status", {}).get("value") in ("loaded", "loading", "sleeping")
                              and item["id"] in (base, base + "@subagent") for item in self.router("v1/models")["data"]):
                        if time.monotonic() > deadline:
                            raise TimeoutError("Previous model context is still unloading")
                        time.sleep(.1)
                    existing = None
            if existing:
                # Never rewrite the placement of an instance already loading.
                # The pair is ready only after the parent has its actual n_ctx.
                deadline = model_load_deadline(model)
                while existing.get("status", {}).get("value") == "loading":
                    self._check_model_load_stopped()
                    if time.monotonic() >= deadline:
                        raise TimeoutError("Native model load exceeded its time limit")
                    time.sleep(0.25)
                    existing = next((item for item in self.router("v1/models")["data"]
                                     if item["id"] == model_id), {})
                status = existing.get("status", {})
                if status.get("value") not in ("loaded", "sleeping") or status.get("failed"):
                    raise RuntimeError("Native model load failed; see gpu-placement.jsonl and llama.stderr.log")
                result = {"success": True, "reused": True}
                return result
            devices = gpu_inventory()
            if replica:
                availability = self._secondary_agent_status(base)
                if not availability["allowed"]:
                    raise ValueError(availability["reason"])
                device = next(item["device"] for item in devices if item["index"] == 1)
            else:
                device = choose_single_gpu(model, devices, self.fit_target)
                self.agent_failures.pop(base, None)
            deadline = model_load_deadline(model)
            forced_policy = None
            for attempt in range(3):
                policy = forced_policy or self.resolve_load_policy(model, device)
                if context_length is not None and not replica:
                    policy = dict(policy, context=context_length, contextPolicy="user-limit")
                    # Fit GPU layers into available VRAM while retaining the
                    # explicitly requested KV window. CPU-backed weights stay mmap'ed.
                    if not device:
                        policy.update(fit="on", gpuLayers="auto", split="layer", fitContext=context_length)
                if replica:
                    # Both instances use the same physical KV dimensions and
                    # template. A child never spills onto its parent's device.
                    props = self.router("props?model=" + urllib.parse.quote(base, safe=""))
                    policy = dict(policy, context=props["default_generation_settings"]["n_ctx"])
                    for key in ("batchSize", "ubatchSize"):
                        if key in self.policies.get(base, {}):
                            policy[key] = self.policies[base][key]
                policy = vision_batch_policy(model, policy)
                if attempt == 0:
                    self.sessions.invalidate(model_id)
                self.placements[model_id] = device
                self.policies[model_id] = policy
                self.refresh()
                self.router("v1/models?reload=1")
                logs = [self.state / "llama.stderr.log", self.state / "llama.stdout.log"]
                offsets = {log: log.stat().st_size if log.exists() else 0 for log in logs}
                self.record(model_id, device, "loading")
                self.router("models/load", {"model": model_id})
                while time.monotonic() < deadline:
                    self._check_model_load_stopped()
                    item = next(item for item in self.router("v1/models")["data"] if item["id"] == model_id)
                    status = item.get("status", {})
                    if status.get("value") in ("loaded", "sleeping") and not status.get("failed"):
                        after = next((item for item in gpu_inventory() if item["device"] == device), None)
                        before = next((item for item in devices if item["device"] == device), None)
                        if after and before and before["free"] > after["free"]:
                            self.allocations[model_id] = before["free"] - after["free"]
                        self.record(model_id, device, "loaded")
                        result = {"success": True, "device": device, "fallback": attempt > 0}
                        return result
                    if status.get("failed") or status.get("value") == "failed":
                        break
                    time.sleep(0.25)
                else:
                    try:
                        self.router("models/unload", {"model": model_id})
                    finally:
                        self.record(model_id, device, "timeout")
                    raise TimeoutError("Native model load exceeded its time limit")
                failure = ""
                for log, offset in offsets.items():
                    if log.exists():
                        with log.open("rb") as stream:
                            stream.seek(offset)
                            failure += stream.read(2 * 1024 * 1024).decode("utf-8", errors="replace")
                memory_failure = re.search(r"out of memory|cudaMalloc.*failed|failed to allocate|unable to allocate|CUDA error.*memory", failure, re.I)
                self.record(model_id, device, "failed", failure[-4000:])
                if replica:
                    self.agent_failures[base] = "subagent.vram_allocation_failed" if memory_failure else "subagent.runtime_load_failed"
                    raise RuntimeError(self.agent_failures[base])
                unsupported_tensor = re.search(r"(?:LLAMA_)?SPLIT_MODE_TENSOR[^\n]*(?:not implemented|not supported|requires)|"
                                               r"tensor (?:parallelism|split mode)[^\n]*(?:not implemented|not supported)", failure, re.I)
                if policy["split"] == "tensor" and (memory_failure or unsupported_tensor):
                    # The failed child has exited. Retry exactly once in layer
                    # mode, retaining the full explicit context and GPU layers.
                    forced_policy = dict(policy, split="layer", contextPolicy="profile-fallback-full-context",
                                         fallbackReason="allocation" if memory_failure else "unsupported-tensor")
                    forced_policy.pop("allReduce", None)
                    self.record(model_id, device, "profile-fallback", {"reason": forced_policy["fallbackReason"],
                                                                      "context": forced_policy["context"]})
                    device = None
                    continue
                if not device or attempt or not memory_failure:
                    raise RuntimeError("Native model load failed; see gpu-placement.jsonl and llama.stderr.log")
                # A failed router child has already exited, releasing its CUDA
                # allocations. Reloading the changed preset joins that child.
                device = None
            raise RuntimeError("GPU load attempts exhausted")

    def _check_model_load_stopped(self):
        # The explicit supervisor stop waits for this lock before checkpointing
        # its models. Release it promptly even if a NAS load is still in flight.
        if (Path(self.state) / "stop.requested").exists():
            raise PreparationCancelledError("Native model loading cancelled by the owning runtime stop")


def start_gpu_control(manager, host, port):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path != "/runtime":
                self.send_json({"error": "Unknown native runtime resource"}, 404)
                return
            # Already resolved at startup. Status/setting refreshes must never
            # enumerate or reopen a NAS tree on their request thread.
            self.send_json({"modelRoot": str(manager.model_root), "discoveryRoot": str(manager.root)})

        def send_json(self, result, status=200):
            encoded = json.dumps(result).encode()
            try:
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(encoded)))
                self.end_headers()
                self.wfile.write(encoded)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def client_disconnected(self):
            try:
                readable, _, _ = select.select([self.connection], [], [], 0)
                return bool(readable) and not self.connection.recv(1, socket.MSG_PEEK)
            except (OSError, ValueError):
                return True

        def do_POST(self):
            try:
                length = int(self.headers.get("Content-Length", "0"))
                if self.path not in ("/models/load", "/sessions/prepare", "/sessions/save", "/sessions/fork", "/sessions/delete",
                                     "/sessions/reset-epoch",
                                     "/agents/status", "/agents/prepare") or not 0 < length <= (
                                         16 * 1024**2 if self.path == "/agents/prepare" else 1024**2 if self.path == "/sessions/delete" else 8192):
                    raise ValueError("Invalid GPU control request")
                body = json.loads(self.rfile.read(length))
                if self.path.startswith("/sessions/"):
                    if self.path == "/sessions/prepare":
                        result = manager.session_prepare(body["model"], body.get("sessionKey"),
                            body.get("sessionId"), body.get("parentSessionId"))
                    elif self.path == "/sessions/save":
                        result = manager.session_save(body["model"], body.get("sessionKey"), body.get("promptTokens"),
                            body.get("force", True), body.get("newTokens"))
                    elif self.path == "/sessions/reset-epoch":
                        result = manager.session_reset_epoch(body["epoch"])
                    elif self.path == "/sessions/delete":
                        result = manager.session_delete(body.get("sessionIds", []), body.get("sessionKeys", []))
                    else:
                        result = manager.session_fork(body["sourceModel"], body["sourceSessionKey"], body["model"], body["sessionKey"])
                elif self.path == "/agents/status":
                    result = manager.agent_status(body["model"], body.get("contextLength"))
                elif self.path == "/agents/prepare":
                    result = manager.agent_prepare(body["model"], body.get("parentSessionCacheKey"),
                        body.get("childSessionCacheKey"), body.get("prefill"),
                        body.get("childSessionId"), body.get("parentSessionId"), self.client_disconnected)
                else:
                    result = manager.load(body["model"], body.get("contextLength"))
                status = 200
            except Exception as error:
                result, status = {"error": str(error)}, 500
            self.send_json(result, status)
        def log_message(self, *_):
            pass
    server = ThreadingHTTPServer((host, port), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def runtime_environment(source, state_directory):
    environment = dict(source, HF_HUB_OFFLINE="1", LLAMA_CACHE=str(state_directory / "cache"))
    # A parent shell's fixed context would bypass the model-maximum fitting policy.
    # No unrelated user environment or process is changed.
    for key in ("LLAMA_ARG_CTX_SIZE", "LLAMA_ARG_KV_UNIFIED_PER_SLOT", "LLAMA_ARG_FIT_CTX",
                "LLAMA_ARG_N_PREDICT", "LLAMA_ARG_THINK_BUDGET", "LLAMA_ARG_REASONING_EFFORT",
                "LLAMA_ARG_REASONING", "LLAMA_ARG_CHAT_TEMPLATE_KWARGS", "LLAMA_ARG_DEVICE", "LLAMA_ARG_SPLIT_MODE",
                "LLAMA_ARG_MAIN_GPU", "LLAMA_ARG_TENSOR_SPLIT", "LLAMA_ARG_N_GPU_LAYERS", "LLAMA_ARG_FIT",
                "LLAMA_ARG_FIT_TARGET", "CUDA_VISIBLE_DEVICES"):
        environment.pop(key, None)
    environment["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID"
    return environment


def runtime_command(binary, preset, host, port, fit_target, gpu_layers):
    # The installed native build crashed in llama.dll after an idle sleep/wake
    # while input_tokens was reusing pre-wake model pointers. Disable that path;
    # the manager still unloads the owned model on actual workload/model switches.
    return [str(binary), "--host", host, "--port", str(port),
            "--models-preset", str(preset), "--models-max", "2", "--no-models-autoload",
            "--jinja", "--no-webui", "--sleep-idle-seconds", "-1"]


def main():
    parser = argparse.ArgumentParser(description="Native TwitchAI llama.cpp supervisor for all model roles")
    parser.add_argument("--model-root", required=True)
    parser.add_argument("--binary")
    parser.add_argument("--state-directory")
    parser.add_argument("--list-models", action="store_true", help="Print the read-only catalog and exit without starting a process")
    parser.add_argument("--port", type=int, default=8181)
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--fit-target", default="2048")
    parser.add_argument("--gpu-layers", default="auto")
    args = parser.parse_args()
    try:
        root = Path(args.model_root).expanduser().resolve(strict=True)
        if root != Path(r"C:\Users\AMD\.cache\huggingface").resolve(strict=True):
            parser.error("Only C:\\Users\\AMD\\.cache\\huggingface is allowed")
        resolve_model_catalog_root(root)
    except OSError as error:
        parser.error(f"AI model directory is unavailable: {args.model_root}. Check the folder, NAS connection and access permissions. {error}")
    if args.list_models:
        print(json.dumps(discover_models(root), default=str))
        return 0
    if not args.binary or not args.state_directory:
        parser.error("--binary and --state-directory are required when starting the native runtime")
    binary = Path(args.binary).resolve(strict=True)
    state_directory = Path(args.state_directory).resolve()
    state_directory.mkdir(parents=True, exist_ok=True)
    preset = state_directory / "models.ini"
    stop_file = state_directory / "stop.requested"
    stop_file.unlink(missing_ok=True)
    gpu_manager = GpuLoadManager(root, preset, state_directory, args.port, args.fit_target, args.gpu_layers, binary=binary)
    gpu_manager.refresh()
    control = start_gpu_control(gpu_manager, args.host, args.port + 1)
    stopped = threading.Event()

    def refresh():
        while not stopped.wait(10):
            try:
                gpu_manager.refresh()
            except OSError as error:
                print(f"Native catalog refresh failed: {error}", file=sys.stderr, flush=True)

    threading.Thread(target=refresh, daemon=True).start()
    command = runtime_command(binary, preset, args.host, args.port, args.fit_target, args.gpu_layers)
    environment = gpu_manager.configure_router_environment(os.environ)
    # Windows venv launchers can lose inherited redirected handles. Give the
    # actual native process explicit persistent log handles instead.
    runtime_stdout = (state_directory / "llama.stdout.log").open("a", encoding="utf-8")
    runtime_stderr = (state_directory / "llama.stderr.log").open("a", encoding="utf-8")
    runtime_stderr.write("Native TwitchAI model command: " + json.dumps(command) + "\n")
    runtime_stderr.flush()
    process = subprocess.Popen(command, cwd=binary.parent, env=environment,
                               creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                               stdout=runtime_stdout, stderr=runtime_stderr)
    job = create_windows_job(process)
    (state_directory / "runtime.json").write_text(json.dumps({"supervisorPid": os.getpid(), "serverPid": process.pid,
        "binary": str(binary), "modelRoot": str(root), "port": args.port, "contextPolicy": "max-fit",
        "fitTargetMiB": args.fit_target, "gpuLayers": args.gpu_layers,
        "reasoningPolicy": "model-highest", "reasoningBudget": -1, "sleepIdleSeconds": -1}), encoding="utf-8")

    def stop(*_):
        stopped.set()
        if process.poll() is None:
            with gpu_manager.lock:
                gpu_manager.sessions.save_all()
            process.terminate()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    try:
        while process.poll() is None:
            if stop_file.exists():
                stop()
            stopped.wait(0.25)
        return process.wait()
    finally:
        stopped.set()
        control.shutdown()
        control.server_close()
        if job:
            job[0].CloseHandle(job[1])
        runtime_stdout.close()
        runtime_stderr.close()


if __name__ == "__main__":
    raise SystemExit(main())
