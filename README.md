# ⚡ OmniCoder Pilot — The Autonomous Local & Cloud AI Coding Assistant

<p align="center">
  <img src="https://raw.githubusercontent.com/username/omnicoder-pilot/main/assets/banner.png" alt="OmniCoder Pilot Banner" width="100%" onerror="this.style.display='none'"/>
</p>

<p align="center">
  <strong>An autonomous, privacy-first AI software engineer and code editor desktop application for Windows.</strong><br>
  Seamlessly combines 100% offline Local LLMs (Ollama) with Frontier Cloud AI (OpenRouter, Groq, DeepSeek, OpenAI).
</p>

<p align="center">
  <a href="#-key-features"><img src="https://img.shields.io/badge/.NET-10.0%20WPF-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10"></a>
  <a href="#-supported-providers--models"><img src="https://img.shields.io/badge/Ollama-Offline%20First-white?style=for-the-badge&logo=ollama&logoColor=black" alt="Ollama"></a>
  <a href="#-supported-providers--models"><img src="https://img.shields.io/badge/OpenRouter-200%2B%20Models-6366F1?style=for-the-badge" alt="OpenRouter"></a>
  <a href="#-supported-providers--models"><img src="https://img.shields.io/badge/Groq-Ultra--Fast%20Free-F55036?style=for-the-badge" alt="Groq"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-green.style=for-the-badge" alt="MIT License"></a>
</p>

---

## 🌟 Why OmniCoder Pilot?

**OmniCoder Pilot** is designed to give developers the power of frontier AI coding agents (like Claude Code, Cursor, Codex, and Antigravity) while keeping **full sovereignty and privacy over your code**.

Unlike web-based assistants that only offer code suggestions, OmniCoder Pilot is an **autonomous software engineer**:
- 📂 It inspects, reads, and navigates full multi-project codebases.
- ✏️ It writes, edits, and refactors multiple files atomically.
- 💻 It maintains persistent terminal sessions to run build scripts, linters, and commands.
- 🧪 It detects test suites (`dotnet test`, `pytest`, `jest`) and iteratively self-corrects bugs until tests pass.
- 🔍 It indexes repositories with AST symbol extraction and semantic term ranking.
- 🌐 It connects to **any local or cloud LLM** without vendor lock-in.

---

## 🚀 Key Features

### 1. 🌐 Hybrid Local + Multi-Cloud AI Architecture
- **💻 100% Offline Local Mode**: Run locally via [Ollama](https://ollama.ai) (`qwen2.5-coder`, `deepseek-r1`, `llama3.3`, `codellama`) with zero data leaving your machine.
- **⚡ Groq (Ultra-Fast Free Cloud)**: Direct connection for blazing-fast inference on `llama-3.3-70b` and `qwen-2.5-coder-32b`.
- **🌐 OpenRouter Integration**: Access 200+ frontier models (`claude-3.5-sonnet`, `gpt-4o`, `gemini-2.5-flash`).
- **🧠 DeepSeek & OpenAI Direct**: Direct API connectivity with official endpoints.
- **⚙️ Custom OpenAI-Compatible Endpoints**: Connect to vLLM, LM Studio, LiteLLM, GitHub Models, Together AI, or self-hosted servers.

### 2. 🤖 Autonomous Multi-Turn Coding Loop
- **Plan & Todo System**: Auto-generates structured task plans and updates checklist status in real time.
- **Self-Healing Error Recovery**: Analyzes failed commands or build errors and reformulates approaches automatically.
- **Reasoning Stream Parsing**: Native support for DeepSeek R1 `<think>` reasoning token streaming alongside tool calling.

### 3. 🛠️ Comprehensive Developer Tool Suite
| Tool Category | Capabilities |
|---|---|
| **📁 File Operations** | `ReadFile`, `WriteFile`, `EditBlock`, `ListDirectory`, `FindFiles`, `FileTree` preview |
| **🔍 Search & Indexing** | AST symbol extraction (C#, Python, JS/TS, Go, Java), SHA256 caching, TF-IDF code search |
| **💻 Terminal & Shell** | Persistent PowerShell session preserving environment variables, virtualenvs, and state |
| **🌿 Git Suite** | `git status`, `git diff`, `git log`, `git blame`, atomic `git commit`, branch management |
| **🧪 Test-Aware Loop** | Auto-detects xUnit/NUnit/MSTest, PyTest, Jest/Vitest; runs tests and feeds failures back to LLM |
| **🌐 Web & NuGet** | Search live NuGet packages, API documentation, and Brave/DuckDuckGo web results |

### 4. 🎨 Modern, Responsive IDE Interface
- **Collapsible Panels (`◫`)**: Toggle the sidebars and tools panel with one click to expand the chat to full screen.
- **Draggable Splitters**: Smooth horizontal and vertical resizing.
- **Visual Tool Cards**: Real-time badges for running, completed, or failed tool calls.
- **Activity Feed**: Live agent thought process and timestamped execution log.
- **In-App Settings Dialog**: Configure API keys, test connection latencies, and switch providers without restarting.

---

## 📊 Supported Providers & Models

| Provider | Supported Models | Function Calling / Tools | Cost |
|---|---|:---:|---|
| **💻 Local Ollama** | `qwen2.5-coder:7b/32b`, `deepseek-r1:8b`, `llama3.3:8b` | ✅ Native | Free / Offline |
| **⚡ Groq** | `groq/llama-3.3-70b-versatile`, `groq/qwen-2.5-coder-32b` | ✅ Full | Free API Tier |
| **🌐 OpenRouter** | `anthropic/claude-3.5-sonnet`, `openai/gpt-4o`, `google/gemini-2.5-flash` | ✅ Full | Pay-per-token / Free models |
| **🧠 DeepSeek Official**| `deepseek-chat` (V3), `deepseek-reasoner` (R1) | ✅ Full | Extremely Low Cost |
| **🤖 OpenAI Official**  | `gpt-4o`, `gpt-4o-mini`, `o3-mini`, `o1` | ✅ Full | Standard API |
| **⚙️ Custom Any API**   | Any self-hosted endpoint (LM Studio, vLLM, LiteLLM) | ✅ Supported | Self-Hosted |

---

## 🛠️ Quick Start & Installation

### Prerequisites
- Windows 10 / 11 (x64)
- [.NET 10.0 SDK or .NET 8.0 SDK](https://dotnet.microsoft.com/download)
- *(Optional for local mode)* [Ollama](https://ollama.ai) installed with a model:
  ```bash
  ollama pull qwen2.5-coder:7b
  ```

### 1. Clone the Repository
```bash
git clone https://github.com/your-username/OmniCoder.git
cd OmniCoder
```

### 2. Build the Solution
```bash
dotnet build OmniCoderPilot.sln
```

### 3. Run OmniCoder
```bash
dotnet run --project OmniCoderPilot.Wpf
```

---

## ⚙️ Configuration & API Setup

You can configure all API keys directly in the app:
1. Launch **OmniCoder**.
2. Click **`⚙ Settings`** in the bottom-left sidebar.
3. Enter your keys:
   - **OpenRouter**: Get free/paid key at [openrouter.ai/keys](https://openrouter.ai/keys)
   - **Groq**: Get 100% free high-speed key at [console.groq.com/keys](https://console.groq.com/keys)
   - **DeepSeek**: Get key at [platform.deepseek.com](https://platform.deepseek.com)
   - **OpenAI**: Get key at [platform.openai.com](https://platform.openai.com)
4. Click **`Test Connection`** and **`Save & Apply All`**.

---

## 🏗️ Architecture Overview

```
OmniCoder / OmniCoderPilot Solution
├── OmniCoderPilot.Core/                     # Core Business Logic & Domain
│   ├── Application/
│   │   ├── AgentOrchestrator.cs     # Main autonomous turn-based loop
│   │   ├── StreamingToolExecutor.cs # Concurrent tool execution engine
│   │   └── Tools/                   # Git, Code Search, Shell, Test & File tools
│   └── Infrastructure/
│       ├── ModelRouter.cs           # Dynamic local vs cloud routing
│       ├── OpenAiCompatibleClient.cs# Universal OpenAI/OpenRouter/Groq client
│       ├── OllamaClient.cs          # Local Ollama REST client
│       ├── RepositoryIndexer.cs     # Multi-language AST indexer & TF-IDF search
│       └── TerminalService.cs       # Persistent PowerShell session engine
└── OmniCoderPilot.Wpf/                      # Modern WPF Desktop Interface
    ├── ViewModels/                  # MVVM ViewModels (Chat, Sidebar, Settings, Tree)
    └── Views/                       # Responsive Windows, Dialogs, and Custom Controls
```

---

## 🗺️ Roadmap & Future Enhancements

- [x] Multi-provider cloud AI routing (OpenRouter, Groq, DeepSeek, OpenAI, Custom)
- [x] Persistent PowerShell terminal sessions
- [x] Git integration (status, diff, log, blame, commit)
- [x] Symbol indexing & TF-IDF semantic repository search
- [x] Test-aware auto-fixing loop (C#, Python, JavaScript)
- [x] Collapsible full-width responsive workspace UI
- [ ] Multi-agent collaborative team workflows
- [ ] Visual diff editor with side-by-side merge
- [ ] Extension marketplace & plugin system

---

## 🤝 Contributing

Contributions are warmly welcome!
1. Fork the Project (`gh repo fork your-username/OmniCoder`)
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

---

## 📄 License

Distributed under the **MIT License**. See [`LICENSE`](LICENSE) for more information.

---

<p align="center">
  Built with ❤️ for developers who demand autonomous AI without sacrificing privacy or speed.
</p>
