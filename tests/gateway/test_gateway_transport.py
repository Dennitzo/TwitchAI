"""Isolated HTTP regression tests; no GPU, Docker or Twitch connection is used."""
import copy
import http.client
import importlib.util
import io
import json
from pathlib import Path
import socket
import threading
import unittest
from email.message import Message
from http.server import ThreadingHTTPServer
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("twitchai_gateway", ROOT / "deploy/twitchai/gateway.py")
gateway = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gateway)


def headers(**values):
    result = Message()
    for name, value in values.items():
        result[name.replace("_", "-")] = str(value)
    return result


def model(name="coding/example", status="unloaded", mode="llama-native", levels="none|low|high"):
    return {"id": name, "status": {"value": status}, "tags": [
        "twitchai-context-policy:max-fit", "twitchai-reasoning-mode:" + mode,
        "twitchai-reasoning-levels:" + levels]}


class Response(io.BytesIO):
    status = 200

    def __init__(self, content, content_type="application/json"):
        super().__init__(content)
        self.headers = {"Content-Type": content_type}

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


class QuietHandler(gateway.Handler):
    def log_message(self, *_):
        pass


class BodyTests(unittest.TestCase):
    def test_fixed_length_preserves_utf8_without_reading_next_request(self):
        body = '{"text":"grüße"}'.encode()
        stream = io.BytesIO(body + b"NEXT")
        self.assertEqual(body, gateway.read_body(stream, headers(Content_Length=len(body))))
        self.assertEqual(b"NEXT", stream.read())

    def test_chunk_extensions_and_trailers_preserve_payload(self):
        stream = io.BytesIO(b"2;name=value\r\n{}\r\n0\r\nX-Example: yes\r\n\r\nNEXT")
        self.assertEqual(b"{}", gateway.read_body(stream, headers(Transfer_Encoding="chunked")))
        self.assertEqual(b"NEXT", stream.read())

    def test_invalid_framing_returns_client_errors(self):
        cases = [
            (b"", headers(), 411),
            (b"{}", headers(Content_Length=-1), 400),
            (b"{}", headers(Content_Length="abc"), 400),
            (b"{}", headers(Content_Length=3), 400),
            (b"", headers(Content_Length=gateway.MAX_BODY_BYTES + 1), 413),
            (b"", headers(Content_Length="9" * 5000), 413),
            (b"", headers(Content_Length=2, Transfer_Encoding="chunked"), 400),
            (b"", headers(Transfer_Encoding="gzip, chunked"), 501),
            (b"z\r\n", headers(Transfer_Encoding="chunked"), 400),
            (b"2\r\n{", headers(Transfer_Encoding="chunked"), 400),
            (b"2\r\n{}XX", headers(Transfer_Encoding="chunked"), 400),
            (b"0\r\nContent-Length: 2\r\n\r\n", headers(Transfer_Encoding="chunked"), 400),
            (b"1000001\r\n", headers(Transfer_Encoding="chunked"), 413),
            (b"1" * 8193, headers(Transfer_Encoding="chunked"), 400),
        ]
        for body, framing, expected in cases:
            with self.subTest(framing=[(key, value[:80]) for key, value in framing.items()], body=body[:40]):
                with self.assertRaises(gateway.RequestError) as caught:
                    gateway.read_body(io.BytesIO(body), framing)
                self.assertEqual(expected, caught.exception.status)

    def test_duplicate_lengths_are_rejected(self):
        framing = headers(Content_Length=2)
        framing["Content-Length"] = "2"
        with self.assertRaises(gateway.RequestError) as caught:
            gateway.read_body(io.BytesIO(b"{}"), framing)
        self.assertEqual(400, caught.exception.status)

    def test_invalid_json_is_rejected(self):
        for body in (b"", b"[]", b"null", b"{", b'{"a":1,"a":2}', b'{"a":NaN}', b'{"a":1e999}', b"\xff"):
            with self.subTest(body=body):
                with self.assertRaises(gateway.RequestError) as caught:
                    gateway.json_document(body)
                self.assertEqual(400, caught.exception.status)


class HttpTests(unittest.TestCase):
    def setUp(self):
        self.catalog = [model(), model("coding/second"), model("embedding/example"), model("coding/example@subagent")]
        self.native_calls = []
        self.completions = []
        self.slot_busy = False
        self.response_factory = lambda: Response(b'{"choices":[{"message":{"content":"{}"}}]}')
        self.patchers = [patch.object(gateway, "native_json", self.native),
                         patch.object(gateway.urllib.request, "urlopen", self.completion),
                         patch.object(gateway, "BODY_TIMEOUT", 0.3)]
        for patcher in self.patchers:
            patcher.start()
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), QuietHandler)
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": 0.01}, daemon=True)
        self.thread.start()
        self.port = self.server.server_port

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        for patcher in reversed(self.patchers):
            patcher.stop()

    def native(self, base, path, data=None, timeout=30):
        self.native_calls.append((base, path, copy.deepcopy(data)))
        if path == "/v1/models":
            return {"data": copy.deepcopy(self.catalog)}
        if path.startswith("/slots?"):
            return [{"is_processing": self.slot_busy}]
        if path == "/models/unload":
            next(m for m in self.catalog if m["id"] == data["model"])["status"]["value"] = "unloaded"
            return {"success": True}
        if path == "/models/load":
            selected = next(m for m in self.catalog if m["id"] == data["model"])
            reused = selected["status"]["value"] == "loaded"
            selected["status"]["value"] = "loaded"
            return {"success": True, "reused": reused}
        raise AssertionError((base, path, data))

    def completion(self, request, timeout):
        self.assertTrue(gateway.MODEL_LOCK.locked())
        self.completions.append(json.loads(request.data))
        return self.response_factory()

    def request(self, path="/v1/chat/completions", payload=None, chunked=False, method="POST", content_type="application/json"):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        body = None if method == "GET" else json.dumps(payload if payload is not None else {
            "model": "coding/example", "messages": [{"role": "user", "content": "guten morgen"}],
            "reasoning_effort": "low"}).encode()
        connection.request(method, path, body=iter([body[:9], body[9:]]) if chunked else body,
                           headers={"Content-Type": content_type}, encode_chunked=chunked)
        response = connection.getresponse()
        result = response.status, response.headers.get("Content-Type"), response.read()
        connection.close()
        return result

    def raw_request(self, raw, shutdown_write=True):
        connection = socket.create_connection(("127.0.0.1", self.port), timeout=5)
        connection.sendall(raw)
        if shutdown_write:
            connection.shutdown(socket.SHUT_WR)
        response = http.client.HTTPResponse(connection)
        response.begin()
        result = response.status, json.loads(response.read())
        connection.close()
        return result

    def test_fixed_length_and_chunked_bodies_reach_inference_unchanged(self):
        for chunked in (False, True):
            status, _, _ = self.request(chunked=chunked)
            self.assertEqual(200, status)
        self.assertEqual(2, len(self.completions))
        self.assertEqual(self.completions[0], self.completions[1])
        self.assertEqual("guten morgen", self.completions[0]["messages"][0]["content"])

    def test_health_and_catalog_include_only_chat_models_and_nonempty_levels(self):
        self.assertEqual(200, self.request("/health", method="GET")[0])
        self.catalog[0]["tags"][-1] = "twitchai-reasoning-levels:"
        status, _, body = self.request("/v1/models", method="GET")
        self.assertEqual(200, status)
        result = json.loads(body)["data"]
        self.assertEqual(["coding/example", "coding/second"], [m["id"] for m in result])
        self.assertEqual(["auto"], result[0]["reasoning_levels"])

    def test_explicit_chunked_load_is_idempotent_and_never_infers(self):
        for reused in (False, True):
            status, _, body = self.request("/v1/models/load", {"model": "coding/example"}, chunked=True)
            self.assertEqual(200, status)
            self.assertEqual({"model": "coding/example", "status": "loaded", "success": True, "reused": reused}, json.loads(body))
        self.assertEqual([], self.completions)

    def test_unknown_model_and_unsupported_reasoning_are_structured_errors_without_load(self):
        for payload, code in (({"model": "coding/missing"}, "model_not_found"),
                              ({"model": "coding/example", "reasoning_effort": "invalid"}, "reasoning_unsupported")):
            status, content_type, body = self.request("/v1/models/load", payload)
            self.assertEqual(400, status)
            self.assertIn("application/json", content_type)
            self.assertEqual(code, json.loads(body)["error"]["code"])
        self.assertFalse(any(path == "/models/load" for _, path, _ in self.native_calls))

    def test_malformed_or_empty_post_never_loads_or_infers(self):
        for body in (b"", b"{", b"[]"):
            status, response = self.raw_request(b"POST /v1/models/load HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: " + str(len(body)).encode() + b"\r\n\r\n" + body)
            self.assertEqual(400, status)
            self.assertIn("error", response)
        self.assertEqual([], self.native_calls)
        self.assertEqual([], self.completions)

    def test_missing_length_truncation_conflicting_framing_and_body_timeout(self):
        cases = [(b"", b"", True, 411), (b"Content-Length: 5\r\n", b"{}", True, 400),
                 (b"Content-Length: 2\r\nTransfer-Encoding: chunked\r\n", b"{}", True, 400),
                 (b"Content-Length: 5\r\n", b"{", False, 408)]
        for framing, body, end, expected in cases:
            status, response = self.raw_request(b"POST /v1/models/load HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n" + framing + b"\r\n" + body, end)
            self.assertEqual(expected, status)
            self.assertIn("error", response)

    def test_wrong_content_type_is_415(self):
        self.assertEqual(415, self.request("/v1/models/load", {"model": "coding/example"}, content_type="text/plain")[0])

    def test_idle_model_switch_unloads_before_loading(self):
        self.catalog[0]["status"]["value"] = "loaded"
        status, _, _ = self.request("/v1/models/load", {"model": "coding/second"})
        self.assertEqual(200, status)
        operations = [(path, data) for _, path, data in self.native_calls if path in ("/models/load", "/models/unload")]
        self.assertEqual([("/models/unload", {"model": "coding/example"}), ("/models/load", {"model": "coding/second"})], operations)

    def test_busy_model_is_not_unloaded(self):
        self.catalog[0]["status"]["value"] = "loaded"
        self.slot_busy = True
        status, _, body = self.request("/v1/models/load", {"model": "coding/second"})
        self.assertEqual(409, status)
        self.assertEqual("model_busy", json.loads(body)["error"]["code"])
        self.assertFalse(any(path == "/models/unload" for _, path, _ in self.native_calls))

    def test_toggle_reasoning_is_translated_without_losing_other_template_kwargs(self):
        self.catalog[0] = model(mode="llama-thinking-toggle", levels="none|on")
        status, _, _ = self.request(payload={"model": "coding/example", "messages": [{"role": "user", "content": "hello"}],
                                           "reasoning_effort": "none", "chat_template_kwargs": {"existing": 1}})
        self.assertEqual(200, status)
        self.assertEqual({"existing": 1, "thinking": False}, self.completions[0]["chat_template_kwargs"])
        self.assertEqual("none", self.completions[0]["reasoning_effort"])

    def test_native_reasoning_controls_template_and_preserves_kwargs(self):
        for effort in ("none", "low"):
            with self.subTest(effort=effort):
                status, _, _ = self.request(payload={"model": "coding/example",
                    "messages": [{"role": "user", "content": "hello"}],
                    "reasoning_effort": effort, "chat_template_kwargs": {"existing": 1}})
                self.assertEqual(200, status)
                self.assertEqual({"existing": 1, "enable_thinking": effort != "none", "reasoning_strength": effort},
                                 self.completions[-1]["chat_template_kwargs"])
                self.assertEqual(effort, self.completions[-1]["reasoning_effort"])

    def test_streamed_response_is_forwarded_with_one_http_status(self):
        self.response_factory = lambda: Response(b'data: {"choices":[]}\n\ndata: [DONE]\n\n', "text/event-stream")
        status, content_type, body = self.request(payload={"model": "coding/example", "messages": [{"role": "user", "content": "hello"}], "stream": True})
        self.assertEqual(200, status)
        self.assertEqual("text/event-stream", content_type)
        self.assertEqual(b'data: {"choices":[]}\n\ndata: [DONE]\n\n', body)

    def test_model_switch_waits_until_current_inference_response_finishes(self):
        entered = threading.Event()
        release = threading.Event()
        switched = threading.Event()
        results = {}

        class BlockingResponse(Response):
            def read1(self, size):
                if self.tell() == 0:
                    entered.set()
                    if not release.wait(3):
                        raise TimeoutError("Test did not release inference")
                return super().read1(size)

        self.response_factory = lambda: BlockingResponse(b'{"choices":[]}')

        def infer():
            try:
                results["inference"] = self.request()[0]
            except Exception as error:
                results["inference"] = repr(error)

        def switch():
            try:
                results["switch"] = self.request("/v1/models/load", {"model": "coding/second"})[0]
            except Exception as error:
                results["switch"] = repr(error)
            finally:
                switched.set()

        inference_thread = threading.Thread(target=infer)
        switch_thread = threading.Thread(target=switch)
        inference_thread.start()
        try:
            self.assertTrue(entered.wait(2), "Inference response was not started")
            switch_thread.start()
            self.assertFalse(switched.wait(0.1), "Model switched while inference was still active")
            self.assertFalse(any(path == "/models/unload" for _, path, _ in self.native_calls))
        finally:
            release.set()
            inference_thread.join(timeout=3)
            if switch_thread.ident is not None:
                switch_thread.join(timeout=3)
        self.assertEqual({"inference": 200, "switch": 200}, results)
        self.assertEqual("unloaded", self.catalog[0]["status"]["value"])
        self.assertEqual("loaded", self.catalog[1]["status"]["value"])

    def test_midstream_failure_does_not_emit_a_second_status_or_json_error(self):
        class BrokenResponse(Response):
            def read1(self, size):
                if self.tell() > 0:
                    raise TimeoutError("Native stream failed after headers")
                return super().read1(size)

        first_event = b'data: {"choices":[]}\n\n'
        self.response_factory = lambda: BrokenResponse(first_event, "text/event-stream")
        status, content_type, body = self.request(payload={"model": "coding/example", "messages": [{"role": "user", "content": "hello"}], "stream": True})
        self.assertEqual(200, status)
        self.assertEqual("text/event-stream", content_type)
        self.assertEqual(first_event, body)

    def test_upstream_timeout_is_504_not_a_client_body_timeout(self):
        with patch.object(gateway, "native_json", side_effect=TimeoutError()):
            status, _, body = self.request("/v1/models", method="GET")
        self.assertEqual(504, status)
        self.assertEqual("upstream_timeout", json.loads(body)["error"]["code"])


if __name__ == "__main__":
    unittest.main()
