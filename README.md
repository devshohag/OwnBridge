# OwnBridge — Phase 7

**Your account. Your code. Your IDE.**

OwnBridge is a free, open-source Visual Studio 2022 extension. It runs the official Codex client (`codex app-server`) and the official Gemini CLI (ACP mode) locally, so users work with their own ChatGPT and Google accounts. There is no relay server, no API key, and OwnBridge never reads or stores account tokens.

## 0.8.2

- The open solution is read from Visual Studio, so OwnBridge knows the workspace even when no file is open in the editor. The context line shows the solution as soon as the panel opens.
- With no solution open, messages go to a "General chat" (questions, explanations). The AI works in an empty OwnBridge folder there and cannot see your projects; Tasks need a solution.

## 0.8.1

- Excel/CSV rows whose status column ("Dev Status", else "Status"/"State", never QC/QA) says Done, Completed, Closed, Resolved or Fixed are loaded as finished tasks.

## 0.8.0 — brand

- Logo: the O pier and W truss joined by a teal arch ("Own" + "Bridge"). In the panel header it is drawn as vectors in the theme's text color; the extension icon is `OwnBridge/Images/OwnBridge.png` (128 px). Sources and larger sizes are in `assets/brand/`.

## 0.7.5

- Files outside a project (Program Files, the .NET SDK, NuGet cache, AppData, temp) never become the workspace; a workspace needs a .sln/.slnx or a git repository. Previously an SDK .targets file opened from a build error could become the workspace.
- Attaching a plan by path uses the plan file's own folder to pick the workspace.

## 0.7.4

- Code blocks and command blocks are boxed with a header (language or "command") and a Copy button. Commands (powershell, bash, cmd, or blocks starting with dotnet/git/cd...) use a green-tinted box so they stand apart from code.

## 0.7.3

- Enter sends the message; Shift+Enter adds a new line (Ctrl+Enter also sends).

## 0.7.2

- Engine pipes are read and written as UTF-8, so Bangla text shows correctly (it showed as "à¦®à¦¾" before) and Bangla prompts reach the AI intact.

## 0.7.1

- Plan headings with `[DONE]`, `[COMPLETED]`, `(done)` or a check mark are loaded as finished tasks.
- A workspace is identified by its folder, not its .sln, so a new project that creates its solution later keeps one chat and one task list. Files in sub-folders of the open workspace stay in it. History saved by older versions (keyed by the .sln path) is still found.
- Tip: put `AGENTS.md` (Codex) and `GEMINI.md` (Gemini) with project rules in the repository root; both engines read them automatically.

## Phase 7 features

- **New UI (Codex style):** header with the AI switch and usage; a context line with the solution, task progress and Chat / Tasks / History / New chat; one view at a time; Settings behind the gear. Colors come from the Visual Studio theme (`VsBrushes`), so light and dark follow Visual Studio automatically.
- **Messages:** your messages as bubbles, AI answers as text with code blocks in monospace boxes, and compact activity lines (search, read, edit, approvals) with icons. The chat shows the latest exchange; *Show earlier* expands it.
- **Clear approvals:** a badge (READ, BUILD, RUN, RISK, EDIT, CREATE, DELETE), a plain-language headline ("Searches or reads files (rg); changes nothing"), **Why** (the engine's reason, or the AI's latest note), the command or a colored diff, then Approve / Decline / Open diff.
- **AI notes:** ChatGPT's short progress notes ("I'll search for ...") are shown as activity lines instead of being hidden.
- **Auto-approve read-only commands (Settings, off by default):** only programs on a short read-only list (rg, grep, findstr, Select-String, Get-Content, dir, git status/diff/log/show...), and only when there is no `;`, `&`, `|`, `>`, `<`, backtick or `$(` outside quotes. Edits, builds and anything unclear still ask.
- **Composer:** Ctrl+Enter sends; a chip turns editor context on or off; the paperclip opens Tasks.

## Phase 6 features (still included)

- **Attach a plan or issue list:** paste a path (or use *Attach open file*) for `.xlsx`, `.csv`, `.docx`, `.md` or `.txt`. Excel and Word are read with OwnBridge's own zip/XML code (first worksheet; Word headings, lists and tables).
- **Task list:** Excel/CSV → one task per row (title and ID columns are detected by header names; every column is passed as details). Documents → one task per section (`##`/`#` headings, `Phase/Step/Task N`, or numbered items). Up to 500 tasks. The list is saved with the chat (`<chat>.tasks.json`) and survives restarts.
- **Run task by task:** *Run next* or *Run* on a row. Each task is its own chat turn with a focused prompt (outline of all tasks + only this task). *Auto-continue* runs the next task after a successful one; Stop turns it off.
- **EDIT / CREATE / DELETE:** the approval card labels every file; deletes get a red warning.
- **Commit per task:** with *Commit after each task* on, only the files you approved for that task are committed (`git add -A -- <files>` + `git commit -- <files>`), message `OwnBridge task N: title`. Other changes in the repo are never included.
- **Export results:** writes `<source>_OwnBridge_<date>.xlsx` next to the source file (original columns + status, summary, commit). The source file is never changed.
- **Model picker:** an editable list per AI. ChatGPT models come from the engine (`model/list`, when supported); Gemini models come from the list Gemini CLI reports when a session starts. Default Gemini model: `gemini-3.8-flash`.
- **Silence warning:** after 20 seconds without engine output the status says the service may be busy or retrying.

## Phase 5 features (still included)

- **History per solution:** conversations are saved as JSON lines under `%LOCALAPPDATA%\OwnBridge\workspaces\<id>\conversations\`. The id is a hash of the solution file path, so two solutions never share history. Opening a file from another solution switches to that solution's latest chat.
- **Two windows, one solution:** an open conversation holds an exclusive `.lock` file. A second Visual Studio window gets its own conversation and cannot write into the first one's.
- **New chat / History:** buttons in the header; History lists only this solution's chats.
- **Handoff:** when you switch AI (or reopen a chat after restarting Visual Studio), the next prompt includes what happened in *this conversation* since that AI last answered (truncated to ~12k characters), with a note to re-read files before editing.
- **Account and usage:** ChatGPT shows the signed-in email and plan, tokens used in this chat, and the short-term and weekly limit percentages with reset times (when the engine reports them). Gemini shows the key hint or Google email; Gemini CLI does not report remaining limits.
- **Engines:** OwnBridge uses `codex` / `gemini` from PATH if installed; otherwise it installs a private copy once into `%LOCALAPPDATA%\OwnBridge\engines` with npm (Node.js 20+ required). Engine start failures now show the engine's own error text.
- **Gemini API key:** Google refuses Gemini CLI sign-in for personal Google accounts, so Gemini can use an API key from Google AI Studio. The key is encrypted with Windows DPAPI for the current Windows user and passed to Gemini CLI as `GEMINI_API_KEY`.

- **0.5.1:** Stop now also cancels a reply that is still starting; Gemini start and session creation time out with a clear message; Gemini traffic is logged to `%LOCALAPPDATA%\OwnBridge\logs\gemini.log` (the API key is never written there); a **Gemini model** box (default `gemini-2.5-flash`, because the free API tier has no quota for Pro models).

Not yet: reading the solution path directly from Visual Studio when no file is open (a file from the solution must be open for the first message).

## Phase 4 features (still included)

- **Gemini:** choose **AI: ChatGPT / Gemini** in the panel header. Gemini runs through the official Gemini CLI in ACP (IDE) mode (`gemini --acp`, falling back to `--experimental-acp` for older CLIs). **Connect Gemini** opens the official Google sign-in when needed.
- **Gemini approvals:** Gemini's permission requests appear on the same approval card. OwnBridge only ever picks the agent's *allow once* / *reject once* option, never *always*. File edits show the old and new text.
- **Switching:** the selected AI is used for the next message; a reply that is already running keeps its engine. Each AI keeps its own session per workspace. Carrying the conversation from one AI to the other (handoff) and saved history are Phase 5.

Requires Gemini CLI on `PATH` for development (`npm install -g @google/gemini-cli`, Node.js 20+).

## Phase 3 features (still included)

- **Solution workspace:** the agent works in the folder of the open solution (the nearest folder above the active file that contains a `.sln`/`.slnx`, otherwise the git root). One Codex thread is kept per workspace.
- **Editor context:** with *Include the open file and selected text* ticked, the active file path and the current selection are sent with the prompt.
- **Approvals:** the thread runs with `sandbox = workspace-write` and `approvalPolicy = untrusted`. The agent may read files; every file change and every non-trivial command shows an approval card with **Approve / Decline**. File changes show the diff; **View diff** opens it as a `.diff` document in Visual Studio. Unknown requests from the engine are declined automatically.
- **Activity lines:** edited files and executed commands are listed in the chat.
- **Stop:** interrupts the running reply (`turn/interrupt`); a waiting approval is declined.
- **Connect ChatGPT:** opens the official browser sign-in and polls the account every 2 seconds until the sign-in is active (up to 5 minutes).

Roadmap: Phase 5 handoff and saved history, Phase 6 professional chat UI, Phase 7 managed runtimes and publishing. The development build still expects Codex CLI on `PATH` (`npm install -g @openai/codex`).

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
- `IChatProvider` / `IChatTurnObserver` — provider boundary shared by both engines.
- `AcpClient` — JSON-RPC 2.0 over stdio for ACP agents; per-process request tables; refuses unknown agent requests.
- `GeminiChatProvider` — Google sign-in via `authenticate`, per-workspace sessions, streaming, tool activity, permission mapping, `session/cancel`.
- `EditorContext` — captures the active file and selection and finds the workspace root.
- `ChatPanelData` / `ChatPanel.xaml` — Remote UI view model: chat, approval card, stop.
- `ConversationSession` — OwnBridge-owned transcript and per-provider thread map (in memory).

## License and affiliation

MIT; see [LICENSE](LICENSE). OwnBridge is an independent community project, not an official Microsoft, OpenAI or Google extension.

## Test Gemini (Phase 4)

1. `npm install -g @google/gemini-cli`, then restart Visual Studio.
2. In OwnBridge select **Gemini** → **Connect Gemini** → finish Google sign-in in the browser → status shows *Connected to Gemini*.
3. With a method selected: `Explain the selected code.`
4. `Add an XML doc comment to the selected method.` → approval card → Decline once, then Approve.
5. Switch back to **ChatGPT** and send a message; both keep working in the same panel.
