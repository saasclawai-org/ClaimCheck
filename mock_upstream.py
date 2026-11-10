import json
import http.server

class H(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(n).decode()
        open("/tmp/mock-last-request.json", "w").write(body)
        if "RESPONSES-JSON" in body:
            # v0.9.19: non-streaming Responses API shape — instructions echo + output_text
            # + function_call arguments all carry a ticket the proxy must restore.
            resp = {
                "id": "resp_mock_1", "object": "response", "status": "completed",
                "instructions": "Client records reference [FEIN_1].",
                "output": [
                    {"type": "reasoning", "id": "rs_1", "summary": []},
                    {"type": "message", "id": "msg_1", "role": "assistant",
                     "content": [{"type": "output_text",
                                  "text": "The employer FEIN is [FEIN_1] per records.",
                                  "annotations": []}]},
                    {"type": "function_call", "id": "fc_1", "call_id": "call_1",
                     "name": "write_file",
                     "arguments": json.dumps({"path": "notes.txt", "note": "FEIN [FEIN_1] on file"}),
                     "status": "completed"},
                ],
                "usage": {"total_tokens": 10},
            }
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(resp).encode())
            return
        if "RESPONSES-STREAM" in body:
            # v0.9.19: streaming Responses API events — text deltas split a placeholder
            # across chunks; function_call arguments stream per-character; *.done and
            # response.completed carry complete (ticketed) text that must restore.
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()

            def ev(obj):
                self.wfile.write(f"data: {json.dumps(obj)}\n\n".encode())
                self.wfile.flush()

            text = "The employer FEIN is [FEIN_1] per records."
            args = json.dumps({"path": "notes.txt", "note": "FEIN [FEIN_1] on file"})
            ev({"type": "response.created",
                "response": {"id": "resp_1", "object": "response", "status": "in_progress"}})
            ev({"type": "response.output_item.added", "output_index": 0,
                "item": {"type": "message", "id": "msg_1", "status": "in_progress", "content": []}})
            for c in ["The employer FEIN ", "is [FE", "IN_1] per ", "records."]:
                ev({"type": "response.output_text.delta", "item_id": "msg_1",
                    "output_index": 0, "content_index": 0, "delta": c})
            ev({"type": "response.output_text.done", "item_id": "msg_1",
                "output_index": 0, "content_index": 0, "text": text})
            ev({"type": "response.content_part.done", "item_id": "msg_1", "output_index": 0,
                "content_index": 0,
                "part": {"type": "output_text", "text": text, "annotations": []}})
            ev({"type": "response.output_item.done", "output_index": 0,
                "item": {"type": "message", "id": "msg_1", "role": "assistant",
                         "content": [{"type": "output_text", "text": text, "annotations": []}]}})
            ev({"type": "response.output_item.added", "output_index": 1,
                "item": {"type": "function_call", "id": "fc_1", "call_id": "call_1",
                         "name": "write_file", "arguments": "", "status": "in_progress"}})
            for ch in args:
                ev({"type": "response.function_call_arguments.delta", "item_id": "fc_1",
                    "output_index": 1, "delta": ch})
            ev({"type": "response.function_call_arguments.done", "item_id": "fc_1",
                "output_index": 1, "arguments": args})
            ev({"type": "response.output_item.done", "output_index": 1,
                "item": {"type": "function_call", "id": "fc_1", "call_id": "call_1",
                         "name": "write_file", "arguments": args, "status": "completed"}})
            ev({"type": "response.completed",
                "response": {"id": "resp_1", "object": "response", "status": "completed",
                             "instructions": "Client records reference [FEIN_1].",
                             "output": [
                                 {"type": "message", "id": "msg_1", "role": "assistant",
                                  "content": [{"type": "output_text", "text": text, "annotations": []}]},
                                 {"type": "function_call", "id": "fc_1", "call_id": "call_1",
                                  "name": "write_file", "arguments": args, "status": "completed"}]}})
            return
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
            content = "The client FEIN is [FEIN_1] per records."
            if "CODEBLOCK-MODE" in body:
                # v0.9.12: non-streaming fenced-code response for suppress-code tests
                content = "Here is code:\n```js\nconst result = 1 + 1;\nconsole.log(result);\n```\nDone."
            resp = {"choices": [{"message": {"role": "assistant", "content": content}}],
                    "usage": {"total_tokens": 10}}
            self.wfile.write(json.dumps(resp).encode())

    def log_message(self, *a):
        pass

http.server.ThreadingHTTPServer(("127.0.0.1", 9988), H).serve_forever()
