# Ollama setup

Gmail Organiser uses [Ollama](https://ollama.com/) for every LLM call by default. Ollama runs on your **host** machine,
not in Docker, so it can use your GPU. Nothing leaves your machine.

With the **Claude API provider** (Settings → LLM provider) the analysis chat calls go to Anthropic instead, but Ollama
is still needed for the embedding model (memory similarity) and for the optional triage and vision models. With the
Claude API, the email content sent for analysis (sender, subject, snippet and body excerpt up to the body limit) leaves
this machine and is processed by Anthropic under your API account.

## 1. Install Ollama

- **macOS / Windows:** download the installer from [ollama.com/download](https://ollama.com/download) and start the
  app.
- **Linux:** follow the install instructions on [ollama.com/download](https://ollama.com/download); the installer sets
  Ollama up as a `systemd` service.

Check that it is running:

```bash
curl http://localhost:11434/api/version
```

## 2. Pull two models

You need one **chat model** (classifies mail and suggests labels, clean-up and filters) and one **embedding model**
(memory and similarity search). There is no default: pick what fits your hardware and enter the names in the setup
wizard or on the Settings page.

```bash
ollama pull <chat-model>        # e.g. a recent instruction-tuned model that fits in your GPU/RAM
ollama pull <embedding-model>   # e.g. nomic-embed-text
ollama list                     # shows the exact names to enter in the app
```

Larger chat models give better suggestions but analyse fewer emails per minute. Without an embedding model the app
still works; memory falls back to exact-sender matches.

Each chat, embedding and vision call may take up to `Llm__ModelTimeoutSeconds` (default 180, 1 to 3600; the api refuses
to start outside that range), model load included; set it in `.env` for a slow model or GPU. When the chat model and the
embedding model don't both fit in GPU memory, Ollama evicts one, and the next call pays its load time again. Chat calls
ask the model not to think (`think: false`), but some models accept only think levels and may still reason; if batches
still time out, pick a non-thinking chat model.

Optional: image attachments are read by local OCR (Tesseract, in the `api` image) by default. To have a **vision model**
describe and transcribe them instead, pull one (`ollama pull <vision-model>`), enter it as the vision model and set
the image mode to "vision" on the Settings page. Vision mode without a vision model skips images; the vision model gets
PNG, JPEG and WebP only (other formats are skipped as unsupported). Each image may take up to `Attachments__ImageTimeout`
(unset: one minute for OCR, and the LLM model timeout, 180 s, for the vision model, model load included). Set the
`Attachments__*` options in `.env`; compose passes them to the `api` container. When the API runs from the IDE, OCR needs
`tesseract` on the `PATH` (or `Attachments__TesseractPath`); without it image attachments are recorded as failed and
scanned PDFs stay unread.

## 3. Make Ollama reachable from the app

Which URL the API uses depends on how you run it. The value is `OLLAMA_BASE_URL` (in `.env` for compose) and can be
changed in the wizard or on the Settings page.

| Run mode | Ollama URL |
|---|---|
| Docker compose | `http://host.docker.internal:11434` (the compose default) |
| API from the IDE (`GmailOrganiser.Api` profile) | `http://localhost:11434` (set in `launchSettings.json`) |

### macOS (Docker Desktop)

Nothing to do. Docker Desktop resolves `host.docker.internal` to the host, and Ollama's default bind
(`127.0.0.1:11434`) is reachable from containers.

### Linux

Compose maps `host.docker.internal` to the host gateway (`extra_hosts: host-gateway`), but Ollama only listens on
`127.0.0.1` by default, which containers can't reach. Make it listen on all interfaces:

```bash
sudo systemctl edit ollama.service
```

Add:

```ini
[Service]
Environment="OLLAMA_HOST=0.0.0.0"
```

Then:

```bash
sudo systemctl daemon-reload
sudo systemctl restart ollama
```

**Firewall:** `0.0.0.0` means Ollama now accepts connections from your whole network, and Ollama has no
authentication. Block port `11434` from outside the machine, and allow only the Docker bridge networks. For example
with `ufw`:

```bash
sudo ufw allow from 172.16.0.0/12 to any port 11434 proto tcp   # Docker bridge networks first
sudo ufw deny 11434/tcp                                          # then nothing else
```

`ufw` applies the first matching rule, so the `allow` must come before the `deny`. Adjust the subnet if your Docker uses a different address pool (`docker network inspect gmail-organiser_default`).

### Development from the IDE

The API runs directly on the host, so it uses `http://localhost:11434` (already set in the `GmailOrganiser.Api`
launch profile). No bind change is needed on any OS.

## 4. Check it from the app

In the setup wizard (or on the Settings page):

1. **Test URL** — checks that the API can reach Ollama at the configured URL and lists the installed models.
2. **Test model** — sends a short prompt to the chosen chat model and checks that it answers.

If **Test URL** fails:

- Compose on Linux: check `OLLAMA_HOST=0.0.0.0` (step 3) and the firewall rule.
- Check that the URL has no typo and includes `http://` and the port.
- From the IDE, use `http://localhost:11434`, not `host.docker.internal`.

If **Test model** fails, check the name against `ollama list` (including the tag after `:`), and that the model
fits in memory: the first call loads it and can take a while.
