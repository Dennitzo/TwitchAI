"""Bounded HTTP/JSON gateway for the independently owned TwitchAI llama router."""
import json
import math
import re
import signal
import socket
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

UPSTREAM = "http://host.docker.internal:8181"
CONTROL = "http://host.docker.internal:8182"
MAX_BODY_BYTES = 16 * 1024 * 1024
MAX_FRAMING_BYTES = 64 * 1024
BODY_TIMEOUT = 30
LOAD_TIMEOUT = 1200
INFERENCE_TIMEOUT = 1800
MODEL_LOCK = threading.Lock()
ACTIVE_STATES = {"loaded", "loading", "sleeping"}


class RequestError(Exception):
    def __init__(self, status, message, code="invalid_request"):
        super().__init__(message)
        self.status, self.code = status, code


def read_body(stream, headers):
    """Decode both fixed-length and HttpClient's chunked JSON, without overread."""
    lengths = headers.get_all("Content-Length", [])
    transfers = headers.get_all("Transfer-Encoding", [])
    if transfers and lengths:
        raise RequestError(400, "Content-Length and Transfer-Encoding cannot be combined.")
    if len(lengths) > 1:
        raise RequestError(400, "Duplicate Content-Length is not accepted.")
    if transfers:
        if len(transfers) != 1 or transfers[0].strip().lower() != "chunked":
            raise RequestError(501, "Only chunked Transfer-Encoding is supported.")
        result = bytearray()
        framing_bytes = 0

        def line():
            nonlocal framing_bytes
            value = stream.readline(8193)
            framing_bytes += len(value)
            if len(value) > 8192 or framing_bytes > MAX_FRAMING_BYTES:
                raise RequestError(400, "Chunk framing exceeds its limit.")
            if not value.endswith(b"\r\n"):
                raise RequestError(400, "Truncated or malformed chunk framing.")
            return value[:-2]

        while True:
            header = line()
            size_text = header.split(b";", 1)[0]
            if not re.fullmatch(rb"[0-9a-fA-F]{1,16}", size_text):
                raise RequestError(400, "Invalid chunk size.")
            size = int(size_text, 16)
            if size == 0:
                while trailer := line():
                    if not re.fullmatch(rb"[!#$%&'*+.^_`|~0-9A-Za-z-]+:[\t\x20-\x7e\x80-\xff]*", trailer):
                        raise RequestError(400, "Invalid chunk trailer.")
                    if trailer.split(b":", 1)[0].lower() in (b"content-length", b"transfer-encoding", b"host"):
                        raise RequestError(400, "Framing fields are forbidden in chunk trailers.")
                break
            if len(result) + size > MAX_BODY_BYTES:
                raise RequestError(413, "Request body exceeds 16 MiB.")
            data = stream.read(size)
            if len(data) != size or stream.read(2) != b"\r\n":
                raise RequestError(400, "Truncated or malformed chunk body.")
            result.extend(data)
        return bytes(result)
    if not lengths:
        raise RequestError(411, "Content-Length or chunked Transfer-Encoding is required.")
    if not re.fullmatch(r"[0-9]+", lengths[0].strip()):
        raise RequestError(400, "Invalid Content-Length.")
    length_text = lengths[0].strip().lstrip("0") or "0"
    limit_text = str(MAX_BODY_BYTES)
    if len(length_text) > len(limit_text) or (len(length_text) == len(limit_text) and length_text > limit_text):
        raise RequestError(413, "Request body exceeds 16 MiB.")
    size = int(length_text)
    data = stream.read(size)
    if len(data) != size:
        raise RequestError(400, "Truncated request body.")
    return data


def json_document(body):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("duplicate key")
            result[key] = value
        return result

    def invalid_constant(_):
        raise ValueError("non-JSON number")

    def finite_float(value):
        number = float(value)
        if not math.isfinite(number):
            raise ValueError("non-finite JSON number")
        return number

    try:
        result = json.loads(body.decode("utf-8"), object_pairs_hook=pairs,
                            parse_constant=invalid_constant, parse_float=finite_float)
    except (UnicodeError, ValueError, RecursionError) as error:
        raise RequestError(400, "Request body must contain valid UTF-8 JSON.") from error
    if not isinstance(result, dict):
        raise RequestError(400, "Request body must be a JSON object.")
    return result


def native_json(base, path, data=None, timeout=30):
    encoded = None if data is None else json.dumps(data, allow_nan=False).encode("utf-8")
    request = urllib.request.Request(base + path, data=encoded, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        raw = response.read(MAX_BODY_BYTES + 1)
        if len(raw) > MAX_BODY_BYTES:
            raise RequestError(502, "Native response exceeds its limit.", "upstream_response_invalid")
        try:
            return json.loads(raw)
        except (UnicodeError, ValueError) as error:
            raise RequestError(502, "Native response is not JSON.", "upstream_response_invalid") from error


def tags_of(model):
    tags = model.get("tags", model.get("meta", {}).get("tags", []))
    if isinstance(tags, str):
        tags = tags.split(",")
    return tags if isinstance(tags, list) else []


def reasoning_profile(model):
    tags = tags_of(model)

    def tag(prefix, fallback=""):
        return next((value[len(prefix):] for value in tags if isinstance(value, str) and value.startswith(prefix)), fallback)

    levels = list(dict.fromkeys(level for level in tag("twitchai-reasoning-levels:").split("|") if level))
    return tag("twitchai-reasoning-mode:", "automatic"), levels


def allowed_model(model):
    name = model.get("id")
    return isinstance(name, str) and name.startswith("coding/") and "@" not in name


def selected_model(data, catalog):
    selected = next((model for model in catalog if allowed_model(model) and model["id"] == data.get("model")), None)
    if selected is None:
        raise RequestError(400, "Select an available local chat model.", "model_not_found")
    return selected


def apply_reasoning(data, model):
    effort = data.pop("reasoning_effort", "auto")
    if not isinstance(effort, str):
        raise RequestError(400, "reasoning_effort must be a string.", "reasoning_unsupported")
    mode, levels = reasoning_profile(model)
    if effort == "auto":
        return
    if effort not in levels:
        raise RequestError(400, "Reasoning level is unsupported by the selected model.", "reasoning_unsupported")
    if mode in ("llama-toggle", "llama-thinking-toggle"):
        kwargs = data.get("chat_template_kwargs", {})
        if not isinstance(kwargs, dict):
            raise RequestError(400, "chat_template_kwargs must be an object.")
        key = "thinking" if mode == "llama-thinking-toggle" else "enable_thinking"
        data["chat_template_kwargs"] = dict(kwargs, **{key: effort == "on"})
        if effort == "none":
            data["reasoning_effort"] = "none"
    elif mode == "llama-native":
        kwargs = data.get("chat_template_kwargs", {})
        if not isinstance(kwargs, dict):
            raise RequestError(400, "chat_template_kwargs must be an object.")
        data["chat_template_kwargs"] = dict(kwargs, enable_thinking=effort != "none", reasoning_strength=effort)
        data["reasoning_effort"] = effort
    elif mode != "llama-fixed":
        data["reasoning_effort"] = effort


def ensure_loaded(model, catalog):
    """Call with MODEL_LOCK held through any subsequent inference."""
    name = model["id"]
    active = [item for item in catalog if item.get("status", {}).get("value") in ACTIVE_STATES]
    previous = [item for item in active if item["id"] not in (name, name + "@subagent")]
    for item in previous:
        if not any(isinstance(tag, str) and tag.startswith("twitchai-") for tag in tags_of(item)):
            raise RequestError(409, "A model outside the TwitchAI catalog is active.", "model_busy")
        if item.get("status", {}).get("value") == "loading":
            raise RequestError(409, "The previous model is still loading.", "model_busy")
        slots = native_json(UPSTREAM, "/slots?model=" + urllib.parse.quote(item["id"], safe=""))
        if not isinstance(slots, list) or any(not isinstance(slot, dict) or slot.get("is_processing") is not False for slot in slots):
            raise RequestError(409, "The previous model is processing a request.", "model_busy")
    for item in previous:
        native_json(UPSTREAM, "/models/unload", {"model": item["id"]}, timeout=60)
    if previous:
        previous_ids = {item["id"] for item in previous}
        deadline = time.monotonic() + 60
        while True:
            current = native_json(UPSTREAM, "/v1/models").get("data", [])
            if not any(item["id"] in previous_ids and item.get("status", {}).get("value") in ACTIVE_STATES for item in current):
                break
            if time.monotonic() >= deadline:
                raise RequestError(504, "Previous model did not unload within 60 seconds.", "model_unload_timeout")
            time.sleep(0.1)
    result = native_json(CONTROL, "/models/load", {"model": name}, timeout=LOAD_TIMEOUT)
    if not isinstance(result, dict) or result.get("success") is not True:
        raise RequestError(502, "Native model load did not report success.", "model_load_failed")
    return dict(result, model=name, status="loaded")


class Handler(BaseHTTPRequestHandler):
    def setup(self):
        super().setup()
        self.connection.settimeout(BODY_TIMEOUT)
        self.response_started = False

    def begin_response(self, status, content_type, length=None):
        self.response_started = True
        self.close_connection = True
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Connection", "close")
        if length is not None:
            self.send_header("Content-Length", str(length))
        self.end_headers()

    def send_json(self, status, data):
        encoded = json.dumps(data, ensure_ascii=False, allow_nan=False).encode("utf-8")
        self.begin_response(status, "application/json; charset=utf-8", len(encoded))
        self.wfile.write(encoded)

    def error_json(self, status, message, code="invalid_request"):
        if not self.response_started:
            self.send_json(status, {"error": {"code": code, "message": message, "type": "invalid_request_error" if status < 500 else "server_error"}})

    def do_GET(self):
        self.handle_request()

    def do_POST(self):
        self.handle_request()

    def handle_request(self):
        try:
            if self.command == "GET" and self.path == "/health":
                self.send_json(200, {"owner": "twitchai"})
            elif self.command == "GET" and self.path == "/v1/models":
                payload = native_json(UPSTREAM, "/v1/models")
                payload["data"] = [model for model in payload.get("data", []) if allowed_model(model)]
                for model in payload["data"]:
                    model["reasoning_levels"] = ["auto", *reasoning_profile(model)[1]]
                self.send_json(200, payload)
            elif self.command == "POST" and self.path in ("/v1/models/load", "/v1/chat/completions"):
                if self.headers.get_content_type() != "application/json":
                    raise RequestError(415, "Content-Type must be application/json.")
                try:
                    body = read_body(self.rfile, self.headers)
                except (socket.timeout, TimeoutError) as error:
                    raise RequestError(408, "Request body timed out.", "request_timeout") from error
                data = json_document(body)
                if self.path == "/v1/chat/completions":
                    if not isinstance(data.get("messages"), list) or not data["messages"]:
                        raise RequestError(400, "messages must be a non-empty array.")
                    if "stream" in data and not isinstance(data["stream"], bool):
                        raise RequestError(400, "stream must be a boolean.")
                if not MODEL_LOCK.acquire(timeout=INFERENCE_TIMEOUT):
                    raise RequestError(503, "Another model operation is still active.", "model_busy")
                try:
                    catalog = native_json(UPSTREAM, "/v1/models").get("data", [])
                    model = selected_model(data, catalog)
                    apply_reasoning(data, model)
                    result = ensure_loaded(model, catalog)
                    if self.path == "/v1/models/load":
                        self.send_json(200, result)
                    else:
                        encoded = json.dumps(data, allow_nan=False).encode("utf-8")
                        request = urllib.request.Request(UPSTREAM + "/v1/chat/completions", data=encoded, headers={"Content-Type": "application/json"})
                        with urllib.request.urlopen(request, timeout=INFERENCE_TIMEOUT) as response:
                            self.begin_response(response.status, response.headers.get("Content-Type", "application/json"))
                            while chunk := response.read1(65536):
                                self.wfile.write(chunk)
                                self.wfile.flush()
                finally:
                    MODEL_LOCK.release()
            else:
                self.error_json(404, "Unknown gateway resource.", "not_found")
        except RequestError as error:
            self.error_json(error.status, str(error), error.code)
        except urllib.error.HTTPError as error:
            if not self.response_started:
                body = error.read(MAX_BODY_BYTES)
                try:
                    payload = json.loads(body)
                    message = payload.get("error", {}).get("message", "Native server rejected the request.")
                except (ValueError, AttributeError):
                    message = "Native server rejected the request."
                self.error_json(error.code, message, "upstream_http_error")
        except (BrokenPipeError, ConnectionResetError):
            pass
        except (socket.timeout, TimeoutError):
            self.error_json(504, "The native model runtime timed out.", "upstream_timeout")
        except urllib.error.URLError as error:
            if isinstance(error.reason, (socket.timeout, TimeoutError)):
                self.error_json(504, "The native model runtime timed out.", "upstream_timeout")
            else:
                self.error_json(502, "The native model runtime is unavailable.", "upstream_unavailable")
        except Exception:
            self.error_json(502, "The native model runtime could not complete the request.", "upstream_failure")


if __name__ == "__main__":
    server = ThreadingHTTPServer(("0.0.0.0", 8180), Handler)

    def stop_server(_signal, _frame):
        # shutdown must run outside the serve_forever thread, including as PID 1.
        threading.Thread(target=server.shutdown, daemon=True).start()

    signal.signal(signal.SIGTERM, stop_server)
    signal.signal(signal.SIGINT, stop_server)
    try:
        server.serve_forever()
    finally:
        server.server_close()
