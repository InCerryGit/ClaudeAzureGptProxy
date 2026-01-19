# AzureGptProxy ([English](./README.md))

> **简要说明**
> 该项目用于将 Anthropic Claude Code 的 Messages API 请求代理到 Azure OpenAI `chat/completions` 端点，并在响应侧转换回 Anthropic 兼容格式（支持 SSE 流式响应与工具调用）。同时支持 Cursor 代理接入，基于 Cursor-Azure-GPT-5 项目。

---

## 🚀 功能简介

- **协议适配**：将 Anthropic Messages API 请求转换为 Azure OpenAI Chat/Responses 请求格式
- **响应转换**：将 Azure OpenAI 响应重新映射为 Anthropic Messages 格式
- **SSE 流式支持**：支持 `message_start / content_block_delta / message_stop` 事件流
- **Tool 调用支持**：支持 tool_use / tool_result
- **Token 统计支持**：支持 `/v1/messages/count_tokens` 本地估算
- **Cursor 代理**：可作为 Cursor 代理接入（基于 Cursor-Azure-GPT-5）

---

## 🧭 Cursor 配置

> 本代理参考 Cursor-Azure-GPT-5 的配置方式。

1. 将服务暴露到公网（Cursor 需要公网可访问 URL），可以直接发布或使用 Cloudflare Tunnel。
2. 在 Cursor 设置 > Models > API Keys 中：
   - **OpenAI Base URL** 填入你的公网地址（例如 `https://your-domain.example.com`）。
   - **OpenAI API Key** 填入 `ANTHROPIC_AUTH_TOKEN` 的值（若未启用鉴权可留空）。
3. 新建自定义模型：`gpt-high`、`gpt-medium`、`gpt-low`（可选：`gpt-minimal`）。
4. 在 Cursor 中选择这些模型即可使用本代理。

更多细节参考 Cursor-Azure-GPT-5：https://github.com/gabrii/Cursor-Azure-GPT-5

---

## 🏃‍♂️ 本地运行

### 1. 准备环境变量

复制 `.env.sample` 为 `.env` 并按需填写：

```bash
copy .env.sample .env
```

### 2. 运行服务

```bash
# Windows (PowerShell)
./start.ps1
```

默认监听地址取决于 `ASPNETCORE_URLS`，启动日志会输出监听地址。

> 说明：`start.ps1` 会读取 `.env` 并设置进程级环境变量。

---

## 📦 Docker 构建与运行

### 1. 构建镜像

```bash
docker build -t azuregptproxy:latest .
```

### 2. 准备环境变量

复制 `.env.sample` 为 `.env` 并按需填写：

```bash
copy .env.sample .env
```

### 3. 运行容器

```bash
# 删除同名旧容器（如果存在）
docker rm -f azuregptproxy

# 启动
docker run -d --name azuregptproxy --env-file .env -p 8088:8080 azuregptproxy:latest
```

---

## ⚙️ 环境变量

| 变量名 | 说明 |
|--------|------|
| `AZURE_OPENAI_ENDPOINT` | Azure OpenAI 资源端点（必填） |
| `AZURE_OPENAI_API_KEY` | Azure OpenAI Key（必填） |
| `AZURE_API_VERSION` | API 版本（如 `2024-10-21`）|
| `ANTHROPIC_AUTH_TOKEN` | 若设置，则 `/v1/messages*` 以及 `/cursor/*`（除 `/cursor/health`）需要 Bearer Token |
| `CURSOR_AZURE_DEPLOYMENT` | 提供 Cursor 端点（`/cursor/*`）转换 Azure Responses API 使用的部署名（必填，是 Azure 里的 Deployment name，不是模型名） |
| `SMALL_MODEL` | 小模型部署名（默认用于 haiku）|
| `BIG_MODEL` | 大模型部署名（默认用于 sonnet/opus）|
| `SMALL_EFFORT` | `SMALL_MODEL` 的 reasoning effort（minimal|low|medium|high；默认 medium；仅 `thinking` 启用且走推理模型时生效）|
| `BIG_EFFORT` | `BIG_MODEL` 的 reasoning effort（minimal|low|medium|high；默认 medium；仅 `thinking` 启用且走推理模型时生效）|

---

## 🔌 接口说明

### `POST /v1/messages`

- Anthropic Messages API 兼容
- 支持 `stream=true` SSE

### `POST /v1/messages/count_tokens`

- 本地估算 token 数量
- 不触发真实生成

---

## 🔒 License

MIT
