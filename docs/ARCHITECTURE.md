# Architecture

Technical decisions and how the signature mechanics actually work. Read `README.md` first for
the stack and the milestone map.

---

## Networking

**FishNet, host-authoritative peer-to-peer over Steam Datagram Relay.**

One player is the host (client and server in the same process). Everyone else connects by SteamID
through the Steam transport, so there is no port forwarding, no dedicated server, and no monthly cost.
Development uses Steam **AppID 480 (Spacewar)** until the real app is purchased.

Tick rate 20–30Hz. Higher buys nothing here — the game is not competitive, and network jank on
ragdolls reads as comedy rather than as a defect.

### Who decides what

| System | Authority | Notes |
|---|---|---|
| Own movement, driven vehicle | Client owner | Predicted locally, reconciled by the server |
| Damage, HP, death | Host | Never trust a client-reported hit |
| Economy, inventory, shop | Host | All transactions validated server-side |
| RNG — roulette, loot, fishing | **Host, always** | Clients only animate a result already decided |
| AI (natives, animals) | Host | Simulated once, transforms replicated |
| Ragdoll, carry | Host simulates | Clients interpolate |

The rule: a client may *predict* anything about itself and may *animate* anything, but may never
*decide* anything that another player can observe or that touches the economy.

### Scene flow and startup

`Assets/_Project/Scenes/Bootstrap.unity` is build index 0 and holds nothing gameplay-specific: a
camera, a light, a placeholder floor, and the `NetworkManager`. Gameplay scenes load and unload around
it, so the connection survives travelling between the island, the boat and the second island.

The `NetworkManager` object carries these components. FishNet creates the rest of its sub-managers
itself in `Awake`, so only these are worth pinning down in the scene:

| Component | Why it is set explicitly |
|---|---|
| `NetworkManager` | Holds `DefaultPrefabObjects`. FishNet can find it by scanning the project, but assigning it writes the reference into the scene where a diff can see it |
| `TimeManager` | Tick rate 30. FishNet already defaults to 30; leaving it implicit means a package update could quietly change how fast the whole game simulates |
| `TransportManager` | Its `Transport` field names `Multipass`. Left implicit it would `GetComponent<Transport>()` and pick whichever of the three transports serialized first |
| `Multipass` | The transport FishNet actually talks to. Holds `Tugboat` at index 0 and `FishyFacepunch` at index 1 |
| `Tugboat` | Plain UDP on port 7770, 8 clients. LAN and every headless test |
| `FishyFacepunch` | Steam Datagram Relay, 8 clients, app 480 |
| `SteamRuntime` | Owns `SteamClient.Init` / `Shutdown` for the process, and survives Steam being absent |
| `TransportSelector` | Picks which link the *client* half dials out on |
| `NetworkBootstrap` | Starts the connection from command-line arguments |
| `SteamLobby` | Creates and joins Steam lobbies, and tells `NetworkBootstrap` who to dial |

### The transport stack

**Both transports are loaded at once, under Multipass.** A host listens on Tugboat and on Steam
simultaneously, so the same running game accepts a friend joining by SteamID and a second machine on
the same LAN joining by IP. Only the client half has to choose, and it chooses at runtime.

The alternative was one transport per build, decided when the scene is generated. It was rejected
because `TransportManager` resolves its transport inside `NetworkManager.Awake`, and `NetworkManager`
runs at `short.MinValue` execution order — no ordinary component can swap the transport before it is
read. Shipping a Steam build and a LAN build means two builds, two smoke-test paths, and a class of
bug that only exists in the one nobody runs.

Multipass is safe here for a specific reason: `ServerManager` computes `Started = IsAnyServerStarted()`,
so a transport that fails to start does not take the server down with it. On a machine with no Steam
client, `FishyFacepunch` declines with one warning and Tugboat carries the session. That is exactly
what every headless run in this project does.

| Argument | Effect |
|---|---|
| *(none)* | Tugboat. Multipass defaults its client transport to index 0, so a build with no arguments behaves as it did before Steam existed |
| `-transport steam` | Client dials out over Steam; `-address` is then read as a SteamID |
| `-transport tugboat` | Explicitly UDP |
| `-steamId 7656119…` | The host's SteamID, and implies `-transport steam` |
| `-steamAppId 480` | Overrides the app id `SteamRuntime` initialises with |

`TransportSelector.PrepareClient` calls `Multipass.SetClientTransport(index)` and
`SetClientAddress(address, index)`, then `NetworkBootstrap` starts the client with the **no-argument**
`ClientManager.StartConnection()`. The overload that takes an address would push it onto every
transport under Multipass and overwrite the one just chosen. If Steam is requested but unavailable,
the selector logs a warning and falls back to Tugboat rather than failing to connect at all.

**Steam is optional on purpose.** `SteamRuntime` catches a failed `SteamClient.Init` and leaves
`SteamRuntime.Available` false; nothing else in the game requires it. `SteamClient.Init` is called
with `asyncCallbacks: true`, so Facepunch pumps its own callbacks and there is deliberately no
`RunCallbacks` in an `Update`.

**The vendored FishyFacepunch fork.** `Assets/Plugins/FishyFacepunch/` is FishyFacepunch 2.1.1 (MIT)
copied in as source rather than referenced as a package, because upstream calls `SteamClient.Init`
unconditionally in `Initialize` and Facepunch *throws* when the Steam client is not running. That code
runs inside `NetworkManager.Awake`, so on a machine without Steam it takes the whole process down
before anything else starts — every headless test in this project, in other words. The fork routes
every entry point through a `TryInitializeSteam()` guard that latches its failure, downgrades the
errors to one warning, and treats a missing Steam client as a declined transport. The edits are marked
`EWYF:` in the source.

**`steam_appid.txt`.** Steam reads the app id from the environment when the Steam client launches the
game and from this file otherwise, which is every run this project makes. `BuildTool` copies the
project-root file next to the built executable on each successful build. It is a development aid: a
shipped depot must not contain it.

**Why a command-line bootstrap.** The whole development loop for this project is a terminal, and a
build that can only be started by clicking a button cannot be tested from one. `NetworkBootstrap`
reads `-host` / `-server` / `-client`, plus `-address`, `-port`, `-quitAfter`, and the test flags
`-latency`, `-botMove` and `-motorLog` described under movement below:

```
Unity.exe -quit -batchmode -nographics -projectPath .   -executeMethod EscapeWithYourFriends.EditorTools.BuildTool.PerformBuild   -buildOutput D:/Builds/EWYF-dev -development -scriptingBackend mono

EscapeWithYourFriends.exe -batchmode -nographics -host   -port 7770 -quitAfter 18 -logfile host.log
EscapeWithYourFriends.exe -batchmode -nographics -client -address 127.0.0.1 -port 7770 -logfile c1.log

# the movement test: four bots walking a lap through a simulated 100ms round trip
EscapeWithYourFriends.exe -batchmode -nographics -host -port 7770     -latency 50 -botMove -motorLog -quitAfter 45 -logfile host.log
```

With no arguments it does nothing and waits for the lobby, which is what a shipped build does. In the
editor it auto-hosts, so pressing play is a one-player session rather than a disconnected one.

`-scriptingBackend mono` builds in about a minute against roughly ten for IL2CPP, which is the
difference between a smoke test that gets run and one that does not. The backend is a serialized
project setting, so `BuildTool` restores whatever was there before — a fast test build must not
quietly change what a release ships with.

### The Steam lobby

A lobby is a Steam-side room, not a game connection. It exists so the overlay has something to invite
people into, and so a joiner can be told which SteamID to dial. `SteamLobby` owns it; the traffic
still runs over the transports above.

**Host.** `CreateLobbyAsync(4)` -> friends only, joinable -> write the lobby data -> start the
server -> start the local client **over Steam**. That last part is deliberate: FishyFacepunch routes
a client whose own server is running in the same process through its `ClientHostSocket`, which needs
no socket, no port and no loopback, so hosting cannot fail because something else holds the UDP port.
Tugboat keeps listening the whole time, so a friend on the LAN still joins by IP.

**Joiner.** An overlay invite, or a friend clicking Join Game, raises `OnGameLobbyJoinRequested` ->
`JoinLobbyAsync` -> `OnLobbyEntered` -> read the host out of the lobby data -> connect over Steam.
Cold starts take the same road from the other end: Steam appends `+connect_lobby <id>` to the command
line when an invite is accepted while the game is closed, and `SteamLobby` reads it from our own argv
and from `SteamApps.CommandLine`.

**Join in progress is not a special case.** The lobby stays joinable while the server runs, so a
friend arriving twenty minutes in walks the same code as one who was there at the start. There is no
late-join branch to get wrong.

Three lobby-data keys, all written by the host:

| Key | Holds | Why |
|---|---|---|
| `ewyf_host` | Host SteamID | Steam hands lobby ownership to another member when the owner leaves, so `lobby.Owner` is not the host — it is whoever is left. The owner field is only the fallback |
| `ewyf_version` | `Application.version` | Builds get handed around as zips long before there is a Steam depot, so version mismatch is the normal case. A joiner on the wrong build is told so and bounced, instead of desyncing |
| `ewyf_name` | Host display name | For a lobby list later |

`NetworkBootstrap` gained `StartHost`, `Connect` and `Disconnect` so the lobby reuses its logging and
its connection rules rather than duplicating them, and it stands down entirely when `-lobbyHost` or
`-lobbyJoin` is on the command line: two things racing to start the same client is a coin flip.

Testing without an overlay:

```bash
EscapeWithYourFriends.exe -batchmode -nographics -lobbyHost -quitAfter 40 -logfile host.log
EscapeWithYourFriends.exe -batchmode -nographics +connect_lobby 109775243737167556 -logfile join.log
EscapeWithYourFriends.exe -batchmode -nographics -lobbyJoin  109775243737167556 -logfile join.log
```

**What one machine cannot prove.** Both processes on this machine are the same Steam user, so a
self-join is seen by Steam as the lobby owner rejoining: the guest branch never runs, and Steam will
not relay a P2P connection from a process to itself. The cross-account leg — invite from the overlay,
a second machine connects over Steam — is a playtest, not something this project can verify against
itself. Same limit as the Steam transport in #13. What is verified here is everything up to it: the
lobby is created with its data, the server comes up on both transports, the local client attaches
over the Steam client-host socket, and a LAN joiner drops into the running session.

There is no lobby UI yet — no `UI/` folder exists. The player list ships as data (`Members`,
`MemberCount`) plus `Entered`, `Left`, `MembersChanged` and `Failed` events, so the HUD that arrives
with #106 draws it without touching this class. Without Steam the component disables itself and the
game hosts over Tugboat, which is what every headless test does.

### SyncVar callbacks fire once, even on a host

Every `OnChange` handler in this project runs unguarded. That is deliberate and it is the opposite of
what the FishNet documentation suggests: the callback carries an `asServer` flag, which reads like an
invitation to skip one of two invocations on a machine that is both.

There is only ever one. `SyncVar.SetValue` picks a single perspective — `asServer` is true whenever the
server is started — and invokes the callback once with it. A host writing its own health therefore gets
exactly one call, with `asServer` true, and never a second one as the client. Code shaped as
`if (asServer && IsClientStarted) return;` compiles, reads as careful, and silently deletes the event on
the one machine that is always in the game.

It cost a full test cycle to find: the host's camera never shook, because `Health.Changed` never
reached it, because the guard swallowed the only invocation. Five callbacks had the same guard —
health, life state, stun, carrier and identity — so on a host the player who is hosting had no impact
shake, no name update and no colour. Every one of them now fires on both sides, and the machine that
does not care about a given change simply has no subscriber for it.

### Player bodies, identity and the registry

A connection is not a player. `PlayerSpawner` on the `NetworkManager` object turns one into the other:
it listens to `SceneManager.OnClientLoadedStartScenes`, not to the connection state, because a
connection that has not finished loading the start scenes has nowhere to put a body yet. The host is a
client of itself and arrives through the same event, so it needs no special case.

Each spawn does four things, in order:

1. `GetPooledInstantiated` + `ServerManager.Spawn(body, connection)` — the connection owns its body.
2. `SceneManager.AddOwnerToDefaultScene(body)` — without it the body stays in the spawner's scene and
   the owning client, which loaded its own copy of the start scenes, never becomes an observer of it.
3. A spawn point if any are assigned, otherwise a generated 8-slot ring facing the middle. A greybox
   scene with nothing but a floor still spawns four players who can see each other.
4. `PlayerIdentity.ServerSetIdentity(name, colourSlot)`.

**Colour is a palette index, not an RGB value.** One replicated byte instead of sixteen, and it
guarantees the set of player colours stays the set that was chosen to be distinguishable — including
through the blur the alcohol buff puts over the camera. The server hands out the lowest free slot and
frees it on disconnect, so with four players the four most distinct colours are always the ones in
play. More players than palette entries wraps rather than refusing to spawn anyone.

Telling four identical bodies apart is not cosmetic. The comedy only lands if you know *whose* ragdoll
went off the cliff, which makes identity a mechanic, and it is why it is host-assigned rather than
client-chosen: two players cannot both be red.

`NetworkPlayerRegistry` is a **static, non-networked** index of the bodies FishNet has already
replicated onto this machine, keyed by owner id. The HUD draws a row per player, the Revive Machine
has to find a corpse's owner, natives pick a target, the scoreboard sums wallets — without it each of
those calls `FindObjectsByType` every frame, which is both slow and subtly wrong, because it also
finds bodies that are mid-despawn.

Bodies register themselves from `PlayerIdentity.OnStartNetwork` rather than being registered by the
spawner: that way clients populate their own registry from what they can already see, with no second
RPC, and a future dedicated server populates it too, where no client callback ever runs. On a client
the registry therefore holds everyone in observer range; on the host it holds everyone. Because the
state is static and a dropped connection does not despawn anything, `NetworkBootstrap` clears it once
both the server and the client have stopped.

**The player prefab is generated, not sculpted.** `PlayerPrefabBuilder` (editor, batchmode) writes
`Assets/_Project/Prefabs/Player.prefab` from a `Bone[]` table: 11 bones, 10 `CharacterJoint`s, a
`CharacterController`, and the whole combat stack. Every bone transform sits *unscaled at its joint
pivot* with the scaled primitive as a `Mesh` child, so resizing a body part never scales the bones
below it. Total mass is ~56 kg — light for a human, which is exactly why hits send people flying. The
builder also registers the prefab in `DefaultPrefabObjects`, because FishNet's auto-scan runs on asset
import and that does not reliably happen inside a single batchmode invocation.

---

### Movement: predicted by the owner, reconciled by the host

`PlayerMotor` is a `TickNetworkBehaviour`, not a `MonoBehaviour`. It runs on the network tick (30Hz),
never on the frame, because everything below depends on the owner and the host feeding the *same*
inputs to the *same* code in the *same* order.

The owner simulates its own movement the instant a key goes down and keeps a history of what it did.
The host re-runs those inputs authoritatively and sends the resulting state back. If the state
differs from what the owner had predicted, the owner snaps to the host's version and replays every
input since; when the prediction was right — nearly always — the replay reproduces the same position
and nothing visible happens. That is what makes movement feel local on a 100ms connection without
letting a client simply declare where it is standing.

Two structs carry it:

- **`MoveData`** (`IReplicateData`) — what was pressed on one tick: a `Vector2` move axis, a yaw, and
  a `MoveFlags` byte for sprint/crouch/jump. Three fields on purpose. This goes over the wire every
  tick, from every player, forever.
- **`MotorState`** (`IReconcileData`) — everything the replicate reads that is *not* in `MoveData`:
  position, velocity, `TicksSinceGrounded`, `TicksSinceJump`, `Crouching`. **The counters matter as
  much as the position.** If a value influences the next tick and is missing from the reconcile, the
  owner and the host drift apart every time it differs, and the player rubber-bands.

`Tick` builds and replicates the input; `PostTick` calls `CreateReconcile`. Anything sampled in
`Update` would be a frame out of step with the simulation.

**Spectators do not get this treatment.** The player `NetworkObject` has prediction on, state
forwarding **off**, and its `NetworkTransform` assigned. That combination makes FishNet call
`NetworkTransform.ConfigureForPrediction`, which switches the transform to server-authoritative and
stops sending it to the owner. So the owner is driven purely by prediction, everyone else purely by
interpolation, and the two never fight over the same transform. Non-owners pay for nothing but a
transform stream.

Jump is a **buffered one-shot**, consumed in `BuildMoveData` rather than read. At 30Hz a third of a
second of taps would otherwise land between two ticks and vanish. Sprint and crouch are held, so they
are just bits.

Stun, downed, carried and ragdolled bodies still fall — they just do not steer. Those states are
SyncVars rather than part of the reconcile, so a replay uses their *current* value and a mispredicted
tick is cleaned up by the next reconcile. A ragdolled body switches the `CharacterController` off
entirely, and the replicate exits early rather than fighting the physics engine for the transform.

The feel is deliberately loose: acceleration and friction rather than instant velocity, a floaty
`_gravityScale` of 2.2, a jump tuned as a height in metres (`v = sqrt(2gh)`, so the knob is a number
a playtester can reason about) and coyote ticks. Sliding past the ledge you meant to stop at is the
joke, not a bug.

### Input: polled, not evented

`PlayerInputReader` is deliberately **not** Unity's `PlayerInput` component. That component pushes
input through UnityEvents and `SendMessage` on the frame the device changed; prediction needs input
sampled *by the tick*. So this polls, and buffers the one-shot presses that happen between two ticks.

The action asset itself is generated: `InputAssetBuilder` (editor, batchmode) writes
`Assets/_Project/Input/PlayerControls.inputactions` through the Input System API rather than by hand,
because binding strings are the part that fails silently — a typo in `<Gamepad>/leftStick` imports
cleanly and simply never fires. One `Player` map, nine actions, keyboard/mouse and gamepad on each.

Only the owner ever calls `Bind`, and it binds a **clone** of the asset. Action assets carry their own
enabled state, so four bodies in one process sharing one instance would fight over it. Non-owned
bodies keep the component sitting inert.

Buttons reach the combat systems through one small owner-only component, `PlayerCombatInput`: attack →
`MeleeAttack.RequestAttack`, alt-attack → `TaserWeapon.RequestFire`, interact and drop →
`CarrySystem`. None of those systems poll input themselves, on purpose — a weapon that reads the
keyboard cannot be fired by an NPC, a scripted test bot or a vehicle turret — so this is the single
place that knows which button means which verb. Combat is not predicted, so it runs on the frame
rather than the tick, but it still *consumes* from the reader's buffer rather than reading the device:
a tap can fall between two frames of a stuttering client, and a punch that silently did not happen is
the worst possible bug in a game about punching your friends.

### Looking around: a camera that is not attached to the body

The camera is **not a child of the player body**, and that is the whole design.

The body is moved by prediction, which means it moves once per network tick — 30 times a second —
while the screen refreshes at 60 or 144. Parenting the camera to it hands that stepping straight to
the player's eyes, and no amount of Cinemachine damping downstream can recover motion that was never
sampled in the first place. FishNet ships a `NetworkTickSmoother` for exactly this, but it is beta,
it is a `NetworkBehaviour` that has to sit on a graphical child object, and it configures through
private serialized structs that are awkward to fill from an editor script. `PlayerCameraRig` does the
smoothing itself instead, in about fifteen lines. Worth revisiting when that component leaves beta.

So the rig owns a **detached target transform** and splits the two halves of a camera pose by where
they come from:

- **Position** is filtered toward the body's eye point with an exponential follow, time constant 35ms
  — roughly one tick. Written as `1 - e^(-dt/tau)` rather than a constant `Lerp` factor, because a
  constant factor is a *different filter at every frame rate*, which is why cameras written that way
  feel snappy on a fast machine and floaty on a slow one. A jump of more than 1.5m snaps instead of
  sliding: that is a teleport or a respawn, not motion.
- **Rotation** is taken from the mouse at frame rate and never filtered at all, so rotational jitter
  is structurally impossible. Yaw comes from the same `PlayerInputReader` field the motor replicates,
  so the camera and the body always agree without either driving the other. Pitch is camera-only and
  is never sent anywhere.

Between the target and the actual `Camera` sits a `CinemachineCamera` with `HardLockToTarget` +
`RotateWithFollowTarget` — no damping of its own, so there is one filter in the chain rather than two
fighting each other. Cinemachine is there for what comes next: spectating a dead friend (#26), a
vehicle chase camera, the revive machine's animation are each a second virtual camera and a priority
change, instead of a pile of if-statements in the rig. `SceneBootstrap` puts a `CinemachineBrain` on
the scene camera; the rig adds one defensively if it is missing. The rig runs at
`[DefaultExecutionOrder(-100)]` because `CinemachineBrain` declares no order of its own, and the
target has to be written before the brain reads it.

Only the owner runs any of this. A spectator's view of someone else's body is the `NetworkTransform`,
so `OnStartClient` disables the component outright on non-owned bodies. Because the camera is not
parented to the body, `OnStopClient` has to destroy it explicitly — otherwise despawning would leave
the highest-priority view in the scene pointed at nothing.

**Ragdolled.** When the ragdoll takes over, the body root stops moving and the head bone is the only
thing that knows where the player's eyes are, so the follow target switches to the head bone and the
time constant is loosened to 90ms — being dragged around should be woozy, not nauseating. Bob and
shake are skipped entirely while limp. The other half of that fix is not in the rig at all: every
ragdoll `Rigidbody` is built with `RigidbodyInterpolation.Interpolate`, because physics runs at 50Hz
and the screen does not. Stepping is invisible on a body across the room and is the entire picture
when the camera is riding that body's skull.

**Head bob** is driven by distance travelled, not by time, so it slows down when you slow down. A
figure of eight: vertical at twice the phase, lateral and roll at once. Both are added *after* the
follow filter, never before — smoothing a footstep is the same as deleting it. When you stop walking
the offset is unwound rather than cut, because dropping it to zero on the frame you stop is a visible
snap.

**Shake is trauma, not amplitude.** A single 0..1 value that decays linearly and is squared on the way
out, so small trauma is a nudge and large trauma is the whole screen. Two consequences, both wanted:
several hits landing together *build* instead of the last one overwriting the rest, and the falloff is
sharp rather than a long fade. The displacement is Perlin noise sampled on five separate rows, so
consecutive samples are related — random per frame is static, not shake. Two things feed it:

- `Health.Changed` — scaled by the fraction of max health lost, so a stray punch is a nudge and being
  run over is the whole screen. Healing raises health and shakes nothing.
- `ShockState.CameraShake` — the taser holds trauma at a **floor** for as long as the shock lasts,
  which gives a continuous rattle without a coroutine ticking it.

Anything else that wants a kick calls `AddShake(0..1)`.

**FOV** eases between 70 and 78 while sprinting on the ground, 180ms time constant.

**Aim.** The rig pitches the body's `AimOrigin` to match the view, so a punch or a taser shot goes at
whatever is under the crosshair rather than straight out of the chest at eye level. Only the
*rotation* is touched, and that is a deliberate limit: the weapons send a direction over the wire and
the server resolves the hit from **its own** copy of that transform, so moving the local one's
position would change nothing that is transmitted while quietly desyncing what the player sees from
what the server checks. `AimValidation` only ever tests the horizontal angle, so pitch is free.

### Proving it works without a keyboard

A headless smoke test has no devices at all, so a run that only checks for silence would pass on a
motor that never moved. Two flags close that hole:

- **`-botMove`** puts the reader into a fixed lap: forward, a steady 60°/s turn, sprint for half of
  every 8-second cycle, a crouch slice, and a jump every 4 seconds. Every branch in the motor is
  exercised, and because the body keeps turning it also covers moving in a direction it is not
  facing — where a yaw that failed to replicate would show up. The turn rate is what keeps the lap on
  the platform: radius is speed over turn rate, so 7.5 m/s at 60°/s is about 7 metres and four bots on
  the spawn ring stay well inside the 50-metre greybox floor. At 25°/s they walked off the edge and
  fell for the rest of the run, which reads as a movement bug and is not one.
- **`-latency 50`** turns on FishNet's `LatencySimulator` for 50ms each way — the 100ms round trip the
  milestone is specified against. It lives behind `DEVELOPMENT_BUILD`, so it is compiled out of a
  release build.
- **`-motorLog`** prints one owner-only line every 60 ticks with the prediction error.
- **`-cameraLog`** prints one owner-only line every 2 seconds with frame count, average and **worst**
  frame time, FOV, peak trauma and how many frames of the interval were ragdolled. The worst frame in
  an interval is the only part of "smooth 60fps, no jitter" a headless run can actually report; the
  average is not a performance number at all under `-nographics`, where nothing renders. Trauma and
  the ragdoll are reported as an interval peak and a frame count rather than as instantaneous values,
  because both are transients — a shock is under a second — and sampling them every two seconds
  reports zero on a run where they fired dozens of times. The first version did exactly that.

The bots also **brawl**: `-botMove` swings a punch every 1.5 seconds and fires the taser every 7. Four
bodies circling a 4-metre ring are inside each other's reach, so a headless run ends up exercising
melee, stun, ragdoll, the shock shake and the ragdolled camera path without anyone touching a
keyboard. Before that, an automated run only ever saw a character standing upright — which is exactly
the case the camera handles well.

**That error has to be measured against our own history, not against where we happen to stand now.**
An incoming state is always a round trip old; comparing it to the present measures the latency and
nothing else. The first version of this did exactly that and reported a steady 2.7m "correction" on a
perfectly healthy motor. The motor now keeps a 128-tick ring of what it predicted for each tick and
compares the arriving state against the entry for *its* tick.

Measured on 1 host + 3 clients, Mono development build, `-latency 50 -botMove`:

```
[PlayerMotor] owner 1 over 60 ticks: 60 reconcile(s), 60 measured, average error 0.0000m, worst 0.0000m
[PlayerMotor] owner 2 over 60 ticks: 60 reconcile(s), 60 measured, average error 0.0044m, worst 0.2634m
[PlayerMotor] owner 3 over 60 ticks: 60 reconcile(s), 60 measured, average error 0.0052m, worst 0.3148m
```

Most windows are exactly zero. The spikes are jump and crouch transitions, where grounding can resolve
one tick apart on the two machines; they are corrected on the next tick and are far below anything
visible. Zero exceptions across four processes.

---

## The signature mechanics

### What one hit does

Every weapon — fists included — embeds a `HitProfile`: damage, damage type, knockback, upward bias,
stun duration. Nothing about how a weapon feels is written in weapon code, so "a shotgun launches
you across the beach and a pistol does not" is a number in an asset that can be retuned without a
recompile.

| | damage | knockback | stun |
|---|---|---|---|
| Fists | low | ~4 | short |
| Bat | medium | ~10 | medium |
| Pistol | medium | ~0 | none |
| Shotgun | high | ~30 | long |
| Sniper | very high | ~45 | long |

Knockback and stun are separate numbers, but a hit meant to launch someone needs both: an upright
character is driven by its controller, not by physics, so the impulse only reads if the victim is
ragdolled. The one exception is a body already on the ground, which takes the impulse with no stun
duration at all — shooting a downed friend still sends them tumbling, which is most of the appeal.

### Stun and ragdoll

**v1 — kinematic switch (build this first).**
The character is an animated controller with a humanoid rig carrying a Rigidbody and Collider per
bone, kinematic by default. On stun or death the host sends a stun state; every client switches the
bones to non-kinematic, applies the impulse, and disables the controller. On recovery the ragdoll
pose is sampled and blended back into animation over a short window.

**v2 — active ragdoll (upgrade after M1 works).**
Every bone gets a `ConfigurableJoint` whose `slerpDrive` targets the current animation pose. A hit
temporarily drops `positionSpring`, so the character flops without going fully limp — still standing,
still trying, visibly failing. This is the trick behind Gang Beasts and Human Fall Flat, and it is
what separates "funny" from "a body fell over".

Do not attempt v2 before v1 ships. It is a tuning problem, and tuning is much easier once the
surrounding systems are stable.

### Carry

Interacting with a stunned or dead player asks the host to validate the grab. On approval the target's
hip Rigidbody goes kinematic and parents to the carrier's `CarrySocket`, and the two colliders are
mutually ignored via `Physics.IgnoreCollision`. Throwing applies an impulse to the hip and releases
the parent. Carrying slows the carrier, which is what makes hauling a corpse across the island a real
decision rather than a free action.

### Taser

Mechanically a ranged punch with a very long stun. What makes it worth carrying is the jitter: while
shocked, a random bone takes a small impulse ten times a second, and the victim's camera shakes.

**The jitter is not networked.** Ten impulses a second per victim is not worth the bandwidth, and it
does not need to be. The impulse step is derived from the network tick, and the random numbers come
from an integer hash of (object id, step), so every machine rolls identical values at the same moment
without a single extra packet. `ShockState` carries one SyncVar holding the whole shock — end tick,
jitter force, interval, shake — because those four values are one fact and separate SyncVars could
land out of order and briefly describe a shock nobody configured.

**Battery is the balance lever.** A taser with no ammunition cost would be strictly better than every
melee weapon, so the decision that matters is whether this target is worth a third of the charge. The
charge itself is also not streamed: a shot writes two values — the charge left at that moment, and the
tick recharging begins — and every peer recomputes the current level from the same formula the server
uses, so there is nothing to drift.

Both patterns are the same idea as the bleed-out timer: replicate the *rule* and the *deadline*, then
let every machine derive the continuously-changing number itself.

### Downed, abducted, dead

Running out of health does not kill you. `Health` has three states — `Alive`, `Downed`, `Dead` — and
the interesting one is the middle.

**Downed.** At 0 HP the character ragdolls, becomes carryable, and starts a bleed-out timer every
player can see. The deadline is stored as a FishNet network tick rather than a local timestamp, so
all four clients count down to the same moment and the HUD number is one everyone agrees with.
Getting picked up in time is cheap and free.

**Abducted.** Nothing stops a hostile native from picking up a downed body, because `Carryable` does
not care who is carrying — it only needs the carrier to have a `CarrySystem`. So natives haul downed
players back to their village and string them up while the timer keeps running. Rescue means
assaulting the village, which turns a teammate going down from a nuisance into an objective, and
gives the natives a reason to exist beyond wandering around. Killing them drops food, ammo and
materials, so the rescue pays for itself.

**Dead.** The timer expires and now it costs. The body has to be physically hauled to the Revive
Machine at base and paid for, at a price that scales with deaths this run. The whole loop is
deliberately inconvenient: the inconvenience is the content.

Dead players are not idle — they become ghosts that spectate and lightly push physics objects, so
they stay engaged and stay able to interfere.

The escalation is the point. Going down is recoverable, being carried off is a fight, and staying
dead is a bill.

**The view from a corpse.** A first-person camera locked inside a ragdoll's skull is a face full of
dirt, and a player who cannot see anything stops caring what happens to their body. Death therefore
pulls the view out to third person. `DeathCamera` is a *second* `CinemachineCamera`, built on death at
priority 20 against the rig's 10 and destroyed on revive, so the Brain blends both ways and neither
camera knows the other exists. It tracks the hip bone rather than the body root, because once the
ragdoll takes over the root stops moving and the corpse slides away from it, and the position damping
is heavy on purpose — a hip being punted down a hill is not something to follow tightly. The whole
class is one public method, `Follow(Transform)`, which is also how the ghost gets built: spectating a
friend is that call with somebody else's bones and the same blend carries the player there.

**A body outlives its owner.** FishNet despawns everything a connection owns the moment that
connection drops, which is the right default and exactly wrong here. A dead player is a physical
object their friends have to haul and pay for, and the most common reason to be dead for a long time
is that the game crashed. If the corpse leaves with the connection, the punishment for a bad
connection is that your friends cannot get you back.

`BodyPersistence` keeps it, and it does so by ordering rather than by a flag. FishNet's
`PreventDespawnOnDisconnect` is serialized and internal — unreachable at runtime, and turning it on
for the player prefab would leave a standing mannequin behind after every disconnect. But
`ServerManager` raises `OnRemoteConnectionState` *before* it sweeps `connection.Objects`, so removing
ownership inside that handler takes the body out of the collection the sweep is about to read. No
prefab flag, no fork, and no despawn-and-respawn dance that would lose the ragdoll's pose and whatever
the body is currently tangled in.

It only fires if you were already down. An upright player who quits takes their body with them:
leaving a standing copy behind would be a free decoy and an invitation to disconnect on purpose, and
there is nothing there to revive. An abandoned body is unregistered from `NetworkPlayerRegistry`, so
the squad list stops claiming someone is present, and appears in the static `BodyPersistence.Abandoned`
list the Revive Machine reads instead. `PlayerMotor` builds no input for a body it does not own, so an
ownerless body simply stands where it fell.

Reclaiming that body when its owner comes back is the next section; `ServerAdopt(NetworkConnection)`
is the call both it and the Revive Machine go through, since a revived body with no owner cannot be
walked away.

**What you were carrying stays on you.** Death does not scatter loot and does not bank it. The body is
already the object that has to be recovered, so making it the container costs nothing and doubles the
stakes of the haul. Inventory itself is M3; the seam it will hook is `Health.ServerStateChanged`,
which fires server-side before the state SyncVar is written.

**A truck bed is a carrier too.** `Carryable` used to reach for a `CarrySystem` by type, which quietly
decided that only a character with arms can hold a body. `ICarryHolder` is that assumption made
explicit and then removed: one property, `CarrySocket`, implemented by `CarrySystem` today and by a
vehicle seat or a boat deck later, with no second attach path and no fake `CarrySystem` bolted onto a
truck. Everything else about carrying — the range check, the throw impulse, dropping on death — stays
with the holder.

`-deathTest <seconds>`, `-deathTestOwner <id>`, `-deathTestKey <key>` and `-reviveTest <seconds>`
drive the headless regression, on the same principle as `-fallTest`: the test lives inside the
component it tests, because the alternative is a build flavour that only exists for tests and is
therefore not the build anyone ships.

---

### Coming back for your own corpse

A crash is the most common way to end up dead for a long time, so the body that outlives its owner is
worth nothing unless that owner can come back and stand in it again. Doing that needs a name for a
player that survives losing the connection, and the two names FishNet hands out do not qualify: client
ids are recycled, so the next joiner walks into the corpse of whoever freed the slot, and every
Tugboat test client shares `127.0.0.1`.

**`PlayerKey` is that name.** One static class, resolved once per process, first hit wins:

1. `-playerKey <value>` from the command line — how the headless harness gives four processes four
   stable identities.
2. The Steam id, when Steam is up. This is the real one in a shipped game.
3. A GUID kept in `PlayerPrefs`, generated on first run. Covers a direct-connect LAN game with no
   Steam, and survives a restart because it is on disk.

Never the client id, and never the address.

**The key travels through an `Authenticator`, not through a message after joining.** FishNet's
`PlayerKeyAuthenticator` runs before the connection is authenticated, which is the only point where
the server is guaranteed to know the key *before* `OnClientLoadedStartScenes` fires and asks who this
is. A post-join RPC would race the spawn, and losing that race means a fresh body is already standing
where the corpse should have been reclaimed. The authenticator broadcasts `PlayerKeyBroadcast` from
the client, validates it (non-empty, at most 128 characters, no duplicate among live connections),
answers with `PlayerKeyResultBroadcast`, and keeps a client-id → key table that `PlayerSpawner` reads
back with `TryGetKey`. Rejecting a duplicate matters: two processes claiming `ALPHA` would otherwise
fight over one corpse.

The spawn path then has one extra question at the top:

```
OnClientLoadedStartScenes
  → ResolveKey(connection)            // null when no authenticator: pre-#111 behaviour, fresh body
  → BodyPersistence.FindAbandoned(key)
      hit  → ServerAdopt(connection) → AddOwnerToDefaultScene → book the colour → done
      miss → spawn a fresh body as before
```

**Ownership first, scene second.** A body added to the client's scene before it is owned arrives with
`IsOwner` false, and every owner-side component — motor, camera, HUD — starts up in spectator mode on
a body the player is supposed to be driving. The spawn ring is also *not* advanced on a reclaim: you
come back where you fell, not at the next free spawn point.

`BodyPersistence` carries the key it was spawned with (`ServerSetOwnerKey`, stamped right after the
spawn because the component only accepts it once the object is networked) and `FindAbandoned` matches
on it, skipping anything that is no longer abandoned. The colour slot is re-booked under the new
connection id, and `TakeColor` now also refuses any slot an abandoned body is still wearing — the
table is keyed by connection id and a disconnect frees the entry, so without that check the next
joiner is handed the colour of a corpse whose owner is about to walk back into it. That is not
hypothetical: it happened on the first run of the four-process test.

**Two flags exist only to make this reachable without a keyboard.** `-carryTest <seconds>` puts a body
on the host's shoulder, because "the owner dropped while somebody was carrying them" is the one case
that cannot be reached by killing and disconnecting alone. It cannot use the sphere cast — that needs
a camera aimed at a body — so it walks the carrier to the target and then goes in through
`ServerTryPickup`, the same door the RPC uses, with every rule still enforced including the server-side
range check. Only the aiming is stubbed. `-deathTestKey <key>` kills the body belonging to one player
key rather than one connection id.

**That second flag exists because connection ids are not stable across a multi-process run.** They are
handed out in the order the transport accepts sockets, and a host's own local client is not reliably
connection 0: a client process that finished booting while the host was still loading the scene takes
0, and the host lands on 1. The first run of this test used `-deathTestOwner 1`, killed the *host's*
body, left the intended victim standing, exited green on all four processes and proved nothing. Any
test hook that has to name a specific player names it by key.

The four-process run, host log, in order:

```
[PlayerKeyAuthenticator] connection 1 accepted with key ALPHA.
[PlayerSpawner] Spawned body for connection 1 at (6.00, 1.20, 0.00), colour slot 1, key ALPHA.
[BodyPersistence] -deathTest: owner 0 spared, key HOST is not ALPHA.
[BodyPersistence] -deathTest: owner 1 killed, state Dead.
[CarrySystem] -carryTest: owner 0 picked up owner 1 = True, carried=True, body at (6.00, 0.08, 0.00).
[BodyPersistence] Owner 1 left while Dead; body kept in the world at (6.00, 0.08, 0.00). 1 abandoned.
[PlayerSpawner] Spawned body for connection 2 at (0.00, 1.20, -6.00), colour slot 2, key BRAVO.
[BodyPersistence] Body of owner 1 adopted by connection 3.
[PlayerSpawner] Connection 3 reclaimed the body of former owner 1 (key ALPHA) at (6.00, 0.08, 0.00), state Dead, colour slot 1.
```

ALPHA died while being carried, dropped out mid-carry, came back on a *different* connection id and
got the same body, in the same place, in the same state. BRAVO, who joined in between, got a fresh
body and a different colour. On its own client ALPHA's returning process draws its own squad row as
`[you] DEAD - carried` — it is not watching that body, it is that body.

### Getting back up

The downed state only means something if there is a way out of it that is not the Revive Machine.
That way is a teammate holding Interact on you for three and a half seconds, and almost every design
decision in it is about who owns that timer.

**Thing and doer, the same split as carrying.** `Rescuable` sits on every player and is what a
teammate aims at; `RescueSystem` sits on every player and is the hold they run when they are the one
helping. Everybody is both, because everybody ends up on the floor eventually. The split is worth the
two files because the two halves answer different questions: the victim knows whether it is a target
and who has it covered, the rescuer knows whether it is still allowed to be helping.

**Progress is replicated on the victim, not the rescuer.** `Rescuable._progress` is a SyncVar at 10Hz
— a bar that fills in three and a half seconds looks identical at ten updates a second and at thirty.
It lives there because the HUD (#106) is already drawing a marker over the downed body, and putting
the bar on the rescuer would mean the HUD has to go hunting for whoever happens to be kneeling
nearby. `_rescuer` is replicated alongside it for the same reason: a third player can see the rescue
is handled and go do something more useful.

**The server times the hold.** A client-side countdown ending in one "I rescued them" message is a
single number a modified build sets to zero, and unlike a mistimed punch this one undoes a death. So
the client sends exactly two things: the press, which goes through `PlayerInteractor` like every other
interaction and gets the same range validation, and the release, sent on the key-up edge only. A
stream of "still holding" packets would tell the server nothing it does not already assume.

Every frame the hold is running, the server re-checks four things, and each one is a rule the mechanic
is actually about:

- the rescuer is alive and unstunned — punch the helper and the help stops;
- the rescuer has taken no damage since the hold began — this is the interrupt the whole mechanic
  exists for, and it is why a firefight is a bad place to pick someone up;
- the target is still `Downed` — bled out, helped up by someone else, or carried off by a native all
  end it;
- the two are within five metres — walking away cancels, and no message from the client is needed.

The damage check compares against `_healthAtStart`, banked in `ServerBegin`, rather than subscribing
to `Health.Changed`. One number read per frame is cheaper than the subscribe/unsubscribe bookkeeping,
and it also catches damage that landed in the same frame as the press. One point of damage is enough:
the interrupt is not about how hard you were hit, it is about whether anyone is shooting at you at
all.

**An empty prompt means "not a target".** Making every player an `IInteractable` broke the Revive
Machine. `PlayerInteractor.RequestInteract` returns true whenever it finds an interactable, and
`PlayerCombatInput` uses that return value to decide whether to fall through to carrying — so a
`Rescuable` on every body in the game swallowed the Interact key on every body in the game, and a
corpse could never be picked up again (#25). The fix is a convention on `IInteractable.Prompt`: empty
or null means the component is present but offering nothing, and `FindTarget` skips it. That does not
contradict the interface's rule that a prompt is not a permission check — the distinction is what a
client can answer for free. Life state is a SyncVar sitting in memory, so "is this even a rescue
target" costs nothing and is never stale in a way that matters. "Can this actor afford it" still needs
the server, and still lives in `ServerCanInteract`.

Interact is also the first verb in the game that is a hold rather than a tap, which the buffered
press in `PlayerInputReader` cannot express — by the time the buffer is read the key may already be
back up. Hence `InteractHeld`, a live read alongside `Sprint` and `Crouch`. Scripted bots report it
permanently held, so an unrelated bot test never fails on a released key.

**Two tuning calls, both provisional until the #29 playtest.** Bleed-out came down from 90 seconds to
45: 90 was chosen before there was any way off the floor, and now that there is one, lying there for a
minute and a half is just a player not playing. Rescue health stays at 35% — enough to stand up, not
enough to stay in the fight, which is what makes the second knockdown feel earned.

**Downed players cannot crawl, and that is a decision, not a gap.** Crawling means un-ragdolling into
a whole second locomotion mode, and a downed player who can drag themselves out of danger deflates the
rescue into a formality. Shorter bleed-out is the answer to the boredom instead. Revisit if the
playtest says otherwise.

`-rescueTest <seconds>` drives the regression. The sphere cast is the one part that cannot run
headlessly — it needs a camera pointed at a body — so the test starts where `Rescuable.ServerInteract`
starts, after the aim has already resolved; everything the mechanic actually guards is downstream of
that. It downs a teammate, drags it to the rescuer's feet, holds halfway, punches the rescuer for one
point, and passes only if that cancels — then holds again uninterrupted and requires the victim back
on `Alive`. The claim is static: every player body on the server carries a `RescueSystem`, so without
one the flag arms a test per player and they all knock each other down at once, leaving nobody upright
to do any helping.

### The squad HUD

A bleed-out timer nobody can see is not tension, it is a coin flip that happens off screen. The HUD is
what turns 45 seconds of `Health` state into a decision: who is down, how long they have, how far away
they are, and whether somebody is already on it.

**The model is separated from the widgets, and that split is what makes the HUD testable.** A headless
run has no screen, no font and no graphics device, so a HUD written as one lump of Canvas code can only
ever be checked for "did not throw". `SquadModel` is pure data — one struct per player, built from
`NetworkPlayerRegistry` plus `Health`, `Carryable` and `Rescuable` — and it runs on every peer whether
or not a canvas exists. `SquadPanel` and `DownedMarkers` are the only parts that touch uGUI, and
`HudRoot` builds them only when `SystemInfo.graphicsDeviceType` is not `Null`.

**Nothing in the HUD talks to the server.** Every number it shows is already replicated onto this peer:
the life state, the tick the bleed-out ends on, the rescuer and the hold progress. A HUD that had to
ask the server what to draw would lie for a round trip every time something happened, which is exactly
when it matters. The corollary is that the HUD is a read-only view — it never sends, so nothing about
it needs validating.

**Built in code, and with legacy `UnityEngine.UI.Text`.** Same rule as the scene and the arena: a thing
that only exists as a binary someone assembled by hand cannot be reviewed in a diff or rebuilt from a
terminal, and a HUD is the easiest place in a project to break that rule. TextMeshPro was the obvious
choice and is not usable here: it needs its essential resources imported through an editor menu before
a single character renders, and an asset that only appears when a human clicks a menu item is the kind
of dependency this project keeps out. The built-in font has no asset dependencies at all. `HudFactory`
is the one file that would change if that ever stops being true.

There is deliberately no `GraphicRaycaster` and no `EventSystem`. Nothing in this HUD is clickable, and
a raycaster stretched over the whole screen is a good way to eat a click the game wanted.

The panel is one row per player in registration order. Sorting by "most urgent" was the alternative and
is wrong: rows that move while you are reading them are rows you have to re-find every time somebody
goes down, which is the exact moment you have no attention to spare. The local player is marked in
place rather than pulled to the top for the same reason. Rows are built once and reused; four players
is a small number, but this refreshes every frame.

Colour carries the state, not the identity. The swatch is the player's own colour and answers *who*;
everything else on the row is coloured by *what happened* — green when someone is helping, purple when
they are being carried off, grey when dead, and amber running to red as the timer empties, so the
colour is the countdown for anyone glancing rather than reading. A red player being dead has to look
different from a red player being fine.

Markers answer the other half. The squad list says *that* someone is down; the marker says *where*,
and a countdown you cannot act on is only stress. Two details do the work. The anchor is the hip bone,
not the body root — once the ragdoll takes over, the root stops moving and a root marker would hang in
the air where the player went down rather than over where they now are, which also makes the marker
follow a corpse that somebody has picked up. And a marker for a player who is off screen is clamped to
the screen edge rather than hidden, because the common case is that they went down behind you; a point
behind the camera is mirrored through the screen centre first, since `WorldToScreenPoint` returns it
upside-down and backwards when `z` is negative and drawing it unmirrored sends the arrow the wrong way.

The local body is found through ownership every frame rather than cached at spawn. Which body is yours
is not fixed for a session: the ghost (#26) and reconnect adoption (#111) both change it.

`-hudTest <seconds>` prints the squad rows once a second on whichever peer it is passed to, and the run
that matters is the client one. Paired with `-rescueTest` on the host, both peers print the same player
counting down from the same deadline:

```
[HudRoot] -hudTest host:   owner 1 Player 2 [2m]  DOWN 0:44
[HudRoot] -hudTest client: owner 1 Player 2 [you] DOWN 0:44
```

That is the claim worth proving. The countdown is derived from a replicated tick, not from a local
timer started by a message, so it is the same number on a machine that is not the server — and it stays
the same number after a dropped packet, which a local timer would not.

`-rescueTest` waits three seconds between downing the victim and reaching for them, rather than half a
second, so that a once-a-second sample on both peers lands inside the window where the victim is simply
down. Without the gap every sample landed mid-rescue and the number the HUD exists to show was never
observed.

### The ghost

A dead player waits. The wait is deliberate — somebody has to walk over, pick the body up, carry it to
the machine and pay — but a player who can only watch a fixed shot of their own corpse has nothing to
do for a minute or more, and the fastest way to lose a friend from a lobby is to bore them. So the
dead get a ghost: a free-flying camera that goes where it likes, follows the argument about whether
hauling them back is worth 250, and can shove ragdolls and loose physics props hard enough to be
annoying. Trolling is the retention mechanic.

**The ghost is not a NetworkObject.** It is a bare transform living on the player's own prefab,
spawned nowhere and replicated to nobody. Nothing about a ghost needs to exist on another machine:
nobody can see it, it has no collider, and the one thing it does that other players can observe — the
shove — travels as an RPC that names the target and the impulse, not as a position anyone integrates.
Making it a spawned object would have bought a despawn ordering problem against `BodyPersistence` and
a second identity per player, for nothing.

**Glued to the body while alive.** `GhostController` keeps its root pinned to the character controller
until death, then unpins it. This is the detail that removes a whole class of ordering bug from
`DeathCamera`: the death camera asks for the ghost's transform the instant the state changes, and if
the ghost were only positioned once it started flying, the camera would blend from wherever the
transform happened to be — usually the world origin — to the body. Pinned, the ghost is already
standing exactly where the player died, so the first frame of the death view is the correct frame.

**The server knows where the ghost is, roughly.** Position goes up as a SyncVar at 10Hz. Nobody
renders from it, so smoothness is irrelevant; what the server needs it for is validation. A shove is a
`ServerRpc` naming a target, and the server checks that the target is within reach of the *reported*
ghost position before it does anything. 10Hz is enough to catch a client claiming to shove a corpse on
the other side of the island, and cheap enough that four ghosts cost less than one moving body. There
is also a 60 m tether back to the corpse — not an anti-cheat measure, just a rule that a spectator who
flies to the far side of the map stops being a participant.

**The shove is an `ObserversRpc`, not a server-side `AddForce`.** Ragdoll bones are not replicated;
each machine simulates the corpse it can see, from the same initial conditions. A force applied only
on the server is therefore invisible everywhere else — the host would watch a leg kick and the client
would watch nothing. Every impulse in this game that has to look the same on four screens takes the
same route (`Health.ObserversIncapacitated`, `Carryable.ObserversThrow`,
`StunState.ObserversApplyImpulse`), and `GhostController.ObserversNudge` joins them. The server owns
the magnitude — the RPC carries a direction and a hit point, and the impulse is scaled server-side —
so a modified client can shove in a stupid direction but not with a stupid force.

**Living players cannot be shoved for free.** Not because anything checks: a standing player's bones
are `isKinematic`, so an impulse into one is discarded by the physics engine. The ghost's cast hits the
same colliders either way, which means the rule needs no code and cannot drift out of sync with the
ragdoll's own kinematic bookkeeping.

**Why 25 Ns.** The skeleton weighs about 56 kg — a 14 kg pelvis and 4 kg limbs — and the shove lands
on whichever bone the cast touched, unlike `Carryable`'s throw, which is 12 Ns and always lands on the
pelvis. One number cannot serve both: a throw-sized impulse into a shin is nothing, and a shin-sized
impulse into a whole body is less. 25 Ns is a fast kick on a limb and about half a metre per second on
the corpse as a whole — enough to start a body rolling on any slope, and on flat ground stopped by
friction inside a couple of centimetres, which is exactly what the word *nudge* should mean. The
"cannot deal damage" rule is not enforced by the magnitude anyway: the shove has no damage path at
all, at any strength.

**Attack means two verbs.** Routing lives in `PlayerCombatInput`, which already owns the mapping from
buttons to combat verbs: with a body, Attack punches; as a ghost, Attack shoves. Letting
`GhostController` poll the input reader itself would have put two consumers on the same buffered
press, and one of them would silently lose it.

`-ghostTest <seconds>` proves both halves in one pass, and the negative half matters more. A dead
client asks the server for a punch, a pickup and an interact — all three must be refused, and they are
refused inside each ServerRpc rather than by hiding the buttons, so an owner-side bypass changes
nothing. Then it shoves its own corpse. Displacement is a bad witness on a flat floor, since friction
stops 56 kg at half a metre per second inside two centimetres, so the test measures imparted *speed*,
peaked across the whole shove window rather than sampled at a fixed offset — the shove is a round trip
and lands one to three frames later:

```
[GhostController] -ghostTest: owner 1 is dead and asked for a punch, a pickup and an interact. interact=False (expected False).
[GhostController] -ghostTest: server verdict carrying=False (expected False), state=Dead. Ghost at (2.25, 0.15, 2.55), nudged=True (expected True). Settling drift over the control window was 0.00m, fastest bone 0.01m/s.
[GhostController] Ghost of owner 1 nudged Player(Clone) with 25.0 Ns at (2.48, 0.07, 3.16) via ragdoll on LowerLeg.L (4kg, kinematic=False, sleeping=False, v=0.00).
[GhostController] -ghostTest: fastest bone peaked at 2.28m/s over the shove window against 0.01m/s at rest, and the skeleton moved 0.01m against a corpse that was already at rest.
```

The control window is the part that makes it evidence: a just-dropped ragdoll is still settling, so
"the body moved after the shove" proves nothing on its own. The test measures an identical window with
no shove in it first, and only then shoves. The nudge line appears in *both* the host and the client
log, which is what proves the RPC round trip rather than a local-only force.

### The Revive Machine

Being downed costs your friends a walk. Being dead costs them money — money that was going to buy the
boat. That is the whole design: letting a bleed-out timer expire produces a bill, and the person who
pays it is standing next to the person who let it happen.

**Only the dead are customers.** `Health.ServerRevive` refuses anything but `LifeState.Dead`, and the
machine works around none of it. A downed player carried here is picked up off the floor for free,
wherever they are, so hauling someone to the machine can never be the *cheaper* option and the machine
can never become the fast path.

**The price is the content.** `_baseCost + _costPerDeath * max(0, Deaths - 1)` — 250 plus 200 for every
previous death this run, read from `Health.Deaths` on the body itself. The friend who keeps dying gets
more expensive, which is precisely the argument the game wants people to have. `Deaths` is a SyncVar
incremented in `Health.SetState` *before* the state is published, so anything reacting to the death
already sees the count that includes it, and the HUD can quote a price without asking the server.

Charging goes through `Wallet.ServerTrySpend`, which is both the check and the charge in one call and
therefore cannot half-bill anyone. `ServerCanInteract` deliberately does *not* consult the wallet: a
broke player gets a refusal with a number in it rather than a button that silently does nothing.

**Swallowing reuses carrying.** The machine implements `ICarryHolder`, so eating a body is literally
`Carryable.ServerAttach(machine)` — the same SyncVar-driven `AttachVisual` that parents the hips to a
carrier's socket and freezes the bones on *every* peer. No second attach path, no replicated animation,
and no reliance on server-side `TeleportSkeleton`, which clients never see. The intake socket then
drags the body into the housing over the cycle, and that motion costs zero bandwidth: it is
`_intakeRest + _intakeTravel * Progress`, where `Progress` is derived from the replicated
`_cycleEndTick` and `_cycleTicks`. Every peer computes the same number from a tick both sides already
agree on.

Detaching goes through the *carrier*, not straight to the `Carryable`: `CarrySystem` tracks what it
holds in its own SyncVar and would otherwise stay convinced it still has a corpse on its shoulder.

**An abandoned body is refused, unpaid.** A corpse whose owner disconnected has nobody to walk out of
the machine, and charging for that would be taking money for nothing. It stays refused until reconnect
adoption lands (#111). Cancellation refunds for the same reason: a body despawning mid-cycle, or the
server stopping, returns the full price to whoever paid it.

**Interact prefers machines over bodies.** `PlayerCombatInput` now holds the short priority list it
always said it would need. The gesture the machine wants is walking up to it holding a corpse and
pressing Interact; if carrying won that key, the press would put the body on the floor instead.
Dropping keeps its own button and the machine takes the body off your shoulder itself, so nothing
becomes unreachable.

**World props are spawned, not placed in the scene.** The machine is the first object that is part of
the *map* rather than part of a player, and it raised a question this project had not answered: FishNet
identifies scene objects by a scene id baked at save time, and every scene here is written by an editor
script running in batchmode — a path where that baking is unproven. Spawning from a registered prefab
is the path a player body already proves works on every connect, so props take it too. `WorldSpawner`
does that on `OnServerConnectionState → Started`, ownerless, from a serialized list of prefab +
position + rotation.

`-machineTest <seconds>` runs the refusal and the sale in one pass, because neither half is convincing
without the other: at T the payer's wallet is emptied and the machine is asked to work (must fail),
three seconds later `-startingMoney`'s balance is restored and it is asked again (must succeed). A
two-process headless run prints the whole loop:

```
[WorldSpawner] Spawned ReviveMachine at (0.00, 0.00, 14.00).
[BodyPersistence] -deathTest: owner 1 killed, state Dead.
[ReviveMachine] Refused: owner 0 has 0 and the cycle costs 250. Death 1 is not free.
[ReviveMachine] -machineTest: broke attempt busy=False (expected False), body state Dead.
[ReviveMachine] Owner 0 paid 250 to revive owner 1 (death 1). 250 left. Cycle runs 4s.
[ReviveMachine] Cycle finished: owner 1 revived=True state=Alive at (0.00, 0.00, 10.60). Paid by owner 0.
```

### Falling out of the world

A game whose central joke is throwing your friends around will drop one out of the world. Three
separate causes did it in the same headless run, and the fix is one of each kind: remove the hole,
make the hole harder to reach, and survive the ones nobody found yet.

**The floor was a `Plane`.** The greybox floor started as Unity's plane primitive: a zero-thickness,
single-sided mesh collider. A ragdoll bone driven into it hard enough is on the far side after one
physics step, and from underneath there is no backface to hit, so there is nothing to land on. It is
now a two-metre-thick box scaled 60 x 60, its top surface at y = 0 where every spawn height already
assumed it was. Thickness is the whole point: nothing in this game moves two metres in one 50Hz step.

**Ragdoll bones were using discrete collision detection.** Discrete detection samples only the end of
a step, so a limb accelerated by a punch is checked after it has already passed through the floor.
While ragdolled, every bone now uses `ContinuousSpeculative` — the cheap variant, and the only
continuous mode a kinematic body is allowed, which matters because the same bones go kinematic when
the body stands back up. Its known weakness is stopping slightly short of a surface, which is
invisible on a limp arm.

**Standing up could plant you inside the ground.** `RepositionRootUnderHips` probed three metres down
from just above the hips; a body that had settled slightly *inside* the floor cast from below the
surface, found nothing, and put the character controller at the raw hip position — under the world,
falling forever. The probe now starts a metre higher, and when it still finds nothing it drops a
second one from 200 metres up, which handles a body that is genuinely under the map.

**And a net under all of it.** `FallGuard` is server-side, checks four times a second, and returns any
body below y = -30 to a spawn point. Plugging holes individually is a losing game — the island alone
will have thousands of metres of coastline, and every one of them is reachable by a friend with a
car. The guard does not prevent falling; it makes falling survivable, and it prints a line whenever
it fires so a fall that *is* a bug still shows up in a log rather than being silently papered over.

Two details make it work on a limp body. A ragdolled player is not where its root transform says it
is — the hips are what the physics engine is moving — so the guard reads the hip bone's height and
teleports the whole skeleton by a single shared offset, keeping every bone's pose and every joint's
configuration. And the upright case goes through `PlayerMotor.ServerTeleport`, which sends nothing:
position is already part of the reconcile state, so the next tick carries the new one as
authoritative and the owner replays into it exactly like any other correction. A teleport is just a
very large misprediction.

`-fallTest <seconds>` throws every body out of the world at that time, because a net nobody has ever
seen catch anything is not a net you can claim works.

---

## World generation

### The greybox arena

M1 does not have an island yet, and it should not wait for one. What it needs is a room with enough
shapes in it to answer one question: *is throwing your friends off things funny?* That room is
`ArenaBuilder`, an editor script, and like everything else in this project it is **built from
constants rather than sculpted** — `ArenaBuilder.BuildArena` opens `Bootstrap.unity`, deletes the old
`Arena` root outright, rebuilds all 29 boxes from numbers, rewires the spawn points and saves. The
whole map is one terminal command, so changing the pit depth is a diff, not an editor session.

The layout is chosen entirely around the shove:

- A **60m plate**, built as *four* slabs rather than one, because the pit needs a hole in it and you
  cannot cut a hole in a Unity primitive. `Floor.South`, `Floor.North`, `Floor.West` and `Floor.East`
  bound an 8m opening.
- An **8m pit, 4m deep**, walled on all four sides so you land in a box, with **a ramp back out**.
  The ramp is not politeness. An inescapable pit is a longer removal from the game than dying is —
  death at least ends at the Revive Machine — and a mechanic that punishes the victim harder than
  killing them would is a mechanic nobody uses twice.
- A **catwalk at 6m** running across the arena and overhanging the pit. This is the arena's best
  shove spot and the reason the pit exists at all: somewhere to carry a stunned friend *to*.
- A **two-stage tower** (platforms at 3m and 6m, ramps between) so the height is reachable on foot,
  and four **blocks** between 1m and 2.5m for cover and for tripping over.
- A **perimeter wall**, 3m, on three sides — so wandering off the edge stops being the joke and
  starts being an accident — with one deliberate **6m gap in the south wall and a plank** running out
  past it. Intentional defenestration stays available, and `FallGuard` stays under test.

Ramps meet what they serve *by overlapping into it*: the tower's ramps end half a metre inside the
platform above them, so the surfaces are coplanar at the seam and there is no step to climb. The pit
ramp is the exception — a hole has no geometry to overlap into — so its top lands exactly on the lip.
Half a metre short, which is what it was first built as, is not a step a `CharacterController` walks
up; it is a half-metre gap straight back into the pit.

Spawn points are four empties on a 6m ring at **y = 1.2**, matching `PlayerSpawner`'s own generated
fallback height rather than inventing a new one, and facing the middle along a **flattened** vector —
the 1.2m of clearance is part of the position, not part of where the player is looking, and folding
it into the facing would pitch the first camera frame into the floor. `ArenaBuilder.WireSpawnPoints`
writes the four transforms into `PlayerSpawner._spawnPoints` through a `SerializedObject`, which is
what makes them real for `FallGuard` too: a rescue reads the same array.

Load time is the arena's acceptance criterion, so `NetworkBootstrap.Start` logs
`Time.realtimeSinceStartup` on its first frame — engine start to scene live. Four headless processes
measure **0.28–0.36s** against a 3-second budget.

The island is **generated from a seed, never hand-sculpted.** An editor script produces the heightmap
from domain-warped noise with an island falloff mask, then derives the splatmap from height and slope
rules (sand near sea level, grass inland, rock on steep slopes). Vegetation is placed by biome mask
using Terrain tree and detail instancing with LOD groups.

This matters for two reasons: the same seed reproduces the island byte-for-byte, and regenerating it
is a single terminal command. No manual editor work sits between an idea and a testable world.

Island 1 is 1024×1024 world units (~1 km²). Island 2 is 512×512 — smaller, denser, meaner.

Points of interest are placed by a `POISpawner` reading a config list. Adding a landmark is a data
edit, not an editor session. `WorldSpawner` is that spawner's honest first draft: the shop, the
casino, the native village and the wreck are all "a prefab at a position", and the only thing that
changes on the way to M2 is where the list comes from.

---

## Data-driven content

**Every piece of content that is not geometry is a ScriptableObject.**

`WeaponDef`, `ItemDef`, `UpgradeDef`, `BuffDef`, `FishDef`, recipes, POI entries, shop inventories.
These serialise as YAML text, which means they are authorable from a terminal, reviewable in a diff,
and mergeable in git.

A new weapon is one `.asset` file plus a prefab. A new fish is one `.asset` file. This is the single
decision that lets content scale without the editor becoming a bottleneck.

---

## Vehicles

| Vehicle | Approach |
|---|---|
| Car | `WheelCollider` with deliberately arcade tuning — high grip, soft suspension, exaggerated mass. Easy to flip in a funny way |
| Boat | Custom buoyancy: 4–8 sample points applying Archimedes force plus drag against the water plane |
| Plane | Arcade flight model. Lift as a function of speed and angle of attack, forgiving stall. Learnable in minutes, landable with difficulty |

All vehicles share one framework: seat definitions, enter/exit interaction, passenger parenting, and
driver ownership transfer validated by the host. Vehicle-versus-ragdoll collisions launch players with
force proportional to impact speed — this is a required feature, not a side effect.

---

## Casino

The roulette wheel does not decide anything. The **host** rolls the result server-side and broadcasts
it; every client then animates the wheel to land on the number that was already chosen. There is no
code path where a client can influence an outcome.

Chips exist only inside the casino and are bought with in-game currency. **There is no path for real
money to enter or leave.** That is what keeps this legally not-gambling and keeps the app compliant —
Steam bans real-money gambling and paid loot boxes outright. The store page must still declare
gambling themes, alcohol references, and violence accurately.

The alcohol NPC trades drinks for buffs applied through the same `BuffDef` system as food and medical
items. The cost is a URP Volume override — depth of field, chromatic aberration, camera noise — that
genuinely impairs vision. The buff has to be tempting and the handicap has to be real, or the
mechanic is decoration.

---

## Performance

**Min spec is a Radeon 760M iGPU at 1080p60.** It is the development machine's integrated GPU, so it
is testable continuously rather than at the end.

Budget: URP forward rendering, SSAO off at Low, shadow distance ~80m with 2 cascades, aggressive LOD
groups, occlusion culling, distance fog to cut draw distance, baked lighting wherever it is possible.
The physics cost of 4 simultaneous ragdolls plus vehicles is the worst case to profile against.

Profile at every milestone, not at M8. Perf problems found late are architecture problems.

---

## Agent-assisted development

Built with Claude Code driving the terminal, via two MCP servers:

- **MCP for Unity** — editor control, scene and asset operations, console reading, test runs.
  **Requires the Unity Editor to be open**; there is no headless iteration loop beyond batchmode compiles.
- **blender-mcp** — low-poly modelling and CC0 assets from Poly Haven.

This is also why the engine choice went to Unity. Unity serialises scenes, prefabs, and
ScriptableObjects as YAML and its scripts are plain `.cs` files, so an agent can read, write, and diff
essentially the whole project from a terminal. UE5's Blueprints and `.uasset` files are binary, which
would have pushed the majority of the work back into manual editor sessions.

What the agent cannot do: playtesting, tuning game feel, and final art direction. Those stay human,
and M1 exists specifically to test the part no amount of code can decide.
