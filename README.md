# OwnBridge — Phase 3

**Your account. Your code. Your IDE.**

OwnBridge is a free, open-source Visual Studio 2022 extension. It runs the official Codex client locally (`codex app-server`) and lets users work with their own ChatGPT account. There is no relay server, no API key, and OwnBridge never reads or stores account tokens.

## Phase 3 features

- **Solution workspace:** the agent works in the folder of the open solution (the nearest folder above the active file that contains a `.sln`/`.slnx`, otherwise the git root). One Codex thread is kept per workspace.
- **Editor context:** with *Include the open file and selected text* ticked, the active file path and the current selection are sent with the prompt.
- **Approvals:** the thread runs with `sandbox = workspace-write` and `approvalPolicy = untrusted`. The agent may read files; every file change and every non-trivial command shows an approval card with **Approve / Decline**. File changes show the diff; **View diff** opens it as a `.diff` document in Visual Studio. Unknown requests from the engine are declined automatically.
- **Activity lines:** edited files and executed commands are listed in the chat.
- **Stop:** interrupts the running reply (`turn/interrupt`); a waiting approval is declined.
- **Connect ChatGPT:** opens the official browser sign-in and polls the account every 2 seconds until the sign-in is active (up to 5 minutes).

Not in this phase: undo/checkpoints (planned later), Gemini (Phase 4), provider switching with handoff and durable history (Phase 5), managed runtimes (Phase 6). The development build still expects Codex CLI on `PATH` (`npm install -g @openai/codex`).

## Build and install (Windows)

Requirements: Visual Studio 2022 17.14+, the *Visual Studio extension development* workload, .NET 8 SDK, Codex CLI.

1. Open `OwnBridge.sln` → **Build → Rebuild Solution** → 0 errors.
2. Close Visual Studio, then in PowerShell from the repository folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\Make-X64Vsix.ps1 -VsixPath .\OwnBridge\bin\Debug\net8.0\OwnBridge.vsix
$id = (& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -all -products * -format json | ConvertFrom-Json | Where-Object installationPath -like "*2022\Enterprise*").instanceId
& "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\VSIXInstaller.exe" /instanceIds:$id (Resolve-Path .\OwnBridge\bin\Debug\net8.0\OwnBridge-x64.vsix)
```

Passing `/instanceIds` is required when several Visual Studio instances are installed; without it VSIXInstaller hands the package to Visual Studio Installer, which shows only "You already have the frameworks, SDKs, and tools installed". Change `2022\Enterprise` if you use another edition.

## Test

1. Open a solution and any `.cs` file in it; select a method.
2. **Tools → Open OwnBridge**. The header shows the workspace after the first Send.
3. Ask: `Explain the selected code.` → answer only, no approvals.
4. Ask: `Add an XML doc comment to the selected method.` → an approval card with the diff appears. **Approve** → the file changes in Visual Studio. Try **Decline** once as well.
5. Ask: `Run dotnet build and tell me the result.` → a command approval card appears.
6. Start a long request and press **Stop**.

## Architecture

- `CodexAppServerClient` — starts `codex app-server`, JSON-RPC over stdio; routes server requests (approvals) to a handler and fails closed.
- `CodexChatProvider` — sign-in, per-workspace threads, streaming, activity, approval mapping (current `accept/decline` and legacy `approved/denied` shapes), interrupt.
- `IChatProvider` / `IChatTurnObserver` — provider boundary for later engines (Gemini via ACP).
- `EditorContext` — captures the active file and selection and finds the workspace root.
- `ChatPanelData` / `ChatPanel.xaml` — Remote UI view model: chat, approval card, stop.
- `ConversationSession` — OwnBridge-owned transcript and per-provider thread map (in memory).

## License and affiliation

MIT; see [LICENSE](LICENSE). OwnBridge is an independent community project, not an official Microsoft, OpenAI or Google extension.
