# AzureGptProxy ([中文](./README.zh-CN.md))

> **Summary**
> This project proxies Anthropic Claude Code Messages API requests to Azure OpenAI `chat/completions` (and Responses where applicable), and converts responses back to Anthropic-compatible format. It supports SSE streaming and tool calls. It also supports Cursor proxy integration, based on the Cursor-Azure-GPT-5 project.

---

## Features

- **Protocol adaptation**: Convert Anthropic Messages API to Azure OpenAI Chat/Responses requests
- **Response conversion**: Map Azure OpenAI responses back to Anthropic Messages format
- **SSE streaming**: `message_start / content_block_delta / message_stop` events
- **Tool calls**: `tool_use / tool_result` support
- **Token counting**: `/v1/messages/count_tokens` local estimation
- **Cursor proxy**: Works as a Cursor-compatible proxy (based on Cursor-Azure-GPT-5)

---

## Cursor configuration

> This proxy follows the Cursor-Azure-GPT-5 configuration style.

1. Expose this service to the public internet (Cursor requires a public URL). You can publish it directly or use a Cloudflare Tunnel.
2. In Cursor Settings > Models > API Keys:
   - Set **OpenAI Base URL** to your public URL (e.g. `https://your-domain.example.com`).
   - Set **OpenAI API Key** to the value of `ANTHROPIC_AUTH_TOKEN` (or leave it empty if auth is disabled).
3. Create custom models named `gpt-high`, `gpt-medium`, `gpt-low` (optional: `gpt-minimal`).
4. Select these models in Cursor to use this proxy.

For more details, see Cursor-Azure-GPT-5: https://github.com/gabrii/Cursor-Azure-GPT-5

---

## Run locally

### 1. Prepare environment variables

Copy `.env.sample` to `.env` and fill in values:

```bash
copy .env.sample .env
```

### 2. Start the service

```bash
# Windows (PowerShell)
./start.ps1
```

The listening address is determined by `ASPNETCORE_URLS`. The startup log prints the final URL(s).

> Note: `start.ps1` loads `.env` and sets process-level environment variables.

---

## Docker build and run

### 1. Build image

```bash
docker build -t azuregptproxy:latest .
```

### 2. Prepare environment variables

Copy `.env.sample` to `.env` and fill in values:

```bash
copy .env.sample .env
```

### 3. Run container

```bash
# remove existing container with the same name (if any)
docker rm -f azuregptproxy

# run
docker run -d --name azuregptproxy --env-file .env -p 8088:8080 azuregptproxy:latest
```

---

## Environment variables

| Name | Description |
|------|-------------|
| `AZURE_OPENAI_ENDPOINT` | Azure OpenAI endpoint (required) |
| `AZURE_OPENAI_API_KEY` | Azure OpenAI API key (required) |
| `AZURE_API_VERSION` | API version (e.g. `2024-10-21`) |
| `ANTHROPIC_AUTH_TOKEN` | If set, `/v1/messages*` requires Bearer token |
| `SMALL_MODEL` | Small model deployment name (default for haiku) |
| `BIG_MODEL` | Large model deployment name (default for sonnet/opus) |

---

## API

### `POST /v1/messages`

- Anthropic Messages API compatible
- Supports `stream=true` SSE

### `POST /v1/messages/count_tokens`

- Local token estimation
- Does not trigger generation

---

## License

MIT
