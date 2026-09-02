"""Synthetic echo behavior over a private gateway; standard library only."""

import json
import os
import time
import urllib.parse
import urllib.request
import uuid
from pathlib import Path


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def main():
    base = os.environ.get("BOT_GATEWAY_URL", "http://127.0.0.1:8080").rstrip("/")
    uri = urllib.parse.urlsplit(base)
    if (uri.username or uri.password or uri.query or uri.fragment or
            (uri.scheme != "https" and not
             (uri.scheme == "http" and uri.hostname in ("127.0.0.1", "::1")))):
        raise ValueError("Use HTTPS or a loopback-only gateway.")
    token_file = Path(os.environ["BOT_GATEWAY_TOKEN_FILE"])
    opener = urllib.request.build_opener(NoRedirect())

    def call(method, payload=None):
        token = token_file.read_text(encoding="utf-8").strip()
        data = None if payload is None else json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(base + "/bot/v1/" + method, data=data,
                                        headers={"Authorization": "Bearer " + token,
                                                 "Content-Type": "application/json"})
        with opener.open(request, timeout=10) as response:
            if response.status == 204:
                return None
            body = response.read(2 * 1024 * 1024 + 1)
            if len(body) > 2 * 1024 * 1024:
                raise ValueError("Gateway response exceeded the limit.")
            return json.loads(body)

    profile = call("getMe")
    namespace = uuid.UUID(profile["botUserId"])
    while True:
        try:
            for update in call("getUpdates", {"limit": 20})["updates"]:
                request_id = uuid.uuid5(namespace, profile["revision"] + ":" + str(update["updateId"]))
                result = call("sendMessage", {
                    "conversationId": update["conversationId"],
                    "requestId": str(request_id),
                    "text": update["text"],
                    "replyToContentId": update["contentId"],
                })
                if result["succeeded"]:
                    call("acknowledgeUpdate", {"updateId": update["updateId"]})
        except (OSError, ValueError, KeyError):
            print("Bot request failed; pending updates will be retried.", flush=True)
        time.sleep(2)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
    except (OSError, ValueError, KeyError):
        raise SystemExit("Check private gateway configuration; no payload details are logged.") from None
