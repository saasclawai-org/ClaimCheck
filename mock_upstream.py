import json
import http.server

class H(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(n).decode()
        open("/tmp/mock-last-request.json", "w").write(body)
        if '"stream":true' in body.replace(" ", ""):
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()
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
