import json
import http.server

class H(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(n).decode()
        open("/tmp/mock-last-request.json", "w").write(body)
        if '"stream":true' in body.replace(" ", ""):
            if "ECHOMODE" in body:
                # v0.9.0: echo the scrubbed user content per-character — streaming
                # restore regression for realistic-mode fakes split across fragments
                req = json.loads(body)
                user = "".join(m.get("content", "") for m in req.get("messages", []) if m.get("role") == "user")
                user = user.replace("ECHOMODE", "").strip()
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Connection", "close")
                self.close_connection = True
                self.end_headers()
                for ch in user:
                    ev = json.dumps({"choices": [{"delta": {"content": ch}}]})
                    self.wfile.write(f"data: {ev}\n\n".encode())
                    self.wfile.flush()
                self.wfile.write(b"data: [DONE]\n\n")
                self.wfile.flush()
                return
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()
            if "TOOLCALL-MODE" in body:
                # v0.8.9 regression: vLLM tokenizes tool_call arguments per character
                # (captured on proliant-vl: '[', 'S', 'S', 'N', '_', '1', ']'). Every
                # fragment must survive the proxy's partial-placeholder hold.
                args = json.dumps({"path": "secrets.txt",
                                   "content": "The client FEIN is [FEIN_1] per records."})
                ev = json.dumps({"choices": [{"delta": {"tool_calls": [
                    {"index": 0, "type": "function",
                     "function": {"name": "write_file", "arguments": ""}}]}}]})
                self.wfile.write(f"data: {ev}\n\n".encode())
                self.wfile.flush()
                for ch in args:
                    ev = json.dumps({"choices": [{"delta": {"tool_calls": [
                        {"index": 0, "function": {"arguments": ch}}]}}]})
                    self.wfile.write(f"data: {ev}\n\n".encode())
                    self.wfile.flush()
                self.wfile.write(b"data: [DONE]\n\n")
                self.wfile.flush()
                return
            # chunks deliberately split a placeholder across SSE events
            for c in ["The client ", "FEIN is [FE", "IN_1] per ", "records. Note [DONE_TEXT] tail"]:
                ev = json.dumps({"choices": [{"delta": {"content": c}}]})
                self.wfile.write(f"data: {ev}\n\n".encode())
                self.wfile.flush()
            self.wfile.write(b"data: [DONE]\n\n")
            self.wfile.flush()
        else:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            resp = {"choices": [{"message": {"role": "assistant", "content": "The client FEIN is [FEIN_1] per records."}}],
                    "usage": {"total_tokens": 10}}
            self.wfile.write(json.dumps(resp).encode())

    def log_message(self, *a):
        pass

http.server.ThreadingHTTPServer(("127.0.0.1", 9988), H).serve_forever()
