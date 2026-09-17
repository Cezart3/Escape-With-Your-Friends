# Escape With Your Friends

Unity 6.3 LTS (6000.3.23f1) + URP + FishNet. Four-player co-op survival, host-authoritative P2P,
shipping on Steam. Repo `Cezart3/Escape-With-Your-Friends`.

**Read `docs/WORKING-AGREEMENT.md` before your first change.** It is the full guide: the roadmap,
how to build and test, how to delegate, and the protocol for two Claude accounts working at once.
This file is only the part you need in context every session.

## Who does what

Claude writes the C# from the terminal. The user playtests, makes the art and feel decisions, and
runs anything that needs a screen. The user is a Romanian-speaking junior dev — **address them in
Romanian**; code, comments, commits, PRs and issue text stay in English.

Claude cannot: playtest, judge feel, decide art, or run the Unity Editor interactively.

## Non-negotiables

- **Never hand-edit `.unity`, `.prefab` or `.asset` files.** Generated content comes from an editor
  script under `Scripts/Editor/`, run in batchmode. Re-runnable beats hand-placed.
- **Every non-trivial system gets a headless harness** behind a `-xxxTest` flag, checked into
  `NetworkBootstrap`. A feature with no harness is not done.
- **Document in `docs/ARCHITECTURE.md`** — a section per issue, spliced before the
  `\n---\n\n## Data-driven content\n` anchor.
- Install large things on **D:** (1TB free), never C:.
- Issues stay one tracked tree with epics, not a flat list.
- The user's email is for attribution only. Never send it to an unrelated service.

## Build and test

```bash
# Build (~18s, 169 MB). Unity batchmode and the open Editor cannot both run — check first.
tasklist | grep -iE "^(Unity|EscapeWithYourFriends)\.exe"

"/d/Unity/Editors/6000.3.23f1/Editor/Unity.exe" -batchmode -quit \
  -projectPath "D:\Proiecte\JocStupid" -logFile "D:\Builds\build.log" \
  -executeMethod EscapeWithYourFriends.EditorTools.BuildTool.PerformBuild \
  -buildOutput "D:\Builds\EWYF-dev" -development -scriptingBackend mono

grep -a "error CS" D:/Builds/build.log | sort -u     # ALWAYS. batchmode exits 0 on a failed build.

# Harness
"D:/Builds/EWYF-dev/EscapeWithYourFriends.exe" -batchmode -nographics \
  -logFile "D:\Builds\t.log" -host -port 8140 -playerKey test:host \
  -scene island -noNatives -noAnimals -someTest -quitAfter 200 &
```

Then poll **the log file**, in a separate foreground call, case-insensitively:

```bash
for i in $(seq 1 130); do
  if grep -qaiE "[0-9]+ passed, [0-9]+ failed" D:/Builds/t.log; then break; fi; sleep 2; done
```

## Traps that have each cost an hour

- `-logfile /dev/stdout` **loses compiler output**. Always a real file, then grep.
- Poll the log, never `tasklist` — the process outlives the result.
- Never chain `cd` or an assignment with `&&` into a backgrounded launch. Absolute paths, each exe
  backgrounded on its own line, polling in its own foreground call.
- `sed -i` fails here with "Invalid cross-device link". Use `sed … > tmp && mv tmp file`, same dir.
- Bash heredocs mangle large C# — use the Write tool for new files, a **quoted** (`<<'PY'`) python
  heredoc for surgical edits. Unquoted heredocs eat backticks and backslashes.
- Headless builds no canvas (`HudRoot.Awake` returns early when `graphicsDeviceType == Null`), so
  **no UI can be harness-tested**. UI bugs are found by reading.
- One runtime asmdef, so `internal` is visible across every runtime script.
- Pair harnesses need the host up ~20s before the client.
- `-nativeTest` must run **solo** — a second player body nine metres away changes what the natives
  hunt (#132). `-partTest` needs a **pair** and `-scene island2`.

## Modes

**Ponytail full** and **Caveman full** are both on. Laziest thing that works; no speculative
abstractions; deletion over addition. Terse Romanian to the user, normal English in the code.

## Delegation

Mechanical work goes to **sonnet** subagents: builds, harness runs, log greps, the git/gh sequence.
Design, C# and prose stay on **Opus**. Tell the agent *not to edit source* and to report verbatim.

## Attribution

Commits end with:

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: <the session URL>
```

PR descriptions end with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`, a blank
line, then the session URL.
