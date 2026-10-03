# Claude review setup

Optional. Claude gives a **second opinion** on the local model's suggestions: you send items from the Review page
("Send to Claude", "Send all pending to Claude"), Claude reads them over the organiser's MCP server and submits a
verdict that appears beside the local suggestion. You still accept or dismiss every verdict yourself.

What it does and does not do:

- Claude gets **review tools only**: `list_pending_reviews`, `get_review_item`, `get_label_tree` (read-only) and
  `submit_review` (records a verdict). No tool changes Gmail; nothing is applied without your approval.
- It uses your Claude subscription, **not an API key**. In headless mode the token is read only by the Claude Code CLI
  inside the `api` container; it is never shown in the UI or logged.
- No model is passed unless you enter one under Settings → Claude → "Model (optional)"; empty means your subscription's
  default.

Pick one mode under **Settings → Claude → Mode**: **Claude Code (headless)** or **Claude Desktop**.

## Headless mode (Claude Code in the `api` container)

The api runs `claude -p` for each batch you send: at most "Max items per run" items per run, one run at a time, each
stopped after "Timeout per run (seconds)" or "Max turns per run" (all on the Settings page).

1. **Build the image with the CLI.** Add `WITH_CLAUDE=true` to your `.env` and rebuild:

   ```bash
   docker compose build api && docker compose up -d
   ```

   The default `false` leaves Node and the CLI out; `true` adds about 480 MB to the `api` image.

2. **Create a token** on your own machine (needs the
   [Claude Code CLI](https://docs.claude.com/en/docs/claude-code/setup) and a Claude subscription):

   ```bash
   claude setup-token
   ```

   It opens a browser to sign in and prints a long-lived token.

3. **Put the token in `.env`** (never commit `.env`):

   ```dotenv
   WITH_CLAUDE=true
   CLAUDE_CODE_OAUTH_TOKEN=<paste the token here>
   ```

   Then `docker compose up -d api` so the container picks it up.

4. **Choose "Claude Code (headless)"** in Settings, save, and press **Test connection**. A good result shows
   "Connected" with the time taken; the check covers the CLI version, the token being set and the MCP server answering.

### When a run fails

A failed run marks its items "Claude unavailable" with the error text, and the queue stops after the first failed run
until you send or retry again. The error kinds:

| Kind | Meaning | Fix |
|---|---|---|
| `cli_missing` | The image has no Claude Code CLI. | Set `WITH_CLAUDE=true` and rebuild (step 1). |
| `token_missing` | `CLAUDE_CODE_OAUTH_TOKEN` is not set. | Steps 2–3, then restart the api. |
| `auth` | The CLI rejected the token (expired or revoked). | Re-run `claude setup-token`, replace the token in `.env`, restart the api. |
| `rate_limit` | Your subscription's usage limit was hit. | Wait for the limit to reset, then retry. |
| `timeout` | The run took longer than "Timeout per run". | Retry, or raise the timeout or lower "Max items per run". |
| `failed` | Anything else (an invalid model name, an api restart mid-run, a CLI error). | Read the error text; check the model setting; retry. The api log has details. |

## Desktop mode (Claude Desktop)

Claude Desktop starts local MCP servers over stdio, so it reaches the organiser's `/mcp` endpoint through the
[`mcp-remote`](https://www.npmjs.com/package/mcp-remote) bridge, run by `npx`. You need **Node.js** on the machine that
runs Claude Desktop, and that machine must reach the organiser's address (`APP_BASE_URL`).

1. **Choose "Claude Desktop"** in Settings and save. The Claude Desktop section shows a
   `claude_desktop_config.json` entry; press **Copy**. It looks like this (it contains your MCP token, keep it
   private):

   ```json
   {
     "mcpServers": {
       "gmail-organiser": {
         "command": "npx",
         "args": ["mcp-remote", "http://localhost:5180/mcp", "--header", "Authorization: Bearer <token>"]
       }
     }
   }
   ```

2. **Add it to `claude_desktop_config.json`** (Claude Desktop → Settings → Developer → Edit Config), merging
   `gmail-organiser` into any existing `mcpServers`:

   | OS | Path |
   |---|---|
   | macOS | `~/Library/Application Support/Claude/claude_desktop_config.json` |
   | Windows | `%APPDATA%\Claude\claude_desktop_config.json` |

   See [Connect to local MCP servers](https://modelcontextprotocol.io/quickstart/user) for details.

3. **Restart Claude Desktop** fully (quit, not just close the window). The organiser's tools appear under the tools
   menu.

4. Send items to Claude on the Review page, press **Copy prompt** in Settings and paste it into a new Claude Desktop
   chat. **Test connection** in Settings checks that the MCP server answers with the current token.

**Status: expected to work, not yet verified by the owner** (see the owner check on #169). If Claude Desktop shows the
server as failed and its log points at the `Authorization` header (on some platforms a space inside an `args` entry is
mangled), use the header through an environment variable instead:

```json
{
  "mcpServers": {
    "gmail-organiser": {
      "command": "npx",
      "args": ["mcp-remote", "http://localhost:5180/mcp", "--header", "Authorization:${AUTH_HEADER}"],
      "env": { "AUTH_HEADER": "Bearer <token>" }
    }
  }
}
```

## Token rotation

- **MCP token** (Desktop mode, and what headless runs use internally): **Rotate token** in Settings. The old token
  stops working at once; copy the new entry into `claude_desktop_config.json` and restart Claude Desktop. Headless
  runs pick up the new token by themselves.
- **Claude Code token** (headless mode): run `claude setup-token` again, replace `CLAUDE_CODE_OAUTH_TOKEN` in `.env`,
  then `docker compose up -d api`. Revoke the old one in your Claude account settings if it may have leaked.

## Privacy

For each item you send, Claude sees what the review tools return: the local suggestion and its reason, sender counts,
up to five sample emails per item (subject, date, snippet, labels, attachment flag, list id), your label tree and up
to ten similar past decisions. Email **bodies** are read from Gmail only when Claude asks for them for a specific item
(`include_bodies`); they are not stored. Nothing is sent to Claude unless you send it, and what Claude sees is
handled under your Claude account's terms.
