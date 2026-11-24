"""Platform-neutral checks of prosper.pyw (run anywhere: python test_prosper.py). The Windows parts (microphone,
clipboard, keystrokes, DPAPI) are exercised by using the script on Windows."""
import http.server
import importlib.machinery
import importlib.util
import json
import os
import struct
import sys
import tempfile
import threading
import unittest
import wave
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
loader = importlib.machinery.SourceFileLoader("prosper", os.path.join(HERE, "prosper.pyw"))
spec = importlib.util.spec_from_loader("prosper", loader)
prosper = importlib.util.module_from_spec(spec)
loader.exec_module(prosper)


class FakeOpenAI(http.server.BaseHTTPRequestHandler):
    """Answers like OpenAI: transcription, chat, and a Responses run with one MCP approval round."""
    seen = []

    def log_message(self, *a):
        pass

    def _send(self, code, obj):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        FakeOpenAI.seen.append(("GET", self.path, self.headers.get("Authorization"), None))
        self._send(200, {"data": []}) if self.path.endswith("/models") else self._send(404, {})

    def do_POST(self):
        raw = self.rfile.read(int(self.headers["Content-Length"]))
        FakeOpenAI.seen.append(("POST", self.path, self.headers.get("Authorization"), raw))
        if self.path.endswith("/audio/transcriptions"):
            ok = b'name="model"\r\n\r\ngpt-4o-mini-transcribe' in raw and b"RIFF" in raw
            self._send(200 if ok else 400, {"text": " um hello world "})
        elif self.path.endswith("/chat/completions"):
            body = json.loads(raw)
            if body["model"] == "bad":
                self._send(401, {"error": {"message": "Incorrect API key"}})
            else:
                self._send(200, {"choices": [{"message": {"content": "Hello, world."}}]})
        elif self.path.endswith("/responses"):
            body = json.loads(raw)
            if body.get("previous_response_id") == "resp_q":  # the user answered the Assistant's question
                self._send(200, {"id": "resp_a", "output": [{"type": "message", "content": [
                    {"type": "output_text", "text": "Hi Priya, Thursday works."}]}]})
            elif "QUESTION:" in body.get("instructions", "") and "draft the reply" in json.dumps(body["input"]):
                self._send(200, {"id": "resp_q", "output": [{"type": "message", "content": [
                    {"type": "output_text", "text": "QUESTION: Who is it for?"}]}]})
            elif "previous_response_id" not in body:
                self._send(200, {"id": "resp_1", "output": [
                    {"type": "mcp_list_tools", "tools": []},
                    {"type": "mcp_approval_request", "id": "apr_1", "name": "create_issue", "arguments": "{}"}]})
            else:
                approved = body["input"][0]["approve"]
                self._send(200, {"id": "resp_2", "output": [
                    {"type": "mcp_call", "name": "create_issue", "error": None},
                    {"type": "message", "content": [{"type": "output_text",
                                                     "text": "STATUS: Created issue #7" if approved else "Skipped"}]}]})


class Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = http.server.HTTPServer(("127.0.0.1", 0), FakeOpenAI)
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()
        cls.base = "http://127.0.0.1:%d/v1" % cls.server.server_port
        cls.real_unseal = prosper.unseal
        prosper.unseal = lambda v: v  # DPAPI is Windows-only: store test values in the clear

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()

    def settings(self, **kw):
        s = dict(prosper.DEFAULTS, api_key_sealed="sk-test", api_base=self.base)
        s.update(kw)
        return s

    def test_transcribe_sends_wav_multipart(self):
        path = os.path.join(tempfile.mkdtemp(), "clip.wav")
        with wave.open(path, "wb") as w:
            w.setnchannels(1), w.setsampwidth(2), w.setframerate(16000)
            w.writeframes(struct.pack("<4h", 0, 1000, -2000, 0))
        ai = prosper.OpenAI(self.settings())
        self.assertEqual(ai.transcribe(path), "um hello world")
        self.assertEqual(FakeOpenAI.seen[-1][2], "Bearer sk-test")
        self.assertEqual(prosper.wav_peak(path), 2000)

    def test_chat_and_errors(self):
        self.assertEqual(prosper.OpenAI(self.settings()).chat(prosper.DICTATION, "x"), "Hello, world.")
        sent = json.loads(FakeOpenAI.seen[-1][3])
        self.assertEqual(sent["reasoning_effort"], "none")  # gpt-6-luna: no thinking for cleanup
        with self.assertRaises(prosper.AiError) as e:
            prosper.OpenAI(self.settings(model="bad")).chat("s", "u")
        self.assertIn("401", str(e.exception))
        with self.assertRaises(prosper.AiError):
            prosper.OpenAI(dict(prosper.DEFAULTS))  # no key

    def test_assistant_with_mcp_approval(self):
        asked = []
        s = self.settings(mcp_on=True, mcp_server="https://mcp.example.com/mcp")
        text, status, calls = prosper.OpenAI(s).assistant(
            "file a bug", {"app": "Outlook", "about_me": "Akash"}, b"\x89PNG", lambda n, a: asked.append(n) or "allow",
            lambda step: None)
        self.assertEqual((text, status), ("Created issue #7", True))
        self.assertEqual(asked, ["create_issue"])
        self.assertEqual(calls, [{"tool": "create_issue", "ok": True}])
        first = json.loads([r for r in FakeOpenAI.seen if r[1].endswith("/responses")][-2][3])
        self.assertEqual(first["tools"][0]["require_approval"], "always")
        self.assertEqual(first["reasoning"], {"effort": "high"})
        self.assertTrue(first["input"][0]["content"][1]["image_url"].startswith("data:image/png;base64,"))
        self.assertIn(prosper.ACTIONS.strip()[:30], first["instructions"])
        self.assertNotIn("QUESTION:", first["instructions"])  # only asks where the answer can be typed

    def test_assistant_asks_then_continues(self):
        ai = prosper.OpenAI(self.settings())
        text, status, _ = ai.assistant("draft the reply", {"app": "Outlook", "selected_text": "see you?"}, None, None,
                                       lambda step: None, can_ask=True)
        self.assertEqual((text, status, ai.last_id), ("Who is it for?", "question", "resp_q"))
        text, status, _ = ai.assistant("Priya", {}, None, None, lambda step: None, previous="resp_q", can_ask=True)
        self.assertEqual((text, status, ai.last_id), ("Hi Priya, Thursday works.", False, "resp_a"))
        sent = json.loads([r for r in FakeOpenAI.seen if r[1].endswith("/responses")][-1][3])
        self.assertEqual(sent["previous_response_id"], "resp_q")
        self.assertEqual(sent["input"], "<request>\nPriya\n</request>")  # the rest is in the earlier response
        self.assertIn("spoke or typed", sent["instructions"])

    def test_shortcuts(self):
        self.assertEqual(prosper.parse_combo("Ctrl+Alt+Space"), (prosper.MOD_CTRL | prosper.MOD_ALT, 0x20))
        for text in ("Ctrl+Alt+T", "Ctrl+Shift+/", "Alt+F2", "Ctrl+Win+K", "F9", "Ctrl+Alt+Num 5"):
            self.assertEqual(prosper.combo_name(*prosper.parse_combo(text)), text)
        self.assertEqual(prosper.combo_name(*prosper.parse_combo("win+ctrl+k")), "Ctrl+Win+K")
        self.assertEqual(prosper.parse_combo(""), (0, 0))
        self.assertEqual(prosper.parse_combo("Ctrl+Nonsense"), (0, 0))
        self.assertFalse(prosper.combo_usable(*prosper.parse_combo("A")))
        self.assertFalse(prosper.combo_usable(*prosper.parse_combo("Shift+A")))
        self.assertTrue(prosper.combo_usable(*prosper.parse_combo("F9")))
        s = dict(prosper.DEFAULTS, keys={"ask": "Ctrl+Shift+K", "screen": ""})
        self.assertEqual(prosper.shortcut(s, "ask"), "Ctrl+Shift+K")
        self.assertEqual(prosper.shortcut(s, "screen"), "")  # turned off
        self.assertEqual(prosper.shortcut(s, "dictate"), "Ctrl+Alt+Space")  # never set: the default
        self.assertEqual(prosper.shortcut(dict(prosper.DEFAULTS), "ask"), "Ctrl+Alt+T")

    def test_test_connection(self):
        self.assertEqual(prosper.OpenAI(self.settings()).test(), "Connected to 127.0.0.1")

    def test_assist_message_layout(self):
        m = prosper.assist_message("make it formal", {"app": "Outlook", "selected_text": "hey", "about_me": ""}, False)
        self.assertTrue(m.startswith("<app>\nOutlook\n</app>"))
        self.assertIn("<selected_text>\nhey\n</selected_text>", m)
        self.assertNotIn("about_me", m)
        self.assertTrue(m.endswith("<request>\nmake it formal\n</request>"))
        self.assertIn("<screen>", prosper.assist_message("x", {}, True))

    def test_effort(self):
        self.assertEqual(prosper.effort("gpt-5-mini", False), "minimal")
        self.assertEqual(prosper.effort("gpt-6-luna", False), "none")
        self.assertEqual(prosper.effort("o4-mini", False), "low")
        self.assertEqual(prosper.effort("gpt-4o", True), None)
        self.assertEqual(prosper.effort("gpt-5.1", True), "high")

    def test_png_encoder(self):
        rows = [bytearray([255, 0, 0, 0, 255, 0]), bytearray([0, 0, 255, 9, 9, 9])]
        png = prosper.png_bytes(2, 2, rows)
        self.assertTrue(png.startswith(b"\x89PNG\r\n\x1a\n"))
        self.assertEqual(struct.unpack(">II", png[16:24]), (2, 2))
        idat_len = struct.unpack(">I", png[33:37])[0]
        raw = zlib.decompress(png[41:41 + idat_len])
        self.assertEqual(raw, b"\0" + bytes(rows[0]) + b"\0" + bytes(rows[1]))

    def test_multipart(self):
        body, ctype = prosper.multipart({"model": "m"}, "file", "a.wav", b"DATA", "audio/wav")
        boundary = ctype.split("boundary=")[1]
        self.assertTrue(body.endswith(("--%s--\r\n" % boundary).encode()))
        self.assertIn(b'filename="a.wav"\r\nContent-Type: audio/wav\r\n\r\nDATA\r\n', body)

    def test_cli_off_windows(self):
        self.assertEqual(prosper.main(["prosper", "nonsense"]), 2)
        if not prosper.IS_WIN:
            self.assertEqual(prosper.main(["prosper", "dictate"]), 1)


@unittest.skipUnless(prosper.IS_WIN, "Windows only")
class WindowsTests(unittest.TestCase):
    """The ctypes plumbing against the real Windows APIs."""

    def test_dpapi_roundtrip(self):
        sealed = prosper.seal("sk-secret ✓")
        self.assertNotIn("sk-secret", sealed)
        self.assertEqual(Tests.real_unseal(sealed), "sk-secret ✓")
        self.assertEqual(Tests.real_unseal("not-base64!"), "")

    def test_clipboard_roundtrip(self):
        saved = prosper.clip_get()
        try:
            self.assertTrue(prosper.clip_set("Prosper ✓ test", keep_in_history=False))
            self.assertEqual(prosper.clip_get(), "Prosper ✓ test")
        finally:
            prosper.clip_set(saved)

    def test_window_info_and_targets(self):
        name, pid, title, cls = prosper.window_info(prosper.GetForegroundWindow())
        self.assertIsInstance(name, str)
        self.assertFalse(prosper.is_target(None))

    def test_shortcut_thread_pauses_and_reloads(self):
        import queue
        import time
        combo = prosper.parse_combo("Ctrl+Alt+Shift+F12")  # unlikely to belong to anything on a test machine
        if not prosper.hotkey_free(*combo):
            self.skipTest("Ctrl+Alt+Shift+F12 is taken here")

        class FakeApp:
            hotkey_thread, results, commands = 0, queue.Queue(), queue.Queue()
        app = FakeApp()
        real_load = prosper.load_settings
        only_ask = dict({c: "" for c, _, _ in prosper.SHORTCUTS}, ask="Ctrl+Alt+Shift+F12")  # the rest off
        prosper.load_settings = lambda: dict(prosper.DEFAULTS, keys=only_ask)
        try:
            threading.Thread(target=prosper.hotkeys, args=(app,), daemon=True).start()

            def until(cond):
                end = time.time() + 3
                while time.time() < end and not cond():
                    time.sleep(0.05)
                return cond()
            self.assertTrue(until(lambda: app.hotkey_thread and not prosper.hotkey_free(*combo)))  # registered
            prosper.PostThreadMessageW(app.hotkey_thread, prosper.WM_APP_PAUSE_KEYS, 0, 0)
            self.assertTrue(until(lambda: prosper.hotkey_free(*combo)))  # paused: the keys reach settings
            prosper.PostThreadMessageW(app.hotkey_thread, prosper.WM_APP_RELOAD_KEYS, 0, 0)
            self.assertTrue(until(lambda: not prosper.hotkey_free(*combo)))  # registered again
        finally:
            prosper.PostThreadMessageW(app.hotkey_thread, prosper.WM_APP_PAUSE_KEYS, 0, 0)
            prosper.PostThreadMessageW(app.hotkey_thread, 0x0012, 0, 0)  # WM_QUIT
            prosper.load_settings = real_load

    def test_screenshot_is_png(self):
        try:
            png = prosper.screenshot(prosper.GetForegroundWindow(), max_side=320)
        except OSError as e:  # a CI machine may have no interactive desktop
            self.skipTest(str(e))
        self.assertTrue(png.startswith(b"\x89PNG"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
