# Nemesis - Unity AI Development Assistant

A local AI-powered assistant for Unity 6 C# development, featuring multi-agent orchestration, code analysis, and intelligent patch generation.

## Features

- **Multi-Agent System**: Three specialized AI agents
  - **Unity Expert**: Architecture, performance, Unity 6 patterns
  - **Generalist**: Q&A, explanations, general help
  - **Researcher**: Web search, documentation lookup

- **Code Analysis**: Roslyn-based C# indexing with:
  - Type graph and dependency analysis
  - Symbol search and go-to-definition
  - Cross-file understanding

- **RAG System**: Vector-based code retrieval for contextual responses

- **Patch Management**:
  - Unified diff generation
  - Preview before apply
  - Automatic backups
  - Rollback capability

- **Web Search**: Unity docs, forums, GitHub, StackOverflow

- **100% Local**: Runs on your machine with Ollama, no cloud dependency

## Requirements

- Windows 10/11
- .NET 8 SDK
- Ollama (for local LLM)
- RTX 4080 or similar (16GB VRAM recommended)
- 32GB RAM

## Quick Start

### 1. Install Prerequisites

```powershell
# Install .NET 8 SDK
winget install Microsoft.DotNet.SDK.8

# Install Ollama
winget install Ollama.Ollama

# Or download from: https://ollama.ai/download
```

### 2. Pull Required Models

```powershell
# Start Ollama
ollama serve

# In another terminal, pull models
ollama pull deepseek-coder-v2:16b    # Main coding model
ollama pull nomic-embed-text          # Embedding model
```

### 3. Build and Run

```powershell
cd Nemesis

# Restore and build
dotnet restore
dotnet build

# Run the server
dotnet run --project src/Nemesis.Server
```

### 4. Open in Browser

Navigate to: http://localhost:5000

## Usage

### Index a Project

1. Go to the **Project** page
2. Enter your Unity project path (e.g., `C:\Projects\MyUnityGame`)
3. Click **Index Project**
4. Wait for indexing to complete

### Chat with Agents

1. Go to the **Chat** page
2. Select an agent (Unity Expert, Generalist, or Researcher)
3. Ask questions about your code or request changes
4. Review generated patches in the **Patches** page

### Apply Changes

1. Go to the **Patches** page
2. Review the unified diff
3. Click **Apply Patch** or **Reject**
4. Use **Rollback** if needed

## Configuration

Edit `src/Nemesis.Server/appsettings.json`:

```json
{
  "Nemesis": {
    "Llm": {
      "Provider": "ollama",
      "ModelName": "deepseek-coder-v2:16b",
      "Temperature": 0.7
    },
    "WebSearch": {
      "Enabled": true,
      "AllowedDomains": ["docs.unity3d.com", "github.com"]
    },
    "Project": {
      "AutoBackup": true,
      "ExcludedFolders": ["Library", "Temp", "obj"]
    }
  }
}
```

## Alternative Models

For different VRAM requirements:

| Model | VRAM | Quality |
|-------|------|---------|
| `deepseek-coder-v2:16b` | ~12GB | Best |
| `codellama:13b` | ~10GB | Good |
| `codellama:7b` | ~6GB | Fair |
| `mistral:7b` | ~6GB | General |

## Project Structure

```
Nemesis/
├── src/
│   ├── Nemesis.Server/          # Blazor Server UI + API
│   ├── Nemesis.AgentCore/       # Agents, Tools, LLM, RAG
│   ├── Nemesis.CodeAnalysis/    # Roslyn indexing
│   └── Nemesis.Shared/          # DTOs, Interfaces
├── tests/
│   └── Nemesis.Tests/           # Unit tests
└── README.md
```

## Security

- All processing is local - code never leaves your machine
- Web searches only use allowed domains
- Automatic backups before any file modification
- Offline mode available

## Troubleshooting

### Ollama not connecting
```powershell
# Check if Ollama is running
curl http://localhost:11434/api/tags

# Restart Ollama
ollama serve
```

### Out of memory
- Use a smaller model
- Reduce `ContextWindow` in settings
- Close other GPU applications

### Indexing slow
- Exclude unnecessary folders in settings
- Index only the `Assets/Scripts` folder

## License

MIT License - See LICENSE file

## Contributing

Contributions welcome! Please read CONTRIBUTING.md first.
