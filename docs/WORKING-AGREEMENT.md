# Working agreement

How this project is built, by whom, with what, and what not to do. Written so a second Claude Code
session can pick up cold without being briefed by hand.

`CLAUDE.md` at the repo root is the short version, loaded into context automatically every session.
This file is the long one. Read it once, then work from `CLAUDE.md`.

---

## 1. What this is

**Escape With Your Friends** — a goofy four-player co-op survival game for Steam. Four friends are
stranded on an island: survive, gather, earn money, buy a boat, cross to a second and far nastier
island, find the aeroplane parts, rebuild the plane, rescue an NPC, leave.

The fun is deliberately bad physics — punching each other, stunning, carrying people around,
throwing them, stupid revives. Network jank on ragdolls is a feature, not a bug.

**Stack.** Unity 6.3 LTS (6000.3.23f1) + URP, FishNet over a Steam transport, host-authoritative
peer-to-peer. No servers, no monthly cost: one player hosts, the rest connect through a Steam lobby.
Target four players, tested to six.

**Why Unity and not Unreal.** Unity's assets are text — scenes, prefabs and ScriptableObjects are
YAML, scripts are C#. That is the whole reason this project can be built from a terminal at all. In
Unreal, Blueprints and `.uasset` files are binary and Claude would be blind.

**Authority.** Own movement and the vehicle you are driving are client-owned with prediction.
Everything else — damage, health, death, economy, inventory, shops, every RNG, all AI — is decided
by the host. RNG on a client is never acceptable, not even for a cosmetic roll.

### Who does what

| | |
|---|---|
| **Claude** | All the C#, the generated content, the harnesses, the architecture docs, the git and GitHub work. |
| **The user** | Playtesting, "does this feel good", art direction, anything needing a screen, anything costing money. |

The user is a Romanian-speaking junior developer, a year out of school, working with a senior mentor
who has no gamedev experience. **Address the user in Romanian.** Everything that lands in the repo —
code, comments, commit messages, PR bodies, issue text, these docs — is written in English.

Claude cannot playtest, cannot judge feel, cannot decide art, and cannot drive the Unity Editor
interactively. When an issue's acceptance is a human verdict, say so and stop; do not invent a
proxy measurement and call it green.

---

## 2. Where the work stands

Milestones M0 through M7 are done: the networked core, island 1, survival and inventory, economy and
combat, vehicles, the casino, island 2 and the ending. What remains is M8 (art and polish) and M9
(Steam), plus a handful of bugs.

### Open and workable right now

These are code, and their acceptance can be checked headless:

| Issue | What |
|---|---|
| **#38** | Perf pass — occlusion culling, LOD groups, fog, baked lighting. Batchmode-scriptable; `-perfLog` gives numbers. |
| **#92** | Achievements + Steam Rich Presence. The tracking layer is testable headless; the Steam sink needs an appid. |
| **#139** | `-partTest`: the second pair of hands is intermittent. |
| **#141** | Seat drift under parallel harnesses. *Fix is written and uncommitted at time of writing — check `git status` first.* |
| **#144** | `Alarm` hands every listener a live target instead of a place to look. Changes how hard a camp converges, so it is a feel decision — get the user's word before shipping it. |

### Open and blocked, with the reason

| Issue | Blocked on |
|---|---|
| #76–#79 | Art. Needs the user to open Blender with the blender-mcp addon running. `get_addon_status` fails until they do. |
| #80–#81 | SFX and music. Needs sourced audio, not code. |
| #82 | "A new player understands the HUD without explanation" — a playtest verdict. |
| #83 | 60fps on the Radeon 760M at 1080p. Human gate, on the user's hardware. |
| #85–#91, #93 | Steam. Needs the $100 Steam Direct fee, a tax interview (W-8BEN) and an appid. Up to 30 days of waiting. |
| #29 | GATE: M1 playtest with four real players. The one that decides whether the game is fun. |
| #6 | MCP for Unity. Needs the Editor open. |

`#94`–`#104` are the milestone epics and `#104` is the roadmap. Do not close an epic; it closes when
its leaves do.

### Picking the next issue

Work M-order, leaves before epics. Skip anything whose acceptance is a human verdict and say why.
Prefer an issue whose acceptance a harness can actually settle — if you cannot state, before you
start, what log line will prove it works, pick a different issue.

---

## 3. The cycle, per issue

This is the whole loop. Every issue goes through all of it.

1. **Read the issue.** `gh issue view <n>`. Read the code it names before writing anything.
2. **Design on Opus.** Decide the laziest thing that works (§8). Where a choice is a feel decision
   rather than a correctness one, either default to the recommended option and say so, or ask.
3. **Write the C#.** New files with the Write tool, surgical edits with a quoted python heredoc.
4. **Build in batchmode**, grep for `error CS`, fix, repeat until clean.
5. **Write a harness** behind a `-xxxTest` flag and run it headless. It must print
   `N passed, M failed` and error on any failure.
6. **Run the regressions** for whatever you touched.
7. **Document** in `docs/ARCHITECTURE.md`: a section spliced before the anchor
   `\n---\n\n## Data-driven content\n`, with the real numbers from step 6 in a code block.
8. **Branch, commit, push, PR, merge, comment on the issue, close it.** Branch name
   `issue-<n>-<short-slug>`. Steps 4–8 are sonnet work (§6).

Nothing skips step 5 because "it obviously works", and nothing skips step 7 because "the code is
self-explanatory". The architecture doc is how the next session — or the user in six months —
finds out why something is the shape it is.

### When a check fails

Diagnose before you touch anything. Two real examples from this repo, both of which looked like
gameplay bugs and were not:

- Four native-AI checks failed deterministically. It looked like arithmetic. It was a second player
  body nine metres from the first: the harness assumed one player, the repro ran a pair, and natives
  hunt whoever is nearest (#132).
- Seat-drift checks failed only when other harnesses were running. It looked like frame-rate
  dependence in the vehicle. It was the test sampling before the glue ran in `LateUpdate` (#141).

In both cases the answer was in the log, sitting next to the failure, and in both cases the log had
to be read rather than guessed at. Add `-nativeLog` / `-vehicleLog` / `-motorLog` and read the
ordering of lines; the order things print in is evidence.

---

## 4. Build and test reference

### Paths

| | |
|---|---|
| Unity | `/d/Unity/Editors/6000.3.23f1/Editor/Unity.exe` |
| Project | `D:\Proiecte\JocStupid` |
| Build output | `D:\Builds\EWYF-dev` (see §5 if two accounts are running) |
| Logs | `D:\Builds\*.log` |

### Build

```bash
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { \$_.Name -match '^(Unity|EscapeWithYourFriends)\.exe$' } | ForEach-Object { \$_.CommandLine }" \
  | grep -iE 'JocStupid(["[:space:]]|$)|EWYF-dev[\/]'     # empty = free

"/d/Unity/Editors/6000.3.23f1/Editor/Unity.exe" -batchmode -quit \
  -projectPath "D:\Proiecte\JocStupid" -logFile "D:\Builds\build.log" \
  -executeMethod EscapeWithYourFriends.EditorTools.BuildTool.PerformBuild \
  -buildOutput "D:\Builds\EWYF-dev" -development -scriptingBackend mono

grep -a "error CS" D:/Builds/build.log | sort -u
```

About eighteen seconds, 169 MB. **Always grep for `error CS`** — a failed compile still exits 0 and
the old exe stays on disk, so the next harness run silently tests the previous build.

Other editor entry points, same shape, different `-executeMethod`:

- `EscapeWithYourFriends.EditorTools.InputAssetBuilder.BuildInputAsset` — regenerate the input
  actions asset. **Required whenever you add or rename an action.**
- The island, POIs, shop stock, sky, item and native catalogues each have a factory under
  `Scripts/Editor/`. Content is generated, never hand-placed.

### Running a harness

```bash
"D:/Builds/EWYF-dev/EscapeWithYourFriends.exe" -batchmode -nographics \
  -logFile "D:\Builds\t.log" -host -port 8140 -playerKey test:host \
  -scene island -noNatives -noAnimals -someTest -quitAfter 200 &
```

Then, in a **separate foreground call**:

```bash
for i in $(seq 1 130); do
  if grep -qaiE "[0-9]+ passed, [0-9]+ failed" D:/Builds/t.log; then break; fi; sleep 2; done
```

Then wait for the process to leave `tasklist`, then grep the log for the result and any
`FAILED:` / `Exception` lines.

Common switches: `-host` / `-client` / `-server`, `-address`, `-port`, `-playerKey`, `-scene island`
or `island2`, `-quitAfter <seconds>`, `-noNatives`, `-noAnimals`, `-botMove`, `-latency`,
`-timeOfDay`, `-quality`, `-save` / `-noSave` / `-savePath`, `-startingMoney`, `-islandSeed`.

### The test flags

```
-abductTest   -animalTest  -boatTest     -buffTest      -carryTest   -carTest
-casinoTest   -chestTest   -chipsTest    -conditionTest -craftTest   -deathTest
-drunkTest    -economyTest -endTest      -fallTest      -fishTest    -flightTest
-ghostTest    -gunTest     -hudTest      -impactTest    -invTest     -itemTest
-lootTest     -machineTest -meleeTest    -moneyTest     -nativeTest  -partTest
-planeTest    -prisonTest  -rescueTest   -reviveTest    -rouletteTest -saveTest
-settingsTest -shopTest    -statTest     -uiTest        -upgradeTest -vehicleTest
-vehicleUpgradeTest        -voiceTest    -voyageTest    -weaponTest
```

Logging flags that make a failure readable: `-animalLog`, `-cameraLog`, `-clockLog`, `-fishLog`,
`-meleeLog`, `-motorLog`, `-nativeLog`, `-perfLog`, `-statLog`, `-vehicleLog`, `-weaponLog`.

### Harness rules learned the hard way

- **`-nativeTest` must run solo.** A second player body changes what the natives hunt. The suite now
  fails a named check if it catches one, but do not put it in that position.
- **`-partTest` needs a pair and `-scene island2`.** The host gets `-partTest`; the client is just a
  warm body with no test flag. Run solo it fails "a second player joined to take the other end".
- **Pair harnesses need the host up about twenty seconds before the client.**
- Timing measurements are sensitive to what else is running. If a check is a distance or a duration,
  give it the machine.
- A harness that needs particular conditions should **check for them and say so**, not quietly
  measure something else. That is the lesson of #132 and it applies to every suite.

### Ports

One port per concurrent process, never reused inside a session. **Consumed through 8135.** See §5
for the split when two accounts are running.

---

## 5. Two Claude accounts at once

This is the part with real teeth. Two sessions on one machine will destroy each other's work unless
the following is respected.

### Separate working trees. Not optional.

Two sessions editing `D:\Proiecte\JocStupid` at the same time will overwrite each other's edits and
fight over git state. The second account works in its own tree:

```bash
git -C "D:/Proiecte/JocStupid" worktree add "D:/Proiecte/JocStupid-b" -b scratch-b
```

Unity will import the new tree and build its own `Library/` — a few minutes and a few GB, once. Disk
is not the constraint here; D: has a terabyte.

### Separate build outputs

| | Project | Build output | Log prefix |
|---|---|---|---|
| **Account A** | `D:\Proiecte\JocStupid` | `D:\Builds\EWYF-dev` | `D:\Builds\a-*.log` |
| **Account B** | `D:\Proiecte\JocStupid-b` | `D:\Builds\EWYF-dev-b` | `D:\Builds\b-*.log` |

Sharing a build output is the fastest way to have one session test the other's binary and report a
result that belongs to nobody.

### Separate ports

| | Range |
|---|---|
| **Account A** | 8100–8499 |
| **Account B** | 8500–8899 |

### Unity batchmode

Unity locks its `Library/` per project, so **two accounts on two project directories can build at
the same time.** Two accounts on the *same* directory cannot, and neither can a batchmode build and
an open Editor.

**So the busy check has to look at command lines, not process names.** A plain
`tasklist | grep Unity.exe` sees the other account's processes too, and a session that waits for it
to be empty will sit behind the other account's work for no reason — or, with both sessions doing
it, take turns forever. The check in §4 matches account A's paths; account B uses:

```bash
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { \$_.Name -match '^(Unity|EscapeWithYourFriends)\.exe$' } | ForEach-Object { \$_.CommandLine }"   | grep -iE 'JocStupid-b|EWYF-dev-b'
```

Expect slower builds when both are running, and remember that a slower machine stretches frame times
in every headless harness. If your check is a timing measurement, wait for the other account to
finish rather than reporting a number taken under load.

### Claiming an issue

Before starting, take the lock GitHub already gives you:

```bash
gh issue edit <n> --add-assignee @me
gh issue list --state open --assignee @me
```

An unassigned open issue is fair game. An assigned one is somebody's.

### Branches and merges

Branch `issue-<n>-<slug>`, one issue per branch, merge to `main` through a PR, delete the branch.
Two accounts on different issues will not collide, with one exception: **`docs/ARCHITECTURE.md`**,
where both splice before the same anchor. That conflict is trivial to resolve — both sections are
wanted, order does not matter — but expect it, and pull `main` before you splice.

### What not to split

Do not have two accounts work on the same issue, the same file, or the same subsystem at the same
time. The coordination cost outruns the parallelism immediately. Two accounts on two unrelated
issues is a genuine speed-up; two accounts on one issue is a merge conflict with extra steps.

---

## 6. Delegating to sonnet subagents

**Opus does design, C# and prose. Sonnet does everything mechanical.** This is a cost decision and a
quality one: a build log grep does not need a frontier model, and a naming decision does not want a
cheap one.

### Delegate

- Unity batchmode builds, and grepping the build log for `error CS`.
- Running harnesses, polling logs, waiting for processes, collecting results.
- Log archaeology: "grep these five patterns and report every line verbatim".
- The whole git/gh sequence: branch, add, commit from a file, push, PR from a file, merge, comment,
  close.

### Keep on Opus

- What to build and how to shape it.
- Every line of C#.
- Commit messages, PR bodies, issue comments, architecture sections.
- Reading a failure and deciding what it means.

### Writing the brief

A sonnet agent starts cold. It knows nothing about this project. Every brief needs:

- **Absolute paths**, spelled out. No `$PROJECT`, no "the usual build command".
- **The shell rules** (§4 and §10) restated. They will be violated otherwise.
- **"Do NOT edit any source file."** Say it explicitly, every time.
- **"Report verbatim. Do not summarize or paraphrase log lines."** Otherwise you get a helpful
  summary of the thing you needed to read exactly.
- **A stop condition.** "If there are any `error CS` lines, STOP and report them. Do not continue."
- For message bodies: write them to files in the scratchpad first and have the agent pass
  `--body-file`, never retype prose through an agent.

### What the brief cannot do

It cannot make the agent's expectations correct. A brief that says "expect exactly one failure" will
get a report flagging a deviation when two appear — which is the agent doing its job, and a signal
to re-examine your own expectation rather than the code. That has already happened here twice.

---

## 7. Token economy

The account is a $20 plan. Budget accordingly.

- **Do not read a file to confirm something you just wrote.** The edit tools error on failure; the
  harness tracks file state.
- **Do not re-read `docs/ARCHITECTURE.md`.** It is six thousand lines. `grep -n` for the anchor,
  splice with python, verify with a one-line grep.
- **Never read a subagent's output file.** It is the full JSONL transcript and it will blow the
  context window. The completion notification carries the result.
- **Grep with a head limit.** `| head -20` is almost always enough.
- **One tool call where one will do.** Combine independent greps with `;` into a single Bash call.
- **Work in parallel where the calls are independent** — several tool calls in one block.
- **Delegate anything that produces a lot of output you only need the conclusion of.** A build
  produces thousands of log lines; you need one grep's worth.
- Write long prose to a scratchpad file once, then reference the file. Do not paste a PR body
  through three messages.

The scratchpad is session-specific and outside the project:
`C:\Users\super\AppData\Local\Temp\claude\D--Proiecte-JocStupid\<session>\scratchpad`.

---

## 8. Ponytail, in this codebase

Ponytail is on at **full**. The rule is the laziest solution that actually works — which is a rule
about the *solution*, never about the *understanding*.

The ladder, stop at the first rung that holds:

1. Does this need to exist at all?
2. Is it already in this codebase?
3. Does the standard library do it?
4. Does a native Unity feature cover it?
5. Does an already-installed package solve it?
6. Can it be one line?
7. Only then: the minimum code that works.

What that looks like here:

- **No interface with one implementation.** No factory for one product. No config for a value that
  never changes.
- **Deletion over addition.** The best fix in #75 was deleting `ApplyWorld` and having each
  component pull its own state in `OnStartServer`. The best fix in #144 will be deleting a line.
- **A bug fix is the root cause, not the symptom.** Grep every caller before you edit. One guard in
  the shared function is a smaller diff than a guard in every caller, *and* it is the correct fix.
- **Never lazy about understanding.** Trace the whole flow first. A small diff in the wrong place is
  not laziness, it is a second bug wearing laziness as a costume.
- **Never lazy about**: input validation at trust boundaries, anything host-authoritative, anything
  the user explicitly asked for, or the one runnable check that proves the logic works.
- Mark a deliberate corner-cut with a `ponytail:` comment naming the ceiling and the upgrade path.

Say what you skipped, in one line, when you skip something: `did X; Y covers it. Need full X? Say so.`

---

## 9. House style

### Code comments

Comments explain **why**, never what. The bar: a comment earns its place if it stops the next person
undoing the decision. Look at `RunSave.Begin` — the comment there exists because `AddComponent` runs
`OnEnable` synchronously, which starts a coroutine that runs its first `MoveNext` synchronously too,
which means the read has to come first. Nobody would guess that, and somebody would "tidy" it.

Do not narrate. `// increment the counter` is noise.

### Commit messages

A title that says what changed in plain words, no conventional-commit prefix. Then prose: what the
change is, what was interesting about it, what was deliberately left out. Then the attribution
lines. Recent titles, for tone:

```
The run is still there tomorrow (#75)
Four numbers and an aeroplane (#74)
Knobs that turn something (#84)
The blowgunner was fine; the test had two players (#132)
```

### PR bodies and issue comments

`Closes #n.` at the top of a PR. Explain the decision, not the diff — the diff is right there. Put
the real harness output in a code block. State what was deliberately **not** done and why. If the
work turned up a bug you did not fix, link the issue you filed for it.

### Architecture sections

One `###` section per issue, titled like a sentence, spliced before the
`\n---\n\n## Data-driven content\n` anchor. Structure that has worked: what the acceptance really
demanded, what was chosen and why, the two or three decisions a reviewer would question, the real
measured output, and what was left out on purpose.

---

## 10. The trap catalogue

Each of these has cost at least an hour.

**Shell and tooling**

- `-logfile /dev/stdout` loses compiler output entirely. Always a real file.
- Polling `tasklist` instead of the log file. The process outlives the result.
- A poll regex that is case-sensitive. Use `grep -qaiE`.
- Chaining `cd` or an assignment with `&&` into a backgrounded launch. Absolute paths, each exe on
  its own line, polling in its own foreground call.
- `sed -i` → "Invalid cross-device link". Use `sed … > tmp && mv tmp file` in the same directory.
- Unquoted bash heredocs eat backticks and backslashes. Use `<<'PY'` and pass paths through the
  environment.
- Bash heredocs mangle large C#. Write tool for new files.

**Unity**

- Batchmode exits 0 on a failed compile. Grep for `error CS` every time.
- Batchmode and the open Editor cannot share a project. Check `tasklist` first.
- Adding an input action without regenerating the input asset — the action silently does not exist.
- `UnityEngine.UI.Slider` is a `Selectable`, not a `Graphic`: it has no `rectTransform`. Use
  `(RectTransform)slider.transform`.
- `HudFactory.Anchor` sets `anchorMin` and `anchorMax` to the **same** vector — a zero-sized rect.
  For a full-screen element use `HudFactory.Stretch`.
- A method named `Slider` shadows the type `Slider`. Qualify fully.
- `Physics.Raycast` from inside a collider does not hit that collider.

**Harnesses**

- Headless has no canvas, so no UI can be tested. UI bugs are found by reading.
- `yield return null` resumes **before** `LateUpdate`. Anything glued in `LateUpdate` is one frame
  stale at that moment, and the gap scales with frame time.
- A suite that assumes one player will silently measure a different one.
- `-botMove` drives the player. A test that teleports the player and then measures will fight it.

**Networking**

- FishNet reuses client ids, and every Tugboat client on one machine shares an address. Neither
  identifies a person tomorrow — that is what `PlayerKey` is for.
- `OnStartServer` runs before FishNet has spawned scene objects. Anything that wants a scene object
  has to pull its own state rather than be pushed at.

---

## 11. Things Claude must not claim

- That the game is fun. That is #29, and it needs four humans and twenty minutes.
- That it hits 60fps on the min-spec iGPU. That is #38 and #83, and it needs the hardware.
- That the HUD is understandable. That is #82, and it needs somebody who has not seen it before.
- That a UI change looks right. Headless builds no canvas.
- That a harness passed, without the log line. Paste the line.

If tests fail, say so and show the output. If a step was skipped, say which. When something is done
and verified, say it plainly without hedging.
