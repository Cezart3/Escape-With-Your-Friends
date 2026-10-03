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
`-latency`, `-botMove`, `-motorLog`, `-clockLog`, `-navWalk`, `-quality`, `-perfLog`, `-invTest`,
`-itemTest`, `-statTest`, `-statLog`, `-buffTest`, `-craftTest`, `-chestTest`, `-uiTest`,
`-moneyTest`, `-shopTest`, `-weaponTest`, `-weaponLog`, `-meleeTest`, `-meleeLog` and `-gunTest`
described under movement, the day/night cycle, navigation, performance, the inventory, loot,
survival, consumables, crafting, storage, the bag screen, money, the shop, weapons, melee and
firearms below:

```
Unity.exe -quit -batchmode -nographics -projectPath .   -executeMethod EscapeWithYourFriends.EditorTools.BuildTool.PerformBuild   -buildOutput D:/Builds/EWYF-dev -development -scriptingBackend mono

EscapeWithYourFriends.exe -batchmode -nographics -host   -port 7770 -playerKey test:host -quitAfter 18 -logfile host.log
EscapeWithYourFriends.exe -batchmode -nographics -client -address 127.0.0.1 -port 7770 -playerKey test:c1 -logfile c1.log

# the movement test: four bots walking a lap through a simulated 100ms round trip
EscapeWithYourFriends.exe -batchmode -nographics -host -port 7770     -latency 50 -botMove -motorLog -quitAfter 45 -logfile host.log
```

**`-playerKey` is not optional in a local smoke test.** Two processes on one machine with no Steam
running fall back to the same stored `PlayerPrefs` key, and the authenticator correctly rejects the
second one — "key already held by connection 0" — so the host spawns one body and the run looks like
a networking failure that is really the persistence system working. Give each process its own key.

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

### Hearing each other

Voice is not a feature of this game, it is the delivery mechanism. Almost every laugh in it is
somebody reacting out loud to a ragdoll, so `VoiceChat` sits on the player prefab next to the
combat components rather than in a UI menu somewhere.

It is **proximity voice, not a party channel**. Hearing a friend get quieter as they are dragged
away is the joke; a channel that follows everyone everywhere would delete it.

```
owner:    SteamUser.ReadVoiceDataBytes  ->  ServerRelay      (unreliable)
server:   distance test, per listener   ->  TargetPlay       (unreliable)
listener: DecompressVoice               ->  ring buffer  ->  streaming AudioClip
```

Steam captures and compresses; the server decides who is close enough to hear it; Unity plays the
result out of an `AudioSource` parented to the speaker's body, at head height, with linear rolloff
from `_fullVolumeRange` to `_maxRange` — so distance falloff costs nothing and is automatically
consistent with where the body actually is, ragdolled or not.

**The server does the range test, not the listener.** Sending every frame to everyone and letting
clients turn the volume down would be less code and it is what a lot of games do. It also ships
every word anyone says to every machine in the lobby: a bandwidth bill that grows with the square of
the player count, and a free wallhack for anyone willing to read their own packets. The listener
never receives a voice frame from someone it is not allowed to hear.

**Unreliable, both directions, always.** A voice frame that arrives late is worse than one that never
arrives — reliable delivery would stall the stream behind a retransmit and then dump the backlog all
at once. A dropped frame is a click; a stalled frame is a robot.

**A dead player speaks from their corpse.** The ghost is deliberately a purely local object: it is
never spawned, so the server does not know where it is and could not range-test against it without
replicating a ghost position that exists for no other reason. The corpse is the better rule anyway.
Death costs you the room: you are heard where your body is, muffled, and drifting off to haunt
somebody across the map means nobody can hear you at all. Downed and dead are filtered rather than
cut — volume 0.75 and a 4kHz low-pass face down on the floor, 0.5 and 700Hz once dead, because being
able to hear the person you are dragging is most of the reason to drag them.

**Open mic, no push-to-talk.** Push-to-talk protects against a reaction reaching the group late,
which is precisely the thing this game is made of. A mute toggle belongs to the settings menu (#84).

Two implementation details worth keeping. Every frame carries a one-byte codec tag, `Steam` or
`RawPcm`; the second exists only so the headless test can push real audio through the real path on a
machine with no microphone and no Steam. And playback writes into a two-second float ring that Unity
drains from a streaming `AudioClip` on the audio thread, so a late frame is silence instead of a
stall and no clip is ever allocated per utterance. An overrun drops the oldest samples: a listener
two seconds behind wants the present, not the past.

Steam is optional everywhere else in this project and it is optional here. With no Steam there is no
capture and no relay, and nothing else in the game changes.

#### Proving it without four microphones

`-voiceTest <seconds>` and `-voiceRange <metres>`.

A headless build has no microphone and usually no Steam, so capture and the codec are the two things
an automated run genuinely cannot exercise. Everything after them can. `-voiceTest` synthesises a
440Hz tone and feeds it into the same `SendFrame` the microphone uses, tagged `RawPcm` so the
listener skips Steam's decoder and nothing else: framing, the unreliable relay, the server range
test, the ring buffer and the muffling all run exactly as they do in a real game. It never claims
anything was *audible* — under `-nographics` the audio thread may never pull a sample — it reports
what the network moved and what reached the buffer.

`-voiceRange` exists because the greybox spawn ring puts four players 6m from the middle at 90° from
each other: neighbours land 8.49m apart and opposites 12m, so a range of 10 straddles the ring and
nobody has to walk. Four processes, all speaking, host killing ALPHA at t=20 by key:

```
host    (0,0,6)  heard owner 1, heard owner 3          ... never owner 2
ALPHA   (6,0,0)  heard owner 0, heard owner 2          ... never owner 3
BRAVO   (0,0,-6) heard owner 1, heard owner 3          ... never owner 0
CHARLIE (-6,0,0) heard owner 0, heard owner 2          ... never owner 1

[VoiceChat] -voiceTest: server relayed owner 0 446 time(s), skipped 223 listener-frame(s) beyond 10m.
[VoiceChat] -voiceTest: heard owner 1: 270 frames, 121230 samples, speaker Dead, volume 0.50, cutoff 700Hz.
```

Every peer hears exactly its two neighbours and never the one across the ring, the server skipped
one listener-frame for every one it sent, and ALPHA kept talking after dying — from the floor, at
half volume, through a 700Hz filter.

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

### The island, from a seed

That is now code, not a plan. Three files do it:

| File | Role |
|---|---|
| `Scripts/World/IslandProfile.cs` | Every number, as a ScriptableObject. The island is `Assets/_Project/Data/Island.asset`, a YAML file you can `sed` |
| `Scripts/World/IslandShape.cs` | The shape as a pure function: `HeightAt(x, z)` in metres above sea level |
| `Scripts/Editor/TerrainGenerator.cs` | Bakes that function onto a grid, writes `IslandTerrain.asset` and `Island.unity` |

One command rebuilds the world:

```
Unity.exe -quit -batchmode -nographics -projectPath . \
  -executeMethod EscapeWithYourFriends.EditorTools.TerrainGenerator.GenerateIsland \
  -islandSeed 20260830 -logFile island.log
```

`-islandSeed`, `-islandSize` and `-islandRes` override the asset and are written back into it, so the
profile always describes the island that was actually baked. A missing profile is created with
defaults, so a fresh clone needs no editor step.

**The noise is hand-written, and that is the whole point.** Unity documents `Mathf.PerlinNoise` as an
unspecified implementation that may change between versions. The acceptance criterion for this issue
is that a seed reproduces the island byte for byte, and "byte for byte until we upgrade the editor"
does not meet it. `IslandShape` is integer hashing (FNV-1a with an avalanche finish), eight fixed
gradient directions, and float lerps — no trigonometry, no library calls, nothing that can drift.

The shape is five layers, in order:

1. **Domain warp** — two noise fields drag the sampling position up to 110m sideways. Without this
   every later layer is visibly radial and the hills read as blobs.
2. **fBm relief** — six octaves at a 380m base feature size, shifted by a *water line* of 0.40 before
   being scaled to 46m. Moving that line up or down is how much of the island is dry.
3. **Coast mask** — a radial falloff whose radius is itself noisy, which is what produces bays and
   headlands instead of a circle.
4. **Mountain** — a dome at 34% of the half-size, carved by ridged noise so it reads as rock rather
   than as a scoop of ice cream. It peaks at ~116m and is the thing you navigate by.
5. **Beach flattening** — anything within 5m of sea level has its slope cut to 30%, which turns the
   shoreline into sand you can drag a boat onto instead of a wall you swim along.

Geometry: **1024 x 1024m** at **1025 heightmap samples** — one sample per metre. Sea level is
**y = 0**, fixed, so buoyancy and the water plane never need a lookup; the terrain object sits at
`(-512, -40, -512)` and its 200m of vertical range covers 40m of seabed plus 160m of headroom.

One tuning lesson worth keeping: the seabed pull must be **squared**, not linear. With
`height = hills * mask - (1 - mask) * 40`, forty metres of sea beats five metres of hill everywhere
except the last tenth of the falloff, so the first island came out at 16% land — a small blob in a
big square of water. `(1 - mask)²` moves the shoreline back out to where the mask actually fades and
leaves a shallow shelf to swim in over. Same seed, same everything else: **23.5% land, 24.6
hectares**, roughly 560m across.

#### Proving determinism without opening the editor

The generator logs an FNV-1a hash of the heightmap and a second one of the saved asset bytes — the
first proves the maths repeats, the second proves the serialisation does. Four runs:

```
seed 20260830   heightmap 0B19930F   asset C3E49260   md5 8d085281...
seed 20260830   heightmap 0B19930F   asset C3E49260
seed 20260830   heightmap 0B19930F   asset C3E49260
seed 7          heightmap E24368CA   asset CE732FF3   md5 98297105...   (20.8% land)
seed 20260830   heightmap 0B19930F   asset C3E49260   md5 8d085281...
```

The last line is the one that matters: going away to another seed and coming back reproduces the
file exactly, so the output is a function of the seed and not merely of an idempotent write.

Every run also prints land fraction, beach fraction, mean land height, peak height, and a 72x30 ASCII
map, so a parameter change that quietly drowns the island is visible in the log:

```
~~~~~~~~~~~~~~~~~~~.:---+++++++++++---------------::..~~~~~~~~~~~~~~~~~~
~~~~~~~~~~~~~~~~~.:--+++++++^+++++++---------------:::..~~~~~~~~~~~~~~~~
~~~~~~~~~~~~~~~~.:--++++^^^^^^^^+++++-------------:::::..~~~~~~~~~~~~~~~
~~~~~~~~~~~~~~~.:--+++++^^^^^^+++++++----------::::::::..~~~~~~~~~~~~~~~
~~~~~~~~~~~~~~..:-----++++++++++----------:::::::::::::..~~~~~~~~~~~~~~~
```

Sampling 1025² points takes ~1.5s, so regenerating is cheap enough to iterate on parameters from the
terminal.

`Island.unity` holds the terrain and a sun and nothing else, and is deliberately **not** in build
settings: it has nothing networked in it yet, and adding it would slow every headless test down for
no gain. Loading it as the game world is #39. `IslandTerrain.asset` is tracked with Git LFS —
`TerrainData` serialises as binary whatever the project setting says, and it is 2MB that a
regeneration rewrites whole.

### Painting it: sand, grass, rock, dirt

The heightmap alone renders as one flat colour, which makes a 24-hectare island impossible to read.
The same generator run now also paints it, from the same seed, with no manual brushwork anywhere:

| File | Role |
|---|---|
| `Scripts/World/IslandSplat.cs` | The cover rules as a pure function: height + slope + position -> four weights |
| `Scripts/Editor/TerrainGenerator.cs` | Samples that over the alphamap grid, generates the layer assets, writes them |

The rules, in the order they are resolved — each one takes what is left of the previous:

1. **Rock** wins first, on `max(steep, high)`: slope past **0.7** or height past **78m**, both with a
   soft edge. Cliffs and the mountain cap.
2. **Sand** takes what is left below **3.5m**, fading out over 3m. That band is exactly the beach
   flattening from layer 5 of the shape, so the sand ends where the walkable shore ends.
3. **Dirt** is a separate 3-octave noise field at a 90m feature size, thresholded at 0.58 — patches,
   not a gradient — and it only eats into what is not already rock or sand.
4. **Grass** is the remainder. It is the default, which is why it never needs a rule of its own.

Then everything is normalised to sum to 1, and a row that somehow sums to zero is forced to pure
grass: Unity renders an all-zero alphamap cell as a black hole, which looks like a bug in the shader
rather than a bug in the weights.

**Slope is a gradient, never an angle.** `SampleSplat` takes central differences on `HeightAt` at
±1m and keeps `sqrt(gx² + gz²)` — rise over run. Converting to degrees would put `atan` in the
deterministic path, and the whole point of hand-rolled noise ([above](#the-island-from-a-seed)) is
that nothing in that path can drift between machines or editor versions. `0.7` is a 35° slope; you
tune it by looking at the island, not by doing trigonometry.

Two details worth keeping:

- The slope comes from the **shape function**, not from the baked heightmap. Sampling the bake would
  make the painting inherit its stair-stepping, and cliff edges would come out jagged in a way no
  texture can hide.
- A little noise is added to the height and slope *before* the thresholds, not to the result. That
  breaks the contour lines you otherwise get where sand meets grass at exactly 3.5m all the way
  round the island.

Geometry: a **512² alphamap** over 1024m — one texel per 2m, a quarter of the heightmap's density,
which is as fine as four blended layers can usefully be. `alphamapResolution` is a plain power of
two (unlike the heightmap's 2^n+1) and, like the heightmap, has to be set **before** the map is
written, because changing it throws the existing maps away.

#### The placeholder ground

Four 256² tileable PNGs in `Art/Terrain/`, generated by the same noise the island is: two octaves,
cross-faded against a wrapped copy of themselves so the tile has no seam. Sand is pale, grass is
green, rock is grey, dirt is brown — placeholder ground, but placeholder ground you can read a slope
off, which greybox grey cannot do.

They are written **only when absent**, and the `.terrainlayer` assets are reused rather than
recreated. Two consequences, both deliberate: the terrain keeps pointing at the same GUIDs across a
regeneration, and an art pass that drops a real texture over `Grass.png` is not undone the next time
somebody rerolls the seed.

#### What it produced

The generator hashes the alphamap the same way it hashes the heightmap, and reports the dominant
layer per cell **over dry land only** — the first report said 84% sand, because three quarters of
the square is seabed and the seabed is all sand. Not useful. Over land:

```
seed 20260830   heightmap 0B19930F   splat 6A9D0D17   asset 55A2B98A   md5 349be580...
                Sand 32.8%   Grass 44.5%   Rock 11.4%   Dirt 11.4%
seed 7          heightmap E24368CA   splat 2C61AA86   asset 58D7C08C   md5 c143ebca...
                Sand 26.3%   Grass 50.3%   Rock  7.2%   Dirt 16.2%
seed 20260830   -> md5 349be580..., and zero textures regenerated
```

Grass-dominant with a real beach ring and rock on the mountain and the cliffs, which is the shape
the log's ASCII map already claimed. Painting 512² cells costs ~2s on top of the ~1.5s of height
sampling, so a full regeneration is still under ten seconds including the asset write.

A read-back check closes the loop: the generator reloads the saved asset and logs what Unity
actually stored — `Heightmap 1025^2, alphamap 512^2, 4 layers` — because `SetAlphamaps` silently
does nothing if the resolution was set in the wrong order, and a green exit code would not say so.

One wart, stated rather than hidden: the byte-for-byte guarantee covers `IslandTerrain.asset`, not
`Island.unity`. The scene is rebuilt from nothing on every run and Unity hands out fresh local
fileIDs each time, so a regeneration with an unchanged seed still shows a diff there — the same sun
and the same terrain under different numbers. Harmless, and #39 replaces that scene with the real
game world anyway, so it is not worth pinning the IDs down for.

### Planting it: trees, bushes and grass

The island was bare after #31 — correct ground, correct paint, nothing growing on it. #32 puts the
vegetation on, under one hard constraint: it has to look like a jungle on a laptop with a Radeon
760M in it.

The whole design follows from that constraint. **Trees go in as terrain tree instances, not as
scene objects.** Fourteen thousand `GameObject`s would be fourteen thousand transforms in
`Island.unity`, culled one by one on the CPU; fourteen thousand `TreeInstance`s are a flat array
inside the terrain asset, culled, LOD-ed, batched and billboarded by the terrain system itself. The
scene file stays four kilobytes. Grass is a **detail layer** for the same reason: the terrain draws
it in patches and drops whole patches at a distance, so the cost is one number in the profile.

Three new pieces:

| File | Job |
|---|---|
| `Scripts/World/IslandFlora.cs` | Where the plants go. Pure function of the seed, no Unity types beyond `Vector3`. |
| `Scripts/Editor/FloraFactory.cs` | What the plants look like. Generates meshes, materials, LOD prefabs and the grass texture. |
| `TerrainGenerator.WriteFlora` / `WriteDetail` | Bakes both into the terrain asset. |

#### Where the plants go

A jittered grid, not a random spray. Uniform random scatter clumps and gaps for free — that is
what uniform random *is* — and the gaps read as bald patches you cannot tune away by adding more
trees. One candidate per grid cell, offset inside its own cell by the same hash that decides
whether it lives, gives even coverage with no visible lattice, at one hash per cell instead of a
rejection loop.

Two passes: canopy on a 4 m grid, undergrowth on a 2.4 m grid. Bushes are not competing with trees
for the same slot, so the ground under a canopy is not bare.

Each candidate asks the same two functions the splatmap asks — `IslandShape.HeightAt` and
`IslandShape.SlopeAt` — and then the splat weights themselves, so the forest agrees with the paint
instead of contradicting it. Suitability per species is a height band × a slope ceiling × a demand
on the cover already painted there:

| Species | Height band | Slope ceiling | Wants |
|---|---|---|---|
| Palm | 0.6–7 m | 0.45 | sand, a little grass |
| Jungle tree | 2.5–58 m | 0.62 | grass, some dirt |
| Highland tree | 28–110 m | 0.70 | grass, dirt, a little rock |
| Bush | 1–92 m | 0.75 | grass, dirt, a little sand |

Slope is a gradient, never degrees — the same rule as everywhere else in the deterministic path, so
no `atan` gets in. Species are tried in order and the roll is spent as it goes, which makes the
ordering a priority: a palm-suitable cell down by the water is a palm before it is anything else.

Clumping is then put back deliberately, per species, as a low-frequency grove mask, so palms bunch
along one stretch of beach and not another and the highland pines thin out before the rock instead
of stopping dead on a contour line.

**The grove span was the trap, and it cost two retunes.** The obvious thing to write is
`(noise - floor) / (1 - floor)` — remap everything above the floor into 0..1. But averaged gradient
noise piles up around 0.5 and almost never reaches the ends, so that expression multiplies the
*entire island* by about a quarter. It does not read as clumping, it reads as a thin forest with no
clearings anywhere: the first run produced 5192 plants and 894 trees over a square kilometre. The
fix is an explicit narrow `GroveSpan` (0.14) as the denominator, which puts the noise's own middle
at full density and lets only the genuine dips open up into clearings.

A second, duller lesson came with it: **changing a default in `IslandProfile.cs` does nothing to an
already-serialised profile.** `Island.asset` keeps whatever it stored; only genuinely new fields
pick up their defaults. Both retunes had to edit the YAML asset in lockstep with the C#.

#### What the plants look like

`FloraFactory` generates four prefabs and six materials from code, same as the ground textures and
for the same reason — nothing sculpted by hand, everything reproducible from the terminal. A fixed
salt separate from the island seed drives the shapes, so rerolling the island does not reroll what
a palm tree looks like.

Each prefab is an `LODGroup` over two generated meshes with two submeshes each (bark, foliage).
LOD1 also stops casting and receiving shadows, which is most of what it saves. Bushes get no
collider; trees get a capsule.

```
Palm          178 tris near / 36 far
JungleTree    172 / 80
HighlandTree   74 / 28
Bush           96 / 40
```

Grass is a 64² RGBA texture of seven parabolic blades, drawn as a `GrassBillboard` detail — no mesh
per tuft. Density follows the grass weight of the splatmap, ramped linearly from `GrassThreshold`
up, so the edge of a grass patch thins out instead of ending on a line you can see from across the
bay.

Two Unity details worth writing down, both of which fail silently:

- **`TreeInstance` defaults to all zeroes**, so `color` and `lightmapColor` have to be forced to
  white or every tree renders black.
- **Detail layers are indexed `[z, x]`**, like alphamaps. This was *verified*, not assumed: a
  throwaway editor script called `GetDetailLayer(0, 0, 4, 2, 0)` on an in-memory `TerrainData` and
  printed `GetLength(0)=2 GetLength(1)=4`. A transposed grass map mirrors the island diagonally and
  looks almost right, which is the worst kind of wrong.

Tree positions are also snapped to the baked heightmap (`SetTreeInstances(…, true)`) rather than
trusted at the sampled height: the shape is continuous but the terrain is bilinear between samples,
and a tree standing on the maths instead of on the mesh floats over every hollow.

#### The performance knobs

All six live in `IslandProfile` and are applied to the `Terrain` component by `WriteScene`, so
tuning min-spec is editing one asset, not hunting through code:

| Knob | Value | What it buys |
|---|---|---|
| `TreeDistance` | 320 m | Trees stop drawing entirely. |
| `TreeBillboardDistance` | 90 m | Mesh → billboard. The band from here to 320 m is nearly free. |
| `TreeCrossFade` | 25 m | Fade across that switch. Zero pops visibly. |
| `TreeMaximumFullLOD` | 60 | Hard cap on full-mesh trees. Bounds the worst frame. |
| `DetailDistance` | 85 m | Grass draw distance. The single biggest cost on a weak GPU. |
| `DetailDensity` | 0.8 | Global grass multiplier. The min-spec escape hatch. |

`terrain.drawInstanced` is deliberately left alone — it is on the way out in Unity 6 and the
terrain instances trees by itself.

#### What it produced

```
seed 20260830   heightmap 0B19930F   splat 6A9D0D17   placement C929620F   asset DCAC426A
                14236 plants: Palm 1608   JungleTree 2399   HighlandTree 232   Bush 9997
                Grass on 29079 of 262144 detail cells (11.1%), up to 6 per cell
seed 20260830   -> placement C929620F, asset DCAC426A, md5 11fbc769..., 0 assets regenerated
seed 7          splat 2C61AA86   placement CF3B98FA   asset 8CCB6F99
                13732 plants: Palm 1111   JungleTree 3459   HighlandTree 7   Bush 9155
```

~172 trees per hectare, and the two seeds disagree in the way they should: seed 7 has more inland
jungle, almost no highland (its mountain barely clears the band) and a shorter beach ring.

The refactor that pulled the inlined central-difference out of `TerrainGenerator.SampleSplat` and
into `IslandShape.SlopeAt` — so the splat, the flora and everything after it ask the same function
— is proven bit-identical by the splat hash staying `6A9D0D17` across it.

**What is not verified here:** the acceptance criterion says "dense-looking jungle that still hits
60fps on the min-spec iGPU", and a frame rate cannot be measured in batchmode with `-nographics`.
What is measured is the budget the frame rate comes out of: instance counts, per-species triangle
counts, and the cap of 60 × 178 = 10680 triangles of full-LOD tree in the worst case. The frame
rate itself needs the Radeon 760M and a human looking at the screen.

### The sea

The acceptance criterion for #33 is two words long — "looks decent, costs almost nothing on the GPU"
— and the entire design is the second half of that sentence arguing with the first.

**The decision that shaped everything else: no camera depth texture.** Every water shader tutorial
starts by sampling `_CameraDepthTexture` to find how far the seabed is behind the surface, and then
fades the alpha and draws foam with it. In URP that requires `m_RequireDepthTexture` on the pipeline
asset, which adds a depth prepass to *every frame of the game*, not just the frames with water in
them. All three quality tiers here have it off, and the min-spec target is a Radeon 760M.

So the depth is baked instead. `WaterFactory.BakeDepthMask` evaluates the same `IslandShape` the
terrain is built from over a 512² grid and writes how deep the water is at each point, normalised
against `ShoreFadeDepth`, into a single-channel PNG. The shader samples it with world-space UVs over
the island square: one texture fetch, no prepass, and the surf line follows the coast exactly,
because it *is* the coast.

The honest cost of that choice: **nothing dynamic gets intersection foam.** A boat hull, a swimming
player and a thrown corpse all pass through the surface without a ring of white around them. If that
turns out to matter more than the frame rate does, the mask can stay and a depth sample can be added
on the High tier only.

#### Five files

| File | Job |
|---|---|
| `Scripts/World/WaterWaves.cs` | The waves, as numbers. One definition, read by both the shader and the physics. |
| `Scripts/World/WaterSurface.cs` | The runtime component: the clock, the camera follow, and `HeightAt` for buoyancy. |
| `Art/Water/Water.shader` | Hand-written URP ShaderLab. Vertex waves, baked-depth fade, foam, ripples, fresnel. |
| `Scripts/Editor/WaterFactory.cs` | Bakes the mask, generates the ripple normals, the material, both meshes and the prefab. |
| `TerrainGenerator.WriteScene` | Drops the prefab into `Island.unity`. |

#### Two meshes, one material

The ocean is a **near patch** of 4 m quads, 320 m in each direction from the camera, and a **far
ring** — a square annulus of eight vertices — out to 4 km. The patch carries the vertex waves; the
ring is flat. Waving the whole ocean would mean either quads so large the waves alias into a
strobing zigzag, or a mesh no integrated GPU wants to see; and past a few hundred metres a wave is
smaller than a pixel.

That split creates exactly one problem, which is the seam. The shader solves it by fading the wave
amplitude to zero between 250 m and 320 m **in object space**:

```hlsl
float edge = max(abs(IN.positionOS.x), abs(IN.positionOS.z));
float fade = 1.0 - smoothstep(_PatchFade.x, _PatchFade.y, edge);
positionWS.y += height * fade;
```

Object space for the fade, world space for the wave phase. That combination is the whole trick. The
patch root snaps to whole cells as it follows the camera, so the wave crests stay locked to the
world and do not slide when the mesh moves — a half-cell slide makes every crest shimmer — while the
fade stays locked to the mesh, so the amplitude is always exactly zero where the geometry ends,
whatever the camera is doing. No crack, no z-fighting, no popping.

Three things have to agree for that to hold, and `WaterFactory.Verify` checks all three on every
generation, because none of them throws an error — they show up as a crease in the ocean, a strip of
missing sea, or waves that strobe, and all three are invisible in a batchmode build:

1. the shader's fade must finish before the patch geometry runs out;
2. the ring's inner edge must be exactly where the patch's outer edge is;
3. the shortest wavelength must span at least four cells.

Number two is a real trap: the patch extent is snapped **up to a whole number of cells**
(`PatchExtent`), because 320 m of 3 m cells is 106.67 cells and a patch that stops two thirds of the
way through a cell leaves a one-metre strip of nothing between itself and the horizon.

#### The waves are shared, not duplicated

`WaterWaves` holds three directional sine waves — direction, amplitude, wavelength, speed — and
nothing else. `WaterFactory` packs them into the material at generation time; `WaterSurface.HeightAt`
evaluates the same sum in C#. A boat that floats on a different set of waves than the one being drawn
is the defining bug of every hand-rolled water system, and the only defence is that there is one
definition and both consumers read it.

```
Wave   direction        amplitude   wavelength   speed
A      (0.86,  0.51)     0.34 m       41 m       3.1 m/s
B      (-0.42, 0.91)     0.19 m       23 m       2.4 m/s
C      (0.60, -0.80)     0.09 m       17 m       1.7 m/s
```

Peak-to-trough swell 1.24 m, and 17 m over 4 m cells is 4.3 cells per wave — just above the aliasing
floor the verifier checks for.

**Not Gerstner.** Gerstner waves displace vertices horizontally as well as vertically, which is what
gives real water its sharp crests, but it also means "how high is the water at (x, z)" stops being a
function and becomes a fixed-point solve. Buoyancy in M5 needs that question answered a few hundred
times a frame. Vertical-only sine waves keep it a single evaluation, and the sharpness is a normal
map's job.

The clock is deliberately not `Time.time`:

```csharp
public static float Clock { get; set; }
public static bool ExternalClock { get; set; }
```

`WaterSurface` pushes it to the GPU as a **global** shader float, `_WaterTime`, every frame, so the
surface being drawn and the surface being floated on are the same instant. When the boats arrive, a
FishNet tick can drive `Clock` on every machine and four players will see one ocean instead of four
that agree on average.

#### What the fragment shader actually costs

One fetch of the depth mask, two of the ripple normal map, and arithmetic. No refraction, no
reflection probe, no shadow sampling, no screen-space anything. The colour is a shallow-to-deep
lerp driven by the mask, tinted toward a horizon colour by fresnel, with a Blinn-Phong glint from
the main light and `SampleSH` for ambient. Foam is the mask's shallow band torn up by two crossing
sines so the edge is lacy and moving instead of a contour line.

The ripples are the standard two-samples-at-different-scales trick — the same tile at 1× and 0.47×,
scrolling in different directions — which is the cheapest thing that stops a tiling normal map from
reading as a tiling normal map. The tile itself is generated from the same wrapped noise the ground
textures use, with the derivatives taken wrapped as well: a normal map that tiles in value but not
in slope shows a hard line every few metres in exactly the lighting you built it for.

`Blend SrcAlpha OneMinusSrcAlpha`, `ZWrite Off`, shadow casting off and `receiveShadows` false on
both renderers. Alpha runs from 0.45 at the waterline to 0.96 out deep, so the sand shows through in
the shallows — which, without a single refraction sample, is most of what sells it as water.

#### Where the numbers live

Seven fields on `IslandProfile`, so tuning the sea is editing one text asset:

| Field | Value | What it does |
|---|---|---|
| `WaterPatchExtent` | 320 m | Half-width of the wavy patch. |
| `WaterCellSize` | 4 m | Quad size. Also the camera snap step, and the lower bound on wavelength. |
| `WaterFadeBand` | 70 m | How far in from the edge the waves start flattening. |
| `WaterHorizon` | 4000 m | Half-width of the flat ring. Eight vertices, so it may as well be large. |
| `WaterDepthResolution` | 512 | Depth mask side. One texel every two metres. |
| `ShoreFadeDepth` | 14 m | How deep counts as fully open ocean. |
| `FoamWidth` | 1.6 m | Depth over which the surf is drawn. |

#### What it produced

```
Baked WaterDepth.png at 512x512: 76.5% of the square is under water, full depth at 14m.
Patch 320m half-extent, 25921 verts, 51200 tris; waves fade from 250m to 320m; ring 320m to 4000m.
Shortest wave 17m = 4.3 cells; peak-to-trough swell 1.24m.
```

Four consecutive generations at seed 20260830: heightmap `0B19930F`, splat `6A9D0D17`, placement
`C929620F`, asset `DCAC426A` — all unchanged by the addition — and `WaterDepth.png` md5
`2a298130…` identical every time. After the first run the meshes and the normal map are reused; only
the mask and the prefab are rewritten, both byte-identical.

The shader is checked, not assumed: `WaterFactory` calls `ShaderUtil.ShaderHasError` after
`Shader.Find` and logs every message, because a shader with a compile error still loads, still
assigns to a material, and only turns magenta on a screen that nobody is looking at during a
batchmode run. Zero errors across all four runs.

**What is not verified here:** whether it looks decent. Nothing in `-nographics` renders a pixel, so
the colours, the foam width, the glint and the swell height are all first drafts waiting on a human
with a screen. What *is* verified is the geometry, the seam, the determinism, the compile and the
budget: 51 200 triangles of water, one mask fetch and two normal fetches per pixel, no depth prepass
and no extra render pass anywhere in the frame.

### Day and night

The acceptance criterion for #34 is "full cycle configurable in minutes, synced across clients", and
the second half of that is where all the interesting design is.

**Nothing about the sky is replicated.** The obvious implementation is a float on the host pushed out
to everyone, and it is wrong in both of its variants: send it every tick and it costs a message a
tick forever, send it occasionally and the clients visibly jump when it lands. FishNet already
synchronises `TimeManager.Tick` — that is its whole job, and it does it for late joiners too — so the
time of day is simply a function of the tick:

```csharp
public static float Elapsed
{
    get
    {
        TimeManager time = InstanceFinder.TimeManager;
        if (time != null) return (float)(time.Tick * time.TickDelta);
        return Time.time;
    }
}
```

Zero bytes on the wire, no drift, and a player who joins an hour in sees the same sunset as everyone
else because they are reading the same counter. With no network manager at all — the editor, a
batchmode run, a future single-player mode — it falls back to `Time.time` and the sky still moves.

`-timeOfDay 0.5` freezes the clock at noon, which is the only way to test a lighting change in a
build that has no screen: without it the run is over before the sun has moved.

#### Four files

| File | Job |
|---|---|
| `Scripts/World/WorldClock.cs` | What time it is. Static, tick-derived, no networking code of its own. |
| `Scripts/World/DayNightProfile.cs` | Every colour and every number, as a ScriptableObject. |
| `Scripts/World/DayNightCycle.cs` | Applies the profile to the light, the ambient, the fog and the sky. |
| `Scripts/Editor/SkyFactory.cs` | Generates the profile and the skybox material, and reports what they do. |

#### One light that turns around

There is a single directional light in the scene. While the sun is up it is the sun; after that it
rotates 180° to come from where the moon would be, takes a cold blue, and drops to a tenth of the
intensity. Two lights would mean URP choosing a main light every frame and shadow cascades handing
over between them mid-sunset; one light that turns around is the standard trick and costs nothing.

**The handover is not at the horizon crossing, and that matters.** Swapping when the sun's elevation
passes zero is the obvious rule and it swings every shadow in the world through 180° in a single
frame, at sunrise, in full view. Instead the moon fades out as the sun comes up and the swap happens
where the two are equally bright:

```csharp
float moonlight = Profile.MoonIntensity * (1f - Mathf.Clamp01(sunlight / Handover));
bool day = sunlight >= moonlight;
```

At the crossover both are around 0.1 against an ambient of 0.14, so the flip is invisible.

A light with zero intensity still costs a shadow pass, so shadows are switched off entirely below
0.02 — which is exactly the part of twilight where they are longest and most expensive.

#### Making the night genuinely dark

The issue asks for night dark enough that flashlights matter, and the lever for that is **not** the
moon's intensity. Direct light only touches what it hits; ambient is what fills every shadow, and a
night with bright ambient reads as an overcast afternoon with a blue filter no matter what the
directional light is doing. So the ambient sky colour runs from `(0.46, 0.60, 0.80)` at noon to
`(0.020, 0.026, 0.048)` at midnight — a factor of about twenty — and the skybox exposure drops from
1.30 to 0.16, which is what makes the sky black rather than merely navy.

The floor is deliberate. 0.14 of blue moonlight is enough to make out a treeline and a shoreline. A
black screen is not tense; it is a bug report.

#### The gradients are generated, not typed

A Unity `Gradient` serialises as a block of packed key data that is unreadable in YAML and
unmergeable in git. `SkyFactory` writes them from code, where they are eight lines anybody can argue
with, into a `DayNight.asset` that is still a plain text file a human can hand-tune afterwards.

Same create-once rule as every other look asset here: the profile exists so that a person tunes it,
so once it is on disk the generator leaves it alone. `-rebuildSky` throws the tuning away and starts
again from the numbers in the code, which is the escape hatch the flora work needed and did not have.

The skybox is Unity's own `Skybox/Procedural`: tint, exposure and atmosphere thickness in, a sky out,
one full-screen pass of arithmetic and no cubemap to load. The cycle drives it through a **runtime
copy** — writing tint and exposure into the asset every frame would leave the repository permanently
dirty at whatever time of day the editor was last closed. The copy is `HideAndDontSave`, and
`WriteScene` puts the real asset back into `RenderSettings.skybox` before saving, or the scene file
would reference an object that does not exist the next time it is opened.

#### What it produced

A day, sampled by `SkyFactory.Report` during generation:

```
00:00 (t=0.000) moon intensity 0.140, elevation 90.0deg, ambient 0.027, fog 0.0060
03:00 (t=0.125) moon intensity 0.140, elevation 45.0deg, ambient 0.043, fog 0.0073
05:45 (t=0.240) moon intensity 0.111, elevation  3.6deg, ambient 0.141, fog 0.0076
06:43 (t=0.280) sun  intensity 0.446, elevation 10.8deg, ambient 0.311, fog 0.0069
08:23 (t=0.350) sun  intensity 1.050, elevation 36.0deg, ambient 0.397, fog 0.0047
12:00 (t=0.500) sun  intensity 1.250, elevation 90.0deg, ambient 0.581, fog 0.0029
16:47 (t=0.700) sun  intensity 0.654, elevation 18.0deg, ambient 0.464, fog 0.0062
18:14 (t=0.760) moon intensity 0.111, elevation  3.6deg, ambient 0.263, fog 0.0075
19:40 (t=0.820) moon intensity 0.140, elevation 25.2deg, ambient 0.086, fog 0.0077
21:00 (t=0.875) moon intensity 0.140, elevation 45.0deg, ambient 0.048, fog 0.0073

Full daylight is 5.0x the brightest real night (1.118 vs 0.226, sun plus ambient).
Cycle 20 minutes, starting at 0.28, replicated by tick.
```

Twilight is excluded from both ends of that ratio on purpose. Dusk is dark, and it is also not night;
counting it turns a real measurement into a meaningless one. The generator logs an error if night
ever comes within a quarter of daylight, because at that point a flashlight is pointless and the
whole feature has quietly stopped working.

#### Proving the sync without a screen

`-clockLog N` prints the tick and the time of day every N seconds. Two processes started seven
seconds apart:

```
host    tick   3  06:43 (t=0.2801)      client  tick   3  06:43 (t=0.2801)
host    tick  95  06:46 (t=0.2826)
host    tick 185  06:50 (t=0.2851)
host    tick 275  06:54 (t=0.2876)      client  tick 314  06:55 (t=0.2887)
host    tick 365  06:57 (t=0.2901)      client  tick 404  06:59 (t=0.2912)
host    tick 455  07:01 (t=0.2926)      client  tick 494  07:02 (t=0.2937)
```

The client's tick jumps by **311 across a three-second interval** — ninety ticks of its own plus the
snap onto the host's clock as it connects — and tracks the host from there. That jump is the test:
without the tick sync the client would have stayed roughly two hundred ticks behind for the rest of
the session, and the two would have been looking at different skies.

**What is not verified here:** whether sunrise is pretty. Nothing in `-nographics` renders a pixel,
so the gradients are a first draft. What is measured is the arc, the intensities, the ambient ratio
and the sync — the parts that are either right or wrong rather than a matter of taste.

### Points of interest

The acceptance criterion for #35 is "a new POI can be added from the terminal in one edit", and the
design falls out of taking that literally.

A Unity object reference in YAML is a GUID. Nobody types a GUID, so `POIEntry` names its prefab by
**asset path** and the editor resolves it once at bake time:

```yaml
- Id: camp.revive
  PrefabPath: Assets/_Project/Prefabs/ReviveMachine.prefab
  Position: {x: -85, y: -43}
  Yaw: 63.16596
  PadRadius: 16
  PadFalloff: 14
  PadRaise: 0.6
  MaxSlope: 0.3
  AllowUnderwater: 0
```

Append that block to `Assets/_Project/Data/POIs.asset`, run the usual island command, and the thing
is standing on the beach with the ground levelled under it. No scene is opened and nothing is
dragged.

#### The ordering constraint that shapes everything

`PadRadius` flattens the ground under a POI, and **the flattening has to happen inside
`IslandShape.HeightAt`, not to the baked heightmap afterwards.** Everything downstream — the
splatmap, fourteen thousand trees, the grass, the spawn heights, the water depth mask — asks the
shape, not the terrain asset. Level the asset alone and all of them go on believing in the hillside
that used to be there: trees standing in mid-air over the camp, rock paint on a flat pad, grass on a
slope that no longer exists.

So the catalog is a **runtime** asset, `IslandProfile` holds a reference to it, and `POIFactory`
attaches it *before a single height is sampled*. The pad is a smoothstep blend toward the raw height
at the pad's own centre, which means the target does not have to be typed in and cannot be typed in
wrong:

```csharp
_padHeights[i] = RawHeightAt(pad.Position.x, pad.Position.y) + pad.PadRaise;
```

That split — `RawHeightAt` for the five layers of island, `HeightAt` for the island with its pads —
is the only structural change to the shape since #30.

The visible consequence is that **the catalog is part of the island's identity**. Adding a POI
changes the heightmap hash, and that is correct rather than alarming: the terrain genuinely is a
different terrain.

#### Where the camp came from

The first entry's position is not typed in. `POIFactory.FindCampSite` searches a 96×96 grid for a
spot between 1.5 m and 12 m above the sea, scoring each candidate on how close it is to the height a
camp wants (4.5 m) and how flat the ground is at four points twelve metres out — because a metre of
noise at the sample point says nothing about the twenty metres around it — with a small pull toward
the middle of the map so the camp is not tucked in a corner.

On seed 20260830 it picks (-85, -43), 4.5 m above the sea, and faces the machine inland so walking
out of it looks at the island rather than at the water. After that first generation it is a number in
a text file like everything else and a human can move it.

The search runs against a copy of the profile with the catalog detached, because the site being
chosen is the site the pad will be built on: searching the already-padded island would be asking
where to put the camp on an island that assumes the camp is already there.

#### Baking, and what it refuses to do

`POIFactory.Bake` resolves each entry against the terrain that now exists, snaps it to the ground,
and writes the resolved list into a `POISpawner` in `Island.unity`. The array is rewritten whole
rather than appended to — it is generated output, and a second run must not leave two revive
machines in the same spot.

It validates, and none of the validations refuse to build:

| Check | Level | Why not fatal |
|---|---|---|
| Below sea level without `AllowUnderwater` | error | Wrecks and docks want to be; it is a tuning question |
| Slope over `MaxSlope` | warning | A pad fixes it, and the author may have meant it |
| Outside the island square | error | Almost certainly a typo, still not worth failing a build |
| Prefab does not exist | warning, skipped | The catalog is allowed to describe the shop before #36 builds it |
| Duplicate id | error | Lookups by id become a coin flip |

That last-but-one row matters more than it looks: the catalog can list the shop, the casino and the
native village now, with their pads already levelled into the terrain, and each one starts appearing
as its prefab arrives. The world gets built in the order the art gets built, without a merge.

#### Spawned, not placed

`POISpawner` instantiates from registered prefabs on the server, exactly as `WorldSpawner` does for
the arena and `PlayerSpawner` does for bodies. FishNet identifies scene objects by an id baked at
save time, and every scene in this project is written by an editor script in batchmode — a path where
that baking is unproven. Spawning from a registered prefab is the path that already works every time
a client connects.

It handles both orders of events, which is not optional: the island is loaded as a scene *after* the
server is up, so the state event that would have started it has already fired. `Start` checks
`ServerManager.Started`, and the event covers a scene that happens to load first.

`WorldSpawner` stays where it is, holding the arena's revive machine. It was the honest first draft
and it still owns the greybox arena; the island's props are the island's.

#### What it produced

```
[POIFactory] Camp site found at (-85, -43), ground 4.5m, score -0.83.
[POIFactory] Generated Assets/_Project/Data/POIs.asset with 1 entries.
[POIFactory] Attached the catalog to the island profile; its pads are now part of the shape.
[POIFactory] camp.revive -> .../ReviveMachine.prefab at (-85.0, 5.1, -43.0), yaw 63, pad 16m.
```

The one-edit criterion, demonstrated by appending eleven lines from a shell and regenerating:

```
heightmap BD4244A0  ->  8E4DA61A          (the new pad is in the shape)
[POIFactory] 'beach.wreck' is at -1.4m, under the sea, and is not marked AllowUnderwater.
[POIFactory] 'beach.wreck' wants .../Wreck.prefab, which does not exist yet. Placed nothing;
             the pad under it is still in the terrain.
[POIFactory] Baked 1 of 2 catalog entries into the island spawner; 1 have no prefab yet.

remove the eleven lines, regenerate  ->  heightmap BD4244A0, asset AFD42A0C
```

Both halves are the point: the edit reached the terrain, the validator caught that the wreck had been
put in the sea, the missing prefab was reported rather than fatal, and taking the edit back restored
the island byte for byte.

The splatmap hash did not move across that experiment, and that is right rather than suspicious — the
wreck's pad was underwater, where the ground is seabed sand at any height and nothing grows.

### Six landmarks, as boxes

#36 asks for blockouts of the six places that make the island a place rather than a heightmap, with
one criterion: all six reachable on foot, each with a clear purpose.

They are deliberately crude — primitives, five colours, no detail — because a greybox that starts
looking finished stops getting replaced, and all of this is meant to be thrown away in M8. What is
*not* crude is the layout, because that is the part being tested and the part that survives the art
pass:

| Landmark | Shape | What the shape is for |
|---|---|---|
| Base Camp | shelter, storage, bench, fire, in a loose ring | Four players landing at once do not all stand in the same box |
| Trading Post | hut with a counter facing out | Trade is a gesture at a counter, not a menu behind a door |
| The Shack | three walls, open front, round table, bar | Four players crowding one table *is* the casino scene |
| Native Village | five huts round an open middle, totem | Cover to break line of sight, nowhere entirely safe, visible over the canopy |
| The Wreck | tipped hull, fallen mast, debris | The first thing seen from the water; where the boat parts come from |
| The Cave | two slabs, a lintel, a room behind | Shelter at night and something to mine, without needing a hollowed mesh |

`Landmark` carries the purpose into the running game — id, display name, one line of why you would
walk there, a radius, and whether it is hostile. A comment in a builder script is not a purpose; the
HUD and the objective system need to be able to read it. It is not a `NetworkBehaviour`: every field
is baked into the prefab and never changes, so replicating it would be sending four clients a
constant they already have on disk.

Decoration keeps no collider. Every collider on a landmark is something a ragdoll can wedge itself
behind, and a blockout has no business generating those by accident — so of 61 parts across the six,
32 are solid and the rest are scenery.

#### Placed by search, not by hand

None of the six has a typed-in coordinate. `POIFactory` searches for each one against the island it
will stand on, with a wish per landmark: a height it wants, a height range it will accept, how far
from the camp it must be, how flat its own footprint has to be, and how far it must stay from
anything already placed.

That ordering matters: the camp is found first and everything else is positioned relative to it, so
the shop is a walk, the village is a hike, and the cave is somewhere in between — on any seed.

```
camp    (-84, -47)   4.4m      shop    (-177, -28)   7.9m
casino  (-270, -74)  5.9m      village ( 130, 205)   9.9m
wreck   (   9, -47)  0.3m      cave    (  -9, 149)  34.0m
```

Each gets a pad, so the buildings stand on level ground — and because pads live inside the height
function (see the previous section), the splatmap and the forest agree with them. Six pads is the
whole difference between heightmap `BD4244A0` and `155D4AAC`.

The revive machine is the exception that proves the rule: it is placed *inside* the camp's pad rather
than on one of its own, because two overlapping pads at different heights make a step in the middle
of the camp.

#### Proving "reachable on foot" without feet

The criterion is a claim about the terrain, not about the prefabs, so it is checked against
`IslandShape`: a cost-carrying flood fill from the camp over an eight-metre grid, where a cell is
walkable if it is above water and no steeper than 0.8 — about 39°, past which a character controller
stalls. It is not a NavMesh — that is #37 — but a NavMesh cannot invent a route the terrain does not
have.

```
camp.base     0m        camp.revive     8m
shop        103m        casino        197m
wreck        96m        cave          222m
village     337m
7 of 7 landmarks reachable on foot across 3359 walkable cells of 8m at up to 0.8 gradient
```

**The first version of this lied and the numbers looked better for it.** It took the shortest distance
found anywhere in a three-cell window around each landmark, to cope with a wreck standing on a tile
that is under water by a few centimetres — which quietly shaved up to thirty metres off every
distance it reported. The shop came out as 72 m of walking from a point 95 m away in a straight line,
which is not a suspicious number unless you look at it. The fix is to start at the centre cell and
grow one ring at a time, reporting how far short it landed when it has to: the shop is 103 m, which
is longer than the straight line, as walking always is.

#### What is not verified

That the buildings look like buildings, and that a player can get *into* them rather than merely up
to them. Both need a screen and a body, and the bodies do not walk on this island until #39 makes it
the scene the game loads. What is verified is that the six exist, are registered as spawnable, carry
their purpose as data, and stand on ground a person could walk between.

### Landing on the island

Until #39 the island existed and the game did not play on it. Bootstrap held the NetworkManager
*and* the greybox arena, only Bootstrap was in build settings, and pressing play put four players on
a sixty-metre concrete plate. This is the change that makes the island the game.

#### Three scenes instead of one and a half

The arena was built into Bootstrap, which was fine while it was the only map and wrong the moment
there were two: **Bootstrap stays loaded for the whole session.** A concrete plate living in it would
sit in the middle of the island's sea, and its directional light would fight the island's day/night
cycle over which light URP treats as the main one.

So the arena moved into `Arena.unity`, the island is `Island.unity`, and Bootstrap went back to being
what its own comment always claimed it was: a NetworkManager and nothing else.

`GameSceneLoader` sits on the NetworkManager and decides which one is played, on the server, because
four clients cannot each pick their own island. It loads it as a FishNet **global** scene — meaning
every connection gets it, including ones that arrive later — which produces exactly the order
everything downstream already assumed:

```
server starts -> global scene loads -> a client connects -> that client loads the global scenes
              -> OnClientLoadedStartScenes -> PlayerSpawner spawns a body
```

The island's spawn points, its POIs, its water and its sun are all in place before anybody has a body
to put on them, and not one "wait until" was needed to arrange that.

```
-scene island   the real map (default)
-scene arena    the M1 greybox, for testing combat without a kilometre of terrain
-scene none     load nothing, which is what the Bootstrap-only smoke tests expect
```

#### Spawn points cannot be wired, so they register themselves

`PlayerSpawner` lives in Bootstrap; the spawn points live in whichever map loaded. **Unity has no
cross-scene references**, so a serialized `Transform[]` cannot span that boundary. `SceneSpawnPoints`
hands them over at run time instead, and takes them back on destroy — a spawner holding transforms
from an unloaded scene would put the next player at the origin without saying why.

Both maps use it, which is how the arena kept working unchanged.

#### Four people, facing each other

The island's ring is written by `TerrainGenerator` around the camp POI rather than at a fixed
coordinate, so it follows the camp when the seed changes or somebody moves it. Four points, 6.5 m
radius, 1.2 m of clearance over the terrain — and **facing inward**.

That last part is not decoration. The first thing that happens in a fresh session is four people
appearing at once, and if they spawn facing outward the first thing each of them sees is trees;
nobody knows anyone else is there until somebody turns round. Facing the middle means the first frame
of the game is your three friends.

#### An obvious first objective

`Objective` is a static holding one imperative line and an optional target transform. It is not
networked, deliberately: an objective is a conclusion every peer can reach from state it already has,
and a replicated one would mean four clients waiting a round trip to be told something each of them
could work out on the spot.

`IslandIntro` sets it to **"Search the wreck on the beach"** and then polls for a few seconds for the
landmark to arrive, because the POIs are spawned by the server and reach a client whenever they
reach it — polling is both simpler and more robust than an event that has already fired by the time
anybody subscribes. `ObjectiveBanner` draws it top-centre with a live distance, because a player who
has just spawned is looking at the middle of the screen.

The wreck is the right first pointer: it is on the tideline within sight of the camp, it is plainly
what you arrived on, and it is where the boat parts come from in M5.

**One bug this found:** the catalog calls it `wreck` and the prefab was built as `Wreck`. The server
stamps the catalog id onto what it spawns, so a case-sensitive lookup worked on the host and silently
failed on every client. The lookup is case-insensitive now, and the reason is written next to it.

#### Two more things that were quietly wrong

`RegisterInBuildSettings` inserted at index 0 with the comment "Bootstrap must stay at index 0" —
correct while Bootstrap was the only entry, and exactly backwards once a second scene was registered
after it, which put Arena at index 0 and made it the scene the player boots into. It now appends and
then moves Bootstrap to the front explicitly.

`SceneSpawnPoints` registered from both `Awake` and `OnEnable`, so every load logged the same line
twice. Harmless, and the kind of thing that trains people to stop reading logs.

#### What a session looks like now

```
[GameSceneLoader] Loading 'Island' as a global scene for every connection.
[PlayerSpawner]   Using 4 spawn points from SpawnPoints.
[POISpawner]      Placed 7 points of interest.
[Objective]       Search the wreck on the beach -> Wreck (wreck).
[PlayerSpawner]   Spawned body for connection 0 at (-84.00, 6.11, -40.50), colour slot 0.
[PlayerSpawner]   Spawned body for connection 1 at (-77.50, 6.11, -47.00), colour slot 1.
```

Both bodies stand on the camp pad at (-84, -47), 4.9 m of ground plus 1.2 m of clearance. The client
resolves the same objective from its own copy of the world. Zero exceptions on either side.

`-scene arena` still produces the M1 session unchanged: the arena's own four spawn points, the revive
machine from `WorldSpawner`, a body at (0, 1.2, 6).

**What is not verified:** what any of it looks like. The camp, the ring facing inward, the banner and
the wreck on the horizon are all first drafts that need a screen and four people — which is now
possible, and was not before this change.

### Somewhere to walk

Natives, animals and every other thing with a brain need to cross a square kilometre of noise-built
terrain without falling off it. `NavFactory` bakes that surface at island-generation time and
`NavWalk` proves an agent can actually use it.

#### The bake is bounded by the waterline

The NavMesh is built into a volume 1024 m square whose **floor is 0.4 m above sea level** and whose
ceiling is above the highest peak. Recast rasterises triangles into the build bounds and clips
whatever falls outside, so the seabed — roughly half the square — is never voxelised at all. That is
both the correct answer (the sea is not walkable) and the reason the whole bake takes **0.7 seconds**
rather than minutes.

Bounding it explicitly also avoids a trap. A surface that measures itself against every source in the
scene would include the water's **four-kilometre horizon ring** and produce a bake volume sixteen
times the island.

Settings: the humanoid agent from `ProjectSettings/NavMeshAreas.asset` (radius 0.5 m, height 2 m,
slope 45°, climb 0.75 m), 0.25 m voxels, 512-voxel tiles, `minRegionArea` 6 m². Big tiles were chosen
deliberately: a tile is 128 m across, so there are 64 of them over the whole island and correspondingly
few internal seams for an agent to catch on.

#### The buildings are baked in, not carved out

The POIs are spawned by the server, so at bake time the scene is bare terrain and a NavMesh built from
it would send agents straight through the shop. `NavFactory` instantiates each POI prefab where
`POISpawner` will put the real one, bakes around it, and destroys it before the scene is saved.

The alternative — a carving `NavMeshObstacle` on every solid piece — costs a re-voxelisation per
obstacle at spawn and buys nothing, because the buildings never move.

**Terrain trees are not in the NavMesh.** Unity collects a terrain as one heightmap source; its
fourteen thousand trees are not colliders it can see. Agents will clip palm trunks. Fixing that
properly means fourteen thousand obstacles or a hand-built modifier per grove, and neither is worth it
before there is an agent to be annoyed by it.

#### Two bugs that cost most of the work

**The first NavMesh contained seven roofs and no island.** A `NavMeshSurface` set to collect a volume
asks the scene which colliders overlap that box, and a `TerrainCollider` created seconds earlier in a
scene that has never been saved answers that question with nothing. The stand-in buildings were found;
the terrain under them was not. The fix is to stop asking: the terrain source is now built by hand from
its `TerrainData` and the position of the terrain object, and each building's colliders are converted
one at a time. Nothing depends on a collider's bookkeeping being up to date.

**The second NavMesh was still empty, and this one was subtler.** The heights are written into the
`TerrainData` minutes earlier and live in a GPU-side texture until something asks for them back. Recast
reads the CPU copy. Without a `SyncHeightmap()` it faithfully rasterises a terrain that is still flat —
which produces a NavMesh of nothing, and looks exactly like a collection failure. One line:

```csharp
terrainData.SyncHeightmap();
terrain.Flush();
```

#### Landmarks are approached, not entered

A catalog coordinate is usually *inside* the building it names — between the shop's hut and its
counter, inside the wreck's hull. The NavMesh is baked around those walls, so the patch under the
coordinate is a sealed room a metre across, and a path to it comes back `PathPartial` for a reason that
has nothing to do with the island.

`NavApproach` is what anything that wants to reach a landmark should ask. It tries the coordinate
itself, then a ring of eight points 10 m out, and returns the first pair that joins up. **Both ends get
that treatment** — approaching only the destination works when the walk starts on open ground and fails
when it starts in the shop, which is exactly the bug the first version had.

Nothing spawns agents inside a sealed building, so the interior patches are harmless. Whether players
can get *into* the buildings is a greybox question and belongs with #38.

#### The acceptance test walks

A bake report cannot tell you whether an agent gets stuck. A path can come back `PathComplete` and
still strand one, because a complete path is a list of corners and getting stuck happens *between*
them — on a tile boundary, on a ledge the agent can see across but not step down, on a sliver of
NavMesh narrower than its radius.

So `NavWalk` puts a real `NavMeshAgent` on the island and watches it. Server-side, not networked,
dormant unless asked for:

```
-navWalk village:camp.base   one leg, between two catalog ids
-navWalk all                 every landmark to the camp, one after another
```

It fails a leg when the agent moves less than 0.6 m in 4 seconds, when the path is partial, or when the
leg runs past its timeout — and it reports how far it walked against the straight line, which is the
number that says whether the route was sensible.

#### What came out

```
[NavFactory] 34 sources over 1024x1024m, from y 0.4 to 170.0: one terrain
             (1025^2, 200m tall, mid-island height 46.1m) and 33 pieces of building.
[NavFactory] Baked in 0.7s at 0.25m voxels, 512-voxel tiles: 3300 vertices, 1516 triangles,
             23.1 hectares walkable out of 21.8 the terrain offers (106%).
```

The comparison is worth keeping: 21.8 ha is what the island's *shape* says a human could stand on,
sampled independently on a 4 m grid with the same 45° limit. A NavMesh that came out at a fraction of
it would mean the bake lost the island rather than trimming it.

Every landmark connects to the camp, and an agent walks every one of them:

```
[NavWalk] camp.revive -> camp.base: arrived in  2.6s, walked  13m for   9m straight (1.47x)
[NavWalk] shop        -> camp.base: arrived in 16.5s, walked  97m for  95m straight (1.02x)
[NavWalk] casino      -> camp.base: arrived in 31.4s, walked 187m for 188m straight (0.99x)
[NavWalk] village     -> camp.base: arrived in 56.9s, walked 340m for 331m straight (1.03x)
[NavWalk] wreck       -> camp.base: arrived in 15.1s, walked  89m for  93m straight (0.96x)
[NavWalk] cave        -> camp.base: arrived in 36.8s, walked 221m for 212m straight (1.04x)
[NavWalk] Done: 6 arrived, 0 failed.
```

**village → camp.base is the issue's acceptance criterion**: 340 m across the island, never stalling,
3% longer than flying. The 1.47x on the revive machine is a nine-metre walk round a hut, which is what
a short leg looks like.

The island regenerated to the same terrain hash (`8F7F1E51`) with the NavMesh added, so none of this
moved the ground.

### Making it run on the laptop

The target is sixty frames a second at 1080p on a Radeon 760M — an integrated GPU sharing system
memory with the CPU. Nothing in this repository can confirm that number: the machine that writes the
code has a discrete card, and a headless build has no frames at all. So this section is two things
kept apart on purpose — the settings that give the island a chance, and the instrument that says
whether it took it.

#### The build was shipping on Ultra

Unity's per-platform default quality for Standalone was 5, which is Ultra: four shadow cascades, 2x
MSAA, full resolution. On a laptop iGPU the first thing a player would have concluded is that the game
is broken.

Three things had to change, and only the first is obvious.

**`m_PerPlatformDefaultQuality: Standalone` is what a build starts at** — not `m_CurrentQuality`,
which is only the editor's. Setting the level in the editor and shipping is how a build ends up on
Ultra while every screenshot in the office looks fine. There is no scripting API for that map, so the
value lives in `ProjectSettings/QualitySettings.asset` and `RenderTuning` checks it rather than
setting it. It is now 2, Medium.

**`GraphicsBoot` picks a level before the first frame**, and says why:

```
[GraphicsBoot] Quality 'Low' (1) - integrated graphics.
               AMD Radeon 760M Graphics, 2048MB video, 31938MB system, 12 cores.
```

The order is command line → the player's saved choice → a guess. The guess looks at the device *name*
before it looks at memory, because an iGPU reports a slice of system RAM: a 760M can claim 2 GB of
"video memory" while having a fraction of the bandwidth that number implies. Guesses about hardware
age badly, so it logs both its answer and the facts it used — a wrong one is a bug report rather than a
mystery, and #84's settings menu will write the preference that overrides it.

`-quality Low` / `-quality "Very High"` / `-quality 3` pins it, and works headless, which is the only
way to test this path without a screen.

#### Three tiers, in a file rather than in an inspector

`RenderTuning.Apply` writes the three URP assets from one table, so the tiers can be read side by side:

| | render scale | MSAA | HDR | shadows | cascades | lights/object |
|---|---|---|---|---|---|---|
| **URP_Low** | 0.8 | off | off | 1024² over 45 m, hard | 1 | 2 |
| **URP_Medium** | 1.0 | off | off | 2048² over 80 m, soft | 2 | 4 |
| **URP_High** | 1.0 | 2x | on | 2048² over 150 m, soft | 4 | 8 |

Render scale is the single biggest lever on an integrated GPU — 0.8 is 64% of the pixels for a
softness nobody notices at 1080p. MSAA stays off below High because this game has no thin geometry for
it to rescue. And all three keep the depth and opaque textures off, because each is a whole render pass
this game does not use — `Water.shader` was written to avoid the depth texture on purpose, and turning
it on in a quality preset would quietly undo that decision.

The high tier's shadow distance is 150 m rather than more because the fog closes at about 700 m and
shadows past that are invisible by definition.

#### Grass is most of the frame

The largest remaining lever is not a renderer setting. It is that the island draws thousands of
alpha-tested grass quads, back to front, filling the screen whenever you look down a slope.

`TerrainQuality` scales the terrain's distances to the quality level, as multipliers of what
`IslandProfile` asked for — so the profile stays the one place the island's look is defined, and this
only says how much of it a given machine gets:

```
[TerrainQuality] low:    trees 192m, grass 43m at 0.48 density, terrain error 10px
[TerrainQuality] medium: trees 272m, grass 68m at 0.68 density, terrain error  7px
[TerrainQuality] high:   trees 320m, grass 85m at 0.80 density, terrain error  5px
```

**Halving the grass distance quarters its area.** Tree billboards keep their share rather than being
pulled in with everything else, because that is the cheap half of the tree budget and losing it makes
the island look bald from a hilltop for almost no saving.

#### Two smaller cuts

Greybox decoration — signs, stock crates, bench legs, the logs on the fire — no longer casts shadows.
Those are extra draws in the shadow pass for silhouettes nobody can pick out from two metres away, and
the shadow pass is where an integrated GPU spends its afternoon. The pieces that make a building's
shape, the ones with colliders, still cast.

The camera's clip planes are now in `CameraTuning` and read by both the scene camera and every player's
Cinemachine lens. Two different things were setting them from two different defaults, and Cinemachine's
wins whenever a virtual camera is live — which is how a build ends up rendering a different distance
depending on whether anyone has spawned yet.

`RenderTuning` also checks the three distances that have to stay in order — where the fog goes opaque,
the far plane, and the outer edge of the water's horizon ring:

```
[RenderTuning] Draw distance agrees with the fog: fog opaque at 693m
               (thinnest density 0.0029), camera far plane 5000m, water horizon out to 4000m.
```

If the fog ever reaches further than the camera does, the sea ends in mid-air. Nobody would change a
fog curve while thinking about a camera, which is exactly why a machine checks it.

#### Occlusion culling was considered and skipped

The issue asks for it. It would do nothing here, and saying so is more useful than baking it.

Unity's occlusion culling works on renderers flagged static, and it culls whole objects. This island has
one terrain — a single renderer that occlusion culling cannot subdivide — and seven buildings that are
**spawned by the server at run time**, so they are not static and cannot be in the bake at all. Nothing
is left for Umbra to cull, and the data would cost bake time and load-time memory to achieve it.

It becomes worth revisiting when there are interiors: the cave, the village huts, anything you can be
inside and not see out of. That is #78's territory, not this one.

#### Measuring it

`PerfProbe` is the instrument. Average frame rate on its own is close to useless for a game — a run
that averages 62 fps and stutters to 14 twice a second feels far worse than a flat 45, and the average
hides it completely. So the number to quote is the **1% low**: the mean of the worst one percent of
frames in the window.

```
EscapeWithYourFriends.exe -perfLog 10 -logfile perf.log

[PerfProbe] AMD Radeon 760M Graphics (2048MB), 1920x1080 fullscreen, quality 'Low', vsync 1, ...
[PerfProbe] 604 frames: 60 fps average (16.6ms), 61 median, 1% low 41 fps (24.4ms), worst 38.1ms.
[PerfProbe] Session: 3611 frames, 59 fps average (16.9ms), worst single frame 210ms. <device again>
```

Every line carries the device, resolution and quality level, because a frame time without them means
nothing. Development builds also draw the two numbers in the top-right corner of the HUD, so somebody
playtesting can see a stutter without reading a log.

**What is not verified:** the sixty. That needs one run on the 760M at 1080p with four players
connected, which is a thing only a person with that laptop can do. If it comes back short, the next
levers in order are: grass distance below 43 m, render scale 0.7, and a cheap variant of the water
shader that drops its second normal sample — the water covers a large part of the screen and is the
only per-pixel cost on the island that has not been touched.

### What you are carrying

Four players, twenty slots each, and a list that resends itself every time anybody picks anything up.
That constraint decided the shape of the whole inventory before any of the rules did.

**A slot is four bytes.** `ItemStack` is a `ushort` catalog index and a `ushort` count, and nothing
else. The obvious alternative — a slot holding a string id, or a reference to the `ItemDef` asset —
fails for two different reasons. A ScriptableObject reference cannot cross the wire at all; there is
no such thing as sending an asset. A string id can, but `"scrap_metal"` is eleven bytes against two,
on a structure that replicates eighty times over in a four-player session, for information that both
peers already have on disk. So the wire carries a number and the catalog turns it back into a thing.

**Index 0 means empty.** Real items run 1..N. This is not a sentinel bolted on afterwards: it means a
default-constructed `ItemStack` *is* an empty slot, so a fresh array needs no initialisation pass and
no null check ever has to distinguish "nothing here" from "not set up yet". `Valid` exists to catch
the one state that should never occur — an index with no count, or a count with no index.

**The catalog is sorted by id, and the sort order is the wire format.** `ItemCatalog` is one asset
holding every `ItemDef`, rebuilt whole by `ItemFactory` and never hand-edited. Every peer has to derive
the same index for the same item, and the only thing guaranteed identical across two machines running
the same build is the ordered set of ids — not file names, not GUIDs, not the order Unity happened to
return from `FindAssets`. Ordinal sort on the id is the one deterministic function available.

Adding an item shifts the indices of everything alphabetically after it, which is fine and worth being
explicit about: nothing persists an index. A save file, a recipe, a shop listing and a quest all refer
to items by id. The index exists only for the duration of a session, between peers that are by
definition running the same build, because Steam will not let two different builds into one lobby.

**Slots are a fixed-length `SyncList`, not a growing list of occupied ones.** A fixed list means
picking something up is one four-byte entry changing at a known position; a compacted list means the
whole array reorders and resends. It also means positions are stable, which the UI in #46 needs — an
item should not slide out from under your cursor because a friend across the island picked up a plank.

#### The rules, and where they live

Everything that decides is `[Server]`. There is deliberately **no RPC that lets a client add an item**
— not a validated one, not a rate-limited one, none. A client can send exactly two messages,
`MoveSlot` and `SplitSlot`, both `[ServerRpc]`, both ownership-gated by FishNet so a client can only
rearrange its own bag, and both bounds-checked on arrival because a message is a request and not a
promise. Picking things up (#42), crafting (#43) and shops (M4) all call the server-side methods here
*after* the server has already decided the player was entitled to it.

Two fill rules are worth the words:

- **Partial stacks before empty slots.** `Add` runs `Fill` twice, first over slots already holding
  the same kind with room to spare, then over empty ones. Filling an empty slot while a half-full one
  of the same thing exists is exactly how a bag ends up as twelve fragments of rope.
- **Weight admits per item, not per stack.** `Allowed` divides the remaining kilograms by the item's
  weight and clamps, so a heavy stack half-fits instead of being refused whole. Refusing whole is how
  a player with 39 kg carried ends up unable to pick up one plank because they asked for ten.

Weight is a limit rather than a second grid on purpose: it is the thing that makes a second trip a
decision, and it costs one float instead of a tetris minigame nobody wanted.

#### Adding an item is a data change

`ItemFactory.Build` seeds fifteen items and rebuilds the catalog:

```
Unity.exe -quit -batchmode -nographics -projectPath . \
  -executeMethod EscapeWithYourFriends.EditorTools.ItemFactory.Build
```

It **creates** an item asset that does not exist and it **rebuilds the catalog**, but it never
overwrites an item that is already there. The seed table is a starting point, not a source of truth —
once an asset exists its numbers belong to whoever is balancing the game, and a rerun of the factory
must not undo their afternoon. So a sixteenth item is either a new row in `ItemFactory` or an `.asset`
file dropped into `Assets/_Project/Data/Items` by hand or by sed. Both end up in the catalog, and
neither is a code change to anything that reads it.

The rebuild also refuses to be quiet about the two mistakes that break lookups: a blank id (nothing
can refer to it, and it will not survive the next rebuild) and a duplicate id (`Find` becomes a coin
flip and one of the two is unreachable). Both are errors, not warnings.

One Unity constraint shaped this file. `ItemCatalog` lives in `EWYF.Runtime` and `ItemFactory` in
`EWYF.Editor`, and `internal` does not cross an assembly boundary — an `internal SetItems` was not
visible from the editor assembly at all. Making it `public` was the wrong fix: the array is the wire
format and nothing at run time has any business rewriting it. The factory writes `_items` through a
`SerializedObject` instead, which is how an editor tool is supposed to touch a serialised field, and
then calls the one public method the catalog does expose — `Invalidate()` — because a `SerializedObject`
write changes the field without going through any code that could have dropped the cached lookups.

#### Proving it

There is no test framework in this project, and adding one to check a stacking rule would be the wrong
trade. What actually needs proving is that the rules hold *in a real session* — server-authoritative,
replicated to a real client, in a Mono build — so the test runs inside the game. On `-invTest` the
server runs a scripted sequence against the first inventory it sees and checks every step, while every
client logs its own replicated copy whenever it changes.

```
EscapeWithYourFriends.exe -batchmode -nographics -host   -port 7792 -playerKey test:host -invTest
EscapeWithYourFriends.exe -batchmode -nographics -client -address 127.0.0.1 -port 7792 -playerKey test:c1 -invTest
```

Twenty-three checks cover: starts empty; every slot valid; rope stacking to ten so fifteen is a full
stack plus a partial; the full stack coming first because partials fill first; a non-stackable hatchet
taking its own slot; merge topping up the target and leaving the remainder; split halving into an
empty slot; swap exchanging two different kinds; out-of-range moves changing nothing; the carry limit
biting while slots are still free; removal finding items wherever they sit; and `TakeSlot` lifting one
slot out whole.

```
[ItemFactory]  15 items in the catalog, 15 created just now
[InventoryTest] start: 0/20 slots, 0.0/40kg [empty]
[InventoryTest] 23 passed, 0 failed. server holds: 4/20 slots, 25.5/40kg [2:rope, 3:boat_part, 4:boat_part, 7:rope x2]
```

and, in the separate client process:

```
[InventoryTest] client sees: 4/20 slots, 25.5/40kg [2:rope, 3:boat_part, 4:boat_part, 7:rope x2]
[InventoryTest] client sees: 0/20 slots, 0.0/40kg [empty]
```

Two processes, two descriptions, and the remote client's copy of the host's bag is character-identical
to what the server holds — which is the only statement about replication worth making. The second line
is the client's own inventory, correctly empty. Zero exceptions on either side.

### Loot on the floor

An inventory that can only ever be filled by the server is safe and completely inert. #42 is the part
that makes it a game: items come out of the bag as real objects, they stay where they land, and
**anybody** can take them.

That last word is the whole issue. There is deliberately no ownership test on picking something up.
A stack you dropped, a stack a friend dropped, a stack that fell out of a corpse — `WorldItem` cannot
tell them apart and does not try. Robbing the pile is the feature.

#### One networked prefab for every item in the game

A networked prefab has to be registered in FishNet's spawnable list, identically, on every peer. A
prefab per item would therefore make adding an item a registration step and a rebuild — which is
exactly the property #41 spent its effort buying. So there is one `WorldItem.prefab`, and what an
item *looks* like on the ground is `ItemDef.WorldPrefab`: an ordinary, non-networked visual parented
underneath at run time, falling back to a category-coloured cube while the game is greybox.

The prefab reference lives on `ItemCatalog` rather than on a spawner component in each scene, for the
same reason the catalog holds everything else: it is already published globally and already wired into
every inventory, so there is exactly one asset to assign and no singleton to place per scene.

#### Physics belongs to the server

The rigidbody simulates on the server and clients set theirs kinematic, letting `NetworkTransform`
drive the transform. Two peers integrating the same collision independently is how a crate ends up in
two places at once, and a crate you can pick up on your screen but not on mine is the one failure mode
worse than jitter.

The prefab's numbers are the feel of loot on the floor, and three of them are not arbitrary:

- **A box collider, not a sphere.** A sphere rolls forever down the island's slopes. Loot that ends up
  in the sea because it was dropped on a hill is not a funny bug.
- **`ContinuousDynamic` collision detection.** A thrown item at 9 m/s covers 30 cm in a tick and the
  ground is a terrain collider; discrete sweeps let it tunnel through and fall out of the world.
- **An aggressive sleep threshold.** Most of these objects spend their lives motionless in a pile at
  the base, and a hundred awake rigidbodies is the host's frame budget spent on nothing.

#### Initialise before spawn, not after

The first version of the spawner called `ServerManager.Spawn` and *then* set the stack. It looked
right and it was wrong: FishNet builds the spawn message from the SyncVar values as they stand at the
`Spawn` call, so a value written afterwards arrives as a separate update. Every client saw the item
exist, briefly, as an empty pile with no visual.

The client-side log caught it — the server's own numbers were perfect the entire time, which is the
argument for making a harness prove the *client's* view rather than the server's:

```
[WorldItem] client sees - at (-84.0, 6.5, -39.9)      <- before
[WorldItem] client sees rope x6 at (-84.0, 6.5, -39.9) <- after
```

The fix is to initialise the instantiated object before spawning it, which also means
`Initialise` cannot carry `[Server]`: that attribute checks the object is initialised, which before
the spawn it is not, and the guard would refuse the one call that matters.

#### Drop, throw, and the one key that does both

Tap `G` to drop the selected stack at your feet; hold it past 0.25 s and release to throw. One binding
for two verbs, because the hold reads as winding up and a separate throw key is one nobody would find.
The decision happens on release rather than on press — deciding on press would send the item away
while you are still winding up.

Two details that only show up in play:

- **A tap fast enough to go down and up inside one frame** would never look like a release. The
  buffered press opens the hold as well as recording when it started, so the release is still seen.
- **A thrown item leaves from eye height, which is inside your own capsule.** It is spawned with the
  thrower's collider ignored for the length of the pickup cooldown, then restored. Without that it
  hits you in the face and lands at your feet, which reads as the throw not working rather than as a
  physics detail. The same cooldown stops your own throw from re-entering your bag before it has
  travelled a metre.

Drop shares the key with throwing a carried body, one step down the same priority list `Interact`
uses: a body on your shoulder is the bigger commitment, so with your hands free the key means the bag.

#### Selecting what to drop

Drop needs a target slot, so `Inventory` gained a replicated `SelectedSlot` driven by the number keys
1–5 and the scroll wheel. Replicated rather than owner-local because everybody needs it — the server
validates against it, and the held-item visual in the art pass reads it off a body that is not theirs.
The wheel wraps rather than clamping, because a wheel that sticks at the end of the row feels broken,
and the scroll input accumulates fractions so a trackpad works as well as a notched wheel.

The visible hotbar is #46. This is the minimum that makes "drop" a thing a player can aim.

#### Taking things

Pickup goes through `IInteractable`, so it uses the same sphere cast, the same range, and the same
server-side revalidation as the Revive Machine. Two rules are worth stating:

- **A full bag takes what fits and leaves the rest.** All-or-nothing would mean an overloaded player
  cannot take a single bandage off a pile of forty planks. The pile shrinks on everybody's screen
  rather than vanishing and respawning, which is why the contents are a SyncVar and not a spawn
  argument.
- **Nothing despawns loot.** There is no timer, because walking back to your stash and finding it gone
  is the most annoying thing a survival game can do. What there is instead is a hard cap of 240
  simultaneous stacks — several full inventories of headroom — beyond which the oldest is removed.
  That trades a case nobody will hit for an unbounded one that would eventually take the host down.

#### Proving it

The acceptance criterion is "you can rob a friend's dropped loot", and no single process can make that
claim. `-itemTest` waits up to twelve seconds for a second player to connect and says so in the log if
none arrives, rather than testing theft against the same player and calling it proof.

```
EscapeWithYourFriends.exe -batchmode -nographics -host   -port 7798 -playerKey test:host -itemTest
EscapeWithYourFriends.exe -batchmode -nographics -client -address 127.0.0.1 -port 7798 -playerKey test:c1 -itemTest
```

Server:

```
[WorldItemTest] 2 player(s) present. Theft can be tested for real.
[WorldItemTest] 18 passed, 0 failed. 2 stack(s) on the ground.
  owner: 0/20 slots, 0.0/40kg [empty]
  other: 4/20 slots, 40.0/40kg [0:rope x8, 1:boat_part, 2:boat_part, 3:boat_part]
```

Client, which is the half that matters:

```
[WorldItem] client sees rope x6 at (-84.0, 6.5, -39.9).
[WorldItem] client no longer sees rope x6.
[WorldItem] client sees rope x8 at (-77.5, 6.0, -47.0).
[WorldItem] client sees the pile change: rope x8 -> rope x6.
[WorldItem] client sees rope x2 at (-84.0, 6.5, -39.9).
```

Read in order: a stack the *other* player dropped appears with the right contents, is taken by this
client and disappears; a pile too heavy to lift whole shrinks by the two that fitted and stays on the
ground with six left; and a thrown stack arrives where it was thrown. The thief ends the run at
exactly 40.0/40kg, which is the carry limit doing its job on the way in.

The eighteen checks cover: dropping spawns a networked item carrying the stack that left the bag; the
bag no longer has it; the item falls under gravity and still exists after settling; a second player
takes it and the first does not get it back; the pile despawns once emptied; a pile can be spawned
without a player dropping it; an overloaded bag takes exactly what fits and leaves a correct
remainder, conserving the total; a throw leaves with speed on it; the thrower cannot instantly take it
back; and it can be taken once it has travelled and landed.

### Staying alive

Four bars — food, water, stamina, warmth — ticked on the host and replicated to everyone. The
acceptance criterion is "annoying enough to drive behaviour but never tedious", which is a judgement
made in play rather than something code can be correct about. So the interesting decisions here are
about **shape**, and every **rate** lives in a text asset where the person tuning it can argue with it.

#### Two of them are clocks; one is an equilibrium

Hunger and thirst fall to zero and stay there until you eat or drink. That is a chore, deliberately —
it is the thing that sends somebody back to the base while the others keep gathering.

Warmth is not a clock. It moves toward whatever the environment says it should be and comes back on
its own:

| Where you are | Warmth settles at |
|---|---|
| Daylight, on land | 100 — the bar sits full and you never think about it |
| Night, in the open | 40 — cold and worth solving, but nothing is killing you |
| In the sea | 0 — 91 seconds from comfortable to freezing |

A warmth bar that only ever fell would mean carrying firewood everywhere, which is the definition of
tedious. This shape means the mechanic is invisible until you do one of two specific things: stay out
after dark, or swim.

**The sea target is zero on purpose.** The first version had it at 8, which read as "cold but not
lethal" and was actually "cannot ever hurt you": damage only applies to a bar at zero, so an
equilibrium anywhere above zero meant hypothermia could never fire at all. Night settles at 40 because
night is *supposed* to be survivable. The water is the one place the bar bottoms out.

Stamina limits a burst, not a journey: about eight seconds of sprint, a one-second pause and then a
refill in under seven. It decides whether you can outrun the thing chasing you, not whether you can
cross the island — a stamina bar that gated travel would turn every walk into a rhythm game.

#### What empty costs

0.8 health per second hungry, 1.2 thirsty, 1.5 freezing, and they stack. On thirst alone that is 83
seconds from full health to downed; with all three it is 29. Long enough to reach a coconut, short
enough that ignoring it is a decision rather than an oversight. Damage lands once a second rather than
continuously, so the number on the HUD visibly ticks and the log stays readable.

One ordering detail matters more than it looks. Damage is evaluated **before** the bars are ticked,
against their values as of the end of last frame. The first version ran it last and hypothermia could
never fire: at several hundred frames a second, warmth's recovery rate lifts it off zero within a
single frame, so by the time the check ran the bar was never actually empty. Evaluating first also
reads correctly — you do not stop being hypothermic the instant you step out of the sea.

#### Hunger and thirst do something before they empty

Below the low threshold they cut stamina recovery, by up to 60% at their worst. This is the only place
they have a mechanical effect short of damage, and it is what makes them worth glancing at rather than
something you notice when the health bar starts moving.

#### Where the rates live

`SurvivalProfile` is a ScriptableObject, and `SurvivalFactory` creates it and then reports what its
numbers mean in minutes:

```
Unity.exe -quit -batchmode -nographics -projectPath . \
  -executeMethod EscapeWithYourFriends.EditorTools.SurvivalFactory.Build
```
```
[SurvivalFactory] A day is 20 minutes. Hunger empties in 40 min walking, 18 running.
                  Thirst in 25 min walking, 9 running.
[SurvivalFactory] Stamina: 8.3s of sprint, 6.7s to refill (after a 1.0s pause), 8 per jump,
                  sprint needs 20 to start.
[SurvivalFactory] Warmth settles at 100 by day, 40 at night, 0 in the sea -
                  91s to freeze in the water, 29s to recover out of it.
[SurvivalFactory] Empty costs 0.8 hp/s hungry, 1.2 thirsty, 1.5 freezing:
                  83s from full health on thirst alone, 29s with all three.
```

The report exists because the stored numbers are unreadable. Nobody has an opinion about 0.067 points
per second; everybody has an opinion about "you get thirsty in 25 minutes and a day lasts 20". Like
`ItemFactory`, it creates and never overwrites — once the asset exists, the numbers belong to whoever
is tuning them.

#### Sprinting, and who tells whom

`PlayerMotor` reads stamina to decide whether a sprint may **start** and reports back whether one is
actually **happening**. Both halves are needed and neither can be inverted: only the motor knows the
difference between holding shift and moving forward while holding shift, and only the stats know
whether there is anything left to spend.

The gate has hysteresis — starting needs a real reserve (20), continuing needs anything above zero.
One threshold for both would let a drained player sprint for a single tick, stop, and do it again
forever.

Stamina is read inside a predicted tick from a SyncVar rather than from reconciled state, which is the
same trade `IsImmobilized` already makes: a mispredicted tick at the moment stamina runs out is
corrected by the next reconcile, and putting four survival floats into every replicate would cost more
bandwidth than a rare one-tick correction is worth. The jump cost and the sprint report are both
applied on the server only, so a replayed tick during reconciliation cannot charge for the same jump
twice.

Send intervals are per bar: half a second for hunger, thirst and warmth, which move by less than a
point in that time, and a tenth for stamina, which drains in eight seconds and is watched while it
happens.

#### The HUD says nothing until it has something to say

Four bars in the bottom-left. Above the low threshold a bar draws dim and unlabelled; at or below it,
it brightens, changes colour and shows its name. That is the presentation half of "never tedious" —
the HUD is quiet until one of them is a problem, and then it is not quiet at all. Stamina is the
exception and stays readable whenever it is not full, because it is a number you watch rather than a
warning you wait for.

Adding these caught a real gap: the HUD object was not in the Bootstrap scene at all.
`CreateBootstrapScene` rebuilds that scene from an empty one, so the `EnsureHud` entry point that
added it in #106 was silently undone the next time the generator ran, and `-hudTest` had been printing
nothing. The HUD is now created by the generator itself, which is where anything that must survive a
regeneration belongs.

#### Proving it

`-statTest` runs eighteen checks on the server, measuring rates against the clock rather than trusting
them — a drain silently running at twice the profile's rate would look correct in every individual log
line.

```
EscapeWithYourFriends.exe -batchmode -nographics -host -port 7803 -playerKey test:host -statTest -botMove
```
```
[SurvivalTest] start: food 100 water 100 stam 100 warm 100 at 06:43, day 0.
[SurvivalTest] 18 passed, 0 failed. end: food 100 water 60 stam 43 warm 16 [freezing], health 92/100
```

The checks cover: everything starts full; hunger drains at the profile's rate; thirst drains faster
than hunger; stamina can be emptied, gates a sprint at both thresholds, recovers on its own after the
delay and cannot overfill; warmth can be driven to zero, costs health while held there, recovers
toward the environment once released, and reaches zero only in the sea; dehydration costs about
1.2 hp/s measured against the clock; it downs you rather than killing you outright; and eating and
drinking put the bars back without overfilling.

The motor-to-stats path is proven separately, by watching a bot that sprints for half of every
eight-second cycle:

```
[SurvivalStats] host owner 0: food 100 water 100 stam 92 warm 100
[SurvivalStats] host owner 0: food 100 water  99 stam 47 warm 100
[SurvivalStats] host owner 0: food  99 water  99 stam 80 warm 100
[SurvivalStats] host owner 0: food  99 water  98 stam 35 warm 100
[SurvivalStats] host owner 0: food  99 water  97 stam 23 warm 100
```

Stamina cycles with the sprint and refills between bursts, and water is visibly ahead of food — which
is the tuning intent, in the log, without anybody reading the asset. On a separate client process the
same body reads the same way, from replicated state alone:

```
[SurvivalStats] client owner 0: food 99 water 99 stam 87 warm 100
```

Zero exceptions on either side.

### Eating, drinking, and being drunk

The acceptance criterion for consumables is that they hook into "the same `BuffDef` system the casino
alcohol will use", which is a statement about layering rather than about food. So nothing in this
system knows what a coconut is.

`BuffDef` is a bag of numbers with a duration. What applies it — a coconut, a bandage, a bottle of rum
in M6, a native's poison dart in M4 — is somebody else's problem. Three kinds of number live on it and
they are genuinely different:

| Kind | When | Example |
|---|---|---|
| **Instant** | Once, on landing | A bandage's first eight points of health |
| **Per second** | Every second for the duration | A bandage's slow heal, alcohol's slow dehydration |
| **Multipliers** | Continuously, while active | Speed, damage taken, stamina cost |

The multipliers scale rather than add, multiply together when several buffs are active, and are all 1
by default so a buff that does not care about them costs nothing to declare.

There is a sixth buff, `drunk`, that nothing in the game hands out. It exists so that M6's alcohol is
an asset and not a system, and so that the multipliers and the screen haze are exercised by something
before then. Its `haze` is a plain 0..1 float the casino will drive a URP Volume from; nothing reads
it yet, and that is deliberate — a post-processing stack built now would be built against a guess.

#### Stacking is three different rules

- **Refresh** — reset the timer, one entry. Two bandages in a row give one bandage's worth of healing,
  twice, rather than a stacked double heal over the same window.
- **Stack** — side by side, effects multiplied. Three drinks are three drinks.
- **Ignore** — while one is running, a second does nothing. This is what stops a panicking player from
  burning four bandages in three seconds for the effect of one.

`Apply` returns whether anything happened, and Ignore is exactly why: a refused buff means the item
should stay in the bag. Charging somebody for a bandage that did nothing is a bug, not a lesson.

#### A second catalog, on purpose

`BuffCatalog` is the same shape as `ItemCatalog` and follows the same rules — sorted by id, index 0
means none, the sort order is the wire format, nothing persists an index. An active buff crossing the
wire is `{ushort index, uint endTick}`.

The end is a **network tick**, not a local timestamp, for the same reason `Health`'s bleed-out is one:
every peer counts down to the same moment and the HUD shows a number your friends agree with.

Two catalogs rather than one shared registry because the lists have different lifetimes and different
authors. Items are content somebody adds all afternoon; buffs are a smaller set that mostly changes
when a system does. Sharing an index space would mean adding a coconut renumbered every buff in flight.

Active buffs are a `SyncList` rather than a fixed array — the opposite choice to inventory slots, and
for the opposite reason. Slots have a natural capacity and are usually occupied; buffs have no
capacity and the common case is zero, so a fixed twenty-entry array would replicate twenty empties per
player to describe nothing happening.

#### Using something takes time

A bandage is three seconds of standing still, and that is the entire reason bandages are interesting:
the decision is not "do I have one" but "do I have time". The timer runs on the server, and being
punched, tased, downed or knocked over cancels it.

**The item is spent at the end of the use, not the start.** Being interrupted costs you the seconds
and not the bandage, which is the version of this rule that does not make players furious. It also
means a cancelled use cannot duplicate anything, because nothing has left the inventory yet. And the
slot is re-read when the use finishes rather than remembered from when it started — three seconds is
long enough for the stack to have been moved, split or dropped.

The client sends "use slot N" and nothing else: not which item, not which effect, not how much it
heals. The server reads its own copy of the slot.

#### What an item leaves behind

`ItemDef` gained three fields — an effect, a use duration, and what is left afterwards. Drinking a
water bottle leaves an `empty_bottle`, which is a real item created by `BuffFactory` rather than by
`ItemFactory` because it only exists as the other half of drinking, and because M4's water filter is
the thing that will turn it back.

That created item forced one small piece of plumbing: an item missing from the catalog cannot be
carried at all, since the index *is* the wire format, so `BuffFactory` rebuilds the item catalog when
it creates the bottle. Leaving it for the next `ItemFactory` run would mean a bottle that vanishes
when you drink from it, on a rebuild nobody would think to do.

#### Where the multipliers actually land

Three systems read them, and none of them had to be redesigned to do it:

- `Health.TakeDamage` scales the incoming amount. The attacker resolved a hit for a fixed number;
  whether the victim is drunk enough not to feel it is the victim's business, and putting the
  multiplier here means every damage source in the game gets it for free.
- `PlayerMotor` scales the target speed, read inside a predicted tick from replicated state — the same
  trade stamina already makes.
- `SurvivalStats` scales the sprint cost.

#### Adding a consumable is a data change

```
Unity.exe -quit -batchmode -nographics -projectPath . \
  -executeMethod EscapeWithYourFriends.EditorTools.BuffFactory.Build
```
```
[BuffFactory]   1  bandaged        15s  Ignore   speed x1.00  dmg x1.00  haze 0.00
[BuffFactory]   2  coconut_water   12s  Refresh  speed x1.00  dmg x1.00  haze 0.00
[BuffFactory]   3  cooked_meal     45s  Refresh  speed x1.00  dmg x1.00  haze 0.00
[BuffFactory]   4  drunk           90s  Stack    speed x0.88  dmg x0.75  haze 0.50
[BuffFactory]   5  hydrated         0s  Refresh  speed x1.00  dmg x1.00  haze 0.00
[BuffFactory]   6  well_fed        20s  Refresh  speed x1.00  dmg x1.00  haze 0.00
[BuffFactory] 6 buff(s) in the catalog, 6 created just now; 5 item(s) are consumable.
```

Like `ItemFactory`, it creates and never overwrites — a buff somebody has tuned is theirs. The one
thing it *does* re-apply on every run is the link from an item to its effect, because that is
structure rather than balance: a coconut with no effect is a missing reference, not a design decision.

#### Proving it

`-buffTest` runs 35 checks on the server. It deliberately does both halves of the acceptance criterion
through the same code path — it eats a coconut, and then it applies the `drunk` buff that nothing in
the game hands out. If the second works through `BuffState.Apply` with no consumable involved, M6 is
an asset rather than a system.

```
EscapeWithYourFriends.exe -batchmode -nographics -host -port 7812 -playerKey test:host -buffTest
```
```
[BuffTest] start: no buffs, food 100 water 100 stam 100 warm 100
[BuffTest] 35 passed, 0 failed.
  end: no buffs | 4/20 slots, 3.3/40kg [0:coconut, 1:bandage, 2:empty_bottle, 3:plank]
```

The end state is itself the evidence for two rules: the bandage survived being interrupted, and the
empty bottle is what the full one left behind.

The checks cover: a fresh player has nothing and all three multipliers read 1; five items are
consumable and a plank is not; using a coconut starts a timed use, does not spend it immediately,
finishes, raises both hunger and thirst and leaves a running buff; a cancelled use costs the time and
not the item and applies nothing; Ignore refuses a second application and does not extend the first;
Refresh applied twice stays one entry; Stack applied twice is two entries whose multipliers multiply;
`drunk` slows you, softens hits and hazes the screen; `Health` actually scales incoming damage (20
asked for, less landed); a buff can be ended early; drinking a bottle leaves an empty one; a plank, an
empty slot and a nonexistent slot are all refused; and clearing everything returns the multipliers to
neutral.

On a separate client process, reading replicated state alone:

```
[BuffState] client owner 0 sees: coconut_water 10.7s, bandaged 13.7s, drunk 88.7s
[BuffState] client owner 0 sees: no buffs
```

Three different buffs with three different remaining times, on a machine that applied none of them.
And on the HUD, from the same state the canvas draws:

```
[HudRoot] -hudTest host: bars food 76 water 80 stam 100 warm 100 | coconut_water 11.7s, bandaged 14.7s, drunk 89.7s
```

Zero exceptions on either side.

**Not done here:** use *animations*. `ItemUse.Progress` is replicated so an animator can be driven off
it on every peer, but there is no rig to drive yet — that belongs with the art pass, not with a
system that would have to guess at it now.

### Making things

**A recipe is a `RecipeDef`: inputs, one output, a station and a number of seconds.** The output is
either an item *or* a structure, never both, and that distinction is what turns a list of recipes into
a progression - the campfire you build is the station the next recipe needs.

The tier-1 set, seeded by `RecipeFactory` and sorted by id into `Recipes.asset`:

| station | recipe | costs | makes |
|---|---|---|---|
| hand | `bandage` | 2x cloth | bandage |
| hand | `rope` | 3x cloth | 2x rope |
| hand | `torch` | plank + cloth + flint | torch |
| hand | `knife` | scrap_metal + rope | knife |
| hand | `campfire` | 4x plank + flint | **Campfire** |
| fire | `cook_fish` | fish_raw | fish_cooked |
| bench | `bottle` | scrap_metal + cloth | empty_bottle |
| bench | `hatchet` | 2x scrap_metal + plank + rope | hatchet |
| bench | `fishing_rod` | 2x plank + 2x rope | fishing_rod |
| bench | `water_filter` | 2x plank + 2x cloth + scrap_metal | **WaterFilter** |
| bench | `crafting_bench` | 6x plank + 2x rope | **CraftingBench** |
| filter | `fill_bottle` | empty_bottle | water_bottle |

The campfire is craftable *by hand* on purpose. Everything else is gated behind a station, but gating
the first structure behind a bench would put the whole progression behind a walk back to camp. One
bench is given rather than crafted, standing at the camp as the `camp.bench` POI, for the same reason:
the first thing you need a bench for is building a second one somewhere else.

**Stations are proximity, not interaction.** `CraftingStation` is not an `IInteractable`. Walking up to
a bench presses no button; the bench simply exists, and `Crafting` asks whether one of the right kind
is within its radius when a recipe is requested. Two consequences, both wanted: an `Interact` prompt
would promise a UI that does not exist until #46, and four players can share one bench without
queueing for it. Live stations are held in a static list rather than found by physics - there are
single digits of them in a session and they never move, so a sphere cast per craft attempt would be
work for nothing.

`Crafting` is the same shape as `ItemUse`, because it is the same gesture - stand still for a few
seconds and something happens - and the two rules that matter were learned there:

- **Inputs are taken at the end, not the start.** Being interrupted costs the seconds and not the
  materials, and a cancelled craft cannot duplicate anything because nothing has left the bag yet.
- **Everything is re-checked when the timer finishes.** Eight seconds is long enough for a friend to
  have taken the planks, for the bench to have been destroyed, or for you to have walked off it.

Two more rules that only crafting needs. `HasRoom` is asked *before* anything is taken, in weight and
slots and mergeable space, because taking four planks and then discovering the hatchet does not fit is
how a player loses four planks and gets nothing. And a structure is dropped onto the ground by a
raycast from three metres up rather than left at the player's feet, because a campfire spawned at hip
height on a slope ends up either floating or buried.

A client sends a `ushort` recipe index and nothing else - not what it costs, not what it makes, not
whether it is standing at a bench. The server reads its own catalog, the same doctrine as `ItemCatalog`
and `BuffCatalog`.

**The campfire is what makes warmth solvable.** It carries a `HeatSource`: 12 warmth per second within
five metres, pushed through the same `SurvivalStats.ServerFeed` door a coconut goes through. Survival
left night at a warmth target of 40 - cold, and worth solving, with nothing yet to solve it with. This
is the answer, and it is why the campfire is in the progression rather than in the decoration.

**Verified headless**, on the island so the camp's bench exists:

```
EscapeWithYourFriends.exe -batchmode -nographics -host -port 7796 -playerKey test:host -scene island -craftTest
```
```
[CraftingTest] 12 recipes, 1 bench(es), 0 fire(s), 0 filter(s) in the world.
[Crafting] Player(Clone) built Campfire at (-79.5, 4.9, -47.0).
[Crafting] Player(Clone) built WaterFilter at (-81.5, 4.9, -50.7).
[CraftingTest] 43 passed, 0 failed.
  end: 5/20 slots, 5.4/40kg [0:bandage, 1:cloth x2, 2:fish_cooked, 3:water_bottle, 5:hatchet] | idle
```

The end state is the progression itself, one item per station: the bandage was made by hand, the fish
was cooked at a fire that did not exist when the session started, the hatchet came off the camp's
bench, and the full bottle came out of a filter the player built. The two cloth are the refund that
never happened - they are what a cancelled craft did not spend.

On a second process, which built none of it:

```
[CraftingStation] client sees a Bench at (-78.0, 4.9, -52.0), radius 4.5m.
[CraftingStation] client sees a Fire at (-79.5, 4.9, -47.0), radius 4.0m.
[CraftingStation] client sees a Filter at (-81.5, 4.9, -50.7), radius 3.5m.
```

The same three coordinates, to the decimetre: the bench the island came with, and the two structures
the other player built while it watched. Zero exceptions on either side.

The test walks the actual progression rather than crafting one thing and calling it proven: a bandage
by hand, then a campfire by hand, then raw fish cooked *at the fire that was just built*, then a walk
to the bench for a hatchet and a water filter, then an empty bottle filled *at the filter that was just
built*. Every step past the first is only possible because the step before it existed.

The checks also cover the rules that are invisible in a log: that the cloth is still in the bag while
the timer runs, that a cancelled craft spends nothing and makes nothing, that a second craft cannot
start on top of the first, that a fire recipe is refused with no fire nearby and a bench recipe from
across the island, that index 0 is nobody and every recipe round-trips through its index, and that the
fire actually warms - eight points of warmth in one second, which the 3.5/s ambient recovery cannot do
on its own.

### Chests

**A chest is thirty slots that nobody owns.** `Storage` holds a `SyncList<ItemStack>` replicated to
everybody who can see it - not gated on an owner the way an inventory is, because a shared chest in a
co-op game is shared. Two of them stand at camp, eight metres out in the gap between two of the four
spawn points, because "the food chest and the parts chest" is a thing four players will agree on in
about a minute and one chest gives them nothing to agree about.

**The whole issue is duplication under concurrent access**, and it is worth being precise about what
that means here. It is not a threading race: the server processes one RPC at a time on one thread, so
nothing interleaves mid-method. It is a *stale view* race. Two players are looking at the same
replicated slot, both press take, and both requests arrive describing a pile only one of them can
have. Three rules answer it, and every transfer obeys all three:

1. **A request names a slot and a count, never an item.** The server reads its own slot. The second
   player to arrive reads what the first one left, which is usually nothing.
2. **Take, give, return the remainder - in one server call.** Nothing runs between those lines, so the
   return cannot fail: the room it needs is the room the take just made.
3. **Nothing is ever added before it is removed.** Every unit exists in exactly one place at every
   line of every method.

Rule 2 is there for the *other* half of the bug, which is not duplication but deletion - items voided
because the destination was full and nobody put them back. That one is easier to write and much harder
to notice, so the harness checks conservation on every transfer: the total across the chest and both
bags must not move in either direction.

**Full hands put something in; empty hands take something out.** The chest is an `IInteractable`, and
this is where it differs from the crafting bench, which deliberately is not one. A bench has nothing
useful to do on a keypress until there is a UI to open; a chest does. Interact deposits the selected
hotbar stack, and with nothing selected it hands back the last thing in there. That is a complete loop
- dump your loot at base, take it back out - with no screen to draw, which makes #46's UI a better way
to do the same thing rather than the only way to do it at all.

The chest has no weight limit. A bag is a decision about what to carry; a chest is where you stop
making that decision, and a container that could be overloaded would just be a worse bag. The bag at
the other end of a transfer *is* weight-limited, which is why a withdrawal can move less than was
asked for and why the remainder has to go back.

**Verified headless**, two processes, on the island so the camp's chests exist:

```
EscapeWithYourFriends.exe -batchmode -nographics -host -port 7803 -playerKey test:host -scene island -chestTest
EscapeWithYourFriends.exe -batchmode -nographics -client -address 127.0.0.1 -port 7803 -playerKey test:c1 -chestTest
```
```
[StorageTest] StorageChest (camp.chest.b): 0/30 slots [], two bags of 20 and 20 slots.
[Storage] Player(Clone) stored 3 item(s) in StorageChest (camp.chest.b); 1/30 slots used.
[Storage] Player(Clone) took 3 item(s) out of StorageChest (camp.chest.b); 0/30 slots used.
[StorageTest] 29 passed, 0 failed. end: 2/30 slots [0:rope x4, 1:plank x9]
```

The race is not approximated. Two clients pressing take in the same tick arrive at the server as two
calls, back to back, with nothing between them, so the test makes exactly that call twice on the same
slot - and anything that survives it survives the real thing. Ten rope in one slot, both players ask
for ten: one gets ten, the other gets zero, and ten still exist. Twelve planks, both ask for seven:
seven then five. A bag too heavy for one more flint takes nothing and the flint stays in the chest. A
chest with thirty full slots swallows nothing and the rope stays in the bag.

From the second process, which asked for none of it:

```
[Storage] client sees StorageChest(Clone): 1/30 slots [0:rope x10]
[Storage] client sees StorageChest(Clone): 0/30 slots []
[Storage] client sees StorageChest(Clone): 1/30 slots [0:plank x12]
[Storage] client sees StorageChest(Clone): 1/30 slots [0:plank x5]
```

The pile of ten leaves once. The pile of twelve becomes five. Seventy-nine states in all, ending on
`2/30 slots [0:rope x4, 1:plank x9]` - character for character what the server said. Zero exceptions
on either side.

**Not done here:** the chest UI, and locks. Both are later issues (#46, #47); a chest that can be
locked is a social mechanic and this one is deliberately not.

### The bag on screen

Five squares across the bottom, and everything else behind Tab.

**Two canvases, and only one of them has a raycaster.** The always-on HUD - squad list, survival bars,
objective, hotbar - deliberately has none, because a full-screen `GraphicRaycaster` is the classic way
a HUD quietly eats the click that was meant to swing a fist. `InventoryScreen` builds its own canvas
above it, brings its own raycaster and its own `EventSystem`, and takes the mouse on purpose: opening
it calls `PlayerInputReader.SetUiOpen`, which frees the cursor and stops the world reading input at
all. Closing gives both back. There is no in-between state where the mouse is doing two jobs.

The `EventSystem` is created by the screen rather than baked into `Bootstrap.unity`, and it uses
`InputSystemUIInputModule` rather than the legacy `StandaloneInputModule` - with the new input backend
the old module gives you a screen that draws perfectly and ignores the mouse entirely.

**What a drag means, in one rule.** The stack under the cursor goes to the slot you released it on.
Same container and the server merges or swaps; different container and it is a transfer. Shift moves
half. That grammar is identical on the bag grid and the chest grid, so there is nothing to learn
twice, and a click with no drag does the obvious thing instead: on one of the first five bag slots it
selects the hotbar slot, and on a chest slot it takes the stack.

**Nothing on screen decides anything.** Every drop is a request - `MoveSlot`, `SplitSlot`,
`RequestStore`, `RequestTake` - and the screen redraws from replicated state on the next frame. A
refused move simply does not happen and the squares snap back; there is no optimistic local copy to
get out of step, which is the same reason the HUD has never had an RPC behind it.

`RequestStore` and `RequestTake` live on `Inventory` rather than on `Storage` because **a chest is
owned by nobody** - a `ServerRpc` on it would have no owner to require. The bag has one, so the
request comes from the player and names the chest, and the server checks the player is actually
standing at it (`Storage.Reach`, five metres). The UI only ever shows a chest within reach, but that is
a courtesy, not a check: the request names a chest by `NetworkObject`, so a client could name any
chest on the island.

**No icons, on purpose.** There is no item art yet, so a slot draws the item's name and its count. A
grid of identical grey squares would be prettier and completely unusable; the `Icon` image is already
in `SlotView` waiting for sprites. The tooltip carries the numbers a player actually decides with:
weight each and for the stack, the stack limit, and for a consumable what using it restores and what
it leaves behind.

Weight is on the HUD above the hotbar, not only inside the bag screen, because "am I about to be
overloaded" is a question you ask while picking things up rather than while sorting them.

**Verified headless**, which for a UI needs saying plainly. A build with no graphics device has no
canvas at all, so **the visual half of this - layout, legibility at 1080p, whether dragging feels
right - is a playtest item and belongs to the #29 gate.** What a terminal can check is everything the
screen is made of that is not pixels, and `-uiTest` checks all of it:

```
EscapeWithYourFriends.exe -batchmode -nographics -host -port 7815 -playerKey test:host -scene island -uiTest
```
```
[UiTest] 16 items in the catalog, bag of 20 slots and 40 kg.
[Inventory] Player(Clone) asked for a chest 202m away; refused (reach is 5m).
[UiTest] 23 passed, 0 failed. end: 24 / 40 kg | 1/20 slots, 24.0/40kg [9:plank x12]
```

All sixteen items produce a tooltip that names them, weighs them and - for the consumables - says what
using them does, which matters more than it sounds: with no icon art, an item whose tooltip is blank is
an unidentifiable grey square. The weight readout reads zero when empty and says `overloaded` when it
is. The hotbar wraps in both directions, which is the wheel's entire behaviour. A drag from bag to
chest stores ten rope, a drag of part of it back takes four and leaves six, and the same request made
from two hundred metres away is refused by the server with the line above - the one refusal in the log
is the deliberate one.

Dragging inside the bag moves a stack to an empty slot, splits it in half, and merges the halves back
together. Zero exceptions.

**Not done here:** icons, an equipped-item model in hand, and controller navigation. The acceptance
asks for a keyboard/mouse layout and that is what this is; a gamepad needs focus movement and a cursor
that is not a mouse, which is its own issue.

### Money

**Per-player, not a shared pot.** A shared wallet sounds friendlier and is worse: half the comedy of
this game is one player refusing to pay to revive another.

`Wallet` is a `SyncVar<int>` that only the server writes. Clients observe, the same shape as `Health`.
A modified client can lie about its balance on its own screen all it likes; the purchase still goes
through `ServerTrySpend` on the host.

**"A client cannot mint money" is kept true structurally, not by checking.** There is exactly one
client-callable money verb in the game - `RequestPay`, handing some of yours to somebody else - and it
cannot create a coin by construction: it takes from the sender before it gives to the receiver, in one
server call, with nothing between the two lines. The same rule as the chests in #44, for the same
reason - a give-then-take would create money for a frame, and a frame is long enough to be a
duplication bug. Everything else that moves money is server-side and is called by something that
already decided a sale, a payout or a charge was earned.

That verb exists partly because it is a real co-op gesture - chipping in for a revive, paying somebody
back for the boat - and partly because it is the honest test of the criterion. A door nobody can open
proves nothing; this one is open, and still cannot mint.

**Every mutation carries a reason and is counted.** `Wallet.Minted` and `Wallet.Burned` are the total
money created and destroyed on this server since it started, including the starting balances, because
a starting balance is money made from nothing and a ledger is only useful if it is honest about that.
A transfer touches neither counter. With them, "did anything create money this session" is a question
with a number for an answer rather than an opinion - which is what the harness asks, and what a live
session can be asked once the casino (#64) is paying out.

The balance shows bottom-right, opposite the survival bars: those are the things killing you and this
is the thing that gets you off the island. It flashes green on income and red on spending, because a
number that changes silently is a number nobody notices changing, and "did that sale go through"
should never be a question.

**Verified headless**, two processes, because the criterion needs two wallets:

```
EscapeWithYourFriends.exe -batchmode -nographics -host -port 7820 -playerKey test:host -moneyTest
EscapeWithYourFriends.exe -batchmode -nographics -client -address 127.0.0.1 -port 7820 -playerKey test:c1
```
```
[MoneyTest] two wallets, $1,000 and $200, $1,200 in the world.
[Wallet] Player(Clone) paid Player(Clone) 300; 700 left, they now have 500.
[Wallet] Player(Clone) paid Player(Clone) 200; 500 left, they now have 700.
[Wallet] Player(Clone) -500 (test purchase), now 0.
[Wallet] Player(Clone) +250 (test sale), now 250.
[MoneyTest] 29 passed, 0 failed.
  end: $250 and $700, $950 in the world, 250 minted, 500 burned.
```

The last line is the whole acceptance in one arithmetic check: 1200 - 500 + 250 = 950. The test does
not go hunting for exploits, because a list of exploits is only ever as good as the imagination that
wrote it. It checks **conservation** - the total across every wallet, before and after everything a
client is allowed to ask for. Minting shows up as that number going up with nothing on the server
having decided it should.

Paying more than you have, a negative amount, zero, yourself, and a wallet that is not there are all
refused, twice: once at the client-facing door before a message is sent, and again on the server which
does not trust that the first check happened.

**Not done here:** where money comes from. Selling loot is the shop (#48), fish is #54, and a lucky
spin is #64. All three are callers of `ServerAdd`, which is why this issue is small and they are not.

### The shop

*Issue #48. `Data/ShopDef.cs`, `Economy/ShopCounter.cs`, `Economy/Trading.cs`,
`Editor/ShopFactory.cs`, `Economy/ShopTest.cs`, and the third panel in `UI/InventoryScreen.cs`.*

The trader stands at `shop.counter`, 95 m of walking from the camp, and is the first place the money
from #47 has anywhere to go. Twelve lines on the shelf: six materials the island gives away anyway,
five made things that are craftable if you would rather walk to the bench, and the boat part everyone
is actually saving for.

**The shop only lists what it sells.** Selling *to* it needs no list at all - anything with an
`ItemDef.Value` above zero is taken at `BuyBackFraction` of that value, half by default. A table of
everything the trader would buy goes stale the moment somebody adds an item, and it goes stale
invisibly: a new item that quietly cannot be sold. A fraction of the item's own value cannot. It also
puts the entire economy in one number - below one it is a spread the player pays for the convenience
of a shop, at one the shop is a storage box that pays you to use it - and that number is what #56
will argue about with real playtest numbers.

A boat part is worth zero, so the trader will not take it back. That is deliberate: the win condition
is not a thing you can flip for cash.

**The acceptance is a race.** "Buy/sell round-trips correctly with 4 players shopping simultaneously"
is #44's chest problem wearing a different hat - four clients looking at a replicated stock of one,
all clicking buy, four requests describing a hatchet that only one of them can have. The rules are
the same three, in the same words:

1. **A request names an offer index and a count, never a price.** Look at `Trading.RequestBuy` -
   there is nowhere to put a number. A modified client can ask for the wrong thing, or for nine
   hundred of it, and both are tested; it cannot ask for a discount, because the wire has no field
   for one.
2. **Take before give, in one server call.** Stock comes off the shelf, then the money leaves the
   wallet, then the item goes in the bag.
3. **Nothing is created that was not destroyed.** Whatever did not fit goes back on the shelf and back
   in the wallet on the very next line - and the money goes back through `Wallet.ServerRefund`, which
   *decrements* `Burned` rather than incrementing `Minted`. A refund is not income, and a ledger that
   called it one would report the shop as printing money every time somebody's bag was full.

Everything clamps rather than refuses. Asking for five when there are two, or when you can afford
three, buys what is possible. A shop that refuses the whole order because one of the numbers was
optimistic is a shop nobody uses twice.

**`Trading` lives on the player, not on the counter.** A shop is owned by nobody, so a `ServerRpc` on
it would have no owner to require and would accept a message from anyone in the game. The bag has an
owner, so the request comes from the player and *names* the counter, and the server checks the player
is standing within `ShopCounter.Reach` of it - twice, once in `Trading.Resolve` and again inside the
transaction, because a check that exists in only one place is a check somebody will refactor away.
Refusals come back as a `TargetRpc` to the one client that asked; the rest of the squad does not need
to know you cannot afford a hatchet.

Stock is a `SyncList<int>`, so the other three players watch the shelf empty as somebody else buys.
That is the only thing that makes the race legible to a player rather than mysterious. One of each
depleted line comes back every ninety seconds - slow on purpose, because a shelf that refills
instantly is a shelf with no stock, and the point of a limited line is that the group has to decide
who gets the hatchet.

**On screen** the shelf takes over the chest's rectangle in the bag screen, because you are never at
both at once. Twelve rows, each with a name, a price and what is left. The click grammar is one rule:
**left is here, right is over there.** Left-clicking a bag slot picks it for the hotbar; right-clicking
one sends it into the chest, or across the counter to be sold. A shelf row buys one, or five with
shift.

`-shopTest` is the harness, and it needs two real processes. Sixty-five checks, all passing:

```
[ShopTest] ShopCounter (shop.counter): rope@10, plank@8, ..., knife@60 x2, hatchet@90 x1, boat_part@400 x4
[ShopCounter] Player(Clone) bought 1x hatchet for 90; 0 left on the shelf.
[ShopTest] the server's answer to that: "a counter 200m away".
[ShopTest] 65 passed, 0 failed. end: alice $984, bob $1,000, 4 minted, 20 burned.
```

Four buy calls back to back on one thread against a shelf of two sell exactly two, leave the shelf at
zero rather than at minus two, and charge nobody for a knife they did not get. A bag filled to its
weight limit buys nothing, is charged nothing, and puts the knife back. And the ledger identity -
wallets, minus minted, plus burned - is asserted after every single step, which is what makes "did the
shop print money" a question with a number for an answer rather than an opinion.

### Weapons

*Issue #49. `Data/WeaponDef.cs`, `Data/WeaponCatalog.cs`, `Combat/Weapon.cs`,
`Editor/WeaponFactory.cs`, `Combat/WeaponTest.cs`.*

The acceptance is one sentence - *"a new weapon is one `.asset` file plus a prefab, no new code"* -
and it is a sentence about what does **not** exist. So this issue mostly deleted things.

**One definition type, not two.** `WeaponDef` replaced `MeleeWeaponDef` outright rather than sitting
beside a new `GunDef`. A second definition type means a second component that reads it, a second
equip path, and a second place to forget the aim check - and the first person to add a crossbow would
have needed a third of each. The honest cost of one type is a handful of fields that only one kind
uses, and that cost is paid in the inspector and nowhere else. `Weapon` likewise absorbed
`MeleeAttack`: there is exactly **one branch on `WeaponKind`**, inside `ServerResolve`, and it picks
between a cone and a ray. Everything on either side of it - the cooldown, the incapacitation gate,
the aim validation, the damage application, the observer RPCs - is shared.

A melee weapon states a cooldown; a gun states rounds per minute, because "rounds per minute" is a
strange thing to say about a bat. Both arrive at `WeaponDef.Cooldown` as the same number.

**Holding it is equipping it.** `WeaponDef.Item` points at the `ItemDef` the weapon *is*, and the
server reads the selected hotbar slot every frame, asks `WeaponCatalog.ForItem` what that is, and
writes the answer into a `SyncVar<ushort>`. Nobody calls an equip function; there is no equip function
to call. That is what makes the acceptance true in play rather than only in principle: point a new
asset at an item and the thing swings. An item that is not a weapon falls back to fists, so you can
punch somebody while holding a fish.

Polling rather than subscribing to `Inventory.Changed`, deliberately: what you are holding is a
*function* of the bag's state, and a function cannot go wrong because somebody forgot to raise an
event.

`WeaponCatalog` is the fourth catalog and follows the same doctrine as items, buffs and recipes -
sorted by id, rebuilt whole from the folder, index 0 means unarmed. **Index 0 is not fists**: fists
are a real asset with real numbers, and 0 is the value the slot holds before the server has decided
anything.

**Anti-cheat is unchanged from #17 and now covers guns for free.** The client sends an aim direction
and nothing else. Range, spread, cone, damage, cooldown and what was actually standing there all come
from the server's copy of the definition, so editing a local asset buys nothing. Hitscan rather than
projectiles: a projectile is a networked object with a position to reconcile, and none of these guns
are slow enough for anyone to see the difference. Where the tracer is drawn is a lie the client tells;
where the damage landed is the server's raycast.

`WeaponFactory` seeds seven. Five came with #49 - fists, knife and hatchet off items that already
existed, a machete that needed a new one, and a pistol - which is enough that both branches are real
and tested. #50 added the bat and the shovel, and the whole cost of doing so was two rows in
`Seeds` and two rows in `ItemFactory`: no component was edited, and the acceptance of #49 was
cashed rather than argued about. #51 adds the guns the same way.

`-weaponTest` is the harness, and it needs two processes. It never names a weapon: it walks the
catalog by index, puts each one in the hand, and asserts the damage that landed equals what *that
asset* says. A hard-coded ten damage in `Weapon` would survive a "does punching work" test and die
here. **27 checks, all passing:**

```
[WeaponTest] catalog holds 5 weapon(s): fists, hatchet, knife, machete, pistol
[WeaponTest] hatchet at 1.2m: 1 hit(s), 34.0 damage, asset says 34.0.
[WeaponTest] pistol at 30m: 1 hit(s) in 4 shot(s), 26.0 damage.
[WeaponTest]   hatchet t1 melee 34dmg 0.75s 2.2m cone 50deg x2  -> 34 damage at 1.1m
[WeaponTest]   knife   t1 melee 22dmg 0.35s 1.8m cone 35deg x1  -> 22 damage at 0.9m
[WeaponTest]   machete t2 melee 40dmg 0.60s 2.6m cone 70deg x3  -> 40 damage at 1.3m
[WeaponTest]   pistol  t1 gun 26dmg 300rpm 1x1.5deg 60m mag 12  -> 26 damage at 20.0m
[WeaponTest] 27 passed, 0 failed.
```

Three of its first failures were the harness lying rather than the code being wrong, and each is
worth writing down because the next harness will meet them again:

- **`Inventory.ServerSelect` wraps modulo the five hotbar slots** rather than clamping, because a
  scroll wheel that sticks at the end of the row feels broken. A sixth item in the bag therefore
  selects the first, and the test measured a hatchet while believing it held a machete. The bag is
  now emptied between weapons.
- **`Health` ignores damage for two seconds after a spawn**, so nobody is killed on the pad they
  arrived on. The first swing landed inside that window and reported a working hatchet as dealing
  zero. The test now swings until one lands rather than sleeping a magic number.
- **A greybox arena has walls in it.** A victim placed thirty metres away had a building seven metres
  along the line, and a working pistol was reported broken. The test now probes twelve bearings for a
  clear lane before placing anybody.

And one that was the design working: a 1.5-degree spread throws a single pellet about 0.8 m sideways
at thirty metres, which is wider than the person being shot at. One shot missing at that range *is*
the gun. Asserting on one shot is asserting on a dice roll, so the test fires until one connects and
reports how many it took - four, in the run above.

**Not done here:** `UpgradesTo` is a link nothing follows (#52). Ammunition, reloading, recoil and
tracers arrived in #51, below.

### Melee, and where people land

#50 asked for two things: melee that feels chunky, and melee that still ragdolls people. The second
one turned out not to be true, and had not been true since M1.

**The impulse went into one bone.** `RagdollController.EnableRagdoll` found the bone nearest the
contact point and gave it the entire blow. A 2 kg forearm took 200 newton-seconds, whipped
spectacularly, and dragged the other 54 kg along by the joints - which is to say the victim flailed
and stayed almost exactly where they were standing. It reads as a hit and it is not one. It now
splits: `_localImpulseShare` (0.3) goes into the struck bone so the hit *reads*, and the remaining
0.7 is spread across every bone in proportion to its mass so the body *travels* as one piece rather
than being torn apart by its own joints.

That changed what the knockback number means, so the whole melee column was rescaled with it. The
unit is now plainly newton-seconds against a 56 kg body: 90 is a stumble, 400 is into the sea.

**The bones were never put back.** This one was hiding behind the first. Physics writes bone
transforms in world space, so a body that has been thrown leaves its skeleton splayed in *local*
coordinates too - and `DisableRagdoll` moved the root back under the hips without ever restoring the
pose. An Animator running a clip overwrites bone transforms every frame, so with animation the bug is
invisible. The greybox player has no clips. The symptom was memorable: **the first throw of a session
worked perfectly and every one after it did nothing**, because the root was standing at z=7 while the
skeleton lay knotted at z=12, and the next hit went into a pile of bones interpenetrating each other
and the floor, where depenetration ate it. `SetRagdollInternal` now restores the rest pose captured in
`Awake` whenever the body stands up. This was a real bug for every character without animation, which
is every character until the art pass.

**Nothing listened for a landed hit.** `PlayerCameraRig` shook on `OnHealthChanged` - when you *took*
damage. Connecting with a swing felt identical to whiffing one. The rig now subscribes to
`Weapon.HitLanded` and kicks by `weapon.Hit.Knockback * _landedShakePerKnockback`, capped, so a bat
shoves the camera and a knife barely moves it. That is the half of "chunky" that is code; the rest is
wind-up, stun and animation, and belongs to a playtest.

`-meleeTest` is the harness, and it needs two processes. It does not assert that an impulse was
applied - that assertion passed for the whole of M1 while nobody moved. It swings, waits a second for
the physics to actually happen, and measures how far the hips travelled horizontally, then checks
that the ordering follows the assets. **25 checks, all passing:**

```
[MeleeTest] 6 melee weapon(s): bat kb400, fists kb120, hatchet kb210, knife kb90, machete kb260, shovel kb300
[MeleeTest]   knife    kb    90 -> 1.15m, ragdolled True
[MeleeTest]   fists    kb   120 -> 1.73m, ragdolled True
[MeleeTest]   hatchet  kb   210 -> 2.39m, ragdolled True
[MeleeTest]   machete  kb   260 -> 3.29m, ragdolled True
[MeleeTest]   shovel   kb   300 -> 3.96m, ragdolled True
[MeleeTest]   bat      kb   400 -> 5.56m, ragdolled True
[MeleeTest]   a downed body took a bat and slid 5.28m
[MeleeTest] 25 passed, 0 failed.
```

Horizontal distance on purpose: a body knocked upward comes back down, and counting the arc would
flatter a weapon with a high upward bias for something nobody sees. Where they end up is what the
table laughs at. The measured curve is monotone in knockback and close to linear at about 1.3 cm per
newton-second, which is why the seeded numbers were left alone - they were measured, not guessed.

The last check is the one the bat exists for: **hitting somebody who is already down shoves them
5.28 m and does not hurt them.** `Health.TakeDamage` refuses anything that is not `Alive`, and
`Weapon.ApplyHit` still routes the impulse through `StunState`, so a downed player waiting on a
rescue can be punted across the beach without ever being killed a second time.

The comedy invariant is asserted rather than assumed: the biggest launcher and the biggest damage
must be different weapons. The bat does 14 damage and throws people 5.5 m; the machete does 40 and
throws them 3.3 m. Picking the funny one has to cost something, or it is not a choice.

**Not done here:** the wind-up is a number the resolver honours but no animation plays into (#M8),
and active ragdoll - `ConfigurableJoint` drives targeting the animated pose, so a hit makes somebody
flail *while still standing* - is still the deferred v2 described above.

### Firearms

#51 added the shotgun, the hunting rifle and the SMG next to #49's pistol, and with them the three
things a gun needs that a bat does not: **ammunition, a reload, and a kick.** Adding the guns
themselves cost three rows in `WeaponFactory.Seeds` and five in `ItemFactory` - no component was
touched, which is the third time #49's acceptance has been cashed rather than argued about.

**Magazines live on the player, per weapon.** `Weapon` keeps a server-side
`Dictionary<WeaponDef, int>` and mirrors the equipped weapon's count into a `SyncVar` for the HUD.
Per weapon rather than one counter, because a single counter would refill the pistol every time you
swapped to the shotgun and back, and the shop sells ammunition by the box. **Guns start empty**: the
magazine you are carrying is one you paid for and loaded. A reload takes rounds *out of the bag* -
they are `ItemDef`s with a weight and a price the whole way through, never a number that appears on
a HUD - and takes the weapon's own `ReloadSeconds` to do it. Swapping weapons mid-reload cancels it,
and since the rounds were never removed from the bag there is nothing to give back and no way to
duplicate anything by swapping quickly.

**One round per shot, never per pellet.** A shotgun blast is eight rays and one shell. The day
somebody writes a twenty-pellet weapon is not the day the ammunition economy should change.

**Recoil moves the aim, not the picture.** `PlayerCameraRig` subscribes to `Weapon.Fired` and calls
`PlayerInputReader.AddRecoil`, so the kick goes into the same pitch value the camera *and* the aim
origin both read - the shot after the kick genuinely goes higher. Putting it anywhere else would
have produced a camera that jumps while the bullets carry on going exactly where they were. The
pitch clamp stays in one place, so a burst from the SMG cannot walk the view past vertical. Everyone
hears a shot, because `Fired` is an observers event and tracers must be drawn on every screen, but
only the owner gets shoved by it.

**Tracers are a listener and never a source of truth.** `TracerEffect` draws one pooled
`LineRenderer` per pellet from the origin and endpoints `Weapon.Fired` carries - and those endpoints
are produced *after* the server's raycast. So a tracer can only ever draw something that has already
been decided. Where the tracer is drawn is a lie the client tells; where the damage landed is the
server's raycast.

`-gunTest` is the harness, and it runs both processes under `-latency 100` because that is the
condition the acceptance is written against - so the test reads the latency the bootstrap applied
and **fails if nobody asked for any**, rather than quietly passing a test of nothing. It also needs
`-scene arena`: it stands the victim at a computed distance at the *attacker's own height*, which on
the island's slopes buries them in a hillside, and every gun then reports landing nothing. That
failure looks exactly like a broken weapon and is not one - #66 spent three runs finding that out. **81 checks,
all passing:**

```
[GunTest] running at 100 ms of simulated latency, victim is owned by connection 1.
[GunTest] 4 gun(s): pistol 26dmg 300rpm 1x1.5deg 60m mag 12, rifle 65dmg 45rpm 1x0.2deg 150m mag 5,
          shotgun 11dmg 70rpm 8x6.5deg 35m mag 6, smg 14dmg 800rpm 1x3.0deg 45m mag 30
[GunTest]   pistol  -> reload 2.10s, 12 shot(s), 12 round(s) left in the bag
[GunTest]   rifle   -> reload 2.50s,  5 shot(s),  5 round(s) left in the bag
[GunTest]   shotgun -> reload 2.90s,  6 shot(s),  6 round(s) left in the bag
[GunTest]   smg     -> reload 2.30s, 30 shot(s), 30 round(s) left in the bag
[GunTest] shotgun at 2.5m landed 5 pellet(s) in one shot.
[GunTest] 81 passed, 0 failed.
```

The ammunition economy is checked the way the shop was in #48 - as a conservation argument. Rounds
do not appear: a gun out of the shop is empty, an empty bag reloads nothing, a full magazine refuses
to top up, a reload of a magazine of *n* removes exactly *n* from a bag stocked with 2*n*, one
trigger pull spends exactly one round whatever it throws, and a magazine of *n* lasts exactly *n*
shots and then hits nobody.

**"No client-trusted damage" cannot be proved by a test that only calls server methods**, and
pretending otherwise would be worse than not testing it. What the harness checks is the shape that
makes it true: the client's only contribution to a shot is a direction, `AimValidation` rejects one
aimed at somebody standing behind the shooter, and what landed equals what the *server's* asset says
rather than any number a caller passed in. The four guns are also checked for being four answers
rather than four skins - distinct rates of fire, distinct ranges, a magazine spread of at least 5x -
and the shotgun has to land more than one of its eight pellets on one person at close range, which
is both the reason it exists and the thing the melee dedupe would have silently destroyed.

Two harness bugs found here were worth more than the feature, because both were the *test* lying:

- **A victim must be stood up before being moved, not after.** `DisableRagdoll` repositions the root
  under wherever the hips came to rest, so clearing a stun after a teleport drags the body straight
  back to where it was lying. Since #50 a landed hit moves people metres, so the previous weapon left
  them in a heap downrange and the next one was shooting at empty arena and reporting itself broken.
- **`Health.Heal` refuses anything that is not `Alive`.** One rifle magazine is 325 damage. A victim
  killed partway through stayed a corpse for the rest of the run, and a corpse stays ragdolled
  through `ServerClearStun`, so every later teleport moved a root while the colliders stayed where
  they fell. `Reset` now revives before it heals - and because a rescue grants two seconds of
  invulnerability, the damage loops now run until health actually *changes* rather than until a ray
  connects.

Both bugs were in `WeaponTest` too, where the growing arsenal exposed them; it is back to 27/27 with
all nine carried weapons proving their own damage, and `-meleeTest` is unchanged at 25/25.

**Not done here:** the shop does not stock the new guns or their ammunition yet - the shelves are a
balance question and belong to #56 - and nothing plays a muzzle flash or a sound (#M8).

### Weapon upgrades (#52)

`WeaponDef.UpgradesTo` had been a field nobody followed since #49. #52 is what pulls on it.

**An upgrade produces a real weapon asset, not a modifier stack.** This is the decision the whole
system hangs off, and the alternative was tempting: keep one `pistol` and layer per-player
"+30% damage" on top of it. It does not work here, twice over. A `ScriptableObject` is a single
global asset, so mutating one would upgrade *everybody's* pistol, and the moment damage comes from
`asset + modifiers` rather than from the asset, #49's "what landed is what the server's asset says"
stops being checkable. So `bat_nailed` is a `WeaponDef` and an `ItemDef` like any other, and an
upgrade is a **swap**: the old item leaves the bag, the new one arrives.

Three lines, two steps each, tier 1 to tier 3 - island-one gear at the bottom, what you carry off
island two at the top:

```
bat     t1 14dmg 1.8/s  ->  bat_nailed   t2 26dmg 1.9/s  ->  bat_shark   t3 42dmg 2.1/s
hatchet t1 34dmg 1.3/s  ->  hatchet_fire t2 52dmg 1.4/s  ->  chainsaw    t3 55dmg 2.9/s
pistol  t1 26dmg 5.0/s  ->  pistol_mk2   t2 34dmg 6.0/s  ->  pistol_auto t3 40dmg 10.0/s
```

**Lines, not trees.** One `UpgradesTo` per weapon, and `UpgradeFactory` fails the build if two
upgrades share a `From`. A branching tree would need a UI to choose between branches and a reason to
regret the choice, and neither exists; a line needs neither, and "the next one" is a thing a player
can hold in their head while being chased.

**The advertised deltas are derived, never typed.** `UpgradeDef` carries `+86% damage, +6 rounds,
25% less recoil` and so on, but `UpgradeFactory` computes every one of them from the two weapons at
bake time, and `UpgradeDef.Verify()` recomputes them at runtime. Hand-typed numbers in the seed table
are one careless edit away from a shop that lies to you, and a shop that lies is a bug players
report as "the upgrade did nothing". They are **an advertisement, not an instruction** - nothing
reads them to decide damage; the weapon you receive decides that.

`IsStrictlyBetter` is the other half: a tier higher, and worse at nothing - not damage, not
knockback, not reach, not rate of fire, not magazine size, and recoil no higher. Six steps all
satisfying that predicate *is* the power curve, and it is checked on every step of every run.

**Two venues, two currencies.** A bench upgrade costs materials and no money; a trader upgrade costs
money and a little scrap. A group that has not sold anything yet can still climb both melee lines
out of what the island gives them, and the guns are what money is *for*. Which venue an upgrade uses
lives on the asset, so that is a balance decision in a data file rather than a rule in a method.

**The transaction is #44's and #48's, unchanged**: take the weapon, take the materials, take the
money, then give the new weapon - and if any step disagrees with the check that preceded it, put all
three back exactly. Money moves through `Wallet.ServerTrySpend` and `ServerRefund`, so #47's ledger
identity still holds across an upgrade. The request carries **a `ushort` index into the upgrade
catalog and nothing else**; there is nowhere in the message to put a price, a material list or a
target weapon, the same way there was nowhere in #48's.

**Loaded rounds follow the weapon.** `Weapon.ServerCarryMagazine` moves what is in the old
magazine into the new one, clamped to the new capacity and only when the calibre matches. Rounds are
not in the bag, so without this an upgrade would quietly destroy a magazine you paid for, and
upgrading would become something you do carefully rather than something you do. Different calibres
still lose them - the alternative is inventing rounds nobody bought.

`-upgradeTest` needs `-scene island`, because the two venues are a camp workbench and a trader's
counter 97m apart. **49 checks, all passing:**

```
[UpgradeTest]   [6] pistol -> pistol_mk2 at the trader: 150c + 3x scrap_metal,
                    +31% damage, +10% knockback, +25% range, +20% rate, +6 rounds, 25% less recoil
[Upgrading] Player(Clone) upgraded bat to bat_nailed at the bench.
[Upgrading] Player(Clone) upgraded bat_nailed to bat_shark at the bench.
[Wallet]    Player(Clone) -380 (upgraded pistol_mk2 to pistol_auto), now 40.
[Upgrading] Player(Clone) upgraded pistol_mk2 to pistol_auto at the trader for 380,
            carrying 7 round(s) across.
[UpgradeTest]   bat t1: 14 damage a hit ->  bat_nailed t2: 26  ->  bat_shark   t3: 42
[UpgradeTest]   hatchet t1: 34          ->  hatchet_fire t2: 52 ->  chainsaw    t3: 55
[UpgradeTest]   pistol t1: 26           ->  pistol_mk2 t2: 34   ->  pistol_auto t3: 40
[UpgradeTest] 49 passed, 0 failed.
```

The acceptance asked for a curve that is *visible*, so the curve is measured rather than read off the
assets: every weapon in every line is equipped in turn, swung at a real second player, and the
damage that actually came off their health is what gets compared. A tier-two weapon that is only
better in the inspector fails here. The refusals are checked the same way - no weapon, wrong venue,
no venue, no materials, no money - and each one has to leave the bag and the wallet exactly as they
were, because a refusal that half-charges you is the bug players actually notice.

One honest gap: the rollback path is unreachable from a test. `Inventory.Add` fails only when all 20
slots are full, and the upgrade frees the old weapon's slot before adding the new one, so "your bag
is full" cannot be provoked. The harness asserts the reachable half - that nothing changed after
every refusal it *can* cause - and the unreachable branch is reviewed code, not tested code.

**One harness per process.** Running `-weaponTest -meleeTest -gunTest` together produced 37 failures
and no bugs: they share one victim, one bag and one arena, and since #47 one *static* money ledger,
so each was reading the state another had just changed. Alone, all four still pass - `-gunTest`
113/113 (81 before the six new weapons), `-weaponTest` 27/27 over all fifteen carried weapons,
`-meleeTest` 33/33, `-shopTest` 65/65 and `-moneyTest` 29/29. The flags are deliberately not
mutually exclusive, because a harness that refuses to run is worse than one you have to run twice.

The first run of the harness failed two checks, and the test was wrong rather than the feature:
`pistol_auto` has the longest range of the three, so scaling the firing distance by range stood the
victim 20m out, where the island had something in the way. Every gun now fires from the same 8m.
Distance was never the variable under test.

**Not done here:** no bench or shop UI lists what is available (the data is on `Upgrading.Available`,
the screen is #M8's), the trader does not stock the tier-2 and tier-3 weapons directly - you arrive
holding the tier below - and whether any of them should ever appear on a shelf is #56's call.

### Wildlife and hunting (#53)

Three species on the island - boar, deer, gull - and **one prefab between them**. The body's size,
colour, collider, agent radius, health, speeds, reach, temperament and loot table all come from an
`AnimalDef` picked at spawn time, so a fourth animal is a row in `AnimalFactory`'s seed table or a
`.asset` file dropped in the folder, and never a new prefab, a new script or a new registration.

That works because of one decision: **which species this is travels as a `SyncVar<ushort>` set
before `ServerManager.Spawn`.** It rides along inside the spawn message, so a client resolves the
definition and builds the right shape in the same frame the object appears. Set it after the spawn
and there is a visible frame of default-sized grey box before it becomes a deer. `AnimalCatalog` is
the sixth catalog on the same doctrine as `ItemCatalog` and the rest - sorted by `string.CompareOrdinal`,
index 0 means none, rebuilt whole by the factory, never hand-edited.

**An animal that runs out of health dies; it does not go down.** Rather than a parallel `Health` for
creatures, `Health` grew one field:

```csharp
[SerializeField] bool _canBeDowned = true;
...
if (_current.Value <= 0f)
{
    if (_canBeDowned) ServerDown(info);
    else ServerKill(info);
}
```

A boar has no friend to drag it to the Revive Machine, so the downed state is a state it could never
leave. Everything else about it - damage, invulnerability, the death event, kill credit - is
identical to a player's, which is what makes weapons work on animals for free: `Weapon` finds its
victims through `GetComponentInParent<Health>()`, so anything with a `Health` and a collider takes
exactly the damage its attacker's asset states. A hatchet does 34 to a boar for the same reason it
does 34 to a friend.

**The brain is five states and one number.**

```
Idle   -> stands a few seconds, then wanders
Wander -> walks somewhere near home, then idles
Flee   -> runs directly away from the nearest person    (skittish)
Chase  -> runs at the nearest person                    (aggressive)
Attack -> in reach, hitting on an interval
```

The number is the distance to the nearest living player; the transitions read off it against
`SenseRadius` (aware) and `ReactRadius` (bothered). The one piece of state beyond that is
`_alarmedUntil`: without it a boar that chases you past its react radius immediately forgets why it
was running, turns round and wanders home, which reads as a bug rather than as an animal. With it, a
fright outlasts its cause by `CalmSeconds`. `Flee` re-aims continuously rather than picking one
destination, so circling a deer does not produce a straight-line sprint past your shoulder.

Sensing reads `NetworkPlayerRegistry.Players` rather than sweeping colliders. Four entries is cheaper
than any physics query, it cannot be blocked by scenery, and it is the same list the natives in #55
will want. **The honest cost: there is no line of sight.** An animal hears you through a rock. That
is a deliberate trade and the place to change it if hunting ever needs stalking.

**The spawner is a top-up loop, not a place-everything-once pass**, because a population is a level
rather than a placement: something kills a boar and a few minutes later there is another boar. Each
zone keeps `Population` alive, and a death holds that zone shut for `_respawnSeconds` (45) - without
that lock, hunting is standing in one place killing the same boar every five seconds, which would
pass the acceptance criterion while ruining the thing it measures. Spawn points must be
`_playerClearance` (40 m) from every player: not for fairness, for the illusion. A deer that pops
into existence eight metres in front of you is a spawner; a deer you find is an animal.

The same loop is also, deliberately, how the NavMesh is waited for. The island is a global scene
loaded after the server is already running, so at server start there is nowhere to put an animal -
`NavMesh.SamplePosition` simply fails and the zone tries again five seconds later, which is the same
code path as a zone that is temporarily crowded. No ordering special case to rot.

Zone centres are **derived from the POIs**, not typed, for the same reason every coordinate here is
derived: the island moves when the seed changes and a hard-coded herd ends up in the sea.

| Zone | Population | Hangs off | Radius |
|---|---|---|---|
| `boar.camp` | 5 boar | `camp.base` | 130 m |
| `boar.village` | 4 boar | `village` | 110 m |
| `deer.inland` | 6 deer | `cave` | 160 m |
| `deer.village` | 4 deer | `village` | 140 m |
| `gull.shore` | 5 gull | `wreck` | 120 m |

Boars are the animal near camp on purpose. They are the aggressive species, they carry the most
valuable hide, and putting them where a new player already stands is what makes hunting the first
thing you do rather than something you unlock by walking. `-noAnimals` empties the island, which is
what every other harness wants - a boar wandering through a melee test is a variable nobody asked
for.

**The loot lands on the ground.** `Animal.ServerDropLoot` rolls the table on death and drops real
`WorldItem`s through the same door `ItemDropper` uses, spread on a small spiral around the carcass so
the whole kill is not one pile of colliders resolving itself into the sky. It is never pushed into
the killer's bag. That is the difference between hunting and a kill counter: the meat has weight,
somebody has to walk over and pick it up, and whoever gets there first is a conversation four players
can have.

Four new items pay for all of it - `hide` (26), `feather` (7), `meat_raw` (14), `meat_cooked` (30) -
plus the `cook_meat` recipe at a fire and a `roast` buff worth 48 hunger and 10 health.

**Is it worth it? The comparison is per kilogram, not per kill.** Twenty slots and a forty-kilo carry
limit mean weight is the real constraint on a trip back to the counter, so the question is not "what
is a boar worth" but "is a kilo of boar better than a kilo of what I would otherwise be carrying".
Scrap metal is what they would otherwise be carrying, at **2.7 c/kg**. From the harness's own table:

| Species | Sale | kg | c/kg | Cooked | kg | c/kg |
|---|---|---|---|---|---|---|
| boar | 37 | 4.1 | **9.1** | 57 | 3.8 | **15.0** |
| deer | 41 | 4.7 | **8.8** | 69 | 4.3 | **16.0** |
| gull | 15 | 0.9 | **16.8** | 21 | 0.8 | **26.6** |

Three to six times scrap raw, and cooking roughly doubles it again - which is what makes the campfire
worth the walk back rather than a survival chore. Against the boat as #56 repriced it - 5600, four
parts at 1400 - a boar is 151 kills, which is nobody's plan: hunting is one of three ways to earn and
four people who did nothing else would take about two and a half hours. See *The economy, priced*.

The speeds are tuned against the player's own sprint, read off `PlayerMotor.SprintSpeed` rather than
copied: both prey animals run *slightly slower* than 7.5 m/s, which is the single decision that makes
hunting playable with a hatchet on day one. A deer that outran a sprint would be huntable only with
the guns from #51, and #53's acceptance is about *early* money. The boar charges slower than a sprint
too, for the mirror-image reason: a charge you cannot escape is not a fight.

`-animalTest` (island scene, alone) checks all of it and passes **91/91**. The data half is
invariants that would otherwise surface three sections later as "the deer just stands there" -
index round-trips, ordinal sort, run speed above walk, react radius inside sense, aggressive species
armed and skittish ones not, every loot item real. The live half spawns a deer twelve metres from the
player and watches it get to 38 m in four seconds, spawns a boar at the same distance and watches it
close to 2.8 m and take 100 health off, then runs the whole chain on one boar: a hatchet swing lands
for exactly 34, the kill drops `3x meat_raw, 2x hide`, two `WorldItem`s appear around the carcass,
they are carried to the counter and sold for 47 - the number the table predicted, to the coin.

Two things the run showed that no assertion would have: the harness player was killed by boars during
the charge test because the camp zone's own population joined in, and the `boar.camp` centre landed
7 m from the spawn point. Both are the feature working.

**Not done here:** no line of sight and no smell, so a boar notices you through a hill; no herd
behaviour, so five deer flee as five individuals; the greybox bodies are two cubes and the "which
end is the front" cue is a darker head; and nothing eats anything but you. What the trader pays for
hide and meat was #56's to revisit and now has a model behind it.

---

### Fishing (#54)

Six things live in the sea - sardine, snapper, tuna, a boot, a bottle and a pearl oyster - and the
whole of fishing is one component, `Fishing`, with four states: `Idle`, `Waiting`, `Biting`,
`Fighting`. It is a `NetworkBehaviour` on the player, next to `Weapon` rather than inside it, because
a rod is not a weapon with a strange swing - it owns the Attack key for as long as a line is in the
water, and a machete should never have to know that.

`FishCatalog` is the **seventh** catalog on the same doctrine as `ItemCatalog` and the rest: sorted
by `string.CompareOrdinal`, index 0 means none, rebuilt whole by `FishFactory`, never hand-edited.
It carries one thing the others do not - the rod itself:

```csharp
public ItemDef Rod => _rod;
```

so "what opens this minigame" has exactly one home, and `Fishing.HasRod` is a comparison against the
selected hotbar slot rather than a string, a tag or a second component.

**Three decisions carry the feature.**

*The species is rolled at the cast, not at the landing.* `ServerCastAt` picks the row before the
bobber has finished falling and stores it. That is what makes the fight already be that species'
fight: the bite wait, the hook window, the distance, the pull and the rhythm are all read off the
definition that is already decided, so nothing has to be retrofitted at the moment of landing and no
frame of the fight is generic.

*The catch goes into the bag.* Unlike a kill in #53, which leaves a carcass four people can argue
over, a landed fish is added straight to the angler's inventory and only the overflow hits the
ground. Hunting is the loud thing you do together; fishing is the quiet thing you do alone, and where
the item lands is most of that difference.

*Three species share one item.* A sardine is `1x fish_raw`, a snapper is `2x`, a tuna is `3-5x`. Size
is count. One `cook_fish` recipe from #43 therefore covers the entire table and the trader needs one
price rather than six.

**The minigame is a rhythm, not a reflex test.** A hooked fish alternates between calm and a *run*,
and the run sits at the **end** of each struggle period so every fight opens calm - a fish that bolts
the instant it is hooked reads as the game cheating rather than as a fish:

```csharp
public bool Running(float fightSeconds)
{
    float run = RunSeconds;
    if (run <= 0f) return false;

    return Mathf.Repeat(fightSeconds, StruggleSeconds) >= StruggleSeconds - run;
}
```

Holding the button reels line in and adds tension - a little while it is calm, a great deal during a
run. Letting go gives line back slowly and lets the tension fall. Tension at 1 snaps the line; line
out past 1.6× the starting distance means it spooled you; distance at zero is a fish. That is the
whole of `Fight`, and the entire decision the player makes is *which of the two phases am I in*,
about ten times per fish.

The numbers are tuned against one claim, and the harness plays both halves of it out for real: **a
sardine lands in a single unbroken pull and a tuna does not.** Six metres at 2.4 m/s is 2.5 seconds of
reeling and 0.45 of tension - comfortably under the snap, first time, with no technique. A tuna is
eighteen metres and runs every three seconds, so holding the button down snaps the line a quarter of
the way in; reeling in the gaps lands it in about sixteen seconds.

**The cast is validated against the sea, and refuses in three different ways** because they are three
different mistakes with three different fixes: "you are already in it", "aim at the water", "too far -
aim steeper or walk in", plus "something is in the way" and "too shallow". The sea has no collider in
this game - it is a mesh and a function - so depth is asked of the ground instead, with a short ray
straight down from the float looking for seabed.

`FishingBar` is the only HUD panel in the game that is load-bearing rather than informational. A punch
has a fist and a gun has a tracer, but a hooked tuna is a number on the server and a bobber thirty
metres away, so without the two bars the fight is invisible and the minigame does not exist. It sits
under the crosshair rather than in a corner with the stat meters, and the two bars deliberately mean
opposite things: line shrinks towards zero as you win, tension grows towards one as you lose.

**Economy.** Priced per kilogram like the hunt, because twenty slots and forty kilos mean weight is
the real constraint on the walk to the counter. Raw fish pays 6.0 c/kg against scrap metal's 2.7;
cooking it pays 14.4, which is what makes the walk back to the fire worth making. A pearl is 70 coins
for fifty grams at 5% of casts, the best ratio in the game and the reason anybody casts a fourth
time. Averaged over the table a cast is worth 11.2 coins and takes 11.4 seconds. Per *minute* it depends
on the bag rather than on the rod: forty kilograms is twenty-eight casts and then a walk to the
counter, which is **41 coins a minute** once the walk is counted. See *The economy, priced*.

A boot is worth **zero**, not one. The trader floors every price they are willing to pay at a coin,
so a value of one would still be a sale and the joke would be a consolation prize; at zero the counter
refuses it out loud and a boot is eight hundred grams of carry limit that exists only to be sworn at.
Twenty-six per cent of casts pull up a boot or a bottle.

**Proof.** `-fishTest` runs the acceptance criterion - "relaxing, slightly stupid, and profitable" -
as three sets of checks that fail independently, on the island, at real time. It rolls the table
twenty thousand times and compares every observed frequency against the advertised one; it finds a
real shoreline by walking outward from the spawn in twenty-four directions and asking
`ServerWaterHit` itself whether each candidate works; it casts with the actual Attack key through the
owner RPC; then it plays five whole fights and one deliberate failure, and carries the morning's catch
to the trader. 167 checks, and the numbers it printed are the numbers the tuning predicted: sardine
landed in 2.5 s at 0.45 peak tension, tuna snapped at 2.4 s, the same tuna landed in 15.8 s at 0.52,
oyster in 16.8 s, and the catch sold for exactly what the table said.

**Not done here:** the bobber is a position rather than an object, so there is nothing floating to
look at yet; no bait, no rod tiers and no line strength, so the only variable is the fish; casting
distance is a fixed range rather than a charged throw; and fish do not exist as creatures, so nothing
can be seen swimming and nothing can be startled. Whether the trader stocks pearls and refuses boots
was #56's to settle: it does neither, and both are now checks rather than habits.

---

### Natives (#55)

Three roles live in the two POIs that were always going to be somebody's: the **scout** (45 hp, sees
furthest, hits least, shouts loudest), the **spearman** (85 hp, 22 damage a blow, the reason you do
not simply walk into the village) and the **blowgunner** (55 hp, nine damage and a 1.6 second stun
from twenty-four metres). `Native` is one `NetworkBehaviour` with a six-state machine - `Idle`,
`Patrol`, `Investigate`, `Chase`, `Attack`, `Flee` - and it runs on the host only, like every other
brain in the game.

`NativeCatalog` is the **eighth** catalog on the same doctrine as `ItemCatalog` and the rest: sorted
by `string.CompareOrdinal`, index 0 means none, rebuilt whole by `NativeFactory`, never hand-edited.
`NativeSpawner` is the wildlife spawner's sibling rather than its subclass, because the two have
separate off-switches - `-noAnimals` and `-noNatives` - and most tests want exactly one of them.

**The acceptance criterion is "a real threat at night but not unfair in daylight", and it is carried
by three numbers and one behaviour rather than by an aggression setting.**

*One: by day they have to actually see you.* Daylight notice is a radius **and** a vision cone **and**
an unblocked ray, which is three conditions the player can break on purpose. After dark the cone and
the ray are dropped and only the radius is left, grown by a third, because they are listening:

```csharp
public float NoticeRadius(float night01) => Mathf.Lerp(DayNotice, NightNotice, Mathf.Clamp01(night01));
public bool  NeedsSight(float night01)   => night01 < 0.5f;
```

`night01` is `WorldClock.Night01`, a function of the sun's actual height rather than of the clock, so
dusk is a slope and not a switch - the cone fades out somewhere in the last half hour of light and
nobody has to be told when.

*Two: by day the chase ends.* Every native is leashed to its camp, at 38-45 m in daylight and 120-130
at night. A daylight chase you run away from is a chase you win in four seconds; the same chase at
midnight follows you the better part of the way home.

*Three: nothing outruns a sprint.* The fastest role is the scout at 7 m/s against the player's 7.5,
and the test asserts that rather than trusting it. Running is always an answer. It is just an answer
that costs stamina and leaves you somewhere you did not plan to be.

**And the behaviour: they shout.** A native that notices calls `Alarm`, and every other native inside
its `AlarmRadius` that is not already busy walks to *where the player was*:

```csharp
static void Alarm(Native caller, Health about, Vector3 where)
```

`where` is the caller's last known position for the target, not the target's current one. That is the
difference between a camp that reacts and a camp that cheats - three natives converging on the bush
you have already left is a fight you can win, and three natives converging on you is not.

Underneath all of it sits `Earshot`, six to eight metres depending on the role: walk into somebody and
it does not matter what the sun is doing.

**Attacks have a tell.** `WindupSeconds` is 0.3-0.6 s between the decision to swing and the damage
landing, so a blow you lose to is a blow you could have stepped out of. A dart is hitscan for the same
reason `Weapon` is - the server decides on the frame it fires, so there is no in-flight object for a
laggy client to argue about - with a four degree cone that makes it miss about two shots in three at
eight metres. The cone is built in the aim's own frame:

```csharp
Vector3 shot = Quaternion.LookRotation(line)
               * (Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0f)
                  * Vector3.forward);
```

rather than by rotating the direction with a world-space Euler, which would collapse the spread to
nothing whenever the shot happened to run along the axis it pitched about.

Below `FleeHealth` - 15% for a spearman, 40% for a blowgunner - a native breaks and runs for its camp.
A wounded spearman that keeps coming is a health bar; one that turns round is a person.

**Camps hang off the POIs, not off coordinates.** `NativeFactory.BakeCamps` reads the island profile
and pins five camp lines to the village and the cave, so moving a POI moves its garrison and the
numbers in this document stay true. The island holds **six natives by day and ten at night**, and the
factory warns at bake time if any camp lands within 150 m of the players' own fire. The nearest camp
is 206 m out, which is 54 m further than its own night leash can reach.

**Economy.** A body leaves rope, hide, flint or feathers. #56 priced a camp sweep against a bag of
fish and found raiding paying a quarter of what fishing paid, which made the most dangerous thing on
the island the least worthwhile, so the quantities went up: a body is now worth about two thirds of
an animal. Killing people is still not a living - it is the toll on the road to the cave - but it is
no longer a mistake.

**Proof.** `-nativeTest` runs the acceptance criterion as a measurement rather than an assertion. It
sweeps a player in from beyond the night radius in two metre steps with a fresh native at every step,
three ways - in the open, turned away, and behind a wall the test builds itself - at noon and again at
midnight, and reads the boundary off the result:

```
[NativeTest] spearman in the open at noon: noticed at 16m.
[NativeTest] spearman turned away at noon: never noticed.
[NativeTest] spearman behind a wall at noon: never noticed.
[NativeTest] spearman in the open at midnight: noticed at 28m.
[NativeTest] spearman turned away at midnight: noticed at 28m.
[NativeTest] spearman behind a wall at midnight: noticed at 28m.
[NativeTest] the sun is worth 12m of notice: a spearman sees 16m at noon and 28m at midnight.
[NativeTest] spearman dragged 45m from camp at noon (leash 40m): gave up.
[NativeTest] spearman dragged 45m from camp at midnight (leash 130m): still hunting.
[NativeTest] a spearman in reach: 4 blow(s), 88 health, first one 0.40s after it noticed (wind-up 0.40s).
[NativeTest] a blowgunner at 8.0m: 11 dart(s), 4 hit, 36 health and a stun.
[NativeTest] one spearman shouted at noon: 2 of 2 out of earshot came looking, within 0.0m of where the player actually was.
[NativeTest] a scout at 14/45 hp (breaks under 35%) is Flee.
[NativeTest] 121 passed, 0 failed.
```

Two things worth writing down for the next harness. `Physics.autoSyncTransforms` is off in this
project, so a collider created and then moved in the same statement is, as far as the physics world is
concerned, still sitting on the map origin until the next `FixedUpdate` - the wall the sweep builds
needs an explicit `Physics.SyncTransforms()` or the native looks straight through it. And an immobile
test body should be grounded with a ray straight down rather than with `NavMesh.SamplePosition`, which
will happily drag it twenty metres sideways and quietly ruin a test whose entire output is a distance.

**Not done here:** no weapons on the ground and no bodies to loot beyond the drop table, so a spear is
something they have rather than something you can take; camps are spawn volumes rather than structures,
so there is nothing to burn down and no huts to hide in; natives do not fight the wildlife and the
wildlife does not fear them; there is no reputation, no trade and no non-hostile native; and they do
not open doors, use the shop, or notice that you have stolen anything.

---

### The economy, priced (#56)

Every number in this game lives in an asset, and until now nothing had ever asked what they add up
to. They added up to this: a cast was worth eleven coins and took eleven seconds, a boat cost sixteen
hundred, and four people therefore reached the boat in **two thirds of one evening**. The entire
middle of the game - the guns, the upgrades, the second trip to the cave - was priced out of
existence by a fishing rod.

`EconomyModel` is the one place that does the arithmetic. **Nothing in it is a constant that could
have been read from an asset**: values come from `ShopDef.PriceFor` - what the trader actually pays,
not what the item claims to be worth - weights from `ItemDef.Weight`, odds from the catalogs' own
tables, the boat's price from the shelf. The only hand-written numbers are the ones no asset knows,
and they are declared together at the top with the reasoning attached, so that when the answer is
wrong it is obvious which assumption to argue with: two minutes to walk a bag to the counter, a
minute to find an animal, `Attention = 0.55` for the fraction of an evening actually spent earning
rather than eating, walking, dying, and carrying somebody who is dead, and `GearShare = 0.45` for the
income that never reaches the boat fund because it was spent on a gun and the bandages that follow
one.

**Everything is modelled as a trip, not as a rate**, because you cannot carry more than forty
kilograms. Income is not "coins per minute of fishing" but "a bag's worth of fish, divided by the
time it took to fill it *and* walk it to the counter". That single distinction is the difference
between fishing paying sixty-seven coins a minute and paying forty-one, and it is why a pearl -
seventy coins at fifty grams - is worth more than its price says: it rides home free.

The model as it stands, printed by the harness from the live catalogs:

```
[EconomyTest] one player, a 40 kg bag, and a 2 minute walk to the counter:
[EconomyTest]   fishing      40.8 c/min     2449 c/h  (313 coins per 7.7 min trip, 40.0 kg)  28 casts at 11.2 c and 12.1 s each
[EconomyTest]   hunting      16.9 c/min     1015 c/h  (386 coins per 22.8 min trip, 40.0 kg)  13 kills at 31 c and 100 s each
[EconomyTest]   raiding      16.7 c/min     1003 c/h  (117 coins per 7.0 min trip, 12.5 kg)  6 bodies at 20 c each
[EconomyTest]   foraging      0.0 c/min        0 c/h  nothing on the island is worth money yet
[EconomyTest]   gambling      0.0 c/min        0 c/h  negative by construction; the wheel is #M6
[EconomyTest] the spread is 2.44x: fishing at 40.8 c/min against raiding at 16.7.
[EconomyTest] the boat costs 5600 coins.
[EconomyTest] 4 players average 24.8 c/min each at 55 % attention: 55 c/min as a group, 30 of it after gear.
[EconomyTest] a 90 minute session puts 2702 coins in the boat fund.
[EconomyTest] the boat is 2.07 session(s) away.
```

**Three things were wrong, and the model is how they were found.**

*The boat was two thirds of an evening away.* A part went from **400 to 1400**, so the boat is 5600
and lands at **2.07 sessions** - a band of one and a half to three evenings, which the test asserts.
Under one and a half the island's middle never happens because the boat arrives first; over three it
stops being a goal and becomes a shift.

*Raiding paid a quarter of what fishing paid*, which made the most dangerous thing on the island the
least worthwhile. Native drop quantities went up until a body was worth about two thirds of an
animal - a spearman now leaves 1-2 hide, 2-3 rope and 1-2 flint - and the spread closed from **4.46x
to 2.44x**. Three is the cap the test enforces, because two activities within three times of each
other are a choice - one is safer, one is faster, one is what you do while it rains - and four people
all doing the one thing that pays five times the others are not playing a co-op game.

*Flint paid less per kilogram than the scrap metal it displaced in the bag*, which made carrying it
home a mistake. It went from **2 to 4**. Scrap at 2.7 c/kg is the floor of the whole economy - it is
what you would otherwise be carrying - and the test now walks the full spoils list past it.

The rest of the shelf was already right and is now checked rather than assumed: **a boot is worth
zero, not one** (the trader floors what it pays at a coin, so one would still be a sale and the joke
would be a consolation prize); **a pearl sells but cannot be bought**, because fishing's best outcome
must never be a purchase; and **a boat part cannot be sold back**, which is what stops four parts and
a refund from being a money printer.

Two activities pay nothing, and both are stated rather than omitted, because a zero that is written
down is a gap somebody can decide to fill. **Foraging** pays nothing because nothing on the island
can be picked up and sold - coconuts are food, driftwood is scenery, and the crafting materials are
bought rather than found. **Gambling** pays nothing because the wheel is #M6 and will be negative on
average when it exists; a house edge is the only thing that makes a casino a casino. It is a way to
turn an evening's income into two evenings' income or into nothing, which is a different feature from
a way to earn.

**Proof.** `-economyTest` prints the table above and passes **26/26**, and the last check is the one
that stops the model from being a spreadsheet agreeing with itself: a bag with one animal's drops and
one native's drops in it is carried to the real counter and sold through `ShopCounter.ServerSell`,
and the coins have to match what the model predicted - *"a bag of boar and blowgunner drops sold for
56 coins (model said 56)"*. `-animalTest` (91/91), `-fishTest` (167/167) and `-nativeTest` (125/125)
all still pass against the new prices; hunting's own boat check was retargeted at the model rather
than at a boar count, because nobody buys a boat with venison alone - what it asserts now is that
four people who hunted and did nothing else would get there in **2.5 hours**.

**Not done here:** the model prices an activity, not a player, so it has nothing to say about four
people doing four different things at once or about one of them being bad at fishing; the gear share
is a single fraction rather than a shopping list, so "how much of a session is a rifle" is still a
question nobody has asked; deaths cost a revive that the model knows nothing about; and the whole
thing assumes the trader is the only buyer, which stops being true the moment there is a second
island.

### Abduction (#107)

**A downed player is not safe on the floor.** Until #107 a body that went down in front of a native
camp stayed exactly where it landed, bleeding out on a timer while the natives went back to
patrolling around it. Now the nearest native that is in the business of it breaks off, walks over,
puts your friend on its shoulder and carries them home. The timer does not stop while this happens.
Being carried off is not a reprieve; it is the same forty-five seconds, spent somewhere worse.

**The hook is `Health.ServerStateChanged`, and that choice is the whole design.** There is a
replicated `StateChanged` too, but it fires after the SyncVar is published and after the ragdoll has
launched, which would mean a window where every client has watched somebody hit the floor and the
server has not yet decided who is coming for them. The server-side event fires first, on the same
frame as the down, from the authoritative side. Everything after the claim is unhurried - the native
walks, and re-checks its own state every tick on the way - but *who claimed this body* is settled
once, immediately, and never re-opened.

**One watcher, not one subscription per native.** `AbductionWatch` is a static server-side class that
hooks each player's `Health` exactly once, keyed by the `Health` itself, and re-hooks on join through
`NetworkPlayerRegistry.PlayerAdded`. The alternative - every native subscribing to every player -
is N x M subscriptions that all have to be unwound correctly when either side despawns, and it makes
a body that goes down alone in the jungle cost a dozen distance checks instead of one dictionary
lookup. `Native.OnStartServer` calls `AbductionWatch.Arm()`, which is idempotent: an island with
nobody on it who takes prisoners never arms at all.

**The claim is exclusive by construction rather than by a registry.** A native holds exactly one
`Haul`, and `Native.ServerOffer` sweeps the live list skipping any body somebody already holds, then
picks the nearest eligible native inside its own `AbductRadius`. Two natives arriving at the same
body is a thing that should look like a scuffle one day; two natives each believing they are carrying
it is a bug today.

**`Carryable` did not need changing to make this work, and that is the point.** It only ever required
a `CarrySystem` on the carrier in the sense of asking *can this thing hold a body* - so the native
implements `ICarryHolder`, gets a `CarrySocket` over its shoulder from the factory, and calls the
same `ServerCanBeCarriedBy` / `ServerAttach` / `ServerDetach` that a player calls. A native carrying
you and a friend carrying you are the same code path with a different holder.

**One state, four ways out.** `NativeState.Abduct` covers both halves of the job - walk to the body,
then walk it home - because from the player's side they are one event, *somebody is taking your
friend away*, and splitting them would produce two states that drop the body for the same four
reasons. Those four reasons are the counter-play, and every one of them is something the other three
players can cause:

| Counter-play | What it costs you | What happens to the body |
|---|---|---|
| Kill the carrier | a fight, and the camp refills a dead native | dropped where it fell, which may be nowhere useful |
| Hurt it past its `FleeHealth` | a few hits, and the native lives | dropped, and the carrier runs for camp |
| Get a hand on the body first | you are now the one carrying somebody, slowly | yours |
| Revive them mid-haul | the revive itself | they stand up next to an angry native |

`Release()` is the single path out of a haul - death, flee, rescue, timeout and despawn all come
through it - so there is exactly one place in the code that can leave a body attached to nothing.

**A kidnapper is committed.** `OnHealthChanged` returns early for a native in `Abduct`: shooting it
in the back does not make it turn round and fight you, it makes it keep walking towards the thing you
do not want it to reach. The way to stop it is to put it down or break it, and both of those drop the
body. The flee check still runs first, which is what makes "hurt it enough" a real answer rather than
a worse version of "kill it".

**Haul speed is the fairness lever.** A spearman hauls at **3.3 m/s** and a scout at **3.5**, against
a player's **7.5 m/s** sprint - half the native's own run speed, by the `_haulFraction` on
`NativeDef`. Catching a kidnapper is therefore never in doubt; what the haul costs you is the time,
the distance, and whatever is standing between you and it. The same lever that makes a native running
away work makes running after one work. `HaulSeconds` (90) is a safety valve rather than a mechanic:
a delivery point that can never be reached should end with a body on the floor, not with somebody
spending the rest of the run on a shoulder.

**The blowgunner does not do this.** `NativeFactory` carries an `Abduction` table next to the loot
table - scout 20 m, spearman 24 m, blowgunner never - and re-applies it on every run, because which
role takes prisoners is a statement about what the three roles are *for*, not a tuning number. A
rebuild that silently left a camp with no kidnapper in it would take #107 out of the game without
anybody noticing. It also makes the fight legible: one body walking away with your friend, one
keeping its distance and shooting whoever runs after it. If every role abducted, the answer would
always be the same fight.

`-abductTest` runs the whole thing headless on the island with the natives and animals switched off,
placing its own spearmen where it wants them:

```
[AbductionTest] a player went down with four natives watching: 1 claimed the body; the one 8m away got it.
[AbductionTest] 4s of being carried: 11.1m closer to camp (44m -> 33m) and 4.0s of bleed-out gone (44s -> 40s left).
[AbductionTest] the haul moved at 2.8 m/s against a 7.5 m/s sprint.
[AbductionTest] the carrier was killed 56m short of camp at (-83.93, 5.08, -35.56); the body landed 1.5m from there, at (-83.93, 6.53, -35.26).
[AbductionTest] the carrier was hurt to 12/85 hp (it breaks under 15 %): carried=False, it is now Flee.
[AbductionTest] a player helped up mid-haul: carried=False, the native is now Chase.
[AbductionTest] a body was carried to the camp in 4.3s and put down 2.6m from the middle of it at (-84.32, 6.59, -30.67) (the carrier stopped at (-84.37, 5.14, -30.97)), with 41s left on the timer.
[AbductionTest] 40 passed, 0 failed.
```

`-nativeTest` still passes 125/125 on top of it.

#### Two physics bugs the haul found

Neither of these was #107's own code, and both of them had been wrong in player carrying (#24) since
it was written. A native walking a body sixty metres is simply the first thing that moved a carried
body far enough for anybody to see it.

**Interpolation overwrites a parented pose.** Every ragdoll bone is a `Rigidbody` with
`RigidbodyInterpolation.Interpolate`, which is right while the body is dynamic and actively wrong
while it is kinematic and parented to somebody's shoulder: interpolation writes the transform from
the last two *physics* poses, so it happily undoes the position the socket just gave it. The measured
symptom was a native hauling at 3.3 m/s with the body it was carrying moving at 0.8, trailing seven
metres behind. `RagdollController.SetBonesKinematic` now turns interpolation off whenever it turns
kinematic on, matching the convention `SetRagdollInternal` already used:

```csharp
bone.isKinematic = kinematic;
bone.interpolation = kinematic
    ? RigidbodyInterpolation.None
    : RigidbodyInterpolation.Interpolate;
```

**`autoSyncTransforms` is off in this project.** `Physics.autoSyncTransforms` is `false`
(`DynamicsManager.asset`), so PhysX does not learn about a reparenting until something syncs. Putting
a body down after carrying it meant going dynamic on bones PhysX still believed were back at the
pick-up point, the solver's pose won, and the body snapped across the map - a native carried somebody
the length of a village and put them down ten metres from where it was standing. `Carryable` now
calls `Physics.SyncTransforms()` after the reparent in both `AttachVisual` and `DetachVisual`.

**Not done here:** the delivery point is the camp centre, because there is nothing in a camp yet to
hang a body on - #108 puts hang points there, a rescue interaction on them, and guards that object to
you walking up to one. A body that dies *while* being carried is not released; the journey continues
and the corpse arrives, which is the worst outcome in the game and the one worth playing to avoid,
but what a village does with a corpse is also #108's. Natives do not fight each other over a body,
do not pick one back up after being made to drop it, and do not react at all to watching a player
carry a downed friend past them.

### The village prison (#108)

#107 ended with a native walking your friend out of sight. This is what is at the other end of that
walk: three frames with hooks on them, behind the totem in the native village, on the far side from
base camp. A haul that finished with a body dumped somewhere in a village would be a disappearance.
A body hanging upside down in the open, in a fixed place, with a clock on it, is a kidnapping.

**A hook holds a body the same way everything else does.** `HangPoint` is an `ICarryHolder` with a
socket, exactly like a player's shoulder, a native's shoulder and the Revive Machine's intake - so
there is no fourth attach path, no special case in `Carryable`, and a body on a hook is in the same
replicated state as a body on somebody's back. That the socket is rotated 180° about Z is the entire
difference between being carried and being strung up. Occupancy is a SyncVar, so a late joiner
walking into the village sees the body, and the crosshair prompt is answered client-side for free.

**Arriving is not a reprieve either.** The bleed-out timer that kept running through the haul keeps
running on the hook. A hung player is still `Downed`, still rescuable by a friend who can reach them,
and `Rescuable`'s prompt is still the one that matters most to whoever gets there. What the village
has bought itself is the walk - the distance between where you went down and where you are now, with
a camp in between.

#### Cutting somebody down is the start of the rescue, not the end of it

`ServerCutDown` frees the body and does nothing else. It falls, it is still downed, it is still on
the clock, and somebody now has to carry it out at carry speed through a village that is fully awake.
The measured line is the point:

```
[PrisonTest] a body was cut down with 44s left (it had 44s on the hook): carried=False, downed=True, the hook is free=True.
```

The hook offers nothing at all while it is free - `Prompt` is empty, because Interact is a shared key
and a component that always answers swallows every other gesture within reach of it. Nobody can cut
themselves down; `ServerCanInteract` refuses the occupant, and refuses anything without a
`CarrySystem`, so a native cannot free the prisoner it just delivered.

#### A guarded camp plays like a night raid

This is the one rule that changes how the village fights, and it is deliberately built out of #55's
existing two levers rather than new ones. `Native.Alertness` is the sun - *unless* the camp is holding
somebody, in which case it is 1.0 whatever time it is. Everything the day/night contract turns on
reads `Alertness` instead of `Night`: the notice radius, whether they still have to actually see you,
and the leash they will follow you out to.

```
[PrisonTest] at noon a native at the prison went from alertness 0.00 (notice 18m, sight required) to
1.00 (notice 30m, sight not needed) with somebody on the hook, and back to 0.00 once they were cut down.
```

So a rescue is a raid: the cover and the angles that get you past an empty village at noon do not work
on a village that is holding your friend. And the daylight contract is untouched everywhere else -
walking past a prison with nobody in it is still a problem that distance and a hill can solve. The
flag clears the moment the last prisoner comes down, which is what makes cutting somebody loose worth
doing even if you cannot carry them yet.

#### If the clock runs out up there, they die up there

Nothing releases a body on death. The corpse stays on the hook, in the middle of a native camp, and
getting it back is now a carry-out plus a Revive Machine bill (#25) - the worst outcome in the game,
and the one the haul is worth racing.

```
[PrisonTest] the clock ran out on a hung player: dead=True, still on the hook=True, hips 0.00m from the socket, death 1.
```

Corpses hang exactly like downed players do: the village does not check for a pulse, and a corpse can
be cut down and carried off with the same gesture.

#### Where the hooks come from

Three of them, spawned as their own POI entries (`village.prison.a/b/c`) rather than baked into
`NativeVillage.prefab`, for the reason the Revive Machine is: a thing with behaviour and a replicated
occupant is a machine, not scenery, and its position belongs in a text file. They sit six metres
behind the totem on the village's own pad, which is the *far* side from base camp - the village faces
the way you come from, so reaching the hooks means going through the huts rather than round them.

Three, because a four-player game can lose three people and still have somebody left to come and get
them; the fourth hook is deliberately missing, since a wipe is a wipe and does not need scenery.

`Native.Deliver` asks `HangPoint.ServerFree(_delivery)` - measured from the delivery point rather than
from wherever the agent happened to stop, because a hook belongs to the camp and not to whichever
carrier got nearest to it. A full prison, or a camp with no prison in it at all (the cave outpost, and
every improvised delivery), is answered with `null`, and the body goes on the ground. That is an
outcome, not an error, and `-abductTest` measures that end of it in full.

```
[PrisonTest] a haul ended at the prison: the body went to HangPoint (village.prison.a), hips 0.00m from its socket, and the player has 40s left on the timer.
[PrisonTest] 3 hook(s) are within reach of this delivery point; with one of them full the next haul is offered HangPoint (village.prison.b).
[PrisonTest] 44 passed, 0 failed.
```

Regressions: `-abductTest` 40/40, `-nativeTest` 125/125.

**Not done here:** the hooks are greybox posts and the hanging pose is a socket rotation rather than an
animation, so "suitably undignified" is currently a physics accident rather than a decision. A body
cut down inside the village is not something the natives react to - they aggro on the rescuer, not on
the empty hook. Nobody re-hangs a body that was cut down and then dropped on the way out, which is a
funnier outcome than it has any right to be and is left alone until somebody has played it. And there
is no loot in the village yet; that is #109.

### Loot worth the walk (#109)

#107 walks your friend to the village and #108 hangs them there. This is the other half of the
reason to go: a village body is carrying something, and what it is carrying is different from what
the one you jumped on the ridge was carrying.

**A role has two loot tables, and a body rolls whichever one its camp says.** `NativeDef.Loot` is
what was on the body; `NativeDef.VillageLoot` is that plus the stores of the camp it was manning.
The choice between them is a single bool decided at spawn - `NativeSpawner.Camp.Stocked`, passed
through `ServerConfigure` - and never by geometry, so a spearman that chased you two hundred metres
out of the village is still a village spearman. What it is carrying left the village with it.

The village is stocked and the cave outpost is not. That is the whole map-level statement: a second
pile of ammunition at half the distance from base camp would make the raid the second-best place to
go, and #107's and #108's entire premise is that the village is where you have to go.

```
[LootTest] 3 stocked camp(s) (village.spearman, village.blowgun, village.scout) against 2 without (cave.spearman, cave.scout).
```

#### The village table contains the wild one

Added to, never swapped out. Two disjoint tables would make the wanderer a separate economy rather
than the poor end of one; a superset makes "go where they keep things" a sentence about quantity,
which is the sentence a raid can be priced against.

```
[LootTest] role         wild                                         value   village adds
[LootTest]   blowgunner 3-5 feather, 2-3 flint, 1-2 coconut @50%       41.8c  6-12 pistol_ammo @90%, 1-3 rifle_ammo @70%, 1-2 meat_cooked @60%
[LootTest]   scout      2-3 rope, 2-4 feather, 1-2 coconut @50%        34.8c  8-14 pistol_ammo, 1-3 cloth @60%, 1-2 fish_cooked @50%
[LootTest]   spearman   1-2 hide, 2-3 rope, 1-2 flint, 1 meat_cooked @40%  67.0c  2-5 shotgun_shell @80%, 2-5 rifle_ammo @80%, 1-2 scrap_metal @70%, 1 bandage @35%
```

Roughly twice the value out of the village - 79.9c against 34.8 for a scout, 119.9 against 67.0 for a
spearman - and no item appears on both halves, because two lines for the same item would mean two
rolls of it and would read as a bug in a log.

The wild table gained food at the same time. Every role now carries something to eat, because a
native crossing the island packed lunch, and because the one thing a won fight could not previously
do was feed you.

#### The ammunition is the point, and it is arithmetic

#51 put four guns on the island and exactly one way to feed them: the trader. That made every
firefight a bill, and made the most dangerous place on the island the worst possible thing to spend
ammunition on. So the claim #109 is built to make is a sum, and `-lootTest` does the sum against the
village *as baked* - the camp populations, the roles' own health, each gun's own damage - with a
third of the shots missing.

```
[LootTest] gun         ammo            day: dropped/needed        night: dropped/needed
[LootTest]   pistol       pistol_ammo      19.1 /  15.6             38.2 /  26.3   pays for itself
[LootTest]   pistol_auto  pistol_ammo      19.1 /  10.1             38.2 /  17.1   pays for itself
[LootTest]   pistol_mk2   pistol_ammo      19.1 /  11.9             38.2 /  20.1   pays for itself
[LootTest]   rifle        rifle_ammo        7.0 /   6.2             11.2 /  10.5   pays for itself
[LootTest]   shotgun      shotgun_shell     5.6 /   4.6              8.4 /   7.8   pays for itself
[LootTest]   smg          pistol_ammo      19.1 /  28.9             38.2 /  48.8   costs more than it takes
```

Run at both populations, because the village is not one place at two times of day; the night raid is
bigger and longer, and the rifle's margin is the thin one there (11.2 against 10.5). The assertion is
made per *calibre* rather than per gun: one gun of each kind coming out ahead is what makes bringing
the right one a decision. **The SMG is the deliberate exception.** Eight hundred rounds a minute is
not a gun a raid can pay for, and that is the trade the SMG exists to offer.

#### What the raid is worth at the counter

`EconomyModel.Raiding` now prices the village table rather than the wild one, because the village is
the camp worth sweeping. A sweep still is not weight-limited - six village bodies come to about
twenty-four kilos against a forty kilo bag - so the trip is as long as the bodies take plus the walk.
Picking off wanderers is deliberately not modelled as its own activity: it is the same row with a
worse table, for about half the money.

```
[EconomyTest]   raiding      40.0 c/min     2402 c/h  (280 coins per 7.0 min trip, 23.6 kg)  6 bodies at 47 c each
[EconomyTest] the spread is 2.41x: fishing at 40.8 c/min against hunting at 16.9.
```

#### Rolled on the host, dropped on the floor

Nothing about this is new plumbing: `Native.ServerDropLoot` already ran server-side only and already
fanned its stacks into `WorldItemSpawner.Drop`, which is #42's pipeline and the same one an animal
uses. #109 changed which array it reads and nothing else about the path, which is why the live half
of the harness is six kills per role per table rather than an elaborate set-up.

```
[LootTest] 6 village spearman(s) left 11x hide, 14x rope, 8x flint, 19x shotgun_shell, 11x scrap_metal, 3x meat_cooked, 15x rifle_ammo, 1x bandage; 17 stack(s) off the camp's stores, 0 off the table.
[LootTest] 6 wandering spearman(s) left 9x hide, 16x rope, 9x flint, 1x meat_cooked; 0 stack(s) off the camp's stores, 0 off the table.
[LootTest] 290 passed, 0 failed.
```

Regressions: `-nativeTest` 136/136, `-abductTest` 40/40, `-prisonTest` 44/44, `-economyTest` 27/27.

**Not done here:** natives do not carry the guns they are dropping ammunition for, which is a fiction
the village gets away with only because nobody has asked yet - the honest fix is a native that shoots
back, and that is a bigger issue than this one. A body's drops land on the ground rather than in a
searchable corpse, so a raid still ends with four people crouching over the same pile. And the cave
outpost now has a reason to feel like a waste of time, which it should eventually answer with
something other than loot.

### A seat, a door, and who is driving (#57)

M5 is four vehicles that agree about almost nothing. A car has wheels on the ground, a boat has a
hull in the water, a plane has neither and an opinion about angle of attack. What they do agree on is
the twenty seconds either side of the driving: somebody presses Interact, ends up in a seat, stops
being a pedestrian, and eventually gets out somewhere. Writing that four times is how you end up with
four different answers to "what happens if the driver bleeds out at speed", so it is written once.

`Vehicle` is that once. It holds the seats, the doors and the ownership, and knows nothing at all
about motion — the buggy this ships is kinematic and does not move unless something else moves it.
#58 bolts WheelColliders onto the same prefab and clears the flag; nothing above has to change.

**Seating is a `SyncList<int>` of object ids, not of NetworkObjects.** A reference to a spawned
object only resolves if that object already arrived on that peer, and the case this has to survive is
exactly the one where it has not: somebody joining a session where the buggy is already full. Ids
always deserialise. `Apply` resolves what it can, marks the rest, and retries next frame — which is
what stops a late joiner from seeing four empty seats and four people standing on the roof. The list
is sized once on the server and then only ever written *by index*, because an add or a remove would
renumber every seat behind it and seat 0 means "driver" to the ownership code.

`VehicleRider`, the component on the body, holds **no networked state at all**. Who is sitting where
is one fact, and a second SyncVar claiming it is a second fact that disagrees the first time a packet
is dropped. The seat list is authoritative; `Vehicle.Apply` tells each body what it is, on every peer.

**Riders are glued, not parented.** `LateUpdate` writes each occupant's transform straight onto its
seat anchor, on every peer, after everything else has had its turn — the motor on the owner, the
NetworkTransform on a spectator. FishNet has networked parenting and it would be the tidier answer on
paper; it is also a great deal more machinery to be wrong about, on a body already running client
prediction underneath a server-authoritative NetworkTransform that deliberately excludes its owner.
The glue is four lines and cannot desync, because every peer is copying a transform it can already
see.

Sitting down is three switches, and the first one is the one that matters:

- **The CharacterController is disabled.** It caches its own position and would drag a body straight
  back out of a moving car — the same fact that makes `PlayerMotor.ServerTeleport` switch it off
  before writing a transform.
- **The reconcile stops writing position.** The motor already refuses to run its replicate with the
  controller off; the reconcile needed telling separately, because it would keep writing a world
  position a round trip behind the car and the glue would keep putting it back, which is a
  description of jitter.
- **Collisions with the vehicle are ignored**, so a chassis cannot punt its own passengers. The
  controller is skipped when the pairs are set: it is switched off for the whole ride, and Unity logs
  an error rather than shrugging when asked to ignore a disabled collider.

Getting out puts all three back and the server teleports the body to *its own* door, dropped onto
whatever ground is under it. Four doors rather than one, because four people leaving at once through
the same square metre is how depenetration fires them across the island.

**Ownership follows the wheel.** Taking seat 0 hands the vehicle's `NetworkObject` to that connection
and leaving it takes the ownership back. Nothing in #57 reads it — the thing is kinematic and the
host moves it. It is here because #58's input has to arrive from a connection that owns something,
and who owns a car is not a question worth answering twice.

**Exit is the only verb a passenger has.** Interact is a shared key with a priority list in
`PlayerCombatInput`, and getting out goes on top of it, but only while actually seated. The obvious
alternative — routing it through `PlayerInteractor` like everything else — means aiming at the car
you are already inside: a sphere cast from a seat hits the chassis, the dashboard or nothing at all
depending on which way your head is turned. "I cannot get out of the boat" is not a bug anybody
should have to report.

**A vehicle is an `ICarryHolder`.** That interface was written during the carry rework with a note
naming the back of a truck and a boat, and this is the truck. A body rides in the cargo socket rather
than in a chair, because a corpse occupying a seat somebody could have used would be the single most
annoying object in the game. `ServerSweep` is the other half of that: a rider who goes down or
vanishes mid-ride is dumped out rather than carried around as a statue.

The buggy itself is generated — `VehicleBuilder` writes four cylinders, a box and, the part that
actually matters, eight transforms: where four people sit and which patch of ground each of them is
put down on. Those are numbers that get moved by feel the first time somebody drives with a friend's
head in the way, and moving them in a generator produces a diff rather than a binary. The island's
own POI bake parks one at base camp.

`-vehicleTest` runs against that parked buggy. Four bodies means four bodies: the harness spawns
three more from the same prefab the player spawner uses, ownerless, which is exactly what a body
whose player is still loading looks like.

```
[VehicleTest] one buggy at (-86.00, 4.91, -38.00), 4 seat(s), cargo socket wired.
[Vehicle] buggy refused Player(Clone): the buggy is full.
[VehicleTest] 180m at 30 m/s with four aboard: worst drift 0.000m over 11976 sample(s).
[Vehicle] Player(Clone) went down in seat 0 of the buggy; dumped on the ground.
[VehicleTest] 79 passed, 0 failed.
```

The drift number is the whole design in one line. It is measured per rider against *its own anchor*
rather than against the vehicle, which would pass even if all four were stacked in the driver's seat.

Refusals are checked by their reason, not just by their refusal, and the order of the checks is load
bearing: a carried body is always also incapacitated, so "somebody is carrying them" has to be asked
before "they are on the floor" or the message is technically true and useless.

Regressions: `-nativeTest` 134/134, `-lootTest` 285/285, `-abductTest` 40/40, `-prisonTest` 44/44,
`-economyTest` 27/27.

**Not done here:** the buggy does not move on its own, which makes "without desync" a claim about a
transform the harness drives rather than about a physics body four clients are predicting — #58 is
where that gets hard. Nobody can shoot from a passenger seat, and a seated body is invisible to the
interactor, so a passenger cannot open a chest they are parked next to either. And a vehicle that
despawns with people inside empties itself politely rather than throwing them, which is the less
funny of the two options and stays that way until somebody asks.

### The buggy drives (#58)

The brief for the car is one sentence — *fun to drive badly* — and the tuning that gets there is not
the tuning a simulator would pick. High grip, soft suspension, a centre of mass at axle height, and a
top speed low enough that hitting it feels like an achievement. `CarController` is four WheelColliders
and about two hundred lines; everything interesting in it is a number that was moved until something
stopped being annoying.

**The rear wheels drive and the front wheels steer, and they do not share the job.** The first version
put motor torque on all four, which is the obvious thing to do and made the car refuse to turn: at
full lock the front tyres spent their entire friction budget dragging themselves forwards and had
nothing left to point the car with. A tyre has one friction budget, not one per direction. Splitting
the jobs also hands the buggy a tail that steps out under power, which is the entire reason it exists.

**Steering lock falls off with speed**, 34° at rest down to 11° at 22 m/s. Without that, full lock at
80 km/h is not a corner, it is a barrel roll, and one that happens faster than a player can react to.
The visual wheels turn from a `SyncVar` rather than from the collider, because the collider only runs
on the host — everywhere else the chassis is kinematic and its wheels are switched off.

**Throttle against motion is a brake, not reverse.** Tapping S at 20 m/s without this puts the wheels
in reverse under a car still doing 20 m/s, which locks them and turns a stop into a slide into
whatever is ahead. Reverse only engages once the car is nearly stopped.

**Motor torque does not wake a sleeping Rigidbody.** PhysX parks the chassis after a few seconds
standing still; the wheels then keep spinning against a body nobody is integrating, and the car sits
there with all four tyres turning and slipping and going nowhere. Anything that asks the car to move
has to `WakeUp()` it first. This one cost a full diagnostic cycle to find and is two lines to fix.

**It rights itself, but only once the joke is over.** `Right` fires when the car is both upside down
*and* going nowhere, so a barrel roll still in progress is left alone to finish. The heading is kept
and only the roll and pitch are thrown away, because the way the car was pointing is usually the way
out of wherever it landed. Position and rotation are assigned rather than moved: `MovePosition` is an
interpolated sweep meant for kinematic bodies and would drag the chassis through whatever it is lying
against on the way up.

**Input is quantised to a tenth and sent only when it changes**, so holding W costs one packet instead
of thirty a second. A keyboard produces about four distinct values per axis anyway. Ownership is the
permission check and there is no second one: seat 0 takes ownership of the vehicle, so a passenger
calling `OwnerDrive` is refused by FishNet before it reaches any code here.

#### The harness needed a floor before it could measure anything

Five runs of `-carTest` reported that the buggy would not move, would not turn, or both. All five were
correct and none of them were about the car.

The buggy is parked by the island's POI bake, and the first run found it wedged against `Shelter.Post2`
at ten kilonewtons — a static collider, which PhysX treats as infinitely heavy, so a 900 kg car pushing
eight thousand newtons against one can spin its wheels forever. Moving it took the peak speed from
3.3 m/s to 5.7. The second run had it nose-first into a bank: same symptom, and only the *direction* of
the contact impulse could tell the two apart. Giving it a terrain pad of its own reproduced the same
geometry 0.63 m higher up. Making the harness hunt for level ground got one honest acceleration number
and then reported 13° of yaw in seven seconds of full lock — because the flat patch it found had a
building on it, and `Terrain.SampleHeight` cannot see walls.

The answer was to stop testing on the island. `Recentre` drops a 1200 m slab of collider at
(4000, 100, 4000) and puts the buggy in the middle of it before every section that drives. It is
twelve lines, it replaced eighty, it gives the same answer every run, and the numbers that come off it
are about the car, not about the island. The suite puts the buggy back where the bake parked it on
the way out, because it borrowed the only car in the world. It also has to have the process to itself:
`-carTest` and `-vehicleTest` both drive that one car, and run together the first one to reach 30 m/s
makes a liar out of the other's "the parked buggy stays parked".

The diagnostics that found all of this are still in the code, because the next report of "the car is
stuck" will need them. `WheelReport` reads torque and steering angle back *off the colliders* rather
than trusting the fields written to them — "the motor is at 900 Nm" is a statement about intent, and
what PhysX integrates is whatever is on the WheelCollider at the end of the step. `OnCollisionStay`
records the hardest real contact and its impulse vector; an earlier version used `Physics.OverlapBox`
on the chassis bounds and confidently named objects the car was nowhere near, because the AABB of a
rotated 1.9 × 3.6 box is about half again too big.

```
[CarTest] 9s of throttle: peak 22.1 m/s (80 km/h), 0-10 m/s in 2.4s.
[CarTest] braking from 22.0 m/s: stopped in 15.0m.
[CarTest] full lock at throttle 0.7: 446 degrees over 7s (64 deg/s).
[CarTest] 12s of real driving with four aboard, peaking at 22.1 m/s: worst drift 0.064m over 23964 sample(s).
[CarTest] flipped, then righted itself after 0.8s on its roof.
[CarTest] driver ejected at 5.0 m/s; the buggy braked itself to a stop.
```

Sixty-four degrees a second at full lock is a ten metre circle at 11 m/s, which is a go-kart, not a
car — deliberately. The drift line is the #57 seat glue still holding at four times the speed it was
written against; its budget went from 2 cm to 15 cm because at 22 m/s the chassis is interpolated
between physics steps and the riders are placed on a different beat, and three centimetres of that is
not a passenger coming loose.

Regressions: `-vehicleTest` 79/79.

**Not done here:** the car is driven by exactly one person and predicted by nobody — the chassis is a
server-authoritative NetworkTransform, so a driver on 100 ms sees their own steering 100 ms late.
That is the hard part of M5 and it is deliberately not in this issue. Hitting a player does nothing
yet (#60). There is no engine sound, no skid, no damage and no fuel, and the buggy respawns nowhere if
somebody drives it into the sea.

### The boat floats (#59)

The sea was already a function. `WaterWaves` has been computing the same three sines for the vertex
shader and for C# since the island shipped, precisely so that a boat and the water it is drawn on
could never disagree about where the surface is. `BoatController` is that promise being collected:
six points on the hull, `WaterSurface.HeightAt` at each, and Archimedes.

**Buoyancy is a spring, and the whole issue is the damper.** A force proportional to how deep a point
is *is* a Hooke spring, with stiffness `floatation × mass × g / draft`. An undamped spring stepped at
thirty hertz does not settle — it rings, and then a rounding error throws the boat into the sky.
"Stable in waves, does not jitter" is therefore a statement about one coefficient and nothing else.

So the damper is not a number somebody typed in until it stopped exploding. It is derived:

```
stiffness = floatation * mass * g / draft
critical  = 2 * sqrt(stiffness * mass)
damper    = _damping * critical / floatCount
```

`_damping` is 0.7 — a fraction of critical, not a force. Change the mass, the draft or the reserve
buoyancy and the damping follows, which means no future tuning pass can quietly leave the hull
underdamped. It acts on the *point* velocity rather than the body's, so the same term damps pitch and
roll as well as heave: the six points are spread out, and a rocking hull has them moving in opposite
directions.

**It rights itself with no code at all.** The centre of mass sits a quarter of a metre below the
floats, so buoyancy acts above weight and the pair is a righting couple. That is how a real boat does
it, and it is the opposite of the car, which needs a timer and a hop because a car on its roof is
genuinely stable.

**Directional stability is where the lateral drag is applied, not how big it is.** The first working
version put both drag components through the centre of mass and measured 1173 degrees of yaw in eight
seconds — a 1.7 metre radius, which is not a boat, it is a fidget spinner. Sideways drag through the
centre of mass produces no moment, so nothing ever straightened the hull out. Moving that force 1.2 m
aft gives a hull that is sliding sideways a restoring moment from the water on its keel, the same way
fletching straightens an arrow, and the turn dropped to 22 degrees a second on a thirteen metre
radius - something a person could steer. Forward drag stays at the centre of mass, because a hull
going the way it points should not be turned by going there.

The harness checks the ceiling as well as the floor for that reason. "Does full lock turn it" passes
for a boat that pirouettes, and a test that only has a floor would have shipped the fidget spinner.

Two smaller things that make it read as a boat rather than as a car on ice: the rudder scales with
speed through the water rather than with throttle, so a stopped boat cannot pivot on the spot and
reverse steers the other way; and everything — thrust, rudder, drag — scales with how many floats are
actually wet, so a hull launched off a wave has no propeller and no rudder until it lands.

The boat is generated by `BoatBuilder`, same as the buggy and for the same reason. One difference
worth naming: **the doors open onto the deck.** A car puts people down beside itself and a boat doing
that drowns its own passengers.

It is baked at a mooring off the camp beach, and because a POI is placed on the ground and the ground
there is underwater, it starts on the seabed and floats itself up in the first second. That needed no
new placement plumbing at all — buoyancy is already the thing that decides where a boat's waterline
is.

#### What `-boatTest` actually measures

Three numbers, all taken against the sea *directly under the hull* rather than against a flat sea
level, because the sea is not flat.

```
[BoatTest] moored on the seabed at y -0.72, floated to -0.66.
[BoatTest] 20s adrift: freeboard 0.59-0.70m, heave up to 0.31 m/s, heel up to 3.5 degrees.
[BoatTest] 12s of throttle: peak 12.1 m/s (44 km/h); coasted down from 11.9 m/s in 7.3s.
[BoatTest] full lock at throttle 0.8: 179 degrees over 8s (22 deg/s).
[BoatTest] pushed 4m under: surfaced in 2.5s, peaking at 1.3 m/s upward.
[BoatTest] 12s of driving with four aboard, peaking at 12.1 m/s: worst drift 0.040m, least freeboard 0.38m.
[BoatTest] 25 passed, 0 failed.
```

Freeboard is the acceptance in one number: the deck never went under and the keel never came out, for
twenty seconds of waves. Heave is the damping check — a 62 cm sea with a ten second period moves a
floating thing at about 0.2 m/s, and 0.31 is that. Metres per second would be an underdamped spring.

The swamping test is the clamp on the buoyant force, tested. Four metres under a 0.7 m draft would
otherwise be nearly six times the hull's weight in lift, and the boat would leave the sea like a cork
out of a bottle — funny once, then a boat on the roof of the shop. Clamped, it comes back up at
1.3 m/s.

The suite runs at (2000, 2000), two kilometres off the island, for the reason #58 learned the
expensive way. Unlike the car's test pad this cost nothing to build, because the water is a function
rather than a collider: open sea is anywhere the island is not.

#### A correction to #58

Regenerating the buggy prefab for this issue turned up that **#58's raised centre of mass never
reached the game.** `_centreOfMass` was changed from `-0.1` to `0.35` in C#, the harness went green,
and the value serialised in `Buggy.prefab` stayed at `-0.1` — a prefab's stored field beats the
field initialiser, and nothing in the loop regenerated the prefab. The flip test passed throughout
because it applies its own torque, so it was measuring the recovery rather than the tipping.

Both prefabs are regenerated from code here. The buggy now actually has the centre of mass the issue
shipped on paper, and it shows: it rights itself in 0.3s rather than 0.8s, and it stops from 22 m/s
in 13.7 m rather than 15.0.

Two assertions went stale in the same moment and are fixed here rather than left red:

- `-carTest` demanded the centre of mass be *below* the chassis floor, which is the opposite of what
  #58 decided. It now checks for axle height.
- `-vehicleTest` teleported a body half a metre up and asked whether it had moved. Gravity answers
  that with "no" as soon as it lands, so the check was really a question about how long the settle
  wait is. It now teleports two metres sideways, away from the buggy.

Regressions: `-carTest` 22/22, `-vehicleTest` 79/79.

**Not done here:** the boat is host-simulated with no prediction, like the car, so a driver on 100 ms
steers 100 ms late. Nothing is tied to it, nothing can be towed, and there is no anchor — a boat left
running drifts wherever the drag lets it. It does not take damage from anything it hits, it has no
wake and no engine sound, and #69's unlock gate does not exist yet, so right now it will happily
carry four people to an island that has nothing on it.

### Running people over (#60)

A vehicle that hits a person hurts them, stuns them and sends them flying, all three scaled by how
fast it was going. The issue calls it a non-negotiable feature, and it is worth saying what it cost:
`VehicleImpact` is eighty lines and adds nothing at all to the combat model.

It could not, because the combat model already did the whole job. A spearman's blow builds a
`DamageInfo`, calls `Health.TakeDamage`, then `StunState.ServerStun(info)`, which ragdolls the victim
and fans the impulse out to every client through an ObserversRpc. `DamageType.Vehicle` had been
sitting in the enum since the first damage commit. A car is a spearman with a bigger number, so this
component's entire job is turning a collision into that number, on the server, in `OnCollisionEnter`.

**Impact speed is the vehicle's own speed, not the relative speed.** A player is a
`CharacterController`, so how fast they were walking barely shows up in `Collision.relativeVelocity`
at all - and the more useful half is that sprinting into a parked car should do nothing, which the
vehicle's speedometer answers for free. Below `_minSpeed` (3.5 m/s) nothing lands, or four people
standing around a boat at a jetty would be permanently on the floor.

Three things had to be measured rather than reasoned about, and each one cost a build.

**A 900kg chassis at 22 m/s covers 44 centimetres per physics step**, which is wider than the person
standing in front of it. Discrete collision detection tunnels straight through, and the feature is
then silently absent rather than broken. The buggy's rigidbody is `ContinuousDynamic` for that reason
and no other.

**`linearVelocity` read inside `OnCollisionEnter` is a post-solve number, and it spikes.** The first
run measured a buggy limited to 22 m/s hitting somebody at **57 m/s**, which paid out 200 damage on a
100hp player and threw the body a hundred and fifty metres into the air - one bug, four red
assertions, because a victim left Downed then failed the recovery and the parking-speed sections too.
A chassis sweeping into a CharacterController comes out of PhysX depenetration carrying a velocity
that never existed. `FixedUpdate` runs *before* the step, so caching the speed there is the speed the
collision was actually delivered at.

**The car has to stop touching the body it just launched.** With the speed fixed, the numbers were
honest - 22.1 m/s, 77 damage, 1213 Ns - and the body still went 225 metres and 186 metres *up*, from
an impulse worth about fifteen metres a second to a 56kg ragdoll, which is four metres of air. The
launch was not doing it. The car was: still doing 22 m/s, it spends the next second shoving a fresh
ragdoll along the ground, and PhysX resolves the overlap by firing it out like a bar of soap. So the
vehicle passes *through* its victim for a second and a half after a hit, using the same
`Physics.IgnoreCollision` trick `Carryable` already uses to stop a carried body fighting its carrier.
That gives `_launchPerSpeed` back its meaning: 55 N-s per metre per second, which at full speed is
1213 N-s, slightly more than a spearman's 1000.

Damage is 3.5hp per m/s, so a full-speed run-over takes 77 of a player's 100 and leaves them standing
at 23. Surviving being hit by a car at 79 km/h is the correct amount of goofy; dying on the spot is
not funny even once.

Riders and carried bodies never reach any of this. `VehicleRider` and `Carryable` already put an
`IgnoreCollision` pair between an occupant and the hull, so PhysX never raises the contact - which
matters, because otherwise driving with four aboard would kill the car park.

One side effect is worth recording, because it is the sort of thing a new feature is good for.
`-vehicleTest` went red on a section that had nothing to do with #60, and the cause turned out to be a
buggy that had been quietly careering across the island for months. `Driving` makes the chassis
kinematic, slides it 180 metres at 30 m/s and hands it back to physics, which returns that momentum
the instant it goes dynamic again - and it is handed back a long way from camp, on a slope, with
nobody at the wheel. Nobody had ever noticed, because until now a runaway car could not do anything
to anybody. It was flattening the spare bodies parked around it, and the next section then tried to
seat a heap on the ground.

The suite zeroes the velocity on the way back out, since it teleported the thing and it has not
earned any. That is not the whole of it - the buggy still rolls down the hill it was left on, and
still runs people over at 16 m/s on the way, which is the feature working exactly as asked. So the
section that needs a passenger upright now stands them up first, the same way `Refusals` already did.

#### What `-impactTest` actually measures

```
[VehicleImpact] Buggy (camp.buggy) hit Player(Clone) at 22.1 m/s: 77 damage, 3.8s stun, 1213 Ns.
[ImpactTest] run over at 22.1 m/s (79 km/h): 77 damage, 1213 Ns, thrown 58.4m and 6.0m up, 23 hp left.
[ImpactTest] the victim stood back up after 0.3s on 23 hp.
[ImpactTest] 5s of creeping throttle peaked at 2.4 m/s and hit nobody.
[ImpactTest] drove 22.1 m/s with a passenger: 0 hit(s).
[ImpactTest] 22 passed, 0 failed.
```

Run on the same twelve hundred metres of flat collider as `-carTest`, for the same reason: nothing
that measures a vehicle is measured on terrain.

```
EscapeWithYourFriends.exe -batchmode -nographics -host -port 7953 -playerKey test:host   -scene island -noNatives -noAnimals -impactTest -vehicleLog -quitAfter 200
```

The flight has a **ceiling as well as a floor**, the same lesson the boat's turning circle taught. A
catapult passes "was the body launched"; a floor-only test would have shipped the one that threw
people 186 metres up and never said a word. And one collision must count as exactly one hit - a
ragdoll under a moving car is a stream of fresh contacts, one per bone, so without the per-victim
cooldown the first person run over takes two dozen hits at once and dies instantly.

### Fuel, dents and putting it back together (#61)

A vehicle runs on petrol, breaks when you crash it, and is mended in place with scrap. The issue's
acceptance is the whole design brief - *"wrecking a vehicle costs something but is never
run-ending"* - and both halves of that sentence are load-bearing. A wreck has to hurt, or driving
badly is free and the trader has nothing to sell. A wreck must never be terminal, so **every failure
state is reversed where the vehicle stands, by a player holding an item.** Nothing tows, despawns or
respawns anything, because a buggy you cannot fix where it broke is a run that ended at a tree.

Fuel and integrity share one component. They fail the same way - the engine stops - and they are
fixed the same way - somebody walks over with something in their hands - so two components would be
two SyncVar sets, two interaction branches and two harnesses for no gain.

**Fuel burns per metre, and only with somebody at the wheel.** Time-based burn makes a parked car a
liability nobody asked for, and distance is the number a player can actually plan against: twelve
litres a kilometre out of a sixty litre tank is five kilometres of driving, and the island is one
across. A driverless car rolling down a hill costs nothing, which is right - the engine is not doing
it. The boat carries twice as much, because running dry on land is a walk home and running dry at sea
is exactly the run-ending outcome the issue says never to ship.

**What is in your hand decides what the key does.** A fuel can refuels, scrap metal repairs, anything
else boards, and the crosshair says which before you press. That is one branch in `Vehicle.Interact`
and no new key, no new screen, and no second `IInteractable` competing for the same press. The prompt
and both server-side halves read the same `ServiceLabel`, so the crosshair and the key can never
disagree.

People do not dent cars - a one-line guard, and without it every run-over, which #60 exists to make
people do constantly, is also a repair bill.

The trader sells fuel and nothing else does: the island gives away scrap metal and makes no petrol at
all. That is #61's "reason to care about the trader", and it is one row in `ShopFactory`.

#### The shelf ignores the list, like everything else here

Adding that row changed nothing, silently. `ShopFactory.EnsureShop` returns the existing
`Shop.asset` untouched, because prices are the sort of thing a human tunes in the inspector - so a
new line in the `Stock` array is invisible, and the only symptom is a shelf that quietly does not
have the thing on it. This is the third asset in the project with that shape, after `POIs.asset` and
every prefab whose serialised field beats its C# initialiser. It now has the same trapdoor the
others do:

```
Unity.exe -quit -batchmode -projectPath . -rebuildShop   -executeMethod EscapeWithYourFriends.EditorTools.ShopFactory.Build
```

#### What `-conditionTest` actually measures

```
[ConditionTest] buggy: 60.0/60L, 100/100 integrity.
[ConditionTest] 10s of throttle: 160m on 1.9L (12.0 L/km, 5.0 km to a tank). 58.1/60L, 100/100 integrity.
[ConditionTest] ran a body over at 20.7 m/s: 57.3/60L, 100/100 integrity.
[VehicleCondition] Buggy (camp.buggy) hit ConditionTest.Wall at 22.1 m/s: -65 integrity, 35/100 left.
[ConditionTest] wrecked, then drained: 0.0/60L, 30/100 integrity, DRY.
[ConditionTest] repaired and refuelled by hand where it stood: 25.0/60L, 100/100 integrity.
[ConditionTest] back on the road: 72m in 6s. 24.1/60L, 100/100 integrity.
[ConditionTest] 32 passed, 0 failed.
```

The gate is checked **through `VehicleRider.Drive`**, not by reading `CanDrive`. A flag being false
proves nothing about whether anything reads it. And the suite runs a positive control first - ten
seconds of owner input that moves the car 160 metres - because a stalled car and a car whose input
path never worked in a headless host look identical from the outside.

The suite's own first run was nine assertions red from one mistake: `Stalling` repaired the buggy to
full before handing it to `Servicing`, which then had nothing to mend, so the interact boarded the
player instead and the next one threw them back out. A test that repairs the car it is about to test
the repair on measures nothing.

### Parts you bolt on (#62)

Five things were on the issue's list - engine, tyres, armour, tank, storage - and four of them
shipped. Each is a **`VehicleUpgradeDef`**: an item id, a slot, a tier and one multiplier over the
number the prefab was baked with.

**There is no upgrade screen, no workbench and no new key.** #61 had already taught a vehicle to look
at what is in your hand and do something with it - petrol refuels, scrap repairs - so a part is the
third answer to that same question. You buy it off the trader's shelf like any other item, carry it
to the vehicle, stand next to it and press E. The whole of the fitting flow is one extra branch in
`Vehicle.ServiceLabel`, which is also what draws the crosshair prompt, so the label and the key
cannot disagree about what the press will do.

**What a vehicle accepts is a list on the vehicle, not a global catalog.** `VehicleUpgrades._fits` is
serialised onto each prefab by its builder: the buggy is given four parts and the boat three. Fitting
tyres to a hull is not refused by a check, it is impossible, because the hull has never heard of
them. That is also why there is no `VehicleUpgradeCatalog` beside the other six - nothing crosses the
wire but the tier numbers, so there is no index to agree on and no static to keep alive.

**Multipliers are absolute, over stock, never stacked.** A fitted engine means "1.5x the buggy as it
was baked", not "1.5x whatever is bolted on now". `Apply()` reads the fitted tiers and rewrites every
affected number from the stock values each controller captured in its own `Awake`, so fitting a part
twice, applying after a late join, or replaying the list in any order all land on exactly the same
vehicle. Stacking is how a second fit doubles a number nobody meant to double.

| Part | Price | What actually changes |
|---|---|---|
| Tuned Engine | 330 | `CarController._motorTorque` **and** `_topSpeed` (`BoatController._thrust` and its ceiling). Torque alone buys a shorter run-up to the same limit, which is not what anybody paying for an engine means |
| Grippy Tyres | 240 | `stiffness` on both friction curves of every `WheelCollider`. It is the wheels that let go; a grip upgrade that did not touch them would be a lie told in the UI |
| Bolt-on Armour | 270 | `VehicleCondition._integrityMax`, **and the extra integrity is handed over**. Plate on a dented car makes it tougher; a version that only moved the maximum would leave a player looking at a car that got more broken the moment they paid |
| Long-range Tank | 210 | `VehicleCondition._tank`. The tank gets bigger; filling it is still the trader's business |

Against the boat part at 1400, a full set of four is most of a run's savings, which is the intended
decision: every one of them makes the rest of the run measurably better, and `-vehicleUpgradeTest`
is where "measurably" is a number rather than a claim.

```
[VehicleUpgradeTest] stock tyres: 531 degrees of yaw in 6s at 10.0 m/s.
[VehicleUpgradeTest] grippy tyres: 578 degrees of yaw in 6s at 10.0 m/s (1.09x).
[VehicleUpgradeTest] stock engine: 117m in 8s, peaked at 22.2 m/s.
[VehicleUpgradeTest] tuned engine: 175m in 8s (1.50x), peaked at 33.2 m/s.
[VehicleUpgradeTest] long-range tank: 60.0/90L, 100/100 integrity.
[VehicleUpgradeTest] bolt-on armour: 60.0/90L, 95/160 integrity.
[VehicleUpgradeTest] 54 passed, 0 failed.
```

Every one of those is driven, not read back: the suite puts the part in a player's bag, selects it,
presses interact next to the buggy, and then drives the same manoeuvre again.

**Two of the measurements were wrong before they were right, and both were the same mistake** - a
number that moved for a reason other than the thing being measured.

The cornering test first ran at full throttle, and grippier tyres came out *worse*: 425 degrees of
yaw against 395. Forward grip is grip too, so the fitted car reached a higher speed in the same six
seconds, and a car going faster on a steering lock that tightens with speed draws a wider circle.
What looked like a failed upgrade was a measurement of acceleration wearing a cornering costume.
Held at ten metres per second - both cars far past what their tyres hold at full lock - the same
upgrade reads 1.09x, and a third assertion now checks that the two runs were driven at the same
speed before the comparison is allowed to mean anything.

The lateral-velocity number that went with it is logged and no longer graded. Sideways velocity at
the centre of mass on a circle is mostly kinematic - yaw rate times the distance back to the rear
axle - so a car that corners tighter reads *higher* on it while gripping better. It stays in the log
because it is what explains a strange yaw reading, not because it grades one.

The armour test hit a car that was still carrying the fifty points of damage the previous assertion
had dealt it, then compared what was left against a fraction of the new, larger maximum. Two dents
are not one crash. It now repairs to full before the yardstick 65-point hit, which is the crash that
leaves a stock buggy on 35 of 100 and an armoured one on 95 of 160.

**Storage capacity is the one that did not ship.** `Storage` is a working networked container and
bolting one to the buggy is one line, but it is also an `IInteractable`, and
`PlayerInteractor.ServerInteract` resolves a target with `GetComponentInChildren<IInteractable>()` -
the first one on the object, whatever the client was actually aiming at. A vehicle with a boot needs
the server to disambiguate two interactables on one `NetworkObject`, which is an interactor change
rather than an upgrade one, and it would have been the larger half of this issue.

### Chips, and the two doors (#63)

**A chip is a number in the same wallet as the money.** `Wallet` grew a second `SyncVar<int>`
rather than a second component, because chips obey exactly the rules money already obeys - server
writes, everyone reads - and the only interesting part is the boundary between them.

That boundary is two methods: `ServerBuyChips` takes money and gives chips, `ServerCashOut` takes
chips and gives money, both one for one, both take-before-give in a single call. **They are the
only two doors, and there is no third one anywhere in the project.** That is the whole of #63's
acceptance - *no path for real money to enter or leave* - held structurally instead of by a rule
somebody has to remember. Chips cannot be bought with anything that is not already money in a
wallet, money cannot be got out of chips except by walking back to the cage, and nothing outside
the casino takes a payment in chips. The shop counter reads `Balance` and nothing else, which the
harness asserts directly: a player holding five thousand chips and no money cannot buy a rope.

One for one, deliberately. A house rate on the exchange would be a second place where value leaks,
and the house already has an edge at the table.

**Neither direction touches `Minted` or `Burned`.** Those count value created and destroyed, and an
exchange is the same value wearing a different hat; counting it would have the ledger report the
casino as printing money every time somebody bought a stack. `Exchanged` is a separate diagnostic
counter that goes up on a buy and down on a cash-out, so it reads as *money currently sitting on
tables*. What a conservation check watches is `TotalInWallets() + TotalChips()`, which no exchange
can move by a single unit - the harness takes that number before a run of buys and cash-outs and
asserts it identical after.

#### Two windows, not one booth

The cage is two prefabs, `ChipWindow` and `CashWindow`, three metres apart on the way in, differing
only in sign colour and a baked `CageDirection`. A single booth that has to mean two opposite things
from one key needs either a rule about which - *it cashes you out unless you are broke* - or a
second key nobody would find. Two booths cost one extra prefab and one extra line in the POI list,
and the player picks by aiming, which is a thing they already know how to do.

It also sidesteps the trap #62 hit from the other side: the server resolves an interaction with
`GetComponentInChildren<IInteractable>()`, so two `Cashier` components on one `NetworkObject` would
quietly have been one. Two objects, two interactables, no disambiguation needed.

Each press moves a fixed hundred - enough to bet with, small enough that a bad night is several
decisions rather than one - **or everything that is left, if that is less**. The remainder is the
part that matters: a player with forty chips left has to be able to get their forty back, or the
cage has quietly eaten them, which is the one thing this issue must never do. A window with nothing
to move returns an empty `Prompt`, and the interactor skips those, so a broke player walks past the
buy window instead of pressing a key that does nothing.

The windows trade in chunks so they need no screen. #65 brings the casino UI, and when it does it
calls these same two wallet methods with a number the player typed.

#### The check that passed for the wrong reason

The trader-does-not-take-chips assertion went green on its first run and was wrong. It set a wallet
to no money and five thousand chips, asked the shop counter for a rope, and got nothing - but the
refusal it printed was *you are not at the counter*. The player was standing wherever the spawn had
left them, two hundred metres from the shop, so `ServerBuy` bailed on distance before it ever looked
at the money. A pass for the wrong reason is worse than a failure, because a failure argues with
you.

It now teleports the player to the counter, asserts they are in reach, asserts the refusal is *not*
about the counter, and then - the part that actually settles it - gives them real money in the same
spot with the same chips still in hand and buys the rope. The purchase is the control: it proves the
counter was willing and the only thing that changed was which balance the coins came from.

Same lesson as #62's tyre measurement, from the other direction: there, a number moved for a reason
that was not the upgrade; here, a number stayed still for a reason that was not the rule. Both are
the same mistake, which is grading something the test did not isolate.

### The wheel does not decide anything (#64)

A single-zero wheel, thirty-seven pockets, and the house edge is the green one.

**The server rolls, then tells everybody the answer.** `Round()` closes the betting window, takes a
number out of a server-side `System.Random`, writes it to a `SyncVar`, and sends one `ObserversRpc`
carrying that number. What crosses the wire is the result - not a seed, not a wheel speed, not a
request to roll - so there is no message a client can send that reaches the outcome, and no timing
it can win. That is #64's acceptance, and it is the same shape as every other authority decision in
the project.

The animation is therefore cosmetic by construction. Each peer eases its own wheel through four
turns and stops on the pocket it was handed, and if it ever stopped somewhere else the only
consequence would be a wheel lying about a payout that already happened. Which is exactly what the
harness looks for: it reads the angle back off the transform after the spin and asks which pocket is
under the marker, twelve rounds running.

#### Ten squares instead of a screen

Betting is ten small objects on the baize - red, black, odd, even, low, high, three dozens and a
straight bet on seven - and the player chooses by aiming at one. The betting UI is #65's job and
needs an interior to sit in; until then the lazy version of *multiple bet types* is the thing a
roulette table already is, a board with the bets written on it.

**Each square is its own nested `NetworkObject`, and that is the load-bearing part.** The client
sends the object it aimed at and the server resolves the component with
`GetComponentInChildren<IInteractable>()`. Ten `BetSpot`s parented to one networked root would every
one of them resolve to whichever came first, and every bet in the game would land on red. This is
the third issue in a row to be shaped by that one line in `PlayerInteractor` - #62 dropped a feature
over it, #63 built two prefabs to avoid it, and #64 finally pays the small cost of nesting.

#### The half a host cannot check

The first run of the harness was green and only half a test. It ran on the host, where the server
and the client are the same process and the wheel is animated by a direct call rather than by the
RPC - so the thing the acceptance is actually about, *what a peer that is not the server ends up
showing*, was never observed. A host cannot receive its own `ObserversRpc`.

So `-rouletteTest` now has a second, much smaller suite that runs when the process is a client: it
finds the table, waits for four spins to settle, and each time asserts that the pocket under its own
marker is the number the `SyncVar` says the host rolled. The host, having finished its own
assertions, keeps spinning on a loop until it is killed, purely so the client has wheels to watch.

Getting there turned up a real gap in the scaffolding rather than in the feature. **Every harness in
the project is started from `OnServerConnectionState`**, which is right for all of them - they all
assert about server state, and the ones that need two players have the host drive the second body.
A pure client therefore ran nothing at all, silently: the first two-process run produced a client
log with no `[RouletteTest]` line in it, not even the error branches. `NetworkBootstrap` now also
starts this one from `OnClientConnectionState`.

And then, once it ran, it failed - which is the whole reason for writing it. **The host said 10 and
the client's wheel was showing 0.** The spin duration was a server-side field: the server had been
told to spin for two seconds, the client was still easing through the prefab's five, and it was
being read a number it had not arrived at yet. Nothing about the result was wrong and nothing a
client did could have changed it, but the wheel in front of a player would have been pointing at
somebody else's number when the chips moved.

So the RPC carries the duration as well as the pocket, and every peer animates for the same length
of time the server did. The harness waits on its own wheel rather than on the server's clock, since
the RPC still lands a tick late. Three of the four checks in this issue came from the two-process
run; none of them were visible from the host alone.

The last one it turned up is a join, not a spin. A client that arrives while the wheel is already
turning never receives that round's RPC and sits with its wheel wherever the prefab was saved -
pocket zero - until the next round. The wheel now snaps to the last number in `OnStartClient`, so a
player walking into the casino sees the table the way a real one looks, and the harness lets any
round that was already in flight when it joined finish unwatched, because grading it would be
grading the join.

#### The arithmetic, on paper

Stakes leave the wallet when the bet is placed, not at settlement: a bet you can walk away from is
not a bet, and it means the table never has to chase somebody who disconnected mid-spin. A winner is
handed the stake back *plus* the odds, because the stake already left.

The pay table is the real one - 35 to 1 on a number, 2 to 1 on a dozen, evens on the rest - and the
harness checks it as arithmetic rather than as a comment. For every kind of bet, the number of
pockets it wins on times what it returns comes to exactly 36 for every 37 staked. Red wins 18 and
pays 2; a dozen wins 12 and pays 3; a straight wins 1 and pays 36. Any bet that came to 37 would be
a casino that loses money, and the 2.7% is the same whichever square you stand at.

**Chips move at a table; money never does.** `ServerStakeChips` and `ServerPayChips` touch only the
chip balance, so `TotalInWallets()` is the same number before and after a spin whoever wins - which
is what keeps #63's rule true now that there is something to lose chips on. They are not doors: the
only two ways value crosses between money and chips are still the two cage windows. Staked and paid
chips get counters of their own rather than riding on `Minted` and `Burned`, because the house is
not a wallet - a losing stake is simply gone and a win is simply made.

### A casino built by people stranded on an island (#65)

The shape of the room was right from the blockout - three walls, an open front, one table four
people crowd round. What it was missing was any reason to believe somebody built it.

So the greybox casino was rebuilt around the table #64 put in it: a floor of mismatched decking
instead of sand, a front wall with a two-metre doorway rather than a missing side, crates to sit on,
a bar with bottles on it, a chandelier that is seven bottles on a wire, and a sign nailed over the
door at an angle nobody could be bothered to fix. Everything in there is salvage, which is the
brief.

**The lighting is the part that does the work.** Five point lights, no two the same colour, none of
them where a lighting designer would put one, and a `TackyLights` component that walks each of them
round the colour wheel on a fourteen-second cycle and breathes their brightness out of step. Every
other room in this game is lit and left alone; this one will not sit still, and that is the whole
difference between a shack and a casino. None of them casts a shadow - five shadow-casting lamps in
one small room is a slideshow on the GPU this game has to run on, and the flatness suits it anyway.

Nothing about the lights is networked. Each peer runs its own chase off its own clock, because two
players seeing slightly different shades of magenta is not a bug anybody can have. On a headless
host the component switches itself off, since there is no graphics device to light.

#### The board, which is a sign rather than a menu

Over the table, and only while you are standing at one: the last number in its own colour - red,
black, green for zero - and a line under it saying what you hold, what is on the cloth, and whether
the table is still listening. It appears within six metres and is gone again when you walk away.

It is deliberately not clickable. Betting is done by aiming at a square and pressing the interact
key, so the board needs no `EventSystem`, no raycaster, and no chance of eating a mouse click the
game wanted. Everything it draws is already replicated onto this peer, which is what lets a player
watch somebody else's stake land without a round trip.

That replication needed one addition. `RouletteWheel` knew what was staked because it holds the
server's own list of bets, and that list is empty on every other peer - a board reading it would
have shown every player their own nothing. The pot is now a `SyncVar` alongside the result.

The strings are pure statics, like `Purse.Text`: a headless run has no canvas, and the claim worth
testing is what the words say, not that a rectangle was laid out. `-casinoTest` reads them in all
three states a table can be in.

#### What a harness can hold of "it reads as a casino"

Nothing, directly - that is a judgement somebody makes by looking. What it can hold is everything
that would have to be true first, and each of these has been wrong in some build: there is a floor,
a roof, three walls and a doorway wide enough for four people arriving at once; the table is inside
the room rather than clipping a wall or out on the sand; there is somewhere to sit and something to
drink at; the lamps are there, there are several, and no two agree on a colour.

The table check is the one that earns its keep. The building is placed by the greybox builder and
the table by the POI catalogue, two systems that have never met, and nothing but arithmetic keeps
them agreeing. A table half inside a wall is the most likely way this issue quietly breaks later.

One thing to know before writing another test that looks a landmark up: **`POISpawner` overwrites
`Landmark.Id` with the catalogue entry's id as it spawns.** The prefab is a *kind* of place and the
catalogue entry is *this particular one*, so the building the greybox builder called `Casino` is
called `casino` by the time anything can see it. This suite sidesteps the question by finding the
landmark nearest the table, which is also what it actually means by "the casino".

### The drink, and what it costs (#66)

There is a man behind the bar now, and he sells one thing.

The buff he sells has been sitting in `BuffFactory.Seeds` since #45 with a comment saying nothing
applies it yet. It was written so that the casino's alcohol would be an asset rather than a system,
and that held: **this issue added no buff system, no drink system and no NPC system.** What it added
was an item, a row in a table, a shopkeeper, one field, and a volume.

#### The barman is a shop with one line on the shelf

`ShopCounter` already knows how to take money, hold stock, restock, refuse a trade from too far
away, and open a trade UI. A barman is a shopkeeper with one thing to sell, so the barman *is* a
`ShopCounter`, pointed at a second `ShopDef` — `Bar.asset`, one offer, unlimited, grog at 25 against
an item worth 12. The prefab is seven boxes and a hat.

**He takes money, not chips.** Nothing outside the cage has ever taken a payment in chips (#63), and
that line is worth more than the convenience of paying for a drink out of your winnings: it is what
keeps "chips buy nothing outside the casino" literally true, which is the sentence #67's compliance
checklist needs to be able to say without a footnote. The harness checks it the way #63 learned to —
standing at the bar, with the refusal read to make sure it is about the money and not the distance.

He stands in the gap between the bar and the back wall that #65's greybox already marked
`BarNpcStand`. That gap was 20cm, which is thinner than a man, so the bar moved forward 15cm. His
placement is also the one POI entry in the catalogue that is **not** rounded to the metre grid: the
slack there is 40cm and the rounding is 50, so `Entry(..., exact: true)` exists for him and for
anything else later that has to stand somewhere specific rather than somewhere near.

#### Two halves of a trade, and both of them measured

The acceptance is that the buff is genuinely tempting and the vision genuinely a handicap. Neither
is assertable. What is assertable is the arithmetic each judgement rests on, and the interesting
thing is that the two halves are enforced in completely different places:

- **Tempting** is `DamageTakenMultiplier` at 0.75 — a quarter off every hit, for ninety seconds,
  for the price of one boat-part-and-a-half of nothing. That already reached `Health` before this
  issue; the harness measures it as a number by landing the same 20-point hit sober and drunk.
- **The handicap** is a new field, `BuffDef.AimWobble`, in degrees, added to the weapon's own spread
  on the server. Added rather than multiplied, because a pistol's spread is zero and a multiplier
  applied to zero is a drink that does not affect aim at all — which is the trap that made the
  original description ("much harder to aim") a lie for four of the six guns.

Seven degrees is the number. That is wider than a shotgun's own cone (6.5) and about thirty-five
times a rifle's (0.2), so drunk sniping is over and drunk brawling is fine. Like every other number
in `Weapon`, the client never sends it: the client sends a direction, and what the drink does to that
direction is not theirs to leave out.

#### Firing sixty shots at the sky to see where they went

The check that matters is not `AimWobble > 0` — that passes on a build where nothing reads the
field, which is exactly the bug worth catching. `-drunkTest` equips a pistol, fires sixty shots
straight up sober and sixty drunk, and takes the angle of each one off the `Fired` event, which is
the same event that draws the tracer. Straight up, because a ray into the sky hits nothing and comes
back at full range; a shot into the hillside gives the same angle with a much shorter arm.

```
[DrunkTest] the barman is standing 4.5m from the wheel.
[DrunkTest] 60 shots sober: 60 shots, 0.72° average, 1.37° worst.
                     Drunk: 60 shots, 4.12° average, 8.24° worst.
[DrunkTest] 36 passed, 0 failed.
```

The worst shot of sixty is the number that pins it: 1.37° against a pistol's own 1.5, and 8.24°
against the 8.5 the drink is supposed to add. Nothing in the harness reads `AimWobble` to decide
what to expect - it reads the asset and the gun, adds them, and checks the shots came in under.

This is #63's lesson in a different hat. A refusal from four hundred metres away looked like a rule
about money; an assertion about a field looks like a rule about aim. Both pass for the wrong reason,
and the fix in both cases is to measure the thing the player experiences rather than the thing the
asset says.

#### The blur is owner-side and unnetworked

`DrunkVision` builds a `VolumeProfile` in code — gaussian depth of field, chromatic aberration, film
grain, and a small barrel distortion — and drives its weight off `BuffState.Haze`, which has existed
since #45 with nothing reading it. Gaussian rather than bokeh because this has to run on an
integrated GPU, and the weight tops out at 0.85 rather than 1 because at 1 the player cannot find the
door, which is annoying rather than funny.

Nothing about it is networked, and that is deliberate: **the haze belongs to a pair of eyes, not to a
body.** Every peer already has the buff list it needs to compute its own, a spectator watching
somebody else drink should see their own sober picture, and a headless host has no screen at all —
so the component switches itself off unless it is the owner and there is a graphics device.

One flag had to be turned on for any of it to appear. URP ignores every volume in the scene unless
the camera asks for them, and a camera built in code does not ask; nothing in this project had needed
post-processing before, so `renderPostProcessing` gets set exactly once, on the one camera this peer
looks through.

The camera also leans. That is separate from the volume and lives in `PlayerCameraRig`, next to the
bob and the trauma shake, because it is the same kind of thing — but it is applied through neither of
them: trauma is a sharp Perlin jitter that decays in a second, and this is a slow lean that lasts a
minute and a half. Three sines at frequencies that do not divide into each other, roll about three
times the size of pitch and yaw, so the horizon tips rather than rattles. A drunk person's horizon
tips; a rattle reads as an explosion. It is a pure static function so the harness can hold it to a
number with no screen in the process.

### The sentence the store page has to be able to say (#67)

Valve bans real-money gambling, and a store questionnaire answered wrongly about a casino is an app
review rather than a bug report. So the checklist lives in [COMPLIANCE.md](COMPLIANCE.md), and the
rule it encodes is a design constraint on everything in `Casino/` and `Economy/` rather than a
paragraph somebody wrote once:

**Money enters this world in exactly two ways** - the 500 a player starts with, and selling something
to the trader - and **chips have exactly two doors**, both of them the cage window. No third path has
ever existed, which is why the barman in #66 charges money rather than chips even though he stands
ten metres from a roulette table. That was the cheaper decision *and* the one that keeps the sentence
"chips cannot be bought with real money and cannot be cashed out" true without a footnote.

Each row of the checklist names where the claim is enforced and which harness proves it, because a
compliance claim nobody can re-check is a promise rather than a fact. What would break it is written
down too: a DLC that grants a starting purse, any way to move a wallet between accounts, or any
randomised reward behind a paid door. Adding a second game to the casino is fine; adding a price tag
to the door is not.

### CI is one build, and it sits out until it is paid for (#10)

`.github/workflows/compile.yml` runs on every push to `main` and on every pull request. It is a
real player build rather than a script-only pass, because Unity refuses to run `-executeMethod`
at all when compilation fails: a build that finishes is proof the code compiles, and it proves
the three enabled scenes still load on the way past. It is the same entry point a person runs
locally - `BuildTool.PerformBuild` with `-scriptingBackend mono` - so there is no CI-only path
that can rot without anybody noticing. Mono because it builds in about a minute against IL2CPP's
ten, and compiling is the whole point; `BuildTool` puts the project's own backend back afterwards.

The job is gated on a `UNITY_LICENSE` secret and **skips, green, when there is none**. A runner
cannot start Unity unactivated, and a workflow that fails red on every push until somebody does
paperwork gets muted inside a week, which is worse than having no workflow at all. #10 itself says
to defer if the licence flow is painful, so the deferral is built into the file: it ships ready and
costs nothing until the secret appears.

To turn it on, add three repository secrets (Settings -> Secrets and variables -> Actions):

| Secret | What goes in it |
|---|---|
| `UNITY_LICENSE` | the full contents of a `Unity_lic.ulf` file, obtained by running [game-ci's activation](https://game.ci/docs/github/activation) once: it emits a `.alf`, which `license.unity3d.com/manual` trades for the `.ulf` |
| `UNITY_EMAIL` | the Unity account the licence belongs to |
| `UNITY_PASSWORD` | that account's password |

Two things about the file that look like mistakes and are not. The licence check is a shell step
writing to `$GITHUB_OUTPUT` rather than an `if:` on the job, because the `secrets` context is not
available in any `if:` expression - `env` is, which is why the three secrets are lifted into `env`
at the top. And the `Library` cache key hashes `Assets/**`, which is slow to compute and still
cheaper than a cold asset import, which takes longer than the build.

What CI deliberately does not do is run the headless harnesses. Those need a *Windows* player plus
a second process on a chosen port, and the assertions live in a loaded scene; reproducing that on a
Linux runner means wine and a display, for tests that already run locally in under a minute. CI is
the compile gate. Testing stays on the machine that can play the game.

One known way for this to fail through no fault of ours: game-ci publishes its editor images per
Unity version, and a brand-new version can be missing for a while. If the job dies pulling
`unityci/editor:ubuntu-6000.3.23f1-windows-mono-3`, the fix is to wait or to pin `unityVersion` to
the nearest published one, not to go looking through the C#.

### The second island is a second set of numbers (#68)

There is no second generator. `TerrainGenerator` takes `-island 2`, which points its five asset
paths at a second set - `Island2.asset`, `Island2Terrain.asset`, `POIs2.asset`,
`Island2NavMesh.asset`, `Scenes/Island2.unity` - and everything downstream reads the new
`IslandProfile.Id` instead, because the profile was already threaded through every factory. The
first island keeps the names it has always had, so its GUIDs and every scene reference to them
survive this change untouched.

```
Unity.exe -quit -batchmode -nographics -projectPath . \
  -executeMethod EscapeWithYourFriends.EditorTools.TerrainGenerator.GenerateIsland \
  -island 2 -rebuildPois -logFile island2.log
```

The whole feature is: five paths, two branches and a float. The branches are the POI catalog and the
camp list; the float is fog.

#### Hostile in three parts, none of them new machinery

**Shape.** `Harden()` writes the second island's parameters once, when the asset is created, and
after that the YAML is the truth like everywhere else. Half the size on each axis with the sample
grids halved to match, so the metres per sample never change. Taller and choppier relief out of less
ground, a coast of headlands instead of a ring of sand, the beach band cut from five metres to 1.6
and the shore left at its natural slope, rock from 26 degrees and from 40m up, and no palms at all -
a palm reads as holiday. What comes out is 512m square, a fifth of it dry, averaging
**23.2 degrees of slope**, with **2% sand against 37% bare rock** where the first island has beaches
and jungle.

**Weather.** `DayNightCycle.FogScale` multiplies the fog density the shared sky profile asks for,
and the second island asks for two. That is the whole of "worse weather": one climate, turned
murkier. It cuts the sight line from 285m to **142m**, so a headhunter with 75m of vision sees you
at nearer the distance you see it. A second `DayNightProfile` with its own gradients was the
alternative, and would have been fifty lines to say the same thing twice.

**People.** The `headhunter` is a spearman by behaviour - #68 adds no AI - and everything else about
it is worse: 140 health against 85, 34 damage against 22, a scout's eyes, and `fleeAt 0`, so it
never runs. It is also worth taking: about 153 coins on a wild body against a spearman's 67, and it
can be carrying a pearl. Five camp lines put nine of them out by day against the first island's
five, on a quarter of the ground.

#### Two bugs the bake found that the code review would not have

**Baking the second island repainted the first island's sea.** `WaterFactory` bakes a depth mask
from the coastline it is given and writes it, the material that samples it and the prefab that
carries the material to three fixed paths. Generating island 2 therefore silently gave island 1 a
surf line drawn round a different island - a difference nobody would see in a diff and everybody
would see standing on the beach. Those three paths now carry the island's suffix; the meshes and
the ripple texture are shape-independent and stay shared.

**`NavFactory` caught a cave that `POIFactory` said was fine.** The first bake put the cave mouth
at 54m up a slope. POIFactory's own reachability check walks a coarse grid and reported all five
landmarks reachable; NavFactory asks the actual NavMesh for a path and found the cave sitting on its
own disconnected island, with two camps of natives on it that could never reach anybody. The pad
flattens the ground a building stands on, which is exactly what hides this: the mouth is walkable
and nothing around it is. The site wish now asks for 30m and weights flatness at 0.85, which moved the mouth to (23, -98)
and turned the verdict into `cave -> camp.base: PathComplete, 214m over 9 corners`. The check that
matters is the one that runs a pathfind.

#### What the harness measures, and what it refuses to

`-island2Test` wants `-scene island2`, and fails loudly on any other map rather than passing
vacuously. The acceptance is "visibly and mechanically more hostile than island 1 within 30 seconds
of landing", and none of those words is assertable, so what is checked is the arithmetic each one
rests on, read off the island that was actually baked:

* the terrain, sampled 96x96: size, mean slope, and the sand and rock fractions of the dry land;
* the fog, read back from **`RenderSettings.fogDensity`** and divided by what the sky profile asked
  for. Asserting `FogScale` on the component would pass on a build where nothing multiplies it
  through, which is the bug worth catching - the same lesson as #66's sixty shots at the sky;
* the headhunter against **the spearman in the same catalog**, so there is no number in the test to
  go stale. Make the spearman as tough as the headhunter and this fails, which is correct;
* what a body is worth, as every drop line's chance times its average count times the trader's
  price, for both roles;
* the distance from the beachhead to the nearest camp, in metres and in seconds of walking.

It cannot check whether the island *feels* worse. That is the playtest.

```
[Island2Test] 1831 of 9216 samples are dry land: 23.2° average slope, 2% sand, 37% bare rock.
[Island2Test] fog 0.0139 against the climate's 0.0069 (2.00x), so you can see about 142m instead of 285m.
[Island2Test] 5 camp line(s): 9 by day, 14 at night, roles headhunter, blowgunner, scout.
[Island2Test] headhunter 140hp / 34 damage / flees at 0.00, against the spearman's 85hp / 22 / 0.15.
[Island2Test] a headhunter carries about 153.0 coins' worth, a spearman about 67.0.
[Island2Test] the nearest camp is village.headhunter (headhunter) 176m from the landing; 9 native(s)
              live within 220m of it, which is 35 seconds of walking.
[Island2Test] 20 passed, 0 failed.
```

Nothing sails there yet - the boat and the crossing are #69, and the reason to go is #70.

### The boat is a gate, a scene swap and a tow (#69)

Three sentences, and each one is a different part of the code.

**The gate is four parts, and they belong to the group.** A boat part in your hand is the fourth
thing the interact key can mean on a vehicle, after a ride, a can of fuel and a piece of scrap -
`Vehicle.ServiceLabel` already had the shape and `BoatVoyage` only had to answer it. That is the
whole of the interface: no new key, no new screen, nothing else standing on the mooring competing
for the same press. Until the fourth part goes in, the hull refuses the *seat* rather than the
throttle, which is why nothing that could make a boat move needed a check adding to it, and why the
refusal is legible standing next to the thing: *"the boat is 2 part(s) short of finished"*.

What is fitted is counted in a static, because the hull at the far island's mooring is a different
`NetworkObject` in a different scene, and asking a group that has already bought a boat to buy the
second island's one as well would be a bug that looks like a design.

**Travel is a scene swap, not streaming.** The islands are two scenes and nothing on one is ever
visible from the other, so loading both to slide between them would be paying for a view nobody
gets. Sail past the edge of the map, hold it for four seconds with somebody at the wheel, and
`GameSceneLoader.ServerTravel` moves the session. Three things make that work and none of them is
mine:

* **`MovedNetworkObjects`** carries the player bodies into the new scene before the old one goes.
  Without it they are despawned with it, because `PlayerSpawner` deliberately puts each body in the
  island scene so its owner observes it - the comment that says so is the reason this was the first
  thing to get right rather than the last thing to discover.
* **`ReplaceOption.OnlineOnly`** unloads what FishNet loaded and leaves Bootstrap alone, which is
  where the NetworkManager, the spawner and the scene loader live.
* **the boat is a scene object, and scene objects cannot be moved between scenes.** That is not a
  limitation worked around here, it is the design: each island keeps its own hull at its own
  mooring, so wherever you land there is something tied up waiting.

Where the edge is comes from the terrain itself - its own half-extent plus sixty metres - so there
is no marker to place, and the second island gets a smaller crossing for free because it is a
smaller island. Anybody still sitting in a vehicle is put out of it first: you cannot take the boat
with you, and a rider glued to an anchor in a scene that no longer exists is a body nobody can move.

Arrivals are put on the destination's spawn points, read out of the scene that just loaded rather
than out of `PlayerSpawner`'s registry. The old island's `SceneSpawnPoints` hands the spawner a null
on its way out, the new one hands it an array on its way in, and the order of those two against each
other during a swap is not a thing worth depending on.

**Losing the boat is a tow, not a respawn.** A hull that cannot drive - wrecked on a rock, or run
dry halfway across - waits forty-five seconds and then turns up at its mooring, repaired and
fuelled, with the crew still standing on it, because riders are glued to their anchors and a
teleport takes them along. It costs the crossing, which is a punishment, rather than the run, which
on an island with no shop and no fuel would be a soft lock. The condition it triggers on is
`VehicleCondition.CanDrive`, so wrecked and dry are the same code path and neither needed a second
one.

#### The bug underneath, which was never about boats

The first crossing that worked left the first island loaded. Everything on it - the shop, the
casino, twenty points of interest and the boat you had just sailed away from - was still standing,
in world space, on top of the second island. The scene unloaded; its contents did not, because they
were never in it.

Unity puts an `Instantiate` with no parent into whatever scene is **active**, and the active scene
was Bootstrap: the map is loaded additively and nothing had ever asked for it to be made active,
because until something unloaded a map it made no difference. `POISpawner`, `WorldSpawner` and the
item spawners all build their world at runtime with a parentless `Instantiate`, so all of it
belonged to the one scene that never goes away.

The fix is in two places, because the world is built at two different times. Everything spawned
*during play* - a dropped item, a corpse, an animal - is covered by making the island the active
scene when it loads, which is one field on the load: `PreferredActiveScene`. Everything spawned
*during the load* is not, because `POISpawner` runs while the scene is still coming up and the
active scene has not changed yet, so it moves each instance into its own scene by hand. Both, or
the boat stays afloat on the wrong island.

Worth writing down because the symptom named a boat and the cause named every spawner in the
project, and because none of it was visible until something finally unloaded a scene.

#### What the harness measures

`-voyageTest` starts on the first island and refuses to run anywhere else. The acceptance - *"travel
both ways works, and losing the boat is recoverable"* - is unusually checkable for this project, so
it is checked directly, in the order a group meets it: the gate, the sinking, the crossing out, and
the crossing back.

One decision in it is worth writing down. Arrival is confirmed by **measuring the ground under the
player's feet**, not by reading `GameSceneLoader.Current`: that string is set by the same code the
test is testing, and it would happily say `Island2` on a session where nothing loaded. The first
island is 1024m square and the second is 512, so the terrain says where you are and cannot be talked
into lying about it. Same lesson as #66's sixty shots at the sky and #68's fog read back out of
`RenderSettings`.

```
[VoyageTest] the boat went from "the boat is 4 part(s) short of finished" to seaworthy on 4 press(es)
             of the same key that pours fuel in.
[VoyageTest] wrecked 643m from the mooring, back alongside it 0.0m out in 6s, 120.0/120L, 140/140 integrity.
[VoyageTest] Island -> Island2: ashore on 512m of terrain, 72m from the hull moored there, which is seaworthy.
[VoyageTest] Island2 -> Island: ashore on 1024m of terrain, 98m from the hull moored there, which is seaworthy.
[VoyageTest] 29 passed, 0 failed.
```

What it cannot check is whether four parts at 1400 coins feels earned rather than grindy. That is
`EconomyTest`'s arithmetic - the boat is deliberately one and a half to three sessions away - and
then the playtest.

---

### Three parts, and both of your hands (#70)

The boat is bought. The plane is *carried*.

That is the whole design decision, and everything else follows from it. A boat part is an
`ItemDef` you buy at a shop, put in a pocket and forget about until you are standing at the
mooring. A plane part is a rigidbody lying in a native village on the far island that somebody has
to pick up and walk home with, at a third of their normal speed, while the people who live there
notice. Same word, opposite verb: one is *shopping*, and the acceptance on #70 asks for a comedy
set piece.

**It is not an inventory item and deliberately cannot become one.** There is no `ItemDef`, no slot,
no stack. `PlanePart` is a `NetworkBehaviour` on a crate with a `BoxCollider`, and the only two
things you can do with it are lift it and put it down.

#### Why it is not a Carryable

The project already carries things: `Carryable` puts a stunned friend on your shoulder and
`CarrySystem` decides who may lift whom. Reusing it was the first thing tried and the wrong answer,
for one reason. `Carryable` parents a ragdoll's hip to a socket and lets **every peer simulate its
own copy**, which is right for a corpse - two clients disagreeing by a metre about where your friend
landed is the game's whole sense of humour - and wrong for an objective. Two clients disagreeing
about where the engine landed is a run that cannot be finished, and the disagreement would show up
an hour in, in somebody else's session.

So a part follows `WorldItem`'s rule instead: **physics on the server, everybody else kinematic
behind a `NetworkTransform`**. It is never reparented to anything. While it is carried the server
writes its transform onto the carrier's `CarrySocket` every frame, which puts it exactly on the
shoulder where the simulation lives and lets everybody else see it arrive there through the
`NetworkTransform`'s interpolation - so on your screen your own engine is welded on, and on your
friend's screen it swims along behind you. That is not a defect being tolerated. A two-metre
propeller on a rubber band is funnier than one bolted to a shoulder, and the machine that decides
where it actually is has no rubber band at all.

The socket comes from `ICarryHolder`, the same one-property interface a corpse is hung on, which is
the one piece of the carry system that did get reused. It cost nothing because it was already
written to be asked for by interface rather than by type.

#### Two pairs of hands, and no rig

One person can lift any part. They will move at 0.30× with the engine, which is slower than a
crouch, and the walk back from the village is two hundred metres. A second player takes the other
end and both of them go to 0.75×.

There is no joint, no second socket, no shared transform. The helper is a `SyncVar<NetworkObject>`
and a distance check: stay within four metres of the part and you are holding it, wander off and you
are not, and the carrier drops back to a crawl the moment you do. That is the entire mechanism, and
it is the entire joke - the interesting failure is not a physics rig coming apart, it is one of the
two people deciding to sprint ahead.

The speed itself is one line in `PlayerMotor`, next to the one the buffs use and read the same way:

```csharp
targetSpeed *= World.PlanePart.SpeedFor(NetworkObject);
```

`SpeedFor` scans every part in the world. There are three.

#### Both hands means both hands

You cannot carry two parts, and you cannot carry a part while a friend is on your shoulder. Both
refusals live in `PlanePart.ServerCanInteract`, which is also where a stunned or downed player is
turned away - the third door on a corridor `PlayerInteractor` already guards twice, and the one the
headless test comes in through.

Getting punched, tasered, shot with a dart or disconnected all drop the part where you stood. They
are polled in one place rather than subscribed to in three, because every one of them ends up as
either `Health.IsIncapacitated` or `StunState.IsStunned` and the part is looking at both every frame
anyway. The part then goes dynamic and rolls downhill, which is the set piece the issue asked for:
two hundred metres of hauling undone by one native with a blowgun.

One line in the drop is not obvious and is copied straight from `Carryable`'s hard-won comment:
`Physics.SyncTransforms()` between writing the transform and clearing `isKinematic`.
`autoSyncTransforms` is off in this project, so without it the solver still has the part where it
was picked up, and going dynamic there throws it back across the island.

#### Where they are

Three parts, at the three places on the second island that want you dead: the propeller ten metres
*inside* the native village, the wing at the cave mouth, the engine in the wreck. That is why those
three places are on an island whose catalogue is otherwise a beachhead and a revive machine - #68's
notes said as much while they were still empty.

They flatten nothing and raise nothing. Every other POI in the game comes with a pad that levels the
ground under it; a crate that arrived with its own patch of level terrain would read as a crate
somebody had placed there for you.

Finding them is `Objective`, the same one global line the rest of the game uses, written by whichever
part happens to be first in the list at 2Hz so that three components do not fight over one string. It
is local and unreplicated like every other objective: each peer can see where the parts are and works
the same sentence out for itself.

#### What the harness measures

The acceptance - *"hauling parts is a comedy set piece, not a fetch-quest chore"* - is a judgement no
headless run can make. What `-partTest` checks is every mechanism the joke is built out of, because
each one stops being funny the moment it stops working: that the parts are a walk from camp and
standing at a landmark, that one person crawls and two do not, that a helper who wanders off loses
their grip, that both hands are both hands, and that a punch puts the engine in the mud where you
were standing.

The pickup goes in through `PlanePart.ServerInteract`, the same door `PlayerInteractor`'s RPC uses.
Only the aiming is stubbed out - there is no camera in a headless run to point at anything - which is
the arrangement `-carryTest` settled on for the same reason.

```
[PartTest] propeller at village, 166m out; wing at cave, 192m out; engine at wreck, 117m out, all of it on foot.
[PartTest] the propeller went from lying on the ground to riding a shoulder at 0.55x, still 0.00m off it after 30m of walking.
[PartTest] a second pair of hands took it from 0.55x to 0.90x, and 20m of wandering took it straight back to 0.55x.
[PartTest] one punch and the propeller was in the mud 0.8m from where the carrier stood, moving under its own weight again.
[PartTest] 38 passed, 0 failed.
```

What it cannot check is the number that matters: whether 0.30× is funny-slow or just slow. That is
the playtest, and the three numbers per part sit in one table in `PlanePartBuilder` so that moving
them is a diff rather than three prefab inspectors.

### The plane is the progress bar (#71)

Three parts come home from three bad places. #71 is what they come home *to*, and its acceptance is
one sentence: *"progress is legible at a glance and replicated to all players."*

The laziest honest reading of that sentence is that **the aeroplane itself is the progress bar**. A
wreck stands on a flat strip a short walk from the beachhead with its starboard wing, its engine and
its propeller missing, and each part you carry back fills one of those holes in on everybody's
screen at once. There is no meter, no percentage and no new UI. If you have to read a number to know
how far along the plane is, the plane is not legible, and a bar floating over a seven-metre object
is a worse version of a fact the object is already telling you.

| File | What it is |
|---|---|
| `Scripts/World/PlaneAssembly.cs` | The airframe. One `SyncVar<int>`, one interaction, and the objective line once the parts are all off the ground |
| `Scripts/Editor/PlaneBuilder.cs` | Generates `Plane.prefab` out of primitives and registers it spawnable |
| `Scripts/World/PlaneTest.cs` | `-planeTest`, and the first harness where **both** ends of the pair run a half |
| `Scripts/Editor/POIFactory.cs` | One more site and one more entry, so the plane is placed like every other landmark |

#### The holes are wired by name

Every child of the prefab called `Fitted.something` is a missing piece. `PlaneAssembly.Awake` walks
its children, records `something` as the `PlanePart.Label` that fills that hole, and switches the
child off. That is the entire wiring between the builder and the component: no serialized table, no
array to drag into an inspector, no id to keep in sync in two files. Adding a fourth part later is a
fourth box in `PlaneBuilder` and a fourth entry in `PlanePartBuilder`, and `PlaneAssembly.cs` does
not change.

What is fitted is a **bitmask**, not a count. The difference costs one character and is the whole
reason the harness fits the wing first: with a count, fitting the wing and watching an engine
materialise is a small lie, and a player who catches the model lying about one thing stops trusting
it about everything. One bit per hole, `_fitted.Value |= 1 << hole`, and `Show()` sets each child
active from its own bit.

`Show()` runs on `OnStartServer`, on `OnStartClient` and on the SyncVar's `OnChange`. The first two
are not redundant with the third: a late joiner is handed the *value* rather than the changes that
produced it, so without the `OnStartClient` call somebody who joined after the engine went in would
be looking at a plane that is still missing it - which is precisely the bug the acceptance is about.

#### Fitting one

`PlaneAssembly` is an `IInteractable` like everything else, so the plumbing is already written.
`ServerCanInteract` asks `PlanePart.HeldBy(actor)` what is on the actor's shoulder and whether this
plane still has a hole for it; it deliberately does not ask how far away the actor is standing,
because `PlayerInteractor` owns that number on the server already and a second copy is a second
place to get it wrong. `ServerInteract` sets the bit, calls `ServerPutDown()` so the collision-ignore
bookkeeping the carry put on the player unwinds through the same path a normal drop uses, and then
despawns the part. The part stops existing because it is now part of the aeroplane; leaving a
carryable engine lying inside the engine bay would be funny exactly once.

The crosshair reads `The plane is missing engine, wing and propeller`, then `missing engine and
propeller`, then `The plane is finished`, which is the same list `Missing()` builds for the objective
line.

#### Who owns the objective line

`Objective` is one global, local, unreplicated string, and two components writing it at 2Hz is two
components flickering. So they take turns by state rather than by priority: `PlanePart` writes
*"Find the propeller and haul it to the plane"* while any part is still lying loose in the world and
falls silent when none is, and `PlaneAssembly` writes *"Bring the engine to the plane"* or *"Get in
the plane"* only when nothing is loose. Neither knows about the other's schedule; each one checks the
same list and one of them always finds nothing to say.

#### Both ends of the harness

`-planeTest` is the first harness where the client does more than exist. The server half hauls the
three parts home - teleporting between them, because #70 already proved the walking works and doing
it again at 0.35x would put three real minutes into every run - and checks that the wing goes in
first and the *wing* is what appears, that the other two follow, and that a finished plane has
nothing left to offer. The client half touches nothing at all: it looks at the aeroplane, waits, and
looks again, and asks `Showing` rather than the SyncVar, because a replicated integer nobody turned
into a wing is not progress anybody can read. Half the acceptance lives on a machine that is not
doing the work, so half the harness does too.

Two things had to give for a client to run a harness at all. Every other test in the project is
started from `OnServerConnectionState`, so on a machine that is not the server none of them exist;
`-planeTest` is started from the client handler too, next to `-rouletteTest`, which was already there
for the same reason. And the scene guard every island harness opens with - *is this Island2?* - is a
question only the server can answer, because `GameSceneLoader.Current` is written on the two paths
the server walks and stays the empty string everywhere else. The client half asks the plane instead:
the plane only exists on the second island, so waiting for it is the same question with an answer on
both machines.

`-planeTest` on the second island: **33 passed, 0 failed** on the host, **8 passed, 0 failed** on the
client, with the plane standing 92m from the camp fire and the three parts 176m, 225m and 127m out.
`-partTest` still 38/38.

### Flying it off the island (#72)

The plane is whole, so now it has to fly. #72 is the arcade flight model, and the shortest statement
of what it owes the player is: *two held keys get you off the ground, and nothing you can do with
them is unrecoverable.*

| File | What it is |
|---|---|
| `Scripts/Vehicles/PlaneController.cs` | Four forces and three torques. The whole flight model |
| `Scripts/Vehicles/FlightTest.cs` | `-flightTest`: grounded, repair, boarding, take-off, handling, stalling, landing |
| `Scripts/Editor/PlaneBuilder.cs` | The airframe grew a rigidbody, four seats, three wheels and a physics material |
| `Scripts/Vehicles/VehicleRider.cs` | One more field and one more branch |
| `Scripts/Player/PlayerInteractor.cs` | Both ends taught that one object can offer two interactions |

#### Four forces, and a stall that falls out of the arithmetic

Thrust along the nose, lift along `transform.up`, anisotropic drag, and gravity. Lift is scaled by a
real angle-of-attack coefficient — `cl = aoa / stallAngle`, clamped to `±clMax` — and that clamp is
the entire stall. Past about fifteen degrees the wing stops paying for more angle, so hauling the
stick back trades speed for nothing and the aeroplane mushes down. There is no stall state, no
discontinuity and no branch: a forgiving stall is what a clipped coefficient does on its own, and a
plane that departs violently is a plane that ends the run of whoever was flying it.

Drag is deliberately lopsided — 4.5 quadratic along the nose against 900 sideways and 700 vertically.
That asymmetry *is* the aeroplane: it means the thing goes roughly where it is pointing, which is the
only aerodynamic fact the player needs to hold in their head.

Shift is throttle, ctrl is brake, and the stick is the movement keys. Bank is turn — roll the wings
and the nose follows, because nobody is hunting for a rudder key mid-panic. `PlaneAssembly.Complete`
is the ignition: an aeroplane missing its propeller makes no thrust, so the three parts from #70 are
the key rather than a checklist to read.

#### One object, two things to do with it

The plane is now a `PlaneAssembly` *and* a `Vehicle` on the same object, and that broke interaction in
a way worth recording. `PlayerInteractor` took the first `IInteractable` it found on the way up from
the collider and offered that; once the plane was finished, the first one was always the assembly,
so the crosshair parked on a component with nothing left to say and the seats underneath could never
be reached.

The fix is at both ends, and it is the same idea twice: the client skips candidates with an empty
`Prompt`, and the server skips candidates whose `ServerCanInteract` says no. `PlaneAssembly.Prompt`
returns null once the last part is in, so it takes itself out of the running and the `Vehicle` is
what you see. Any future object that wants two interactions gets this for free.

`VehicleRider.Drive` grew a `boost` parameter and a third branch rather than an `IDriveable`. A car
and a boat take a throttle and a steer; a plane takes a pitch, a roll, a power key and a brake key,
so the interface would have to be the union of both signatures — which is what the branch already is,
minus a vtable.

#### What the harness caught

Three of the seven `-flightTest` runs failed on the same line: the aeroplane sat on its finished
strip at full throttle and would not move. A dynamic, awake, unconstrained 1100kg body taking 9000N
and answering with exactly zero. Ruling things out by reading did not find it, so the harness grew a
probe that asks PhysX directly — every rigidbody field, the friction on every collider, the clearance
between each collider and the ground, and a 10 m/s shove to see whether the body answers to anything
at all. It took two runs of that to produce the contradiction that named the bug:

```
Fuselage bottom at 8.85, ground 7.90, clearance 0.95m
9 contact(s) [Island2<-Gear.Tail, ..., Island2<-Fuselage, Island2<-Fitted.wing]
```

The belly was touching something a metre above the ground and reporting it as the island. **Terrain
tree colliders belong to the `TerrainCollider`**, so a tree comes back wearing the terrain's name, and
the aeroplane had spawned inside a trunk.

It was standing in a wood of its own making. Flora is scored by slope, and a POI pad is the flattest
ground on the island — levelling a clearing was the same instruction as ordering a forest to grow in
it. `IslandShape.InsidePad` is the fix and it is one guard in `IslandFlora`, which clears the camps and
the village floor at the same time.

Two more, both found the same way:

- **A physics material built in memory is dropped when the prefab is serialised.** The first pass set
  a slippery material on the three wheels; the saved prefab came back with `m_Material: {fileID: 0}`
  on all seven colliders, so the airframe was riding on Unity's default rubber. It lives at
  `Prefabs/PlaneSkin.asset` now, and every collider wears it — a belly or a wingtip touching the strip
  should cost the player a scrape, not the aeroplane.
- **Control authority was computed from forward speed alone**, and a stalled aeroplane falls
  belly-first with nothing on its nose. Authority went to zero exactly when the elevator was the only
  way out: the stick went dead and stayed dead until the ground arrived. Falling at thirteen metres a
  second is thirteen metres a second of air over the tail whichever way the nose is pointing, so
  authority reads the whole velocity now. That one was a deep-stall lock with no way out, and it is
  the check `-flightTest` earns its keep on.

The take-off roll is about twenty-five metres, which is why the strip's pad went from 22m to 30m. It
is the one pad on the island with a length requirement rather than a footprint.

### Going back for the one you left (#73)

The aeroplane was never the point. Somebody was left on the first island, and the whole reason for
three parts, a strip and a flight model is to go and get them.

**They are a passenger, not a parcel.** The obvious reading of "pick up the NPC" is to make them a
`Carryable`, like a corpse — and that is the expensive reading twice over. A `Carryable` requires a
`RagdollController`, so the NPC would need the full bone rig; and `CarrySystem`'s target finder only
ever offers a body whose `Health.IsIncapacitated`, so somebody standing on a beach waving at you
would never be offered in the first place. Both would have to be bent to fit. So `Castaway` walks the
navmesh the island already has, and rides in the plane's `CargoSocket` — the seat `Vehicle` already
keeps for a body. No rig, no new socket, and no argument with the seat-and-ownership machinery.

Four stages, one `SyncVar<int>`: `Waiting`, `Following`, `Aboard`, `Home`. Interacting moves them
along it; there is no state that is not one of those four, and no stage the player cannot see the
consequence of.

**The objective chain has no chain file.** The acceptance for this issue was *"the chain is clear
without a tutorial"*, and the temptation is a `QuestManager` holding the list of steps. There isn't
one. Each component writes its own line at the moment it can see the step has happened, continuing
what `PlaneAssembly` already did when the last part went in:

| Who writes it | When | The line |
|---|---|---|
| `PlaneAssembly` | parts still missing | *Find the missing plane parts* |
| `Castaway` | waiting | *Find the one you left behind* |
| `Castaway` | following | *Get them to the plane* |
| `Castaway` | aboard | *Fly them home* |
| `PlaneAssembly` | plane whole, nobody to rescue | *Get in the plane and fly to the other island* |

`Objective` is local, derived and unreplicated, so all four players read the same sentence without
anybody sending one. The only coordination between the two writers is one line in `PlaneAssembly`:
if a castaway exists and is not rescued, it does not write. A manager would have bought a registry,
an ordering and a priority scheme to express exactly that.

**`PlaneVoyage` is the boat's rule with one extra condition.** It reuses `BoatVoyage.OffTheMap`
rather than copying the sea's edge, so a hull and an airframe can never disagree about where the
world ends. What it does not borrow is the rest: a hull has four parts, a fuel tank and a mooring to
be towed back to; an aeroplane had its parts gate in #71, no fuel by design, and no tow. The extra
condition is altitude. Taxiing off the end of the strip is not a decision to leave, and the harness
parks a plane past the edge on the ground with nobody in it to prove the crossing stays at zero.

**The far island needs its own airframe, for the reason each island keeps its own hull.** Scene
objects do not cross scenes, so without one the flight is one-way and the rescue is unfinishable.
`PlaneAssembly.Owned` is a static, exactly like `BoatVoyage`'s owned parts: the group built an
aeroplane once, so the one waiting on the other side stands there whole. Island 1's catalogue gained
a strip, an airframe and the castaway themselves, sited next to the wreck they came off.

Two runs of `-rescueTest` failed on the same check — *they walk to the plane on their own feet* —
for two entirely different reasons, and only the second one was a bug in the game.

The first was the harness:

```
[Castaway] lost sight of everyone and sat back down.
```

The strip is two hundred metres from the beach and the leash is sixty, so a test that teleported the
leader to the aeroplane in one frame had abandoned the follower by the component's own rule. That is
the leash working. The harness moves the leader ten metres at a time now, and only once they have
caught up — which is what walking is. The board radius went from four metres to six in the same
pass: it is measured from the aeroplane's root, and a follower who stops two and a half metres behind
somebody standing beside the fuselage is not within four metres of its origin.

The second was real, and the bake had been reporting it all along:

```
[NavFactory] castaway -> camp.base: setting off 13m from the marker because the marker is inside
the building
```

Their site stood them six metres from the wreck, and six metres from the wreck is *inside the hull*.
They stood up, announced they were following, and travelled nothing — while the harness printed
`on the mesh True`, which is what made it puzzling and is also the lesson: **`isOnNavMesh` is true on
any polygon, including a sealed pocket of mesh inside a building with no way out of it.** It is not
a question about the agent, it is a question about the island, and the bake was already answering it
in a line nobody was reading. Sixteen metres out — past the wreck's own 10m pad, onto open sand —
and the same line now reads *setting off 2m from the marker*, while the castaway walks 201m to the
strip on their own feet.

One more fell out of the regression set rather than the new harness. `-flightTest` began dying on
`NullReferenceException at PlaneController.get_Bank ()` — a destroyed component — because
`PlaneVoyage` is now on the aeroplane prefab and that test flies hard enough to leave the map at
altitude with somebody at the controls, which is the crossing's exact trigger. The scene changed
underneath the test. The game was right and the harness was surprised, so `-flightTest` switches the
voyage off for its duration: whether the gate works is `-rescueTest`'s question.

Two habits came out of it. `Castaway` warps onto the nearest mesh at spawn, which does not rescue a
sealed pocket but does cover the ordinary case of a marker a metre inside a wall; and the harness
prints `agent enabled … on the mesh …` before it starts, so the next one of these names itself
rather than looking like a pathing failure. The probe in the walk loop is worth keeping for the same
reason: a follower that does not follow looks identical from outside whether the leader never moved,
the path is partial, or the agent is parked somewhere with no exit.

### The run has an ending (#74)

The acceptance was *"a real ending, not a fade to black"*, and the difference between the two is
entirely whether the screen is about **this** run. So the ending is four numbers that could not have
come from any other afternoon: how long it took, how many times you died, what you left at the
roulette table, and how many of your friends you drove into.

**Leaving is not crossing.** `GameSceneLoader.Crossing` toggles between the two islands and nothing
else, so an aeroplane that flies off the first island with the person you went back for cannot be
handed to it — it would put everybody back on the island they just escaped from. `PlaneVoyage`
already knew when a departure was serious (past the edge, high, somebody at the controls, held for
four seconds); the only addition is what it does at that moment when `Castaway.Instance` is aboard.
It ends the run instead of travelling.

**Two counters, not a statistics system.**

| Number | Where it comes from |
|---|---|
| Deaths | summed off `Health.Deaths` at the end |
| Chips gambled | one line in `BetSpot`, where the stake goes down |
| Friends run over | one line in `VehicleImpact`, where it already logs the hit |
| Seconds | `Time.time` |

Deaths get no counter of their own because `Health` has counted them per body since #24, and summing
four integers once is cheaper than keeping a fifth in step with them forever — there is nothing to
fall out of sync, which is also the only thing `-endTest` has to prove about them. The `VehicleImpact`
line tests for a `PlayerMotor` first: `Hits` counts anything with a body in it, and a boar under the
wheels is not a line anybody wants read out at the end.

The figures move on the server and reach the other three once, in the RPC that ends the run, rather
than as four SyncVars ticking all game for a screen nobody sees until it is over.

**The ending is a HUD element, not a scene.** A cutscene scene would mean a second camera rig, a
second lighting setup and a transition to get back out of, all to show a panel over an aeroplane
that is already flying away from an island. The aeroplane *is* the cutscene. `EndingPanel` fades
black over it, writes the four lines, holds, and scrolls the credits — and because it draws locally
off `RunSummary`, all four of you watch your own copy fly out rather than sharing one camera.

`-endTest` does not check the panel, because a headless build has no canvas to draw one on. It
checks the four integers, which is the part that can be wrong: that an empty aeroplane past the edge
ends nothing, that a real death on a real body reaches the tally, that leaving with somebody aboard
ends the run *and leaves everyone on the island they were on*, and that the numbers on the ending are
the numbers that happened.

Skipped: taking control away when it ends. The aeroplane flies itself out and the acceptance asks
for an ending, not for the game to grab the stick. Worth adding if it looks wrong in play.

### Quitting without losing the run (#75)

A run is four people, an afternoon, and a pile of things they earned. Until this issue the pile only
existed in memory: closing the game threw away every wallet, every bag, the aeroplane they had
half-built and the fact that they were standing on the second island at all. `RunSave` puts that on
disk.

**One file, written by the host.** The host is already the authority on every number worth keeping —
`Wallet` is a SyncVar only the server writes, so is `Inventory`, so is `PlaneAssembly` — so the save
is a snapshot of what the server believes, and the other three get it the way they get everything
else: replicated, when their body spawns and their wallet and bag are filled in. There is no
client-side save file, no merge between four copies and nothing to reconcile.

**Keyed by `PlayerKey`, which #111 already had to solve.** A saved bag has to find its way back to
the same person across a process restart. FishNet reuses client ids, and every Tugboat client on one
machine shares an address, so neither identifies anybody tomorrow. The key — a Steam id, a
`-playerKey` flag, or a GUID in `PlayerPrefs` — is the only identifier in the project that means the
same thing in the next session, and it was already sitting on every body for reconnect adoption.
`PlayerSpawner` calls `RunSave.ServerApply` on the line after it stamps the key, because that is the
one point where both halves of "what does this person own" are known at once.

**Autosave, not a save on quit.** `OnApplicationQuit` fires on the polite ending and on no other. The
endings that actually happen are alt-F4, a crash, and a router, so the file is written every thirty
seconds as well; the quit hook is there so that quitting on purpose loses nothing at all, and the
periodic write is the one that will do the real work. Each write goes to `run.json.tmp` and is copied
over the real file, because a write interrupted halfway is precisely what a crash is.

**What is saved is what the issue asked for.** Money, chips, bags slot for slot, which island the
group is on, which holes in the aeroplane are filled, and which upgrades are bolted to which vehicle.
Deliberately not saved: where anybody was standing, what is lying on the ground, which animals are
alive, how full the chests are, the time of day. Position is the most tempting of those and the least
valuable — a body restored mid-air, or inside geometry that moved under it, is worse than a walk back
from the spawn.

Two details in the file are worth more than they look:

- **Items are stored by `ItemDef.Id`, not by `ItemStack.Index`.** The index is a position in a
  catalogue that is regenerated every time somebody adds an item, so a save written with indices
  would quietly turn a bag of fish into a bag of dynamite the first time the content list grew —
  during development, which is exactly when saves get tested. Ids cost a few bytes and survive it.
  The same reasoning puts aeroplane parts in the file by label rather than as the `_fitted` bitmask.
- **Everything merges, and every restore only ever raises.** A player who left an hour ago is not in
  the scene, and an island the group sailed away from has no vehicles loaded, so a snapshot that
  rebuilt its lists from whatever is in memory would delete both. On the way back in,
  `PlaneAssembly.ServerFit` adds a part and cannot remove one, and `VehicleUpgrades.ServerRestoreTiers`
  takes the higher of the two tiers — a stale file cannot unbolt something fitted since it was
  written. `Inventory.ServerRestore` writes the slot it is given rather than going through `Add`,
  because `Add` packs into partial stacks first: right for a pickup, and wrong for a restore that
  would otherwise rearrange somebody's hotbar every time they resumed.

**Nothing pushes state into the world; the world pulls it.** `RunSave` arms inside the server's own
"started" callback, which is before FishNet has spawned a single scene object. The first version of
this restored the world from a loop, and it silently did nothing: `PlaneAssembly` was not spawned yet
and `VehicleUpgrades._tiers` was still an empty list, so iterating it restored no upgrades and said so
to nobody. The aeroplane appeared to work only because it has a second route in through the `Owned`
static. So each thing restores itself in its own `OnStartServer` — `PlaneAssembly` reads
`RunSave.SavedParts` and `SavedPlaneOwned`, `VehicleUpgrades` reads `RunSave.TiersFor(name)` on the
line after it builds the list — because that is the first moment it can. Sailing between the islands
then needs no handling at all: scene objects are rebuilt by the load and ask again on the way up.

**The island itself is restored by sailing to it.** `RunSave` arms before every harness in
`NetworkBootstrap`'s server block, and if the file was written on a different map than the one the
command line asked for, it calls `GameSceneLoader.ServerTravel`.

**Off by default in a headless run.** A build with no graphics device is a harness, and twenty
harnesses sharing one `persistentDataPath` would each inherit the last one's island and wallet — the
`-endTest` assertion that the run starts on `Island` would fail for a reason that has nothing to do
with #74. So headless has to ask, with `-save`; a build with a screen saves unless told `-noSave`.
`-savePath <file>` moves the file, which is how two harness lanes stay out of each other's way.

**Skipped deliberately:** a save-version migration. Version 1 is discarded, not upgraded, when the
shape changes, because there is one version of this game and nobody has a save worth migrating yet.
`RunSave.Read` is where that goes when somebody does. Also skipped: a slot or profile system —
there is one run at a time and a menu to choose between three of them is #84's problem, not this one.

**Harness — `-saveTest write` then `-saveTest read`.** The acceptance is "quit and resume without
losing progress" and the only honest test of that is two processes, so the test is one thing in two
halves. The write half plays a little, saves, checks the file on disk holds the key, the money, the
item *ids* and the island, and then does the whole round trip in-process — throw the state away,
forget the file was ever read, read it again, put it back — so that a broken serializer fails in one
run rather than in a pair somebody has to remember to run both halves of. The read half is a
genuinely fresh process with fresh SyncVars and nothing but the file to go on, and it forces nothing:
everything it checks was put back by the game's own load path before the coroutine ran.

```
EscapeWithYourFriends.exe -batchmode -nographics -host -port 8108 -playerKey test:host -scene island \
    -noNatives -noAnimals -save -savePath D:\Builds\save-test\run.json -saveTest write -quitAfter 120
EscapeWithYourFriends.exe -batchmode -nographics -host -port 8108 -playerKey test:host -scene island \
    -noNatives -noAnimals -save -savePath D:\Builds\save-test\run.json -saveTest read  -quitAfter 120
```

```
[SaveTest] wrote run.json: 1234 money, 77 chip(s), 2 stack(s), plane whole, island Island.
[SaveTest] write: 9 passed, 0 failed.

[RunSave] Armed; island 'Island', 1 player(s), 3 part(s) fitted.
[VehicleUpgrades] Buggy (camp.buggy): restored from a save. Engine stock, Tyres tier 2 (x1.00), Armour stock, Tank stock.
[RunSave] test:hos… resumed with 1234 money, 77 chip(s) and 2 stack(s).
[SaveTest] read: 7 passed, 0 failed.
```

`run.json` is byte-identical after the read run, which is the check that matters most and is the
easiest to forget: a resume that quietly overwrote what it resumed from would pass every assertion
above and still lose the run on the second restart.

### The settings menu, and making it mean something (#84)

A settings menu is the easiest feature in the game to ship broken, because a slider that moves and
changes nothing looks exactly like a slider that works. So the rule here is that **every setting has
exactly one reader somewhere in the game, and the test asks the reader rather than the stored
number.** The volume check reads `AudioListener.volume`; the colour check reads what a spawned
player's body is actually tinted with; the rebind check reads the binding override sitting on a
freshly spawned player's own copy of the input asset.

`Core/GameSettings.cs` is the store. It is `PlayerPrefs`, **not the save file**, and that division is
the whole design: `RunSave` holds what the four of you earned, `GameSettings` holds how one of you
likes to sit. Somebody loading a six-hour-old save still wants their own sensitivity, and somebody
starting a fresh run does not want to redo their keybinds. Nothing here is networked and nothing here
should be — a replicated setting is a setting somebody else can change on your machine.

Quality is the exception that proves it. `GraphicsBoot` already owns a quality preference, guesses a
tier from the GPU on a first run and applies it before the first scene loads. A second key would give
one number two owners, and they would disagree the first time anybody passed `-quality`. So
`GameSettings.Quality` is a *window onto* `GraphicsBoot.PreferenceKey`, not a copy of it.

Three of the values are carried as relationships rather than as absolutes, which is the part that is
easy to get wrong and impossible to notice afterwards:

- **Sensitivity is a multiplier** over the tuned `_lookSensitivity` on the prefab, so `1.0` means the
  speed that was actually playtested and retuning the prefab does not silently move everybody's
  setting.
- **Sprint field of view is a difference.** `PlayerCameraRig` widens by `_sprintFov - _baseFov`, so a
  player sitting on 110 still gets the same lurch rather than none at all.
- **Rebinds are the Input System's own override JSON**, stored as one opaque string. A hand-rolled
  map of action name to key would have to be taught about composites, modifiers and gamepads one at a
  time; this way `PlayerInputReader` loads the string into its instance and the format stays the input
  asset's business.

Colourblind mode swaps `PlayerIdentity.Palette` for an Okabe-Ito set. This is not decoration: the
whole game identifies people by colour — the squad list, the downed markers, the tint on the body —
so on the wrong palette two of the four are the same person. Because a colour is applied once when a
body spawns rather than read every frame, `GameSettings` raises a `Changed` event and `PlayerIdentity`
subscribes; it is the only subscriber, and the only one needed.

`UI/SettingsScreen.cs` is the menu, on its own canvas above the HUD with its own raycaster, opened
with Escape via a new `ToggleSettings` action. There is no Apply button — every widget writes on
change and `PlayerPrefs.Save()` runs immediately, for the same reason `PlayerKey` writes its generated
key immediately: the session somebody spent ten minutes rebinding in is quite likely the one that
crashes. Rows are laid out by a counter rather than a layout group, and one widget — a Button with a
caption that redraws itself — serves the toggles, the quality cycler, the resolution cycler and the
rebind rows.

Writing that screen turned up a bug in #74's ending panel that no harness could ever have caught:
`HudFactory.Anchor` sets `anchorMin` and `anchorMax` to the *same* vector, so the full-screen fade
rect it was building had zero size and the ending would have drawn nothing. Headless builds no canvas,
so there is no test that could have failed. The fix is `HudFactory.Stretch`, which is what the call
site meant, and its doc comment now says why the two helpers are not interchangeable.

Verification is `-settingsTest write` then `-settingsTest read`, two processes, because "persists
across sessions" is not honestly testable in one. The write half also checks that absurd values are
clamped rather than believed — a preferences store is a text file somebody will edit — and the read
half is a fresh process that forces nothing: everything it checks was put back by the boot hook and
the spawn path on their own.

```
[SettingsTest] write: 13 passed, 0 failed.
[SettingsTest] read: 8 passed, 0 failed.
```

**These two runs must not run in parallel with anything, including each other.** Every process on the
machine shares one `PlayerPrefs` store, so the read half also resets everything on its way out rather
than leaving the next harness on 2.5x sensitivity with a rebound jump key.

Field of view and resolution are the two settings the harness cannot check, because both need a screen
a headless run does not have. Both are one-line reads of this same store, and the store is what is
under test.

Not done, on purpose: audio buses (one master and one voice slider is what people actually use), a
per-setting revert, and gamepad rebinding UI — the store and the rebind path already handle a gamepad
path, there is simply no button for it yet.

### A harness that measured the wrong player (#132)

`-nativeTest` failed four checks, deterministically, with the same numbers in every run: a blowgunner
that fired no darts at all, the two checks that depend on a dart landing, and a shout whose listeners
arrived 10.9m from where the player had been standing. Deterministic failures are usually arithmetic,
so this looked like a bug in the camp.

It was a bug in how the suite was run. The repro was a two-process pair, and `-nativeTest` is a
one-player suite. Every measurement it takes is a distance between one player and one native placed a
fixed few metres from it — and a native hunts whoever is *nearest*, not whoever the test had in mind.
The host's player and the client's spawn about nine metres apart on the ring, so the second body was
comfortably inside the experiment:

```
[PlayerSpawner] Spawned body for connection 0 at (-84.00, 6.17, -40.50) ...   <- object 38
[PlayerSpawner] Spawned body for connection 1 at (-77.50, 6.11, -47.00) ...   <- object 61

[Native] blowgunner 102 noticed 61 at 4.6m (notice 34m, night 1.00, sight not needed).
[Native] spearman  104 noticed 61 at 15.0m (notice 18m, night 0.00, sight required).
```

Both failures fall straight out of those two lines. The blowgunner was placed eight metres from
object 38 and turned to face it, then locked onto object 61 at 4.6m instead. A ranged native will not
attack a target it cannot see, object 61 was outside its vision cone, and the test disables the
NavMesh agent so a body will stay exactly where it was put — so it stood there in `Chase` forever,
holding a target it could neither see nor walk to. Zero darts. The alarm check reads `a.Suspect`, and
`Sense` overwrites `_suspect` with the nearest sighted player every time it notices one, so the
listener's suspect point became object 61's position: 10.9m from where object 38 was standing.

Run alone, on the same build, with nothing changed:

```
[NativeTest] 161 passed, 0 failed.
[Native] blowgunner 101 noticed 38 at 8.0m (notice 34m, night 1.00, sight not needed).
[NativeTest] a blowgunner at 8.0m: 11 dart(s), 5 hit, 45 health and a stun.
[NativeTest] one spearman shouted at noon: 2 of 2 out of earshot came looking, within 0.1m of where
             the player actually was.
```

The fix watches for it at the moment it matters. Every native this suite places goes through one
helper, and that helper now asks whether anybody else's body is closer to the spot than the player
being measured is:

```csharp
void Crowd(Vector3 spot, PlayerMotor motor)
{
    if (_crowded) return;

    float mine = Vector3.Distance(motor.transform.position, spot);

    PlayerMotor nearer = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
        .FirstOrDefault(m => m != null && m != motor && m.IsSpawned
                             && Vector3.Distance(m.transform.position, spot) < mine);

    if (nearer == null) return;
    ...
}
```

The first version of this was a refusal at start-up, and it was wrong in a way worth recording: it
passed cleanly against a client that had not arrived yet. The suite waits for *a* player, the host's
own body is there immediately, and the client in the repro connects some twenty seconds later - so the
check ran, found one player, and waved the run through. A client can join at any point in a
three-minute run, and the only honest place to ask is where the answer is used.

One named failure that says which body, how far away and what to do about it beats four failures deep
in the ranged and alarm checks that name everything except the cause. This is the same class of
problem as #141, where seat-drift checks fail only when harnesses run in parallel: a harness that is
quietly sensitive to what else is running will eventually be believed about something it never
measured.

```
EscapeWithYourFriends.exe -batchmode -nographics -host -port 8128 -playerKey test:host   -scene island -noAnimals -nativeTest -quitAfter 200
```

Run as a pair anyway, that check is the first thing in the log and the rest read as its consequences:

```
[NativeTest] FAILED: only the player being measured is on this island (object 60 is 4.6m from a
native put 8.0m from it, so the native will hunt that one instead) - run -nativeTest on a host with
no client attached.
[NativeTest] FAILED: a blowgunner fires (0 darts).
...
[NativeTest] 157 passed, 5 failed.
```

It flags rather than aborts. A run with the wrong number of people in it is not worth reading, but the
157 checks that do not care how many players there are still say something, and threading an abort
through a dozen coroutines to suppress them would be more code than the problem is worth.

Tracing this turned up something real that is *not* fixed here and is filed as #144: `Alarm` hands
every listener a live `Health` target, so `TickInvestigate` promotes it to `Chase` on the next tick
and it walks to where the player *is* rather than to the shouted spot — which is exactly what the
comment above `Alarm` says it must not do. It causes none of the failures above, and it changes how
hard a whole camp converges on you, so it is a deliberate change rather than a side effect of a
harness fix.

### A seat that was never loose (#141)

The passenger checks in `-carTest` and `-boatTest` failed only while another headless harness was
running: 0.350m of drift on the buggy against 0.062m alone, 0.504m on the boat. The issue asked the
right question — is this a harness that needs to be run alone, or a seat follow that depends on frame
rate, which a player on a slow machine would see too?

Neither. The glue is not frame-rate dependent; the measurement was. `Vehicle.LateUpdate` is the last
thing to write a rider's transform, and it snaps them straight onto their anchor. The two suites
sample from a coroutine that resumes on a bare `yield return null`, and Unity resumes those after
`Update` and **before** `LateUpdate`. So by the time the test looks, physics has already moved the
vehicle for this frame and the glue has not yet run: every rider is exactly one frame of travel
behind their seat, by construction. A frame twice as long is a frame of travel twice as long, which
is the whole of the "factor of five and a half" — it tracked the neighbours' CPU load, not anything
in the vehicle.

`VehicleTest` never had the problem, and the reason is worth keeping: it moves the buggy itself,
by setting its position, *before* it yields, so when it wakes the glue has already caught up with
that move and nothing has moved the vehicle since. The car and boat suites drive for real — a motor
and buoyancy under physics — so the vehicle keeps moving in the frame the test wakes in.

The fix is in the measurement, not the tolerance. Each sample also records how far that seat's
anchor travelled since the previous sample, and the check is on what is left once that is taken
off:

```csharp
float drift = Vector3.Distance(occupant.transform.position, anchor.position);
float travelled = seen ? Vector3.Distance(anchor.position, wasAt[i]) : drift;
wasAt[i] = anchor.position;
float slip = Mathf.Max(0f, drift - travelled);
```

A rider exactly one frame behind has `drift == travelled` and a slip of zero. A rider who has come
loose and is being dragged falls further behind each frame and shows up at once. The raw figure is
still logged next to it, so the frame-time effect stays visible rather than being hidden:

```
alone
[CarTest]  12s of real driving with four aboard, peaking at 22.1 m/s: worst slip 0.000m (0.054m raw)
           over 23972 sample(s).                                             22 passed, 0 failed.
[BoatTest] 12s of driving with four aboard, peaking at 12.1 m/s: worst slip 0.000m (0.033m raw)
           over 23976 sample(s), least freeboard 0.38m.                      25 passed, 0 failed.

both at once
[CarTest]  ... worst slip 0.000m (0.058m raw) over 23968 sample(s).          22 passed, 0 failed.
[BoatTest] ... worst slip 0.000m (0.031m raw) over 23968 sample(s).          25 passed, 0 failed.
```

With that, `Glued` goes back to what it was meant to be, a rounding budget: **0.02m**, down from the
0.15m it had been widened to in order to swallow a frame of travel. Both suites are now safe to run
alongside other harnesses.

The same afternoon exposed a flaw in the working agreement's "is Unity busy" check. It matched
process *names*, so with a second Claude account working in its own worktree it would have waited
on the other account's Unity forever. `CLAUDE.md` and `docs/WORKING-AGREEMENT.md` now filter the
command lines instead — `JocStupid` followed by a quote, a space or the end, or `EWYF-dev` followed by a slash — which
matches this checkout's editor and build and neither of `JocStupid-b` or `EWYF-dev-b`.

### A part that was still where it had been (#139)

`-partTest` failed one paired run in three, always with the same five checks: the second pair of
hands did not take the other end, and a punch then dropped the part a long way from the carrier.
Nothing changed between passing and failing runs, so the question was what the failing ones were
seeing. The probe was widened to print positions after the handshake and at the punch, and the first
failure answered it:

```
probe: after the handshake the helper is nobody and the part is still up; carrier at (5.00, 3.85, 92.00),
       mate at (6.50, 4.85, 92.60), part at (5.00, 4.85, 92.60).
probe: punched at (5.00, 3.85, 92.00), carrier now at (5.00, 3.85, 92.00), part at (-26.17, 6.84, 63.32).
FAILED: where you were standing (42.4m).
```

Everybody is where they should be, 1.5m apart, and yet the grip check let the helper go. The part
then landed at `(-26, 63)`, which is exactly where it had been riding a shoulder thirty metres into
the walk, *before* the test teleported the carrier home. The physics body never left that spot.

This is #107 again, for parts instead of bodies. `PlanePart` is built with an interpolated rigidbody.
While it is carried it is kinematic and `Update` writes its transform onto the shoulder every frame,
but before `Update` runs, interpolation writes the transform back from the body's last two
**physics** poses. While walking those are a step stale. After a teleport with no physics step since,
they are the whole jump stale. A headless host runs many frames per 20ms step, so the handful of
frames between the teleport, the handshake and the punch could easily contain no step at all. In
those runs:

- `Update`'s grip check read the part at the old spot, 42m from the helper, and cleared the helper
  the frame after it was set.
- `ServerPutDown` placed the part correctly, but the interpolation history still held the old pose,
  and the transform was written back to it.

Whether a physics step happened to land in that window is the whole of the one-in-three.

The fix is the one `RagdollController.SetBonesKinematic` already makes: while something else owns the
pose, nothing interpolates it. `ServerLift` sets `RigidbodyInterpolation.None` and `ServerPutDown`
puts `Interpolate` back when the part goes dynamic. Six paired runs in a row, three with the host's
body carrying and three with the client's (the suite takes whichever `FindObjectsByType` returns
first):

```
[PartTest] a second pair of hands took it from 0.55x to 0.90x, and 20m of wandering took it straight back to 0.55x.
[PartTest] one punch and the propeller was in the mud 1.1m from where the carrier stood, moving under its own weight again.
[PartTest] 38 passed, 0 failed.        (x6)
```

The wider probe stays. The next time this suite fails, it will say where everything was.

Not changed: clients keep interpolation on their kinematic copy, which is moved by the
NetworkTransform rather than written every frame by gameplay code. If a carried part is ever seen
trailing on a client, the same line belongs in `OnStartClient`.

### Achievements, decided by the host and unlocked by the player (#92)

The issue asks for achievements "for the stupid stuff" that "fire reliably in multiplayer for all
clients". Two facts shape the design:

- **Only the host saw it happen.** A client does not know it ran anybody over, only that the car it
  was sitting in moved.
- **An achievement belongs to the Steam account at the keyboard.** The host's Steam client cannot
  unlock anything for somebody else.

So the host decides and the owner unlocks. `Achievements.ServerAward(body, id)` sends one reliable
FishNet broadcast, `AchievementUnlock`, to the connection that owns the body. The receiving client
adds the id to `Achievements.Unlocked` and, if Steam is up, calls
`new Steamworks.Data.Achievement(id).Trigger()`. Bodies nobody owns (natives, boars) are skipped
inside `ServerAward`, so the hooks do not have to know about them. `NetworkBootstrap` registers the
handler once, because an unlock is addressed to a connection, not to an object.

There is no event bus. Each award is one line in the place that already knew:

| Id | Hook | Earned by |
|---|---|---|
| `RAN_OVER_FRIEND` | `VehicleImpact`, next to `RunSummary.ServerRanOver` | whoever is in seat 0 when a player goes under the wheels |
| `LOST_IT_ALL` | `RouletteWheel.Settle`, after the payouts | anyone who bet this spin and now holds no chips; once per owner per spin |
| `DIED_TEN` | `Health.SetState`, where `Deaths` is counted | the body, on exactly its tenth death |
| `FIRST_TRY` | `PlaneVoyage`, where the run ends | everyone aboard, if `PlaneController.Touchdowns` is still zero |

`FIRST_TRY` is the one that departs from the issue's wording, "land the plane on the first try",
because this game ends in the air. The version that exists here is that the flight that ended the
run was the plane's only one. `Touchdowns` counts returns to the ground after more than three
seconds aloft, so a bounce on the take-off run does not count.

Rich Presence lives in `SteamRuntime.Update`. It is checked once a second and sent only when it
changes:

- `status` reads "Stranded on the island", "On the island that shoots back", "Escaped" or "In the
  menu". It is the one key Steam shows without a localisation file.
- `steam_player_group` and `steam_player_group_size` come from the lobby, so the friends list shows
  a party as one.
- `steam_display` needs tokens uploaded against the real app id, so it waits for #85.

The ids are also the API names the Steamworks backend will need. Spacewar (480) has its own fixed
set, so until then a Trigger does nothing and the log line is the evidence.

`-achievementTest` is a pair on `-scene island`, and **both** processes take the flag, because the
half the acceptance is about, a client being told, can only be checked on the client. The host earns
everything through the real hooks, never by calling `ServerAward` itself:

- the guest dies eleven times;
- the guest bets everything on one number until it is gone;
- the host runs the guest over on a pad in the sky;
- the two of them fly off with the castaway.

Each side then checks it heard exactly what it earned: the driver and not the one under the wheels,
the one who died and not the one who killed them.

```
host    [Achievements] DIED_TEN to object 34 (connection 1).
        [Achievements] LOST_IT_ALL to object 34 (connection 1).
        [Achievements] RAN_OVER_FRIEND to object 33 (connection 0).
        [Achievements] FIRST_TRY to object 33 (connection 0).
        [Achievements] FIRST_TRY to object 34 (connection 1).
        [AchievementTest] ... the host was told: RAN_OVER_FRIEND, FIRST_TRY.
        [AchievementTest] 20 passed, 0 failed.

client  [AchievementTest] this client was told: DIED_TEN, LOST_IT_ALL, FIRST_TRY.
        [AchievementTest] 5 passed, 0 failed.
```

Not tested, because nothing headless can test it: the Steam call itself, and what the friends list
shows.

### The demo is one gate (#90)

The demo is the first island and nothing past it. Punching, carrying, fishing, the shop, the table
and the buggy are all already on that island, so the demo is not a cut of the content. It is one
switch, `Core.Demo.On`, and two places that read it.

**The gate.** `GameSceneLoader.ServerTravel` is the only way off an island. The boat and the plane
both end up there. In the demo it ends the run instead of loading anything:

```csharp
if (Core.Demo.On)
{
    Debug.Log($"[GameSceneLoader] Demo: leaving {Current} for '{requested}' ends the run.");
    World.RunSummary.ServerEnd();
    return true;
}
```

It returns `true`, so both voyages latch the way they would after a real crossing and do not call it
again every frame. The ending panel reads the same flag and says "That's the demo." instead of "You
got off the island.". What the panel says next, and whether it should link to the store page, is
waiting on the app id (#87).

**No save.** `RunSave.Begin` does nothing in the demo. A demo that shared a save folder with the full
game would load a full-game save sitting on the second island, which the demo cannot show.

**One way to end a run.** Ending a run used to be an `ObserversRpc` on `PlaneVoyage`, which is fine
for one ending but not for two, and the demo's ending has nothing to do with a plane. It is now a
broadcast, `World.RunEnded`, sent by `RunSummary.ServerEnd()`, which tallies once on the host and
hands the same four figures to everybody. `NetworkBootstrap` registers `RunSummary.OnEnded` for it.
The host's own client hears it too, and `Finish` already ignores a second ending, so nobody needs
`ExcludeServer`.

**Building it.** `BuildTool -demo` adds the `EWYF_DEMO` scripting define, and `Demo.On` is then
true in a build that has no flag to remove. `-demo` turns it on in any build, which is how the
harness sees it.

**`-demoTest`** is a pair on `-scene island -noNatives -noAnimals`. Both processes take `-demo
-demoTest`, and the host also takes `-save`, so "the demo keeps no save" is a real question and not
a default. The host kills and revives the guest once so the ending has something to carry. Then it
flies the plane past the edge with the castaway still on the beach. In the full game that exact
move crosses to the second island. The host checks that the run ended, the death is in it, and the
scene did not change. The client checks that it was told, with the same death count, and that the
second island never loaded.

```
[PlaneVoyage] 1 aboard at 300m and 4s past the edge of Island; making for Island2.
[GameSceneLoader] Demo: leaving Island for 'Island2' ends the run.
[RunSummary] Over after 0m 29s: 1 death(s), 0 chips gambled, 0 friend(s) run over.
[DemoTest] 7 passed, 0 failed.        (host; client: 4 passed, 0 failed, and no save file written)
```

Moving the ending onto a broadcast touched every run that ends, so `-endTest` (17/0),
`-voyageTest` (29/0), `-flightTest` (29/0) and the `-achievementTest` pair (20/0, 5/0) were run
again. The same pass caught an old flake in `-saveTest`. Both halves took "the first"
`VehicleUpgrades` that `FindObjectsByType` returned, and that call promises no order. With a buggy
and a boat on the island, the write half could upgrade one and the read half check the other. Both
halves now take the first vehicle by name, which is also what the save file is keyed by. Three
rounds after that: write 9/0 and read 7/0 each time.

What no harness can check is the acceptance itself: whether the demo is fun on its own and turns
players into wishlists. That needs players, and then Steam's numbers.

### A shout gives a place, not a person (#144)

`Native.Alarm` has always been documented as "go and look at where the player was". The code did
more than that. Every listener was also handed the live `Health` it was shouted about, and
`TickInvestigate` turns a native with a target into a chasing one on the next tick. So the walk
toward the spot lasted one frame. After that the listener ran at the player's current position,
through terrain it had never seen them from. That is the camp that cheats, which the comment above
the method says it is not.

The fix is one deleted line, `other._target = about;`, plus the parameter it used. A listener now
gets `_suspect`, `_hasSuspect` and `_forgetAt`, walks to the spot, and picks the player up only
through `Sense`. `Sense` applies the same day and night rules to it as to any other native: sight
by day, the notice radius at night, and earshot either way. A player who has moved on from where
they were shouted about can get away. A player who stays put is still found, because that spot is
where the listeners are going.

This makes a camp less punishing. That is a feel change, so it was held back from #132 to be
decided on its own. It shipped with the version the code already documented.

**The check.** `-nativeTest` already had a shout test, but it counted `Investigate`, `Chase` and
`Attack` all as "came looking". With one player standing still, the honest listener and the cheating
one end up in the same place, so the test could not tell them apart. It now also checks, in the same
frame as the shout and before either listener could have seen anything, that both are in
`Investigate` with no target. Against the old `Native.cs` that check fails:

```
old Native.cs:  [NativeTest] FAILED: the shout hands over a place, not the player (Chase/True, Chase/True).
fixed:          [NativeTest] 162 passed, 0 failed.
                [NativeTest] one spearman shouted at noon: 2 of 2 out of earshot came looking,
                             within 0.1m of where the player actually was.
```

Regressions: `-abductTest` 43/0, `-prisonTest` 44/0, `-rescueTest` 15/0.

One more rule for running it. `-nativeTest` must run **without `-noNatives`**, as well as solo. Its
"the camps man themselves" check needs the spawner switched on. With the usual `-noNatives -noAnimals`
pair of flags it fails with `0 spawned` on any code.

---

## One palette, and a test that counts it (#79)

Every factory used to call `new Material(Shader.Find("Universal Render Pipeline/Lit"))` and pick a
colour where it stood. Three problems in one line: the island ended up wearing forty near-identical
browns, none of them had instancing on, and each baked prefab carried its own material asset - so a
hundred crates were a hundred materials and a hundred draw calls, all of them slightly different
crates.

`Editor/Palette.cs` is the whole fix: thirteen named entries, each a shared `.mat` asset in
`Assets/_Project/Art/Greybox`, instancing on. A factory asks `Palette.For(colour)` and gets the
*nearest* entry, or `Palette.Named("Wood")` when it knows what it wants. Snapping is the feature, not
a compromise - a coherent look **is** a short list of materials that everything is painted with, and
the quickest way to get one is to make the wrong answer unavailable. `EWYF/Art/Rebuild palette`
writes the whole set, and the bake commands are re-runnable, so the day somebody wants warmer sand
they change one number and re-bake rather than repainting a hundred prefabs.

The runtime half is `-lookTest` (`World/LookTest.cs`), which runs solo on either island, waits for
the scene, and walks every `Renderer`. It fails on the states that are wrong no matter what the art
ends up being - a missing shader, a renderer with nothing on it, a `MeshRenderer` whose material
cannot batch - and then on the one that is a judgement call: more than `Budget = 40` distinct
materials in a scene. That number is a ratchet rather than a measurement. It passes today, and the
day somebody adds their own brown it does not.

What a terminal cannot settle is whether the result is *nice*. It can settle that there is exactly
one of each colour, which is the part that was actually broken.

---

## The game makes its own noise (#80, #81)

There is no audio budget and no sound designer, so nothing is imported: every sound in the game is
computed. `Audio/Synth.cs` is forty lines of the usual primitives - white noise off a seeded
`System.Random`, an exponential `Decay`, sine, square, and a `Clip` helper that clamps, guards NaN
and fades 4 ms at each end so nothing clicks. `Audio/Sfx.cs` turns those into the fourteen sounds the
game needs (`Punch`, `Taser`, `Zap`, `Shot`, `DryFire`, `Reload`, `Step`, `Coin`, `Spin`, `Crash`,
`Click`, ...), caches each one the first time it is asked for, and plays it through a pool of eight
`AudioSource`s on a `DontDestroyOnLoad` object. Eight is the number of overlapping sounds a
four-player brawl actually produces; the ninth steals the oldest voice, which nobody has ever heard
happen.

Sound is a *client* concern, so the hooks hang off the `[ObserversRpc(RunLocally = true)]` methods
that already exist to say "this happened" - one line each in `Weapon`, `TaserWeapon`, `Health`,
`RouletteWheel`, `Fishing` and `StunState`. No new RPCs, no new network traffic, and a sound cannot
desync because it is never authoritative. Footsteps are the exception and get their own watcher
(`Audio/Footsteps.cs`) over `NetworkPlayerRegistry`, measuring distance travelled rather than
listening for an animation event: a stride is 2.2 m, the player must be grounded, and the whole thing
lives outside the player prefab so it survives prediction replaying the same tick three times.

Music is generative and streaming. `Audio/Music.cs` creates one `AudioClip` with
`AudioClip.Create(..., stream: true, OnRead)` and fills the buffer on the audio thread: a two-sine
pad plus a pentatonic pluck with rests, the scale, root, tempo and pad chosen by `Mood`. The mood
comes from the loaded scene name, hooked to `SceneManager.activeSceneChanged`, so the menu, the first
island, the second island and the casino each sound different without anybody shipping four tracks.
It never loops, because there is no loop - only a function of time.

The audio thread is a hostile place, so `OnRead` touches no Unity API at all; `Mathf`, `System.Math`
and a seeded `System.Random` are the entire vocabulary.

`-audioTest` checks what a headless process can check: every `Sound` produces a clip that is audible
(peak above 0.05), of sane length, cached by reference rather than rebuilt, and that playing it
headless is harmless. For the music it pulls two consecutive 8192-sample buffers and asserts they
differ, which is the only automatic way to catch "the soundtrack is four seconds long", and that
Island and Island2 do not produce the same samples. Whether it is *good* is a human verdict; whether
it is silent, clipping, or on a loop is not.

---

## A menu that is the lobby (#82)

Before this, the game connected on startup because a command-line flag told it to, which works
exactly once - for the developer. `UI/MenuScreen.cs` is the first thing a player sees: its own canvas
at sort order 400, five rows built with the same `HudFactory.Button` as the rest of the HUD, and a
status line.

It is deliberately not a screen *manager*. There are no states, no stack and no transitions -
`Update` asks whether a session is running and shows or hides itself accordingly. That single check
covers everything the flow actually needs: the menu vanishes when a game starts, and it comes back by
itself when the run ends or the host drops, which is the case a state machine would have to be told
about.

Host goes through `SteamLobby.Instance.HostLobby()` when Steam is up and falls back to
`NetworkBootstrap.StartHost(NetLink.Tugboat)` when it is not, so the game is still playable over a
direct connection with no Steam client running - the case Valve's build review tests and nobody else
does. Joining is not a button: you accept a friend's invite, Steam hands the lobby to
`SteamLobby`, and the menu shows the member list. Invite opens the overlay. There is no server
browser, no lobby code and no friends list of our own, because a four-player game with Steam invites
needs none of them.

Headless builds create no canvas (`HudRoot.Awake` returns early when there is no graphics device), so
UI is normally unreachable from a harness. `MenuScreen` makes an exception for `-menuTest` and builds
its canvas anyway, which lets the harness check the wiring rather than the pixels: the menu exists
and is shown, the status admits Steam is missing, nothing is connected, `HostClicked()` brings up a
server and a client, the menu hides, `LeaveClicked()` tears the session down, and the menu comes
back. Nine checks for the sequence a player performs in their first thirty seconds.

---

## The walk cycle is a function, not a file (#76, #77)

#77 asked for a Mixamo set retargeted onto the rig. Mixamo is a browser and an account, and this
project is written from a terminal, so that route was never going to close. The alternatives were
worse: every free humanoid library ships its own skeleton, and retargeting onto
`PlayerPrefabBuilder`'s generated bones is a job for a person with the editor open.

So the animation is code. `Player/BodyAnimator.cs` is one watcher over `NetworkPlayerRegistry` -
the same shape as `Audio/Footsteps`, and for the same two reasons: prefabs here are generated and
never hand-edited, and player movement runs inside a predicted tick that FishNet replays. It reads
each body's rest pose off the rig at runtime, measures how far the body moved since last frame, and
writes six bone rotations in `LateUpdate`: legs opposed on a sine, knees folding on the half of the
wave where the leg swings through, arms counter-swinging, and a tucked pose while falling. The chest
is deliberately left alone - an idle breathing sway is the obvious next touch, and the head is a child
of the chest with the camera rig riding it, so two degrees of breathing is two degrees of camera
rocking at every moment the player is standing still and reading the world. Carrying something replaces the arm
swing with both arms forward, because arms swinging through a carried crate reads as a bug.

Two properties come free from driving it with *distance* rather than playing a clip. There is no foot
sliding to tune - a sprint is the same wave sampled against more metres, so the stride lengthens by
itself. And there is nothing to network: every client computes the same pose from a position it
already has, so the animation costs zero bandwidth and cannot desync.

The part of #77's acceptance with teeth was "works with both the animator and the ragdoll bone
setup". It is one branch: a ragdolled body is skipped entirely, because those transforms belong to
the physics solver, and writing over them is how you get a corpse with a twitching hip. `-animTest`
drags a body forward by hand and measures the bones against the pose the rig was built in: the legs
must leave rest, they must be opposed to each other, the arms must be opposed to the legs, standing
still must settle back to rest, a ragdoll must receive no poses at all, and getting up must hand the
bones back.

For #76 the honest answer is that "funny at a glance" is a human verdict and low-poly modelling needs
Blender open. What a batch job can do is the part that was actually missing, and it happens in
`PlayerPrefabBuilder.Dress`: the primitives are painted from the palette - skin, cloth, dark boots -
and given the details that make a silhouette read as a person, which are hands, feet, a nose so the
head has a front, and a cap. Every one of them is mesh only, with its collider destroyed on the spot,
so none of it changes a mass, a joint limit or how far a body flies when a car hits it. On top of
that each body's *cloth* is tinted per player through a `MaterialPropertyBlock` - four shirts, no
extra materials, still batching - because four grey bodies in a four-player game is four people who
cannot tell which one is them.

---

## Why the blockout reads as unfinished (#78)

The issue asks for modelled POIs. That needs Blender and a person, and "reads as intentionally
stylised" is a verdict only a human can give. But the reason a greybox reads as *unfinished* is not
that the shapes are simple - plenty of shipped games are this simple - it is that every building is a
stack of axis-aligned boxes with a flat slab on top. Nothing in the world is at an angle, so the
whole island looks like scaffolding.

So `GreyboxBuilder` grew two pieces of shared vocabulary. `Roof` builds a pitched roof from two
tilted slabs and a ridge, which is four primitives and the only angle in the building. `Opening`
insets a dark recess where a door or a window goes, rather than cutting a hole: a wall with a hole in
it stops being convex, and a ragdoll finds every non-convex wall on the island. At ten metres the
recess reads the same and costs one shadowless box.

Both are used by the shop and the casino, which are the two landmarks with walls. The casino keeps
its real doorway - a two-metre gap between two front walls, wide enough that four people arriving at
once do not wedge - so it gets windows only.

This does not close #78. It closes the half of it that does not need a person to look at the screen.

---

## Somebody else's models, and which way is up (#76, #78, #79)

The playtest verdict on the first art pass was that carrying works and the look does not: trees
"drawn in Paint", people "like Mario in 1990". Everything was procedural geometry or a Blender
script's idea of a tree, painted from fifteen programmer colours. The acceptance is a human one -
does it look made by artists, on a Radeon 760M at 60 fps - so this section records the machinery
and the decisions, and `docs/ART-PLAN.md` holds the full argument, the licence table and the task
list.

**Kenney for everything static, Quaternius for people.** The build machine could not open any
asset site, only GitHub. Kenney's whole library exists there as a CC0 mirror, so every model this
pass uses was parsed for triangles, materials and size, and rendered and looked at, before it was
chosen. No Quaternius file could be checked, and its people are the one thing Kenney cannot supply,
so that is the only thing taken from it. The characters are the next section.

**Four editor files, one direction.**

| File | Job |
|---|---|
| `ArtCatalog` | Every model the game uses: id, kit, file, category, size in metres, whether it stands. |
| `ArtExtract` | Copies exactly those files, plus each kit's colormap and **its own `License.txt`**, out of the zips you downloaded. Fails listing anything missing, with the nearest names in the zip. |
| `ArtLibrary` | Imports them: one shared material per kit, scaled to the catalogue size, checked upright; builds the terrain tree prototypes. Replaces `ModelLibrary`. |
| `ArtDress` | Hangs models on the greybox: `Fit` one model in a box, `Tile` a module across one. |

The extractor exists because "somebody unzipped it into Assets" is a folder nobody can state the
contents or the licence of. This one holds the catalogue and nothing else, and the licence file
beside each kit is the one from the zip, not a claim about a web page.

**The axis story, again.** The Blender pipeline shipped a forest lying on its back: its export was
already Y-up, and "bake axis conversion" applied a second rotation. It was caught only because the
importer logged bounds. Third-party kits are exported however their authors exported them, so this
time nothing is assumed:

- a model marked upright is measured after import, and if it lies down it is re-imported with
  the other `bakeAxisConversion`; if it still lies down that is an error naming the file;
- the setting a kit's trees prove is applied to the rest of that kit, because a bottle cannot
  prove which way is up and was exported the same way as the palm. A prop is never imported before
  the upright model that teaches its kit: `Source` imports the teacher first, so the answer does
  not depend on which id somebody asked for;
- if neither setting stands it up, the importer is put back as it was and the error names the file,
  so a bad model does not leave its kit taught the wrong way;
- `-lookTest` now checks it at run time too, on the terrain's tree prototypes, which it never
  used to see at all.

**Materials: the kit's atlas, not the palette.** Every current Kenney kit paints every model
from one 512² `colormap.png`, so every slot in a kit is remapped to one `Kenney_<Kit>` material.
Snapping the artist's colours onto `Palette` would have been the one-line change and the wrong
one: fifteen programmer colours are how the island got its look. The one flat-coloured kit
(furniture) gets a `Flat_<RRGGBB>` material per distinct colour, shared across kits. Colormaps
import without mipmaps and uncompressed: a mip or a compression block of a swatch sheet averages
neighbouring swatches, and a distant palm turns the colour of the rock beside it on the sheet.
`Palette` stays, for what is still a primitive: bet spots, markers, the revive machine.

**The greybox stays the layout.** `ArtDress` never moves a box. The box keeps its name, its
collider and its transform; only its renderer goes, and the model hangs under it in an unscaled
wrapper whose own scale does the fitting, so the model's root keeps the rotation and unit scale its
importer gave it. That is why `-casinoTest` still measures the same doorway, the NavMesh (baked
from colliders) is unchanged, and the POI validation sees the same footprints. Turns are quarter
turns only: under a stretched parent, a quarter turn permutes the stretch and anything else
shears. A box is stripped only after its model is in, so a machine without the kits builds the
old greybox rather than an invisible building.

A wall is a floor tile stood on its edge. For a structure module `Tile` turns it so its thinnest
side faces the box's thinnest side, then grids it across the other two, which is how one Kenney
plank tile is the casino's floor, its five walls and its sign. Anything else only ever turns about
the vertical, to line its long side up with the box's: a stump or a crate whose thinnest side
happens to be sideways would otherwise be laid on its flank. A model's own root translation is
zeroed in its wrapper, because a kit file whose root sits off the origin would otherwise hang
beside its box.

**Stations are dressed on the saved prefab.** The chest, the bench, the fire, the filter, the
shop counter and both cage windows are networked prefabs that scenes place and FishNet's
spawnable list names by id. `ArtDress.DressPrefab` loads a saved prefab's contents, dresses them
and saves over the same path, so the GUID and every file id inside survive a dressing, and a
child called `Art` tells a re-run it has already been there. The builders that rebuild every run
(storage, stations) dress after every save; the ones that build once (shop, cage windows, table)
dress the prefab that is already there. `Replace` puts one model over several blocks that are one
thing, turned a quarter if that lines its long side up with theirs. Where a block's collider is
the object (the chest's body, the bench top, a counter), the model fills the collider rather than
keeping Kenney's proportions: bumping into a bench that is not drawn there is worse than a bench
a little wider than drawn. Kenney's workbench is square, so the long bench is two of them.

**Lighting.** SSAO is a renderer feature written by `RenderTuning` on Medium and High, never Low.
On Medium it runs after opaque, from depth alone, at half resolution with four samples, because the
before-opaque mode needs a depth prepass an iGPU cannot pay for. HDR is now on for Medium: URP 17
renders it into 32-bit R11G11B10, the same bandwidth as LDR, and without it the ACES curve had
nothing to roll off and the bloom threshold could never be crossed. And `PostProcess` now switches
post-processing on for the camera. Until now only `DrunkVision` did, so the global grade appeared
the moment somebody got drunk and not before.

It still would not have drawn. `ProjectSetup` makes the renderers with
`ScriptableObject.CreateInstance<UniversalRendererData>()`, and that leaves `postProcessData` null:
URP's own "Create > Rendering > URP Universal Renderer" menu fills it, the field carries no
`[Reload]`, and a renderer without it builds no post-processing pass at all, whatever the camera or
the volume says. So the grade, the bloom and `DrunkVision` itself had never been on screen.
`RenderTuning` now gives every renderer that lacks it URP's own `PostProcessData.asset`, and
`ProjectSetup` sets it at creation. The line to look for:

```
[RenderTuning] URP_Medium_Renderer had no post-processing data; it has URP's now.
```

**Measured.** Nothing yet. The build machine has no Unity and could not download a kit. The
first run on the real machine prints, and belongs here:

```
[ArtExtract]   Copied N model(s) into Assets/_Project/Art/ThirdParty, 0 missing.
[ArtLibrary]   48 of 48 models ready, 0 failed.
[RenderTuning] SSAO on URP_Medium_Renderer (after opaque, depth, half res, 4 samples, Kawase).
[LookTest]     N passed, 0 failed.
[CasinoTest]   N passed, 0 failed.
[ArtDress]     Dressed Assets/_Project/Prefabs/Stations/StorageChest.prefab, guid ... kept.
```

**Left out on purpose:** the natives and the NPCs (T10, on the same bodies); animals, vehicles, weapons and the plane (P4 in the plan);
grass, which stays a billboard; an HDRI, which a 20-minute day cycle cannot use (§7).

---

## People over the ragdoll (#76, #77, ART-PLAN T9)

The player was eleven primitives in a shirt colour. It is now a skinned Quaternius body, and the
eleven primitives are still there underneath doing everything they did: colliders, masses,
joints, every hit and every throw. They are just no longer drawn. Nothing about combat, carrying
or the ragdoll changed, which was the point: the riskiest part of the art pass may not touch the
physics that the game is built on.

**Two editor steps, in this order.**

| Step | What it makes |
|---|---|
| `CharacterArt.Build` | Extracts Universal Base Characters and Universal Animation Library out of their zips, imports every body and clip as Humanoid, paints the bodies, and writes `_Characters/Player.controller` and the `UpperBody` mask. |
| `PlayerPrefabBuilder.BuildPlayerPrefab` | Puts every body `CharacterArt.Bodies()` accepts on the ragdoll, measures each against it, and adds `CharacterSkin`. |

The controller is emptied and refilled in place on every `Build`, so its GUID survives and every
prefab wearing it stays pointed at it. The player prefab still follows `Build`, because the bodies
it wears are whatever `Build` imported.

**Extracted by kind, not by name.** No Quaternius file name could be read from here, so unlike
`ArtCatalog`'s Kenney list nothing is named. `CharacterArt` takes the zip whose name holds the
pack's hint and the most FBX files, prefers a `Unity` or `FBX` folder when a file exists in
several, and copies every FBX, every texture that is not a preview, and the licence. A zip
without a licence is an error, not a warning. A body is any model that builds a human avatar,
has a skinned mesh and stands 1.4-2.2 m; when the pack names some `FullBody`, only those count.
At most four, because four players.

**Import.** Humanoid, avatar from the model, axis conversion baked. A model that comes in a
hundred times too big or small is rescaled by a power of ten; one that lies down is re-imported
without the baked conversion, the same test as the Kenney trees. Clips take their names from the
take (after the `|`), loop when the name says `Loop`, and keep root height and rotation locked, so
a walk walks on the spot and the motor stays the only thing that moves a player.

Materials are remapped to `Quaternius_<slot>`, painted from the texture the FBX embeds, else a PNG
in the pack whose name matches the slot, else a flat colour and a warning. Eyebrows and lashes
render both faces.

**The controller.** Layer 0 is a 1D blend on `Speed` at the motor's own speeds (idle 0, walk 2.2,
jog 4.5, sprint 7.5) plus `Seated`, `Air` and `Punch` from any state. Layer 1, `Carry`, is masked
to the arms and weighted by `CarrySystem.IsCarrying`. A state whose clip is missing is left out
rather than left empty, because an empty state is a T-pose; only a missing idle or walk fails
the build. The carry pose is UAL's `Driving_Loop` (both hands forward on a wheel), and seated is
the same clip whole: the library has nothing closer, and it is a guess until somebody looks.

**Fitting a body to the ragdoll** (`PlayerPrefabBuilder.Wear`). Every bone is found through the
avatar's own `humanDescription`, not by name, so a UE rig and a Rigify rig fit the same way. The
body is turned to face +z if its toes point the other way and scaled so its hips are the
ragdoll's hips (clamped 0.7-1.4). Then eleven links:

| Physics bone | Model bone | Points at |
|---|---|---|
| Hips | Hips | Spine |
| Chest | Chest | Neck |
| Head | Neck | Head |
| UpperArm.L/R | UpperArm | LowerArm |
| LowerArm.L/R | LowerArm | Hand |
| UpperLeg.L/R | UpperLeg | LowerLeg |
| LowerLeg.L/R | LowerLeg | Foot |

The physics head drives the model's *neck*, because the physics head pivots at the base of the
neck. The rig's `.L` sits at +x with the nose at +z, which is a person's right, so sides are
paired by which side of the body each arm is on rather than by name. Each link stores the model
bone's rotation relative to its physics bone once the two point the same way; that offset is all
there is.

Skinned bounds are a 2.4 m cube on the hips, because a ragdoll goes a long way from its root and
bounds that stay on the root cull a body lying three metres off.

**The colour is a headband.** The bodies are textured and have no hair; there is no material on
them a tint would not spoil (a green texture is green skin). `Band` bakes the mesh, takes the ring
of vertices 7.5 cm under the crown, and fits a thin cylinder round it, parented to the head bone.
The bands are what `PlayerIdentity` tints now. Which body a player wears is `ColorIndex % bodies`,
so four players in a full lobby are, as far as the pack allows, four different people.

**`CharacterSkin` at run time** (order 100, after the ragdoll and the motor):

- *Standing*, the animator plays. `Speed` is measured from how far the root moved, like
  `BodyAnimator`, so a spectator's copy needs nothing a spectator does not already have. Nothing
  is networked.
- *Limp*, the animator is switched off, every bone of the body is put back to its bind pose, and
  the eleven linked bones are laid along their physics bones, parents first; the hips are placed
  where the physics hips say. Restoring the bind pose first is what keeps the spine, collarbones
  and hands from staying wherever the last clip froze them.
- *Getting up* blends from the last limp pose to the animator's over 0.35 s. The frame the root
  jumps to the hips counts as no movement, so standing up is not a sprint or a jump.
- *Punch* is not triggered from a seat: the state machine will not take it there, and an unused
  trigger stays set until you get out.
- *Owner*: your own body is shadow-only while alive (the camera is in its head) and drawn when
  dead, because death is third person.
- *Headless*, the bodies are destroyed in `Awake`: fifteen thousand skinned triangles and an
  animator per player buy a dedicated host nothing. `-skinTest` is the one exception.

`BodyAnimator` still poses the physics bones. Nobody sees them, but their colliders are where
your arms are.

**`-skinTest`** (host, solo, either island). Per body: 11 links wired, a human avatar and a
controller; triangles under the Character cap (16 000, raised from the plan's 15 000 because the
female body is 15 060); at most three materials. Then: exactly one body shown and it is the one
the colour slot picks; every primitive hidden; the colour on the band and not on the body; the
owner's body shadow-only; standing still reads as still; a forced walk drives the blend and swings
the thigh more than 10°; after a shove, limp, every link within 20° of its physics bone and hips,
elbows and knees within 0.2 m of theirs; standing again gives the animator back; carrying brings
the arm layer to full weight and turns the upper arm more than 20°.

**Not verified.** Nothing here has run: the build machine has no Unity and could not download the
packs. The UBC geometry was read from a glTF re-export, and the zip layout, the FBX names, where
the textures live and whether Unity's avatar auto-mapping takes the UE rig are all unknown until
`CharacterArt.Build` runs and prints. Every one of those logs what it found and fails with a
message that says which.

## The islanders in the same bodies (#76, ART-PLAN T10)

The natives, the castaway and the barman were boxes. They now wear the players' Quaternius bodies,
with no ragdoll under them: nobody here goes limp, so the animator has the body the whole time.

**Dressing** (`CharacterArt.Dress`, called by each builder). The bodies go under a `Skin` child,
inactive, each with its own band. Then the greybox loses its look: every other direct child keeps
its transform and its collider and loses its `MeshRenderer` and `MeshFilter`. The transforms
matter: `Native` still scales `_body` and `_head` per role, and a dart still leaves from the head.
With no bodies imported or no controller, `Dress` touches nothing and says so, and the boxes stay
rather than leave an invisible person.

| NPC | Dressed by | Bodies | Band |
|---|---|---|---|
| Native | `NativeFactory` (rebuilt whole) | all | `Accent`, then the role's warpaint at spawn |
| Castaway | `CastawayBuilder` (rebuilt whole) | the first | `Canvas` |
| Barman | `CasinoFactory.Barman` | the second | `Dark`, where the hat was |

The barman prefab is built once and kept, so an existing one is dressed in place through
`ArtDress.DressPrefab` (marker `Skin`), GUID and all. That is why the controller is now refilled in
place rather than recreated: a controller with a new GUID would leave the barman pointing at
nothing, and nothing would rebuild him.

**`NpcSkin` at run time** (order 100). Nothing is networked; everything is read off state every
peer already has.

- *Which body*: `ObjectId % bodies`, picked once the object is spawned. Before that the id is the
  same placeholder on every object, and a camp would be four twins.
- *Speed*: measured from how far the root moved, as `CharacterSkin` does.
- *Seated*: the castaway's synced stage is `Aboard`. It boards by being parented to the plane's
  `CarrySocket`, but only the server parents it; `NetworkTransform` does not sync the parent here.
- *A single body* (the castaway, the barman) is shown at once, without waiting for the spawn.
- *Dead*: `Health.State == Dead` plays `Death01` from any state. It does not loop, so a dead
  native lies there until it despawns.
- *Carrying*: the native's carry socket has a child. `Carryable` parents the carried hips there
  on every peer, so the child count is the same everywhere. It drives the arm layer, as
  `CarrySystem.IsCarrying` does for a player.
- *Role*: `Native.ApplyShape` calls `Fit`, which scales every body so its crown is at the role's
  height (1.7-1.92 m), and puts the role's `MarkColour` on the band. Warpaint was the one thing
  that told a spearman from a blowgunner, and it still is.
- *Headless*: the bodies are destroyed in `Awake`, as for the players. `-skinTest` is the
  exception.

**The controller** has a fifth parameter, `Dead`, and a `Dead` state on UAL's `Death01`. Players
never set it; their death is the ragdoll.

**Order.** `CharacterArt.Build` must run before `CasinoFactory.Build`, `NativeFactory.Build` and
`CastawayBuilder.Build`. Any of them run first gives boxes and a warning, and a rerun after
`Build` fixes it.

**`-skinTest`** also checks every live `NpcSkin`: there is at least one (the barman and the
castaway are both on the first island); each shows exactly one body, animated, with a controller
and a human avatar; no `MeshRenderer` is left outside the bodies; the body is under the Character
triangle cap; and a native's band carries its role's warpaint.

**Left out, known:**

- Natives do not swing. Their attacks are resolved on the server with no RPC, so a client has no
  moment to play `Punch` on. The fix is an observer event per swing; it is not in this PR.
- Nobody holds a spear or a blowgun. The weapon models are T12.
- The castaway's waving arm is gone. It was the one thing that read from the air. UAL's file
  names could not be read from here, so no wave clip is wired, and the castaway idles.
- The castaway sits with `Driving_Loop`, like a seated player.

**Not verified.** Nothing here has run, for the reason T9 gives.

---

## The vehicles in the kits' clothes (#79, ART-PLAN T11)

The buggy and the boat are dressed by the builders that already make them, after the greybox is
built and only when the kit is on disk. `ArtLibrary.Source` returning null leaves the old boxes.

**Nothing physical moves.** The buggy's chassis box keeps its BoxCollider. `CarController`'s belly
check reads that collider, so the box has to stay. The wheel colliders, the seats, the exits, the
cargo socket, the hull collider and the six floats are the same numbers as before. The art only
has to land on them.

**The buggy** is the car kit's `race`, not the plan's SUV.

- Every closed car in the kit has its wheel arches inside a body far wider than its track. Matching
  the arches to the buggy's 1.56 m track and 2.5 m wheelbase makes the SUV 2.8 m wide.
- Four seated riders would put their heads through a closed roof.
- The race car is open. Scaled so its axles are the buggy's 2.5 m apart (×1.64), its wheels land
  within 4 cm of the physics ones and its body is 1.97 m wide against the chassis's 1.9.

`VehicleBuilder.Dress` works from the imported model, not from numbers copied out of the kit. The
axis conversion and the unit scale never enter the sum.

1. It unpacks the instance and finds the renderers whose names start with `wheel`. It tells front
   from back by `front` in the name.
2. It yaws the car so front is +Z, scales it to the wheelbase, and puts its axle midpoint on the
   buggy's.
3. It moves each kit wheel onto the nearest wheel visual, the transform `CarController` turns.
4. The visual loses its cylinder and its (0.45, 0.16, 0.45) squash. The wheel is scaled to the
   collider's 0.45 m radius and centred on the axle.

A kit with a different wheel count is logged and left as greybox. The roll bar goes; it was cosmetic.

**The wheel visuals were broken before this** and nobody could tell on a cylinder.

- `CarController.Update` rebuilt the pose from scratch every frame, so the roll never accumulated.
  The wheels twitched one frame's turn and stood still.
- It applied the steer *before* laying the wheel on its side, which turned it about its own axle.

It is now `Euler(0, steer, 0) * Euler(0, 0, 90) * Euler(0, _rolled, 0)`, read right to left: roll
about the axle, lay the wheel down, steer about the car's vertical. `_rolled` accumulates. It is
subtracted because the laid-down axle points out of the left side, and a positive turn about it
rolls backwards. Clients are unchanged: speed still comes from the transform and steer from the
`_shownSteer` SyncVar.

**The boat** is the watercraft kit's `boat-speed-j`, fitted keeping its shape into the hull's
2.4 × 6 footprint and standing on the keel.

- It is drawn 1.78 × 4.27, so it comes out 2.4 × 1.69 × 5.76. The hull collider is 12 cm longer
  at each end.
- The kit points its bow at +Z; its narrow, raised end was measured there. `BoatBuilder` already
  treats +Z as ahead.
- Bow and Console go; both were cosmetic and collider-free.

**Order.** Both builders rebuild their prefab from nothing, so they run before the terrain bakes
that place them (PR 153, step 6c).

**Not verified, and what to look at:**

- Whether the kit's wheels import as separate renderers named `wheel-*`. The build log prints
  `Dressed as race` if they do and `left as greybox` if not.
- Whether the tyres roll the right way on screen. The sign was worked out, not watched.
- Where the helm and bench riders sit relative to the speedboat's windscreen and floor.
- Whether a race car reads as the island's buggy at all. It is one entry in `ArtCatalog`. Going
  back to the SUV would mean moving the wheels, which is a handling change.

## The weapons in the kits' clothes (#79, ART-PLAN T12)

**Where a weapon is seen.** `WeaponDef.ViewPrefab` has no reader: nothing draws a weapon in the
hand. A weapon is seen only lying on the ground, and there it was its item's 0.3 m category-coloured
cube, because no item had a `WorldPrefab`. So `WeaponFactory` now does two things:

1. It dresses each weapon's view prefab with a kit model, in place (`ArtDress.DressPrefab`), so the
   GUID the weapon asset names survives. The `Art` child is the marker; a second run skips it.
2. It puts that prefab in the weapon's item's `WorldPrefab`, but only when that slot is empty.
   `WorldItem` already instantiates `WorldPrefab` under its visual root.

Held-weapon drawing is a feature, not art, and is not part of this.

**The fit.** The model replaces the `Body` box and the melee `Grip`, centred where the box was.

- Kenney draws a gun lying along +Z with the muzzle forward, which is how the box lies.
- The knife and the survival tools stand on their handles. A model taller than it is long gets
  `Euler(90, 0, 0)`, which puts the tip at +Z and the handle at -Z, where the grip was. That needs
  `ArtDress.FitBox` with a `Quaternion`; it is still a quarter turn, so the stretch does not shear.
- Most weapons keep their proportions, scaled to the box's length.
- Two have no model in any kit, so they are stretched to fill the old box. The machete is the knife
  drawn out long. The three bats are the survival kit's small log, thinned to a club.
- The chainsaw has no row and stays a box.

The knife is `upright` in `ArtCatalog` only so the weapon pack learns its axis from it.
`WeaponFactory` clears `ArtVisual.Upright` on a laid-down model, so `-lookTest` never expects a
dropped knife to stand.

**Harness.** `-weaponTest` checks that every carried weapon with a model shows it on the ground.
The fit itself runs in the editor and is only logged: `[WeaponFactory] <id> wears <file>.`, 14
times.

**Not verified.** None of this has run in Unity. Nobody has seen:

- which way the survival tools' heads point once the axis conversion is applied;
- how a 1.25 m rifle looks on a `WorldItem` whose collider is the old 0.3 m box, dropped at a
  random rotation, which may leave half of it in the ground.

---

## Textured kits: a third material mode (#76, ART-PLAN P6)

`ArtLibrary` points every material slot an FBX brings at a shared material, and it had two ways
to make that material:

- one per Kenney kit, painted from the kit's `colormap.png` (`Atlas`);
- one per distinct colour, for the older kits (`Flat_<hex>`).

The Quaternius kits P6 moves to are neither. Each model is painted from its own textures, and
the foliage is leaf cards with alpha. So a pack can now say `textured: true`. For such a pack:

- **`ArtExtract`** copies every texture in the zip to `<pack>/Textures/`, one per file name,
  preferring the copy under a `Unity` folder, then one under `FBX`. This is
  `CharacterArt.Unique`, shared now. It also accepts any `licen*` `.txt` or `.md` as the licence,
  because Quaternius does not always call it `License.txt`. The rule is unchanged: no licence in
  the zip, no import.
- **`ArtLibrary.TexturedMaterial`** makes one URP Lit material per slot, named
  `<Author>_<Pack>_<slot>`. The texture comes from the first of these that exists:
  - the one the FBX points at (Unity's importer looks in a `Textures` folder beside the model,
    which is where extraction puts them);
  - the file in `Textures/` named after the slot, found by `CharacterArt.BaseColour`, the lookup
    that already paints the bodies.
- The texture is capped at **1024**, the 760M's budget.
- A texture **with an alpha channel** is taken for a leaf card. Its material is alpha-clipped at
  0.5, queued as alpha test, and drawn from both sides, so a frond does not vanish edge-on.
- A slot with **no texture** keeps its colour, with a warning in the log.

Nothing else in the pipeline changes: the fit, the upright check, the per-kit axis, the collider
and `ArtVisual` are the same for every mode.

**Not verified.** It has been type-checked with Roslyn only. No pack sets the flag yet, so no
material is made this way until P6 V1 adds the MegaKit, and nothing on the island changes before
then.

Left for the eye:

- Leaf backs are lit with the front's normal, because URP Lit has no back-face flip.
- An opaque texture that happens to carry an alpha channel gets clipped and drawn from both
  sides. That costs a little and shows nothing, but the log names every one ("alpha-clipped,
  both sides").
- `-lookTest` counts these materials against `LookTest.Budget`, which will need raising once the
  real count is known.

---

## A long model on the ground gets a long box (#79)

A pile on the ground is one networked `WorldItem`: a rigidbody, and a `BoxCollider` of 0.56 m made
by `WorldItemBuilder`. The model the item wears (`ItemDef.WorldPrefab`) is drawn inside that box
and has no collider of its own. Since the weapons were dressed (T12), a rifle, a shotgun and a
shovel are longer than the box. They balanced inside a cube, and after the drop's random rotation
they lay half in the sand.

`WorldItem.Fit` runs whenever the visual is rebuilt:

- If the model is **longer than the box** on any axis, the box becomes the model's own bounds.
  The bounds are every renderer's local bounds, carried into the item's axes. The minimum is
  0.06 m per axis, because a knife-thin box on a terrain collider jitters. The rifle lies as long
  as it looks, on its flat side.
- Anything **smaller** keeps the cube, on purpose: a berry is picked up by its box, not by its
  pixels. The box goes back to the cube if a pile ever changes to a smaller kind.

It runs on every peer, as the visual does, so a client's interaction ray hits the same box the
server simulates.

**Harness.** `-itemTest` drops the item whose world model is the longest in the catalog (a rifle,
with the weapons dressed), tilted by 45°. It checks that the box holds the whole model. If no
item wears a model, the check is skipped and the log says so.

**Not verified.** Type-checked with Roslyn only. `Renderer.localBounds` is missing from the old
reference assemblies, but is in Unity 6000.3's own source, so it was checked by name there.

Left for the eye: whether a rifle now rests flat, and how a pistol looks in the 0.56 m cube. A
pistol is shorter than the cube, so it floats as before.

---

## The weapon in the hand (#79)

Until now nothing drew a held weapon. `WeaponDef.ViewPrefab` had no reader, so a player with a
rifle selected looked empty-handed to everybody.

`CharacterSkin.Hold` now draws the selected slot's weapon in the right hand of the body the
player wears:

- **Which weapon.** It is read off `Inventory`, whose slots and selected index already replicate
  to every peer. The selected index was replicated for exactly this, as the comment on
  `_selected` says. The item goes through `WeaponCatalog.ForItem`, and the model is the weapon's
  `ViewPrefab`: the same dressed prefab it wears on the ground (T12). An item that is not a
  weapon stays in the bag. Nothing new is networked.
- **Where.** On the humanoid avatar's `RightHand` bone. `Inventory.Changed` rebuilds it, as does
  switching bodies.
- **Which way.** Read off the hand's own bones, so it is right in whatever pose the clip puts
  the hand in. The first version recorded the hand at bind pose and kept the weapon pointing the
  player's forward from there. That held for arms down, but an aim pose would have held a gun
  sideways.
  - A gun points from the wrist (`RightHand`) through the knuckles (`RightMiddleProximal`), with
    its top toward the thumb (`RightThumbProximal`).
  - A blade stands out of the fist on the thumb side, with its edge the way the knuckles face.
  - The hand closes on the palm, halfway from wrist to knuckles. It grips the back of a gun's
    body, a third of the way up, or the last tenth of a blade, where WeaponFactory put its
    handle (`GripPoint`).
  - A rig without finger bones falls back to the forearm for "along" and the player's up for
    "top".
- **The arms.** A third controller layer, `Armed`, sits over the arms through the same
  upper-body mask as `Carry`. Its states:
  - `Pistol_Idle_Loop` for a gun and `Sword_Idle` for a blade, chosen by the int `Armed`
    (1 is a gun, 2 is a blade).
  - `Pistol_Shoot` and `Sword_Attack` on the trigger `Fire`.
  - `CharacterSkin` fades the layer in while something is drawn in the hand, and out while
    carrying or seated, where both hands are busy. The model is hidden for as long. `Weapon.Attacked` fires `Fire` when
    something is in the hand. Bare fists keep the whole-body `Punch`.
  - `Armed` is only ever set to 1 or 2, and each `Fire` transition wants one of them. So every
    `Fire` is taken, and none waits in the trigger for the next weapon.
  - `CharacterArt.Build` builds the layer only if all four clips are in the library, and warns
    otherwise.
- **Colliders.** Any collider or rigidbody in the prefab is switched off and destroyed. Under the
  hand, it would join the player's own collider and catch rays aimed past it.
- **Visibility.** It follows the body's rule: shadow-only for the owner while alive, drawn for
  everybody else.

**Not done: a first-person view model.** The owner sees their weapon only as a shadow, like
their own body, because the camera is inside the head. A camera-held model needs its own near
plane, position and field of view. Those are feel decisions, left for a session that can see
the screen.

**Harness.** `-skinTest` gives the host the first weapon in the catalog that has a model, and
selects it. It then checks five things:

- the weapon is drawn under the right hand bone;
- it is shadow-only, since the host owns it;
- it has no live collider;
- the model's box is within 10 cm of the wrist;
- a second later, the `Armed` layer is fully in. This fails until `CharacterArt.Build` has
  re-run, since the controller is content.

**Not verified.** Type-checked with Roslyn only.

Left for the eye:

- The grip fractions in `GripPoint`.
- Whether the Quaternius thumb bone gives "up" for a gun, or a quarter turn off.
- Whether a blade's edge faces the right way. That depends on which way the kit drew it.
- Whether `Pistol_Idle_Loop` reads as holding a gun with the legs running underneath.

Each of these is one number or one sign in `Hold` or `GripPoint`, once somebody has looked at
it.

---

## The island's nature from Quaternius (#76, ART-PLAN P6 V1)

The first kit to use the textured mode. Quaternius's Stylized Nature MegaKit, free Standard
edition, CC0 (`License_Standard.txt`, copied as `License.txt`), replaces Kenney's jungle trees,
pines, ground plants and loose rocks. The palms stay Kenney's, because the free edition has
none, and one Kenney palm stays in the jungle so it still reads as tropical.

**What was taken, measured in Blender before choosing:**

| Species | Models | Triangles |
|---|---|---|
| JungleTree | CommonTree 1, 3, 5 + Kenney `palm-detailed-bend` | 6 265, 3 505, 3 182 |
| HighlandTree | Pine 1, 2, 4, 5 | 3 947, 3 648, 3 370, 1 646 |
| Bush | Bush_Common, Fern_1, Plant_1_Big, Flower_3_Group | 900, 288, 360, 755 |
| Ground | Rock_Medium 1-3 + Kenney log and stump | 342, 244, 522 |

The twisted trees (about 10 000 each) and the dead trees (5 600 to 6 600) were left out. The
jungle is the densest thing on the island.

**Caps.** `ArtVisual.Cap` went from 600 to 6 500 for a tree and from 300 to 1 000 for a plant.
Quaternius's trees are real models, not Kenney's twelve-sided cones. What bounds the cost is
`IslandProfile.TreeMaximumFullLOD` (60): the bake logs a worst case of 60 × 6 265 = 375 900
triangles at full detail. That is comfortable on the 760M. The leaf cards are alpha-clipped and
two-sided, and that overdraw is the cost to watch when the user plays on the iGPU.

**`ArtExtract.Pick`** now prefers a path containing "unity". Quaternius ships `FBX/` and
`FBX (Unity)/` with the same file names. Kenney zips have no such folder, so nothing else moved.

**Ids.** New ids (`Broadleaf*`, `Pine*`, `Bush`, `Fern`, `Boulder*`). `Leafy` and `Flowers` kept
their names and their prefab GUIDs but now point at the Nature kit. `Rocks` stays Kenney's for
`GreyboxBuilder`. Eight prefabs that nothing references any more were deleted.

**Run:**
- `ArtExtract` copied 61 models, 0 missing.
- `ArtLibrary.BuildAll`: 62 of 62 ready. Seven slots were painted from the kit's textures:
  bark and rocks opaque, the leaves and flowers alpha-clipped.
- Island 1: 15 012 plants. Island 2: 1 221.
- Build clean.
- `-lookTest` 10/0 on both islands: 26 and 16 distinct materials, under the budget of 48.

---

## The players woke up inside the village (bug report, 2026-09-28)

**Symptom.** On the first island a native camp stood where the players spawn.

**Cause.** `TerrainGenerator.WriteScene` opens a fresh scene with `NewScene`, and that unloads
unused assets. On the first bake after an art import, the POI catalog was one of them, so from
then on `profile.Pois` read as null. Everything baked after that point hangs off the catalog, and
each piece fell back to the origin:
- the herds (`AnimalFactory.BakeZones`);
- the native camps (`NativeFactory.BakeCamps`);
- the spawn ring (`WriteSpawnPoints`).

All three ended up at (0, 0), seven metres apart. `e1194e2` had patched the same null in
`POIFactory.Bake` alone, so the landmarks were right and nothing else was.

**Fix.** `WriteScene` reattaches the catalog right after `NewScene`, once, for every caller.

**Test.** `-nativeTest` measures each camp against every spawn point and wants 25 m of clear
ground beyond the camp's radius plus its night leash. The exception is the second island. It is
the hostile one and 512 m across, and a headhunter chasing you to the landing at night is the
design there. So on that island it measures with the night's notice instead: a camp may not *see*
the landing.

**Run.**
- Island 1: camps at 331 m and 210 m from the base; `-nativeTest` 164/0.
- Both islands: `-lookTest` 10/0.

---

## The slot cabinets (#159)

Three cabinets along the casino's back wall, right of the bottles, each its own game. The names,
symbols and numbers are ours; the mechanics are the genre's common stock.

| Cabinet | Grid | How it pays | The extra |
|---|---|---|---|
| **Coconut Sevens** | 5x3 | 20 fixed lines, 3+ left to right; stars pay anywhere | Double-up card: red or black, up to 5 times |
| **Wrath of the Volcano** | 6x5 | 8+ of a symbol anywhere; winners burst and the rest tumble | Lava orbs (x2-x500) multiply a paying sequence; 4 volcanoes give 15 free spins where the orbs add up; ante bet (#255) |
| **Reef Rush** | 7x7 | Clusters of 5+ touching; burst and tumble | A cell burst twice becomes a x2 spot, then x4 ... x128; 3+ chests give free spins where the spots never reset |

**The game is arithmetic, and it lives in one file.** `SlotMath` takes a kind, a seed and a bet and
returns every picture of the spin and the payout, with no Unity, no network and no clock in it. Two
things follow. The harness can play 200 000 spins of each game in seconds. And a spin crosses the
network as four bytes: the server rolls a seed, works the spin out, and sends the seed; every client
replays all of it - a Volcano feature is a few hundred grids - and draws what the server already paid.
That only works if the generator is ours, so `SlotRng` is SplitMix64 rather than `System.Random`, whose
sequence belongs to whichever runtime is underneath.

**Authority is the roulette wheel's.** The stake leaves the wallet on the press, `ServerSpin` decides
everything before a reel moves, and the win is paid when the server's copy of the animation ends, so
the chip counter never gives a spin away. Nothing a client sends can reach a seed. Chips only: the
two doors between chips and money are still the cage's.

**The numbers.** Every paytable is in hundredths of the bet; a spin sums in those and rounds down to
chips once. Tuned by simulation under .NET 8 against the same file, several million spins each:

| Cabinet | Return | Hits | Feature | Best seen |
|---|---|---|---|---|
| Coconut Sevens | 95.9% (the card is a fair 50/50 on top) | 34% | - | 300x |
| Wrath of the Volcano | ~95.5% | 24% | 1 in ~440, ~31% of the return | ~2000x |
| Reef Rush | ~95% | 34% | 1 in ~370, ~34% of the return | 5000x (the cap) |

No spin pays past 5000x. The two feature games are volatile: a 200 000-spin sample lands anywhere
from about 91% to 100%, which is why `-slotTest` checks a band and a golden number rather than 95.0.

**One material.** Thirty-odd symbols in their own colours would be thirty materials against
`LookTest`'s budget of 48. `SlotFactory` paints every colour into one 8x8 atlas and bakes each symbol
as a copy of a primitive, or a few combined (the seven, the crown, the starfish), whose UVs all sit on
its own texel. A symbol changing is a mesh swap. The cabinets themselves are palette boxes.

**The screen is local.** Reels flicker and stop left to right, winners pulse and burst, tumbles fall
into the gaps, Reef's spots grow with their multiplier. None of it is replicated and none of it
decides anything; `-slotTest` reads the grid back off the cells to prove it stopped where the seed
says. The board at the top of the screen (`SlotBoard`) takes over from the roulette board inside
2.2 m of a cabinet and shows the running win, the free spins left and the multiplier.

**Buttons** are nested `NetworkObject`s for `BetSpot`'s reason. Spin, Bet (10/20/50/100, cycling),
and on Sevens Red and Black, which only the player who won may press, and only until they spin
again.

`-slotTest` (with `-scene island -noNatives -noAnimals`), host side: hand-built grids pay what the
tables say; a seed plays the same spin twice; 20 000 fixed seeds win exactly what they won under
.NET (the check that Mono and .NET agree, which is what a client's replay depends on); the three
returns sit in their bands; three spins per cabinet pay what their seeds say and leave the screen on
the last picture; the card doubles or takes the stake; the ledger balances; busy and broke are
refused. A second process with `-slotTest -client` checks that it replays the host's spins to the
host's wins and draws their last pictures.

Run `SlotFactory.Build`, then the island bake with `-rebuildPois` so the three POIs place them.

---

## The blackjack table (#161)

Four seats against the house along the casino's left wall, paid in the chips the wheel and the
cabinets take. Standard rules and nothing clever: six decks shuffled fresh every round, blackjack
pays 3:2, the dealer peeks under an ace or a ten and stands on every 17, soft ones included. Double
on any first two cards, after a split too; split any two cards of the same value, once per seat;
split aces take one card each, and 21 after a split is 21, not blackjack. No insurance, no surrender.

**The rules are arithmetic, in one file**, like the slots. `BlackjackMath` holds totals, payouts and
the seeded shoe (Fisher-Yates over `SlotRng`); `BlackjackRound` is one round from the deal to the
dealer's last card, with no Unity in it. `BlackjackTable` drives it and owns the clock, the wallets
and the wire. So the harness can play 200 000 rounds of basic strategy (`BlackjackStrategy`) in a few
seconds: they return 99.5% of the first bet, the half percent a real table keeps.

**What crosses the wire is the cards as they land, not the seed.** A slot sends its seed because the
whole spin is decided before a reel moves; a blackjack seed would hand every client the hole card and
the next card in the shoe. The round writes each card it shows into a log, `hand * 64 + card`, with a
split written as its own entry, and the hole card goes in only when it turns over. The log is a
`SyncList<int>` every peer rebuilds the hands from (`BlackjackMath.Rebuild`); bets, owners, the turn
and the payouts are small synced lists beside it. The server shuffles from `System.Random` and nobody
else sees the seed.

**Authority is the wheel's.** A press on a seat's bet button takes 50 chips there and then, up to 500,
and sits the player there (one seat each). The first bet opens a 10 s window; then the cards go out.
A double or a split takes its stake when pressed. Hands act in seat order; a player who does nothing,
walks off, dies or disconnects is stood after 20 s, and a hand whose owner is gone is paid to nobody.
The dealer's cards go out one at a time, the payouts land, and the felt stays up five seconds before it
clears.

**The felt shows colour, the board shows ranks.** Cards are pre-placed palette slabs, eight a hand,
shown face up or face down in blue; the faces are drawn by `CardFaces` (#163). `BlackjackBoard` takes the top of the screen within 2.2 m (after a slot cabinet, before the
roulette board): the dealer's hand on the big line, yours under it with totals, and whose turn it is.
Every seat has five buttons, each a nested `NetworkObject` for `BetSpot`'s reason: a big gold bet at
the rail, and hit (green), stand (red), double (white), split (blue) behind it. The prompts only offer
what the player may do right now.

`-blackjackTest` (with `-scene island -noNatives -noAnimals`), host side: totals, the dealer's 17 and
every payout on paper; the shoe is six whole decks and a seed is a shoe; scripted rounds for the
peek, split eights with a double, split aces, a bust, seat order; 5000 random rounds end, never leak
the hole card into the log, and rebuild from it to the server's hands; 200 000 fixed shoes by the
chart net exactly what they did under .NET (Mono shuffles like .NET) and return 98.5-100.5%. Then
rigged shoes through the real buttons: a hit to 21 against a dealer bust, a split and a double, a
dealer blackjack, a player who times out, the board's strings, the ledger, and the refusals (short
stack, the 500 cap, a second seat, betting after the deal, someone else's hand, a double without the
chips). A second process with `-blackjackTest -client` watches three rounds and checks the dealer
never showed two cards while players acted, and that every payout is what its own rebuilt cards make it.

Run `BlackjackFactory.Build`, then the island bake with `-rebuildPois` so the `casino.blackjack` POI
places it.

---

## Casino juice: card faces, big wins, sounds (#163)

A win should feel like one. Three pieces, all generated in code, nothing checked in.

**Card faces.** `CardFaces` draws all 52 faces into one 468x216 atlas at runtime: a 3x5 pixel font
for the ranks, 7x7 suit glyphs, red and black ink on cream, point-filtered so the pixels read as part
of the low-poly look. Each card is a quad whose UVs sit on its cell, so the whole table adds one
material (a copy of the felt's Plastic wearing the atlas, same shader), and only once a hand is dealt;
the prefab still wears palette materials only, so `-lookTest` sees no change. The seats fan their
cards so only the right third of each shows under the next, so the index (rank over suit) lives there,
top right as the player stands; the big rank and suit in the middle are for the dealer's row and the
top card. The face plates are the old pips, resized to cover the card: rerun `BlackjackFactory.Build`.

**Big wins.** `BigWin.Tier` grades a win by the stake multiple: 10x big, 25x mega, 50x epic. Every
peer calls `BigWin.Celebrate` from the code that already knows, at the moment its own screen settles:
the slot cabinet when its replayed spin ends, the roulette wheel from a new `RpcWon(win, staked)` it
sends per winning player (the sum of their spin, so a straight-up hit is 36x, mega). Below the big tier
that is just the win chime. Above it: the fanfare, 14/28/42 pooled octagon coins (32 triangles, no
collider, no shadow, a hand-integrated bounce off the floor they came from), and `WinBanner` on the HUD
for anybody within 10 m, the tier's name slamming in and the number counting up with an ease-out over
2.5/3.5/5 s. The coins wear the palette's Gold, which `SlotFactory` now hands each cabinet and the
cabinet registers on wake; the roulette wheel borrows it, so with no cabinet in the scene roulette
still cheers but throws no coins. Rerun `SlotFactory.Build`.

**Sounds.** Four more `Synth` clips: `ReelStop` (a thunk per reel landing, reels landing in the same
frame share one), `Deal` (a snap for every card and the hole card turning), `Win` (a major arpeggio)
and `BigWin` (the arpeggio twice then a shimmering chord, 1.8 s). The pool is twelve voices now, so a
seven-reel stop does not cut its own spin. Two old gaps closed on the way: the host now hears its own
roulette spin, and the blackjack payout chime plays on every peer instead of only on the server.

`-audioTest` covers the new clips (every `Sound` is audible, bounded, built once). `-slotTest` checks
the tier thresholds and that the count-up runs 0 to the win without going back. `-blackjackTest` reads
the atlas back: every face inked, red suits red and black black, rank and suit inside the strip a fan
leaves uncovered, 52 distinct faces. The banner, the coins and the sound are playtest questions.

---

## The island's pirate pieces from Quaternius (P6 V2)

The palms, the barrels, bucket, bottles and chest, the wreck, its boat and the cave's walls now come
from Quaternius's Pirate Kit, by the same hand as the nature kit V1 put on the islands. Only
`ArtCatalog` rows change; the greybox layout, the dressing code and the prefab GUIDs stay.

- **The pack.** `new("Quaternius", "PirateKit", "", …, atlas: true, atlasFile: "Atlas_Pirate.png")`.
  The kit is painted like Kenney's, from a swatch sheet, so it rides the atlas path; `Pack.AtlasFile`
  (default `colormap.png`) names the sheet, and `ArtExtract` and `ArtLibrary.AtlasMaterial` read it.
  The FBX's one material is called "Atlas" and links no texture, which the atlas path never needed.
- **The zip.** Quaternius hands the kit out as a Google Drive folder, so the zip is named
  `drive-download-<date>.zip`. The hint is empty: every zip is a candidate and `BestZip` keeps the
  one holding the most of the pack's files.
- **The licence.** That zip carries no licence file. Rather than write one, `ArtExtract` takes a
  text file a person saved beside the zip, named after it (`<zip>.License.txt`), and until it exists
  the run fails with one missing line naming the page to read and the path to save to.
- **Rows.** Palms `Environment_PalmTree_1..3` (four slots, two sharing a model at different sizes,
  so `IslandFlora`'s weights stand); `Prop_Barrel`, `Prop_Bucket`, `Prop_Bottle_1`,
  `Prop_Chest_Closed`; the wreck `Ship_Large`, the debris boat `Ship_Small`, and a new `Cliff`
  (`Environment_Cliff1`) that `GreyboxBuilder.DressCave` fits on the cave's three standing walls.
  Each wall and the wreck are turned by `ArtDress.Along` so the model's long side lies along its
  box, whichever axis the kit drew it along; the right wall takes a half turn more than the left, so
  the mouth's two sides face each other. The lintel stays a Kenney rock. Kenney's pirate kit keeps the rocks, the thatch and the crates, and
  its palm stays in the catalogue as `KenneyPalm`, placed nowhere, so `PackAxis` still has an
  upright model to learn that kit's up-axis from.
- **Caps** (`ArtVisual.Cap`), from the measured counts: small prop 600 (bucket 532), prop 2 000
  (chest 1 636), wreck 30 000 (the ship 20 636). The boat (5 578) and the cliff (8 596) are filed as
  wrecks: landmark pieces placed a handful of times, not scatter.

Tested by the existing harnesses: `ArtLibrary.BuildAll` ("N of N models ready", caps checked),
`-lookTest` on both islands, and each builder's "Dressed … guid … kept" line.

---

## One shader for every kit (#79, ART-PLAN P6 V6)

After P6 the island is Kenney swatch atlases, Quaternius painted textures, Kenney flat colours and
the greybox palette. On URP/Lit those read as separate kits even where the colours agree: a smooth
N.L gradient shows every baked brush stroke in a painted bark texture and nothing on a swatch, and
the shade side goes grey on both. So everything now wears one hand-written shader,
`Art/Stylized/Stylized.shader` (`EWYF/Stylized`):

- **Two light bands.** The sun and every lamp go through one `smoothstep` around the terminator
  (`_RampCentre` 0.05, `_RampSoftness` 0.08). Cast shadows are deliberately not banded: the
  attenuation carries the light's shadow strength (the moon's is 0.35) and URP's fade at the shadow
  distance, and a step erases the first and turns the second into a ring around the camera.
- **The shade side is ambient, tinted cool** (`_ShadowTint`), not a darker grey. The trilight
  ambient from `DayNightProfile` still drives it, so dawn and night behave as before.
- **Painted detail per kit.** `_Detail` blends the texture toward its own read three mips up: the
  broad colours without the brush strokes. The nature kit keeps 45%. The second fetch sits behind
  `_DETAIL_SOFTEN`, which `StyleLook` turns on only where `_Detail` < 1, so nothing else pays for it. `_Saturation` and `_Brightness` nudge a kit toward the rest.
- **Rim and one hard highlight.** A thin lit-side rim for silhouettes against the sea; a single
  stepped specular spot only above smoothness 0.3, so metal and gold shine and nothing else does.
- **Emission** behind `_EMISSION` (`_EmissionColor`), for the campfire flame. The flame has its own
  `Greybox/Flame.mat` (`StyleLook.Glowing`); it used to switch emission on for the palette entry it
  snapped to, Gold, which lit every gold object. `Wear` now strips `_EMISSION` from palette entries.
- **Passes**: forward (main-light cascades with URP's shadow-distance fade, per-pixel additional lights and their
  shadows, light cookies, soft-shadow
  levels, SSAO-in-lighting, fog, instancing), ShadowCaster, DepthOnly, and DepthNormals for the High
  tier's SSAO. Forward renderer only, like every tier. One `UnityPerMaterial` buffer across all
  passes, so the SRP Batcher still takes it. Falls back to URP/Lit.

**Property names are URP/Lit's** (`_BaseMap`, `_BaseColor`, `_Cutoff`, `_AlphaClip`, `_Cull`,
`_Smoothness`, `_EmissionColor`, with `[MainTexture]`/`[MainColor]`; the alpha-clip toggle drives
`_ALPHATEST_ON` like Lit's). That is what makes the switch safe:
`StyleLook.Wear` sets `material.shader` on the existing asset and every texture, colour, cutout and
cull the generators wrote survives. Same asset, same GUID, no prefab touched.

**`StyleLook`** (`Scripts/Editor/StyleLook.cs`) holds the whole look in one table: the four shared
numbers and a per-kit row by material-name prefix. `StyleLook.Apply` (batchmode, or
EWYF/Art/Apply stylized look) walks `ThirdParty/_Materials`, `Art/Greybox` and `Art/Casino`.
It also runs at the end of `ArtLibrary.BuildAll`, and every generator that makes a material
(`ArtLibrary`, `CharacterArt`, `Palette.Named`, the roulette wheel) creates it with `StyleLook.New`, so nothing new lands on URP/Lit. Tuning is editing the table and
re-running Apply.

**The grade stays the one volume `PostProcess` already builds** (ACES, small contrast and
saturation, warm white balance, cool-shadow split). It is global, one profile for every scene, so it
already unifies the kits at the output; nothing new was added there.

**`-lookTest`** logs a shader histogram and gains a check: every `Kenney_*`, `Flat_*` and
`Quaternius_*` material, every palette entry and the roulette wheel wear `EWYF/Stylized`. A generator that goes back to URP/Lit, or a shader
that fails to compile and falls back, fails it. The material budget is unchanged: the switch
re-shades materials, it adds none.

**The committed `.mat` files still say URP/Lit until somebody runs `StyleLook.Apply` and commits
the result**; they are generated, never hand-edited, and a cloud session has no Unity. Until then
the new check fails, which is the point: it is the reminder. The name check cannot see a shader
that imports but fails to compile for the player (the material keeps the name); that is the build
log's `Shader error in 'EWYF/Stylized'`.

**The terrain** has its own shader on the same lighting; see "The ground, lit like the models".

---

## The ground, lit like the models (#79, ART-PLAN P6 V6)

After V6 every model was banded and the terrain still wore URP Terrain/Lit, so the seam moved to
where each rock meets the sand. `Art/Stylized/StylizedTerrain.shader` (`EWYF/StylizedTerrain`)
draws the ground through the same `StylizedLighting.hlsl` that `EWYF/Stylized` now includes: one
function, one band curve, one cool shade tint, so the two cannot drift apart. The ground takes no
rim and no highlight.

**Kept small for the 760M.** Four layers in one pass (`IslandSplat.LayerCount`), one control fetch
and four albedo fetches, no normal maps, no height blend, no holes. There is no
add pass, so a fifth layer would not draw.

**No basemap, no instancing, on purpose.** URP's terrain swaps to a baked basemap shader past
`basemapDistance`, found through a `BaseMapShader` dependency. A custom shader without one would
fall to a shader URP does not have. So `TerrainGenerator` sets `basemapDistance` to 20000 (never)
and `drawInstanced` off (the instanced path samples the heightmap in the vertex shader, which this
one does not). Full-shader terrain to the horizon costs five fetches a pixel, less than Terrain/Lit.

**Wiring.** `TerrainGenerator.EnsureTerrainMaterial` switches the material to the shader and calls
`StyleLook.WearTerrain` for the shared ramp, softness and tint. If the shader did not import, it
falls back to URP Terrain/Lit with the old 400 m basemap and instancing on. The switch only happens
on a regeneration, because the shader needs the terrain settings the generator writes beside it.
`StyleLook.Restyle` refreshes the numbers on terrain materials already switched, so tuning the look
table reaches the ground too. `-lookTest` checks every active terrain for the
shader, instancing off and basemap distance past 10 km.

---

## Animals and the plane in Quaternius's clothes (#79, ART-PLAN T13, T14)

**Sources.** Deer from Quaternius's Animals (poly.pizza `T6Cs7tmMHJ`, flat colours, 2 176
triangles, 26 clips). The boar is Quaternius's Pig from the farm animals (poly.pizza `u35l6uP5vj`,
one `Atlas.png`, 2 808 triangles, 8 clips). Both CC0. The Animal cap went to 3 000. No CC0
propeller plane was found, so the plane row is out: `PlaneBuilder.Dress` returns early without
it, and the plane stays the greybox.

**Run.** ArtExtract 63 models, 0 missing. ArtLibrary 64 of 64. The pig's slot is
`AtlasMaterial` and its file `Atlas.png`, so `CharacterArt.BaseColour` now also matches a file
whose name sits inside the slot's, but only when nothing matches the usual way round. Build clean.
`-animalTest` 93/0 (solo, animals on). `-planeTest` 33/0 and `-flightTest` 29/0 on `island2`.
`-lookTest` 11/0 on both islands.

`-animalTest`'s hunt used to swing where it asked the boar to spawn. `ServerSpawn` snaps to the
navmesh up to 25 m away, and since the players wake in the camp (#168) that point can land out of a
hatchet's reach. The player now stands 1.6 m from wherever the boar actually landed.

**Animals.** There is still one animal prefab. `AnimalArt.Dress` hangs every species' model in it,
each fitted in shape into that species' body box (`AnimalDef.BodySize`, feet on the ground, turned
a quarter if its long side runs across), and
`Animal.ApplyShape` shows the one its species index names and turns the boxes' renderers off. A
species with no catalogue row (the gull) keeps its boxes. The match is by id: the Animal row whose
id is the species id with a capital.

Each model gets its own controller in `Art/ThirdParty/_Animals/`, built from its own FBX's clips:
a 1D blend on `Speed` (idle at 0, walk at 2, run at 7 m/s; clips named exactly idle, walk,
gallop/run/fly, else the shortest name holding the word) and a held `Dead`. `Animal.LateUpdate` measures the speed from the transform, as
`NpcSkin` does, so clients animate without an agent. `ArtLibrary` imports Animal rows with a Generic
rig and every clip looping except a death; everything else stays unrigged.

`-animalTest` checks that every live animal draws exactly one look, and that some animal wears a
model when the prefab has any.

**Plane.** `PlaneBuilder.Dress` fits the catalogue's plane, in shape, over the box around the whole
airframe (fuselage, cockpit, wings, tail and the three holes). The holes must still read, so any
mesh named like a part (prop, engine/motor, a wing right of centre) is moved under its `Fitted.*`
box, under an unscaled holder so a tilted blade does not shear, and `PlaneAssembly` hides and shows
it with the box. A hole nothing moved into keeps its grey box; a model with no such mesh at all is
dropped and the greybox kept, because a whole plane drawn over its own holes reads as finished. The wheels and every collider are the greybox's. `PlaneTurns` turns the model if it is found
facing backwards. The loose parts lying on the island (`PlanePartBuilder`) are still boxes.


## Shared jackpot and bonus buy (#174)

**One pot for the floor.** `SlotMachine` keeps the jackpot in server statics: `_pot` in hundredths
of a chip (so a 10-chip stake still adds to it) and `_owed`, the pots already dropped whose winners'
screens are still playing. Every stake at any cabinet adds `SlotMath.JackpotPct` (1%) to it. Each
cabinet has a `_jackpot` SyncVar that `Publish()` writes on every served cabinet at once, so all of
them show the same number, `pot + owed`. Because a dropped pot stays in `owed` until `Settle`, the
ticker does not fall before the reels stop, and it can't give a spin away. Statics outlive a
session, so the first cabinet to start on a fresh server resets them. A cabinet despawned mid-spin
takes its own drop (`_owing`) off the ticker in `OnStopServer`.

**The hit is the seed's.** `SlotMath.JackpotHit(seed, stake)` runs its own `SlotRng`, salted, and
returns true with odds `stake / JackpotOdds` (1 in 1,500 at 100; a Volcano buy at 100 is 13,700,
about 1 in 11). The reel sequence is untouched, so
every golden stays valid. The server sends the amount along with the seed in `RpcPlay`, and a client
never computes it. The chance is proportional to the stake, so the pot is worth the same fraction of
every stake: the 1% contribution plus the 500-chip reseed (500 / 150,000 of what is staked) adds
about 1.3% to the return, on top of each game's base RTP.

**The show** reuses `BigWin`. `JackpotTier` (4) sits above epic, reads JACKPOT, counts up for 7 s
and throws the whole coin pool. `WinBanner` gets a fifth colour. `SlotBoard` shows a third line,
`JACKPOT n`, that rolls up to the replicated value and snaps down when a pot drops.

**Bonus buy.** `SlotMath.Spin(kind, seed, bet, buy)` rolls the first grid as usual, then `Force`
turns random cells into volcanoes (4) or chests (3) until the feature triggers. The extra draws
happen only when `buy` is set, so an ordinary seed plays exactly as before. Prices come from
simulating 300k buys under .NET: Volcano 137 bets (96.4% on the harness seeds; 100 bets since #255), Reef 119 (95.3%).
Big-win tiers still count against the base bet, as real cabinets do. `_lastBuy` replicates so a
late joiner replays the right spin. The button (`SlotAction.Buy`) is appended to the enum so
serialised values keep their meaning. It uses the card button's red, so it adds no material to
the scene.

**Harness** (`-slotTest`): the drop rate at 100 and at 10; buy goldens for 20k seeds plus
"every buy triggers"; the buy button's price and payout in the world; the pot growing by exactly
the stake share; a forced drop (`SlotMachine.ForceJackpot`) that pays the pot, resets it, holds
the ticker until settle and throws a tier-4 banner; every cabinet showing the same pot; and the
client seeing it. Volcano and Reef now have three buttons.

---

## The look, from a screenshot: shade, canopies, the red bush, the lost sky

The first playtest on the stylized look found four things. `-shots <folder>` (`ShotTest`) is how they
were judged from a terminal. It runs in a real window (no `-nographics`) and takes four eye-level
views round the spawn and one from above, on a camera copied from the main one. Then it quits.

- **Shade was a hole.** The shade side was the trilight ambient alone, near black under a canopy. It
  is now never less than 42% of the sun's colour (`StylizedLighting.hlsl`). That is scaled by the
  sun, so the night stays dark.
- **Canopies were black speckle.** Every leaf card banded on its own normal, and half of them fell
  into shade. Alpha-clipped materials now bend the normal 60% toward up, so a crown is lit as one
  mass.
- **The bushes were red.** The kit's `Bush_Common` wears the twisted tree's autumn leaves
  (`Leaves_TwistedTree_C.png`). `StyleLook` puts the white leaf mask of the same cards on that one
  material and tints it green. The pirate atlas's lime palms lost some saturation (0.8) and
  brightness (0.9).
- **The camera walked into trees.** The broadleaf trunks flare to 0.9-1.0 m at the root, measured
  in Blender at their placed height. The capsules were 0.36-0.45 m; they are now 0.75-0.85 m.
- **The sky was lost** whenever the clock was frozen. `RenderSettings` belong to the active scene, and
  the network scene loads switch it after `DayNightCycle` has written the sky. A moving clock
  rewrites it within a second, but a frozen one never did. A lost sky now forces a re-apply.

lookTest 12/0 on both islands, nativeTest 162/0, animalTest 94/0.

---

## The second playtest: the car camera, puddles, the pig's sphere, the boar

**Chase camera while seated.** `PlayerCameraRig` used the walking eye (body + 1.55 m) in every
vehicle, which from a kart seat is a view of the bonnet or the sky. Seated, the eye is now behind
the vehicle along the mouse look: pivot at `1 + 0.35 r` above the vehicle, `2.5 + 1.6 r` back,
where `r` is the vehicle's renderer bounds radius (clamped 1.5..12, measured once per vehicle, the
riders excluded), so the kart, the boat and the plane each get a distance to their size. A sphere
cast pulls the camera in front of terrain and walls. No follow lag and no head bob: the vehicle
is already smooth. `CharacterSkin` draws your own body while seated, since it is now in view.

**Puddles.** The sea is one plane at 0 under the whole island, so every inland dip below 0 showed
it. `TerrainGenerator.FillPuddles` flood-fills the sub-sea samples from the map edge and raises
every one the fill never reached to +0.3 m. Heightmap only; `IslandShape` still calls those spots
water, so nothing is planted in them. Island 1: 556 samples raised. Island 2: 0.

**The white blob under the animals.** The poly.pizza glTFs of the Pig and the Deer carry a hidden
2 m `Icosphere`, which the FBX conversion exported as a visible, untextured mesh (the stray
`NoName` material). Both were re-converted with every mesh that has no armature modifier dropped,
and the zips in the art folder rebuilt. It also inflated the pig's bounds: native 1.90x2.61x2.07
before, 0.84x1.60x2.03 now, so the pig fills its body box instead of being shrunk to fit a sphere.

**The boar.** Every stun ragdolls the victim, so a boar with 0.9 s of stun and 950 of knockback
floored you and threw you on every tackle. Now 10 damage, no stun, no knockback: a bite that hurts
without knocking you over, ten of them to go down.

**The toy palette.** Kenney's colour maps are saturated swatches, and its orange logs, stumps and
crates were most of what read as "Roblox" next to the Quaternius nature. `StyleLook.Kits` now
pulls `Kenney_*` to 0.62 saturation and 0.85 brightness, and the `Flat_*` swatches to 0.75 and 0.9.
The logs and crates come out wood-brown. `-lookTest` 12/0.

---

## Guns you can have, a wreck with something at it, a first objective that is true

The third playtest: "implement the guns so we can shoot", and "I go to the shipwreck and there is
nothing". Both were true, and neither was a missing system.

**The guns existed and could not be had.** #51 built the whole arsenal - hitscan, pellets, recoil,
magazines, R to reload, server-owned damage, `-gunTest` - and then nothing sold it and nothing
dropped it. The trader (`ShopFactory.Stock`) now sells the pistol (180, two on the shelf), shotgun
(300), SMG (390), rifle (480) and all three kinds of rounds, unlimited. The pistol fits in the $500
a run starts with. The shelf is written once, so the change needs `ShopFactory.Build -rebuildShop`.

**The wreck had a hull and nothing else.** `POISpawner.Loot` is a table of what lies on the ground
at a POI when the island starts: at the wreck a pistol, 36 rounds, rope, cloth, planks, bandages
and bottles; at the cave shells, a torch and flint. Each stack is dropped on a ring 9-12 m out, raycast onto the
ground. The scatter waits for `ItemCatalog.Active`, which the first inventory to wake publishes and
which arrives after the POIs do.

**The first objective sent you to somebody you had never left.** A run starts on the first island,
and the `Castaway` there wrote *Find the one you left behind* from the first frame. So you walked to
the wreck, found a stranger in their underwear, and pressed E to be told to take them to a plane
you did not have. Until `PlaneAssembly.Owned`, a waiting castaway now writes the boat instead:
*Fix the boat: 1/4 parts (the Trader sells them)*, then *Sail the boat to the other island*, and
they cannot be led anywhere; their prompt says so. It re-reads every second while waiting, so the
part count and the plane being finished both show up without a hook in either. `RescueTest` checks
the boat line first, then fits the plane as the earlier harnesses already did.

**`-armsTest`** (`World.ArmsTest`, solo, island): the pistol and its rounds lie within 20 m of the
wreck and near its height, and the island's trader - not the casino bar, which is a `ShopCounter`
too - sells every gun and every round.

**`-gunTest` aims at the chest now.** It fired flat from the eye at the victim's feet, which is
level ground's assumption. On the regenerated island 1 the lane runs downhill and at 20 m a flat
ray passes over the victim's head: the pistols still scattered into it sometimes, and the rifle's
0.2 degrees never did (0 of 25, "a ray from the eye sees Island at 57 m"). The test was wrong,
not the gun. Run as a pair at 100 ms, 113/0. When a gun lands nothing, the log now names
what the ray actually met. `-weaponTest` had the same flat `Toward` and the same 30 m miss
("line hits nothing at all"); it aims eye to chest too now, 28/0 as a pair. The pair battery after
this section's changes: demoTest 7/0, achievementTest 20/0, partTest 38/0, voyageTest 29/0.

**`-shotsAt a,b`** extends `-shots`: each name is a landmark id or an object name, shot from 10 m.
It is how the castaway was found to be standing on the beach all along.

---

## Playtest cheats: F5 money, F7 the plane

The economy puts the boat two to three evenings away for four players (`EconomyTest` holds it
there: 4 parts at 1400, a boar worth 37), which is the game and is not a playtest of the ending.
`World.DevCheats`, host only, in development builds or with `-cheats`: **F5** adds $1000 to every
wallet, **F7** fits every part of the aeroplane on this island (`PlaneAssembly.ServerFitAll`, so
`Owned` follows). A release build is not a development build and has neither. `-cheatTest` runs
both once: 4/0.

---

## The playthrough bot, and what it found on its first walk

`-playthrough <folder>` (`World/Playthrough.cs`) plays the first island the way a person does, in a
real window, and screenshots every step with the HUD on. It drives the player through
`PlayerInputReader.BotDriven` - `BotMove`, `BotLook`, `BotPress("interact"|"attack"|"reload"|"jump"|"inventory")`,
`BotHotbar` - so every action goes through the same motor, interactor, hotbar, weapon and vehicle
code a keyboard does. The only shortcuts are F5's money and a logged teleport after sixteen
seconds without progress. Route: spawn, the wreck, pick up the pistol and rounds with E, load,
hunt a boar, the trader, buy boat parts in trips (four do not fit one back), fit them, board,
sail straight away from the island's centre, arrive on Island2. Every step is a PASS/FAIL line;
a FAIL never ends the run. It shoots back at any non-skittish animal that targets it on the way,
and waits out being down. Use a fresh `-playerKey` per run, or the save hands the bot last run's bag.

```
EWYF.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile pt.log -host -port 8516
  -playerKey bot:ptN -scene island -noNatives -timeOfDay 0.45 -playthrough D:\Builds\shots\ptN
```

About six minutes; 16 passed, 0 failed. What the first runs found, each fixed here:

- **The shop drew sixteen rows and the trader has twenty-four.** Every gun and the boat part - the
  thing the whole island is saving for - were drawn below the panel. The shelf is now two columns
  of twelve and the panel grows rightwards (`InventoryScreen.ShopRows`); `-armsTest` checks the
  trader's offer count against it.
- **Nobody gets up when everybody is dead.** Death is meant to be fixed by friends hauling you to
  the Revive Machine; alone, or in a full wipe, the run stopped for ever. `World/WipeGuard.cs`:
  eight seconds after every player is dead, everybody stands up at their spawn on half health with
  their bag. `-wipeTest` (solo) kills the host and checks it: 5/0.
- **#178's boar never reached its asset.** `AnimalFactory` never overwrites a tuned asset, so the
  seed change (damage 10, no stun, no knockback) sat in C# while the boar still hit for 18 and
  threw you 950 N. `AnimalFactory.Build -reseed boar` overwrites the named rows; rerun that way.
- **Greybox drops were magenta.** A primitive's built-in material has no shader in a URP player;
  they now wear `EWYF/Stylized`, which every art material uses and so always ships.
- `[Health] X downed by <type> <amount> from <attacker>` - one line per knockdown, so a playtest
  log says what put somebody on the ground.

---

## The playthrough bot's second half: Island2, the flight back, the ending

`-playthrough` now plays the whole run. `Run()` picks up wherever the world is: the first island
with no aeroplane yet, Island2, or the first island with the aeroplane already built (the rescue).
On Island2 it lifts each loose plane part, carries it to the airframe and fits it with E, boards,
flies (throttle held, pitch 0.5 on the ground once at 20 m/s, a gentle climb to fifty metres over
the strip, then hands off - the plane levels its own wings), and on the first island walks to the
Castaway, presses E, leads them to the plane, flies them out and checks `RunSummary.Over`.

```
EWYF.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile q.log -host -port 8547
  -playerKey bot:qN -scene island2 -noNatives -noAnimals -timeOfDay 0.45 -playthrough D:\Builds\shots\qN
```

A windowed run saves (`-noSave` for a fresh world), so a second run starts where the first ended.
Last runs: 14 passed, 0 failed from Island2 to the ending; 9 passed, 0 failed starting on Island2
with a save on Island (the save sails there, then the rescue). What it found, each fixed here:

- **The carried wing covered the whole screen** (a playtest, fullscreen). The carry socket is at the
  face; the carrier's own renderers go `ShadowsOnly` (`PlanePart.OnHandsChanged`), everybody else
  still sees it on the shoulder.
- **E at the plane sat you in the pilot's seat instead of fitting the part in your arms.**
  `Vehicle.ServerCanInteract` refuses anybody holding a plane part.
- **E put the carried part down instead of reaching what was in front of it.** `PlayerInteractor`
  now sphere-casts all hits nearest-first, skips your own held part (keeping it as the fallback, so
  E with nothing else in view still puts it down) and your own capsule, and stops at the first solid
  thing so it never reaches through walls.
- **A loaded save showed spare parts.** The save restored what was fitted, but the scene still
  spawned all three; hauling a second engine did nothing. `PlaneAssembly` despawns a loose part
  with no hole left for it.
- **A plane that went into the sea sank for ever.** `PlaneVoyage.Ditch()`: four metres under sea
  level, everybody is put out and the plane goes back where it was parked.
- **After a flight the view froze over the old island** while the body walked on. The camera and
  its target were made in the scene that unloaded, and the new scene's camera has no Cinemachine
  brain. `PlayerCameraRig.LateUpdate` rebuilds both and ensures the brain every frame.
- **The first island's aeroplane faced a treeline 45 m away** and could not get off the ground.
  `IslandShape.InsidePad` now also clears trees from a 120 m by 24 m runway ahead of any `plane`
  entry (the ground is untouched), and on the first island the plane faces away from camp, toward
  the coast. Regenerated with `TerrainGenerator.GenerateIsland -rebuildPois` (the only catalog change
  is the plane's yaw, 90 to 270). Island2 is not regenerated; its runway already worked.
- **The Castaway sat back down on the way to the plane.** They walked at 3.4 m/s with a 60 m leash,
  and anybody sprinting (7.5 m/s) lost them in fifteen seconds. They now run at the leader's sprint
  speed when more than three follow-distances behind.
- **A save that sails on at start-up arrived nowhere.** Starting on Island2 with a save on Island,
  the first island's own load finished after the trip began and `GameSceneLoader.OnTravelled` took
  it: `Current` said Island2 with Island on screen, and nobody was put ashore. It now waits for the
  load of the scene it asked for, and `RunSave` does not sail until the first island has finished
  loading (leaving mid-load kept both islands loaded and spawned nobody).
- `PlaneController.FlightReport()` includes power, brake and pitch, so a plane that will not go says
  why.

---

## The boar, retuned after a fresh-world bot run

A fresh-world run of the playthrough bot (`-scene island -noSave`, natives and animals on) went down
five times on the first walk from spawn to the wreck, unarmed: `boar.camp` puts five boars within
130 m of the camp, and each one sensed you at 30 m and charged at 7.2 m/s against a 7.5 m/s sprint.
The boar is described as having bad eyesight; now it has. `AnimalFactory` seed: sense 30 -> 18 m,
react 16 -> 14 m (`-animalTest` spawns one 12 m ahead and needs the charge), run 7.2 -> 6.2 m/s.
Reseeded with `AnimalFactory.Build -reseed boar`. The next fresh run reached the wreck, the pistol,
the trader and the boat at full health; `-animalTest` 93/0.

The bot also learned two things a player does: it shoots natives that target it (not only animals),
only when it has rounds and is not carrying a plane part, and it buys a box of 36 pistol rounds at
the trader on the first trip.

Open, for a playtest to judge: on Island2 the bot still goes down near the plane. It runs out of
rounds against charging boars (three kills in a magazine-and-a-half), and headhunters hit for 34.
The bot aims badly at a charging animal, so this may be the bot and not the island.

A follow-up run on a fresh Island2, the bot armed with a pistol and 72 rounds, logged every fight
(`[Playthrough] fight with X: n shot(s) from d m, hp before -> after, loaded, spare`). The guns
are fine: a boar takes four pistol rounds, a headhunter (140 hp) about six at ~26 each. What downs
the bot is that headhunters hit for 34 and come in pairs, and that a part on your shoulder means no
gun. That is balance for a playtest to judge (four players, the trader's shotgun and rifle), not a bug.

**Shift-click in the shop buys a stack** (up to 36) instead of five. Rounds are sold one at a time
at $3, and a box of pistol rounds was eight shift-clicks. `-shopTest` (pair) 65/0.

---

## The wreck comes first

A fresh spawn read "Fix the boat: 0/4 parts" instead of "Search the wreck on the beach". Both lines
are written every frame by somebody - `IslandIntro` for the wreck, `Castaway.Say` for the chain
from the boat onward - and the castaway, standing next to the player, won.

`IslandIntro.Visited` is a static that goes true the first time any player body comes within 14 m
of the wreck landmark. `Castaway.Say` leaves the objective alone while nobody has been there and
the boat has nothing fitted. A static, like `PlaneAssembly.Owned`, because the wreck lives on the
first island only and the flag only ever has to answer "has this run started yet".

`RescueTest` starts past the wreck, so it sets `Visited` itself. The playthrough bot now checks the
line names the wreck at spawn and the boat after the wreck.

Three more from the same fresh-world runs:

- **The engine fell through Island2.** `POIFactory` put it six metres from the wreck along the
  wreck's own facing, which is the long axis of a fourteen-metre hull: the engine spawned inside it
  and physics pushed it out downwards. The bot followed it five million metres down. It now sits six
  metres to the side (`-island 2 -rebuildPois`; only `part.engine` moved in `POIs2.asset`). It still
  fell after the moved spawn, but only after a boat crossing and never on a direct load, so a part
  now stays kinematic where the island put it until somebody first lifts it (`ServerPutDown` already
  makes a dropped one dynamic), and a part below -30 m goes back to where it started.
- **E did nothing at the plane.** The interactor's cast stops at the first thing with nothing to
  offer, and with a pistol out that was the player's own arm, 0.3 m in front of the camera at the
  wrong pitch. Hits on your own NetworkObject are skipped.
- **ShopTest took the first counter it found**, and on some runs that was the casino's barman, who
  sells grog. It now takes one that stocks rope.

---

## Island2 with natives on: the headhunter, softened

The first bot run from a fresh Island with natives on reached Island2 and died at the cave and the
village on a loop. Numbers from the log: a headhunter was 140 hp (six pistol rounds at ~26), hit
for 34 (three hits to down a 100 hp player), came in pairs, and ran at 7.4 m/s against a 7.5 m/s
sprint. Island2 has no shop, and headhunters dropped no pistol rounds.

- `NativeFactory` headhunter: 130 hp (five rounds), 31 damage (four hits), 7.0 m/s, so running is
  an answer again. Still over the spearman by `Island2Test`'s 1.5x health and 1.4x damage.
  `NativeFactory.Build -reseed headhunter` overwrites an existing asset, AnimalFactory's rule;
  without it a seed change never reaches the asset.
- Headhunters drop 6-10 pistol rounds (90%), on both tables.
- Native loot lands 1.4 m out instead of 0.55 m, clear of the body.
- `Island2Test` asked for exactly five POIs; the plane, its parts and the mooring since made it ten.

The bot now reloads after every fight rather than at the start of the next, and picks up pistol
rounds after killing a native and at Island2's wreck. With natives on it still does not clear the
village solo: it cannot sneak, hits about half its shots, and never eats, so an hour of deaths ends
in starvation ("downed by Environment"). That is the bot, not the island; four players who bought
a shotgun on the first island are who the village is for. Runs with `-noNatives` go end to end.

Three more, from the same runs:

- **Guns missed at close range.** `Weapon.ResolveHitscan` took the first `Raycast` hit and threw the
  round away if it was the shooter's own body, which is what it was when aiming down at something
  close: the bot put twenty rounds into a boar at its feet for no damage. It now walks
  `RaycastAll` nearest first and skips the shooter.
- **E did nothing pressed against the plane.** `PlayerInteractor` dropped every hit that overlapped
  the cast's start, meant for your own capsule, which also dropped a fuselage 0.4 m away. Your own
  body is now skipped by owner instead, so the overlap rule is gone.
- **The Island2 take-off ran into the base camp.** The plane faced the camp and rolled into the
  shelter posts sixty metres on. Tail to the camp now, as on the first island (`-island 2
  -rebuildPois`; only the plane's yaw moved, -39 to 141).
- **The engine took two tries to lift.** The wreck's mast stood between the bot's eye and the
  engine on the camp side. It sits on the far side of the hull now (`Facing(wreck, camp) - 90`),
  and the first E lifts it in both bot runs.

---

## Playtest fixes: the chequered sea, late abductions, friendly fire

- **The sea was a checkerboard with a see-through horizon.** `WaterDepth*.png` imported as a
  single-channel texture with the default *Alpha* component, so it became Alpha8 and sampled as
  `(0,0,0,a)`. The shader reads `.r`, so every pixel of ocean was zero-depth shallows: the surf band
  covered the whole sea, its two crossing sines drew the checkerboard, and the shallow alpha let the
  dark lower skybox through the horizon ring. `WaterFactory` now asks for the *Red* component (R8).
  Regenerated with `GenerateIsland -island 1` and `-island 2`.
- **Nobody got dragged.** A downed body was offered to the natives once, at the instant it fell.
  Downed by a dart from range, a fall or hunger, with no hauler inside its radius, it was never
  offered again. `Native.ClaimLate` runs on the sense tick: a hauler that comes within
  `AbductRadius` of a downed, unheld body takes it. Bodies within 10 m of that native's own camp are
  skipped so a delivery is not re-claimed, and a native that just let go waits 30 s.
  `-abductTest` checks a spearman placed after the fall.
- **Friendly fire already works.** Neither `Weapon` nor `Health` checks which side the victim is on.
  `-gunTest` shoots the other process's player body, so there is nothing to add.

## #197, #210: story beats, the seat view, and a plane that keeps to its strip

**Story beats** (`World/StoryBeat.cs`). A few seconds of letterboxed camera at the moments that
deserve one: arriving on each island (`IslandIntro`, on the wreck or the strip), the plane becoming
whole (`PlaneAssembly`), finding the castaway (`Castaway`). `StoryBeat.Play(id, title, line, focus)`
plays once per id per process and refuses when the player's camera is more than 60 m away from the
focus, so a teammate finishing the plane across the island does not yank your view. It adds a
CinemachineCamera at priority 30 (rig 10, death camera 20) and the brain blends in and out. The
camera orbits 35° at 11 m, picking a start angle with a clear line of sight (a sphere cast that
ignores the focus itself and players), clamped above the terrain. Any key or left click skips.
Headless it records the id and draws nothing. The playthrough bot photographs each one
(`beat_<id>.png`) and checks the plane and castaway beats played.

FishNet calls a SyncVar's `OnChange` on the host only as the server, so the castaway beat does
not filter on `asServer`. Once per id is what keeps it single.

**Seat view** (`Player/PlayerCameraRig.cs`). Driving the buggy or the boat puts the camera at the
seat's anchor plus 0.8 m, looking where the vehicle looks. The mouse turns the head ±120° from the
nose. **V** toggles the chase camera. The plane defaults to chase (V swaps it to the seat) until
it has a cockpit model (#203): from its seat the wing's underside filled the screen.

**Plane ground steering** (`Vehicles/PlaneController.cs`). On the wheels, the roll axis steers
(`GroundSteer`) and yaw is damped (`GroundYawDamping`), so a take-off roll holds its line and can be
corrected. The parts hauled in can knock the parked plane twenty degrees off its strip. The
playthrough bot now steers back to the strip's POI yaw before it lifts, which is what a pilot does.
On island2 it went into the treeline without that.

## #206, #208: what loot looks like, and its icon

`Editor/ItemArtFactory.cs`, run in batchmode **without** `-nographics` (the icons are rendered):

    Unity.exe -batchmode -quit -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.ItemArtFactory.Build

A boar used to drop two yellow cubes: 29 of the 44 items had no `WorldPrefab`, and `WorldItem` falls
back to a greybox. Now:

- **Kit items** (meat, fish, coconut, bottles, planks, flint, scrap, cloth, kits) wear a Kenney model
  (Food Kit, new; Survival Kit, more pieces), fitted with `ArtDress.FitBox` to a size in metres.
  The glbs come from the CC0 mirror and are converted with `tools/art/glb2fbx.py` in Blender.
- **Everything else** (ammo boxes, shells, pearl, rope, feather, bandage, torch, boot, hide, rod,
  jerrycan, tyre) is a few colliderless primitives in `Palette` colours. Cheap, but they read.
- Prefabs land in `Prefabs/Items/<id>.prefab`. A world prefab somebody else chose (the guns, from
  `WeaponFactory`) is never replaced; one this factory made is rebuilt every run.
- Then **every** item with a world prefab is photographed (orthographic, three-quarter view,
  transparent, 128 px) into `Art/Icons/<id>.png`, imported as a Sprite and assigned to `_icon`.
  The pistol and its rounds finally have icons. A new item gets one by existing.

`WorldItem.Fit` now also shrinks the pickup box's **height** to the model (the footprint stays
generous): a steak in the 56 cm cube hovered 20 cm over the sand.

Harness: `-itemTest`, `-lootTest`, `-weaponTest` (unchanged; they cover the prefabs spawning and
the boxes); the look was checked on the icon sheet and the bot's screenshots.

## Water: no foam polygons on flat sand at sea level

The playtest showed pale-white polygons on the beach and under the wreck. The wreck's pad and the
flattest tideline sit within centimetres of the sea plane, so the plane cut through the terrain's
triangles there. The baked depth mask reads ~0 at those spots, which the shader drew as full
shallow-alpha foam. `Water.shader` now multiplies alpha by `smoothstep(0, _EdgeFade / _ShoreDepth,
mask)` (`_EdgeFade` = 0.35 m). The last few centimetres are a transparent film, and the surf line
sits just offshore. No terrain regeneration was needed. Checked on the bot's wreck and sailing
screenshots.

## #207: shots that read as hits

The playtest said hit registration looked wrong. The hits were right; nothing showed them:

- **The tracer had no material.** `PlayerPrefabBuilder` made it in memory and handed it to the
  prefab, which keeps no reference to a non-asset object, so `_material` was `{fileID: 0}` in
  `Player.prefab` and every tracer was drawn with nothing. It is now `Art/Stylized/Tracer.mat`:
  URP Particles/Unlit, additive, so the vertex-colour fade along the line works.
- **The tracer started at the eye.** That is where the server's ray leaves, but a line from the eye
  is a dot for the shooter and a line out of everybody else's forehead. It is drawn from the muzzle
  now. For the shooter it is the tip of the gun on their screen (`CharacterSkin.ViewMuzzle`, below).
  For everybody else it is `CharacterSkin.Muzzle`, the front face of the gun in the hand. **The end
  is still the server's hit point**. `-gunTest` now checks that every hit is exactly the end of a
  drawn tracer.
- **Muzzle flash**: a small additive star (a core along the barrel, two thin blades across it),
  parented to that same tip for 50 ms, so it points wherever the gun points. The first version had a
  point light, and it drew a bright disc on the ground every shot, so the light is gone.
- **The item in your own hand.** The body's copy of the held model is shadow-only for its owner,
  because the camera is inside the head. So `CharacterSkin` builds a second copy that hangs off the
  camera, low and right. A gun lies along the view, with its back at least 28 cm out (the near plane
  is 15 cm) and at most 55 cm long. A blade stands up and forward. Anything else is turned three
  quarters and shrunk to 22 cm. It kicks back and up on each attack, bobs with walking, and hides
  while carrying, seated, ragdolled, dead or in a story beat. Any selected item is now drawn, in the
  hand for everyone else and in front of the camera for you: a non-weapon wears its ground model,
  shrunk to 30 cm and held by its middle. `-skinTest` checks the view copy and its muzzle, and a
  plain item held hand-sized.
- **Impacts**: `Weapon.ApplyHit`'s observers RPC arrives before the shot's, so the ends that drew
  blood are known when the ends come in. Those throw dark red bits. Every other end that stopped
  short of the gun's range throws sand-coloured dust. Bits are pooled cubes (at most 96),
  simulated by hand with gravity for 0.55 s. There are no rigidbodies.
- **Hitmarker**: when your own shot lands, four strokes open round the crosshair for 180 ms, with a
  high click in your ears.

All of it lives in `Combat/TracerEffect.cs` and skips headless. The victim's flinch was already
there (`StunState` shove, camera shake).

## #214: the "flying" boar

The bot photographed a boar several metres up in the air after a hit. It was not knockback:
animals have no rigidbody. The boar camp sits six metres from the wreck, the NavMesh is baked over
the wreck's deck, and `AnimalSpawner.Ground` had put the boar on that deck at y 2.77. Seen from
the sand below, it hung in the sky. Spawn candidates more than a metre above
`Terrain.SampleHeight` are now rejected (`OnTheGround`), so animals start on the island itself.
`-animalTest` checks every spawned animal against the terrain.

## #199: walking into trees

Terrain trees collide through the capsule on their prototype prefab, and the terrain puts that
capsule on the tree's spot. `ArtLibrary.AddCollider` centred it on the kit's pivot, not on the
trunk. The Quaternius palms are modelled leaning, so their bark stood 0.3 to 0.45 m off the pivot.
The big boulder was 0.8 m off. A body stopped by the capsule could stand inside the bark on one
side, and on the other side it hit nothing it could see.

`EnsureFloraPrefab` now measures the trunk: the xz box of the model's vertices in 25 cm slices from
the ground up to 1.75 m. It stops at the first slice more than twice as wide as the lowest (and over
1.2 m), because that is a pine's branches, which you walk under. It then moves the model so the
trunk is on the spot. It moves the model rather than the capsule, so the two stay together however
the terrain turns the tree. A tree's capsule radius is now the measured trunk (0.85 of the half
width) instead of the catalogue's guess, so a palm is 0.41 to 0.62 m and a pine 0.24 m. Rocks keep
their old radius and are only centred. The measured centre, after the move, is stored on
`ArtVisual.Trunk`, which is NaN on a prefab that was never measured.

`FloraFactory.Bake` rebuilds the prototypes in place. The prefab GUIDs do not change, so the islands
pick them up without a regeneration.

`-treeTest` (`-scene island` or `island2`) checks that every prototype's trunk is within 5 cm of its
collider. It then walks the player into one isolated tree of each kind, from 2.6 m out on gentle
ground, and checks three things for each tree: the player never reaches the tree's spot, stops
within a hand of contact, and can back out a metre. Results: 56/0 on island, 47/0 on island2.

## #200: where the buggy stops

`-carTest` ends with a tour: the buggy drives nearest-next through every POI on land, steering like a
driver would (a sphere cast at bumper height, 7 m ahead; blocked means turn to the freer of ±35°).
Speed under 0.5 m/s for two seconds with throttle held is a stall. It backs off at opposite lock and
tries again, twice per leg, 45 s per leg.

Each stall is named from the contact that held it. `CarController` keeps the hardest contact since
the last `WheelReport` (`LastHitPoint`, `LastHitCollider`), and the report clears it:

- a non-terrain collider is a **prop**;
- a terrain contact within 0.4 m of a tree instance's capsule is that **trunk** (tree colliders live
  inside the `TerrainCollider`, so the instance list is the only way to tell them apart);
- anything else is **the ground**, which is the bug the issue was about: seams, a crest under the
  belly, invisible colliders.

First full run: 51 stalls, none on the ground. They were every one against a trunk, a stump, a
small boulder or a camp prop, clustered in the woods round base camp. A stump is 0.72 m tall and the
wheel radius is 0.45 m, so those are real obstacles. The "random" stops from the playtest were the
tree capsules standing off their trunks, which #199 fixed.

One false positive came first: the pad tests left a 72 kN contact on record and the tour's first
stall was blamed on it. Reading the report once before the tour clears it.

The check is `no stall with nothing to stop it`. The tour's distance check only asks that the car
got somewhere (over 50 m, one stop reached): the bot is a poor driver in woods, and a check that
measured the bot would say nothing about the car.

## #201: a sharp image

The Low quality tier rendered at 0.8 scale and stretched the result back up bilinearly, so on the
machines that pick Low the whole image was soft. The bot windows made it look worse still: they are
1280x720 and Windows scales them again, which is not what a player sees, so blur in a bot screenshot
is not evidence about the shipped game.

Every tier now renders at scale 1.0. Low pays for it in shadows instead: the map drops from 1024 to
512 and the distance from 45 m to 35 m, still one cascade. `RenderTuning.Apply` is where the numbers
live and `ProjectSetup` seeds the same scale for a fresh checkout. Re-run `RenderTuning.Apply` in
batchmode to rewrite the URP assets; nothing is edited by hand. There is no render-scale slider: the
Settings screen only offers the quality tier, and a second knob for the same lever was not worth it
while every tier is at 1.0.

`-qualityTest` (any scene, host) walks every quality level, reads its URP asset and checks that its
render scale is at least 1, or that it upscales with FSR when it is below 1. It cannot judge how the
image looks; that is a playtest question.

## #203: a plane and a boat that look like them

**The boat** was already dressed on main: `BoatBuilder` fits Kenney's Watercraft speedboat
(`Art/ThirdParty/Kenney/Watercraft/boat-speed-j.fbx`, CC0, catalogue id `Boat`) into the hull's
footprint with `ArtDress.FitBox`, strips the hull, bow and console boxes and keeps the hull collider.
Nothing changed there except a harness check that the prefab really came out dressed.

**The plane** has no model in the project. The closed PR #157 (`claude/project-thread-7e12e5`) dressed
it with a Quaternius plane that was never downloaded, and that dressing only worked when the model
had separate propeller, engine and wing meshes. Revived here instead: `PlaneBuilder.Dress` builds a
composite of built-in primitives in the `Palette` (capsule fuselage, canopy, port wing, swept fin,
tail plane) under an `Art` child carrying an `ArtVisual` (`Plane`, `Vehicle`, cap 4000 triangles, roughly
1 900 used). The fuselage and port-wing boxes lose their renderers and keep their colliders; the
wheels stay and go `Dark`.

**Parts.** `PlaneAssembly` hides and shows the whole `Fitted.*` child, so those stay the part visuals
rather than moving into the composite: `Fitted.engine` swaps its cube mesh for a sphere (metal cowling),
`Fitted.propeller` gets an unscaled `Art` holder with a second blade and a hub (crossed blades), and
`Fitted.wing` wears the same red as the wing that is already on. Seats, exits, cargo socket and every
collider are untouched.

`-flightTest` (`-scene island2`) and `-boatTest` (`-scene island`) each check: a model is drawn under
`Art` (over 100 triangles), it is under `ArtVisual.Cap(Vehicle)`, no greybox box is still drawn, and the
fuselage / hull collider is still there.

Regenerate with `PlaneBuilder.Build`; the prefab keeps its GUID. `BoatBuilder.Build` is only needed if
the boat prefab predates the Watercraft dressing.

## #204: every state has a clip

Every state the body can be in now has a clip, and every transition between them blends. All of it is
derived on each peer from state already replicated; nothing new goes over the wire. Swimming reads
`WaterSurface.IsSubmerged` 0.9 m above the feet (the probe `SurvivalStats` uses for warmth), and
reload rides `Weapon.Reloading`, the observers call every peer already gets.

| State | Source of truth | Clip | Blend |
|---|---|---|---|
| Idle / walk / jog / sprint | measured speed | `Idle_Loop`, `Walk_Loop`, `Jog_Fwd_Loop`, `Sprint_Loop` (1D tree on Speed) | in the tree |
| Jump / fall | vertical speed, not swimming | `Jump_Loop` | 0.15 s |
| Crouch | not on the body: `PlayerMotor` crouch slows the player and the tree plays `Walk_Loop` at 2.2 m/s | `Walk_Loop` | in the tree |
| Swim (new) | submerged at the waist | `Swim_Idle_Loop` / `Swim_Fwd_Loop` (tree on Speed) | 0.25 s in and out |
| Carry | `CarrySystem.IsCarrying` | `Driving_Loop` on the arms (layer 1) | layer weight, 0.25 s |
| Seated / driving | `VehicleRider.IsSeated` | `Driving_Loop` | 0.3 s in and out (was 0.15) |
| Entering / leaving a seat | the same bool | blended into and out of `Driving_Loop`; `Sitting_Enter/Exit` are unused, the vehicle places the body | 0.3 s |
| Aim / hold a gun | inventory selection | `Pistol_Idle_Loop` on the arms (layer 2) | layer weight, 0.17 s |
| Fire | `Weapon.Attacked` | `Pistol_Shoot` | 0.05 s in, 0.1 s out |
| Reload (new) | `Weapon.Reloading(started)`, guns only | `Pistol_Reload` on the arms | 0.1 s in, 0.15 s out |
| Melee swing | `Weapon.Attacked`, blade in hand | `Sword_Attack` | 0.05 s in, 0.1 s out |
| Punch | `Weapon.Attacked`, fists | `Punch_Jab` | 0.15 s in, 0.1 s out |
| Downed / limp | `RagdollController.IsRagdolled` | animator off, skin follows the physics bones | none needed: physics is continuous |
| Getting up | ragdoll ends | 0.35 s slerp from the last limp pose into the animator | 0.35 s |
| Dead (native) | `Dead` bool | `Death01` | 0.15 s |

`CharacterArt.BuildController` adds the `Swimming` bool and `Reload` trigger, the Swim state, the
Armed layer's Reload state, and the longer seat blends. It also fails the build, naming the
transition, if any transition on any layer has a zero duration (`Snaps`). Rebuild the controller,
then the prefab so it picks it up:

    -executeMethod EscapeWithYourFriends.EditorTools.CharacterArt.Build -artZips "D:\Downloads\ewyf-art"
    -executeMethod EscapeWithYourFriends.EditorTools.PlayerPrefabBuilder.BuildPlayerPrefab

`-skinTest` (host, solo, `-scene island`) checks that the controller has every parameter and state
above, that `Weapon.Reloading` reaches the Armed layer's Reload state within a second (when the
first weapon with a model is a gun), and that forcing the swim state reaches Swim within 1.5 s and
returns to Move afterwards. The zero-duration rule is enforced at controller build time, because
the runtime animator does not expose transitions.

## island2 objective: the plane's objective

While any plane part lay loose, `PlanePart.PointAtOne` wrote a fixed "Find the X and haul it to the plane", naming whichever loose part came first in `PlanePart.All`. The line never mentioned the plane, so fitting the engine and the wing left it reading "propeller" until the last part was in hand.

The sentence now comes from `PlaneAssembly.Status` ("Fix the plane: 1/3 parts (engine and wing still to find)"), the same shape as the boat's "Fix the boat: 3/4 parts". It is derived from the replicated `_fitted` mask, so every peer reads the same line, and the target still points at a loose part. Harness: `-partTest` (pair, `-scene island2`) checks the count is in the line and that it changes after a part is fitted.

## #207: a native is as wide as it looks

The bot's playthrough took six pistol shots to take 52 hp off a Blowgunner and three for 55 off a
boar. Every hit does the same 26; the difference was hits. `Native.Configure` sized the capsule by the
body's *depth* (`min(x, z) / 2`, 0.175 m), a 0.35 m tube inside a 0.5 m silhouette, so with the
pistol's 1.5° spread two shots in three passed through an arm. It now takes the width (`max`), 0.25 m.
`-nativeTest` checks a spawned native's radius is at least 0.22 m.

---

## #202: the sea to the horizon

**What showed.** From the plane the square edge of the wave patch could be seen against the flat
ring, and past the ring there was void: the ring reached 4000 m from the camera while the far plane
is 5000 m, so at altitude the last kilometre of view was clear colour with a hard line where the
water stopped.

**Why.** The patch already flattens its waves to exactly zero over its last 70 m and the vertex
normal fades with them, so the geometry meets the ring cleanly. What still differed was the
per-pixel ripple normals, which are deliberately not faded at the seam (fading them at the patch
border left the ring mirror-flat against a rippled sea, the older bug). From altitude they also
shimmer into a noisy band near the horizon. And the ring's reach came only from `WaterHorizon` on
the island profile, unrelated to the camera.

**What changed.**
- `Water.shader` gained `_RippleFade` (start, end in metres). The ripple normals are scaled by
  `1 - smoothstep` of the distance from the camera, not from the patch. It starts at twice the patch
  half-extent, well outside the patch, so it is one smooth function of world position with no seam
  of its own, and the far sea calms toward the fresnel horizon colour.
- `WaterFactory.EnsureRingMesh` takes the larger of `WaterHorizon` and `CameraTuning.FarPlane` as
  the ring's outer half-width. The ring follows the camera in xz like the patch, so it reaches the
  far plane in every direction from any altitude and the world's edge is clipped by the camera
  instead of being visible. `EnsureMaterial` pushes `_RippleFade` next to `_PatchFade`.
- The ring mesh is rebuilt when its outer size changes; the material is rewritten every run.
  Regenerate through `TerrainGenerator.GenerateIsland`, which calls `WaterFactory.EnsureWater`.

**How it is tested.** `-waterTest` with `-scene island` (`WaterTest`, registered in
`NetworkBootstrap`): the ring mesh reaches the far plane, the ring follows the camera, the wave
fade and the ripple fade are configured, and the ripple fade starts outside the patch. It prints
`[WaterTest] N passed, M failed.` Headless cannot render, so how the horizon looks from altitude is
still for a human with a screen.

## Camps sized for the party

A camp's `Population` and `NightExtra` are written for four players. `NativeSpawner.Top` now tops a
camp up to `ForParty(camp.Wanted(night), clients)`: 55% of the roster solo, 70% for two, 85% for
three, all of it for four, never under one. `Camp.Wanted` itself is unchanged, so the economy
numbers `LootTest` reads off it still describe the four-player island.

Why: the full bot playthrough, solo, was downed nine times on island 2 and never reached the
propeller; almost every down was a headhunter's 31-damage club. Natives already alive when a player
leaves are not culled; the camp just stops topping up.

`-nativeTest` checks `ForParty(6, 4) == 6`, `ForParty(6, 1) < 6` and `ForParty(1, 1) == 1`.

## #209: weapon skins

A skin is a colour tint for one weapon: id, label, weapon id, `Color`, price. They are rows in
`SkinCatalog.All` (`Data/SkinCatalog.cs`), not assets - four values that nothing references by object.
Shipped: pistol Gold and Jungle camo, rifle Bone, machete Obsidian.

- **Bought at the trader, as a stand-in item.** `ItemFactory` seeds one `skin_<id>` `ItemDef` per skin and
  `ShopFactory` lists each as an unlimited offer at the skin's price. `ShopCounter.ServerBuy` recognises
  the item (`SkinCatalog.ForItem`) and calls `ServerBuySkin` instead of filling a bag: it spends the
  price, unlocks the skin and wears it at once. Owning it already re-wears it for free, so switching
  back is never a second charge. Smaller than teaching the shop a second kind of row: the shelf, the
  screen, the buy RPC and the reach check are all reused unchanged.
- **`SkinLocker`** sits on the player. What each weapon wears is a `SyncDictionary<weaponId, skinId>`,
  so friends see it; what you own is a server-side set. Server-authoritative, like the wallet.
- **Saved** in `SavedPlayer.skins` / `wornSkins` (owned and worn skin ids), keyed by `PlayerKey` like the
  rest, restored in `RunSave.ServerApply`. Additive, so the save version does not change.
- **Drawn** by `CharacterSkin.Tint`, whenever the held item or the worn skin changes: a
  `MaterialPropertyBlock` (`_BaseColor` and `_Color`) on every renderer of the hand copy and the
  first-person copy. No material is made. It replaces the colour outright, so a skin flattens a
  multi-material model to one colour.
- Skins are ignored by selling (value 0) and never enter a bag.

Regenerate: `ItemFactory.Build`, `PlayerPrefabBuilder.BuildPlayerPrefab`, then `ShopFactory.Build` with
`-rebuildShop` (the shelf is only rewritten on request; this resets prices). Harness: `-skinShopTest`,
solo host, `-scene island`.

## Tracers across the voyage

`TracerEffect` parents its pooled tracers and impact bits to a root of their own, not to the
shooter, so a tracer stays where it was drawn. That root was a plain scene object, and the player is
not: the voyage to island 2 unloaded the root with the first island, and from then on every frame
walked a list of destroyed bits. The long bot playthrough logged a hundred thousand
`NullReferenceException`s from `TracerEffect.Update` after landing. The root is now
`DontDestroyOnLoad`, and still destroyed with its owner.

`-voyageTest` checks `PoolAlive` after both crossings.

## Late guns (economy overhaul)

The gun ladder now runs to the run's real ceiling. Trader prices (`ShopFactory.Stock`): pistol 180, shotgun 2500, SMG 8000, rifle 25000, machine gun 90000, sniper rifle 250000, one of each on the shelf. Knife (60) and hatchet (90) stay early and cheap; ammo prices are unchanged.

Two new hitscan weapons are rows in `WeaponFactory.Seeds`, both on `rifle_ammo`: `machinegun` (tier 3, 620 rpm, 60 rounds, 3.5s reload, 50 damage, spread 2.5) and `sniper` (tier 4, 40 rpm, 5 rounds, 3s reload, 195 damage, range 200). Their ItemDefs live in `ItemFactory` with a `Value` of at most a tenth of the shop price, so the trader's half-of-value buyback can never turn buy-then-sell into a profit. The Kenney pack has no LMG, so the machine gun wears the SMG model scaled up and the sniper the rifle model, via `WeaponFactory.Art`.

`Shop.asset` is only written once, so an existing checkout needs `-rebuildShop` (which resets every price) to pick the new prices up. `-shopTest` checks the ladder is strictly ascending, the two new prices, catalog membership, hitscan kind and the buyback bound.

## Weapon mods (economy overhaul)

Per-weapon upgrades bought at the trader with the weapon in your hand. `Economy/WeaponMods.cs` on the
player, harness `-gunsmithTest` (solo, `-scene island`).

| Track | Levels | First level | Top level | Effect |
|---|---|---|---|---|
| Firepower | 5 | 25% of the weapon | 5x the weapon | +12% damage per level |
| Scope | 4 | 50% | 5x | ADS zoom 1.25x (iron) -> 2x, 3x, 4.5x, 6x |
| Red dot | 1 | 75% | - | aimed scatter halved again, dot drawn while aiming |
| Flashlight | 1 | 30% | - | spot light on the camera, toggled with **T** |
| Recoil grip | 1 | 3x | - | kick x0.35 |

- **Prices are multipliers of the weapon's shop price**, read off the counter you stand at. Levels
  climb geometrically between the first and last multiplier and round to three significant figures.
  A counter that does not sell the gun (the barman) cannot mod it; a pistol's full kit is under 4k,
  a 250k gun's firepower 5 alone is 1.25M. Grind or gamble, by design.
- **Mods belong to the player, per weapon id**, in a `SyncDictionary<string, byte>` ("rifle/Sight"
  -> level). Dropping a gun and picking up another of the same kind keeps them. Saved in
  `SavedPlayer.mods` as "rifle/Sight=2" lines.
- Melee weapons take firepower only; guns take all five.
- **Aiming**: right mouse with a gun in hand raises it (the taser keeps right mouse otherwise).
  `Weapon.Aiming` is set by `PlayerCombatInput` and sent with the shot; the server halves the cone
  (a quarter with the red dot). The camera zooms by the sight level and scales look speed by 1/zoom.
- The server reads damage, scatter and the price; the owner's camera reads zoom, the red dot, the
  grip's kick and the light. The light is local only: squadmates do not see your beam.
- The shop panel grew to 31 shelf rows plus 5 gunsmith rows (two columns, 40 px pitch, taller than
  the chest panel). The gunsmith rows show the held weapon's icon and "Scope 1/4  $194".

## #228: the player hitbox and the shot's own frame

A shotgun at 2.5 m landed none of its eight pellets in GunTest. There were two causes.

**PhysX queries a CharacterController at 0.8 of its size.** The CCT builds its kinematic actor with
`scaleCoeff` 0.8, and Unity does not expose it. A raycast at a standing player met a capsule
0.17-1.57 m tall with a 0.24 m radius, not the 0-1.75 m, 0.3 m one the controller reports. An
`OverlapSphere` still found the full one. So a level shot at eye height (1.55 m, on a tiny slope)
passed over the head of every standing player. Probing the height in 10 cm steps showed the gap.

`PlayerMotor` now adds a trigger `CapsuleCollider` at the controller's true size:

- `ApplyHeight` keeps it in step with the crouch.
- `LateUpdate` switches it off with the controller, so a seated or ragdolled player is hit through
  the seat or the bones as before.
- `Weapon.ResolveHitscan` queries with `QueryTriggerInteraction.Collide` and skips every trigger
  that `PlayerMotor.IsHitbox` does not recognise.
- Every overlap in the game already passes `Ignore`, and no script has `OnTriggerEnter`, so
  nothing else notices the new trigger.

**The scatter was in world frame.** `Quaternion.Euler(pitch, yaw, 0) * direction` pitches about
world X, which does nothing to a shot fired along X. A gun facing east or west threw a flat fan,
and one facing north or south a round cone. GunTest's lane happened to turn east, so every pellet
stayed at eye height. `Scatter` now rotates in the shot's own frame through `LookRotation`.

**Harness:** GunTest checks that a level eye-height ray reaches the victim's hitbox, and the shotgun
now lands 7 of 8 pellets. Results: GunTest 151/0, WeaponTest 28/0, MeleeTest 33/0,
VehicleTest 79/0.

## Game stage and the wild zone (economy overhaul)

The late guns cost 90k and 250k, and a boar's hide is worth 13 at the counter. Without something
that grows, the last rungs are a wall. Two things close it.

**The game stage** (`Economy/GameStage.cs`) is the reward for climbing the gun ladder. The first
time anybody holds a gun whose shop price reaches a threshold, the stage rises and never falls:

| Gun held   | Price   | Stage | Drops sell at |
|------------|---------|-------|---------------|
| pistol     | 180     | 0     | x1            |
| shotgun    | 2,500   | 1     | x1.5          |
| smg        | 8,000   | 2     | x2.25         |
| rifle      | 25,000  | 3     | x3.38         |
| machinegun | 90,000  | 4     | x5.06         |
| sniper     | 250,000 | 5     | x7.59         |

- **Only drops scale.** A drop is anything in an animal's loot or a fish catch, read off the
  catalogs so a new species needs no list. An item the shop itself sells stays at its flat
  buy-back, because a box of rounds that sells back at 7x is a money printer.
- **Server state.** `WeaponMods.Update` reports each newly equipped hitscan gun to
  `GameStage.Observe`, priced at the highest counter that sells it. `ShopCounter` mirrors the
  stage in a SyncVar, so the shop header shows `stage N: drops xM` to clients. The sell path
  prices through `GameStage.SellPrice`.
- **Per session in memory.** `NetworkBootstrap.StartServer` calls `GameStage.NewSession`, so
  leaving to the menu and hosting a fresh run does not carry the last run's stage over.
- **Saved with the run** as `SavedRun.stage`. `Stage` is the max of the session and the save, so a
  reload cannot lose it.

**The wild zone** (`AnimalFactory.BakeZones`) is a pair of zones at the standable land farthest
from the base camp: a 10 m grid over the island, above +3 m, slope under 20 degrees. On Island it
sits 408 m from camp, and on Island2 235 m. Two species live only there:

- **jaguar**: aggressive, 150 hp, runs 7.4 (faster than a sprint). Drops a pelt (450), 1-2 fangs
  (80 each) and meat. Worth 593 a kill, eight boars.
- **stag**: skittish, senses at 55 m, runs 8.5. Drops 1-2 antlers (270 each), hide and meat.
  Worth 500. It is meant to need a rifle and a scope.

The pelt and antler prices were cut from 600 and 360 after an economy review. At the old
prices, four players hunting at stage 3 and 4 went from the rifle to the sniper in about 2.6 hours,
fast for the last gun; the new prices make it about 3.5 to 4.

`AnimalDef.Wild` marks them. It is set every bake like the loot, from `AnimalFactory.WildSpecies`.
The early-game hunting rate in `EconomyModel.Hunting` and AnimalTest's "a sprint catches prey"
check both skip wild species, since neither is about the evening's hunt near camp.

Regenerating: run `ItemFactory.Build` and `AnimalFactory.Build`, then
`-executeMethod EscapeWithYourFriends.EditorTools.AnimalFactory.Rezone`. Rezone re-bakes only the
zones of both island scenes, without a full terrain regenerate. It loads the profile after
`OpenScene`, which unloads it along with the POI catalog; loaded first, every zone lands on the
origin.

**Harness:** `-stageTest -scene island -noNatives -noAnimals -save -savePath X`, solo, 20 checks:

- the ladder and the multiplier
- a pistol leaves the stage alone, a rifle raises it to 3, and it never falls
- the counter mirrors it
- a pelt sells at x3.375 while stock sells flat
- a save round trip
- the wild zone exists, sits over 300 m from camp and holds the only jaguars
- a jaguar spawned there lands on the NavMesh
- a jaguar kill is worth more than five boars

---

## A wipe is not free

A design review found the cheapest way to revive one dead friend: the other three die too. `WipeGuard`
got everyone up at their spawn for free eight seconds after a full wipe, bags and all, while the
Revive Machine charges `250 + 200 x (deaths - 1)`. Solo players never paid for a death at all.

- **A wipe bills everyone.** Each body pays its own Revive Machine price from its own wallet, or
  everything it has if that is less. Nobody goes negative, so a broke squad still gets up.
- **The price follows the stage.** `ReviveMachine.PriceFor` is multiplied by
  `GameStage.Multiplier`, the same x1.5 per stage that drops sell at. At stage 3 a flat 250 was
  pocket change.

`-wipeTest` now checks the charge too: 500 in the wallet, 250 out, back up at the spawn (7/0).

ponytail: a friendly-fire death still raises the victim's own price. That needs the killer on the
damage record; add it if griefing shows up in public lobbies.

---

## The gun ladder, after How to Fish

How to Fish prices its guns 50, 150, 650, 3,800 and 20,000. The shotgun is three pistols away, so the
first real upgrade comes in the first half hour. Each rung after that is four to six times the last
and comes with a new place to spend it. Ours climbed 180, 2,500, 8,000, 25,000, 90,000 and 250,000.
That put a fourteen-times wall right after the pistol, and the last two guns sat past the end of the
run.

| Gun | Was | Now |
|---|---|---|
| pistol | 180 | 180 |
| shotgun | 2,500 | 550 |
| smg | 8,000 | 2,300 |
| rifle | 25,000 | 6,500 |
| machine gun | 90,000 | 20,000 |
| sniper | 250,000 | 70,000 |

`GameStage.Thresholds` follow the guns: 550, 2300, 6500, 20000 and 70000. The shotgun now buys
stage 1 early, so drops sell at x1.5 from the first half hour on, and `-economyTest` moved the boat
from 2.07 sessions to 1.64. That is about two and a half hours, still past Steam's two-hour refund
window but much closer to it. Mod prices are multiples of the gun's own price (`WeaponMods`), so they
followed without a change. The rifle arrives about when the boat does; the machine gun and the sniper
are island 2's answer to the village.

The shelf is written once, so the change needed `ShopFactory.Build -rebuildShop`.
`-shopTest` 72/0, `-stageTest` 20/0, `-gunsmithTest` 25/0, `-economyTest` 27/0.

---

## Casino days: a new game every day, bigger chips every day

Gamble With Your Friends paces its casino over twelve days: new floors open as the days go by, and
each floor has more games and higher limits. Our day is twenty minutes (`WorldClock`), so a run of
three to four hours lives about ten of them. `CasinoDays` uses the same shape, one step per day:

| Day | Opens | Stakes |
|---|---|---|
| 1 | roulette | x1 |
| 2 | blackjack | x1.5 |
| 3 | Coconut Sevens | x2.25 |
| 4 | Volcano | x3.4 |
| 5 | Reef | x5.1 |
| 11+ | | x57, the cap |

- **A closed game says when it opens.** The spin button or the first blackjack seat reads
  "Coconut Sevens opens on day 3 (today is day 1)" and refuses the press on the server, so a
  player sees what is coming.
- **Every stake grows by the day's scale.** Roulette chips, the blackjack chunk (and its ten-chunk
  seat cap), the four slot stakes and the cage's exchange chunk are all `CasinoDays.Scaled(base)`:
  x1.5 per day, the same step `GameStage` pays drops at, rounded to three figures.
- **The day survives a save.** `SavedRun.day` holds it. The live value is the saved days, read once
  per session, plus `WorldClock.Day`; reading it only once is what stops a save from counting the
  session twice. A client reads the day off the cage (`Cashier` mirrors it in a SyncVar), the way the
  shop counter mirrors the stage.

New games join the calendar by adding to `CasinoGame` and `Opens`. Video poker and the "specials"
(crash, mines) are next.

`-casinoDaysTest` (solo, `-save -savePath X`) runs 17 checks: one game a day, closed games refuse a
player with chips, stakes x2.25 on day 3, the cap, and the save round trip. `-slotTest` and
`-blackjackTest` set `CasinoDays.AllOpen`, since they test the games and not the calendar. Slots
77/0, blackjack 68/0, roulette 26/0.

---

## Image clarity, part 1 (#240)

The user saw the image as blurry and old. Three causes were in our code:

- **No anti-aliasing at any tier.** `PostProcess` now sets SMAA (high) on the main camera from
  Medium up, alongside the post-processing flag it already set. Low keeps none; FSR with RCAS
  sharpening is its planned answer (#240, part 2).
- **No anisotropic filtering.** Most imported textures say aniso 1, so "per texture" meant none and
  the ground smeared a few metres out. `GraphicsBoot` forces it on from Medium up.
- **Every integrated GPU got Low**, including the Radeon 760M that ART-PLAN.md targets at Medium.
  Low means no HDR (half the grade and all of the bloom gone), a 512 shadow map and LOD bias 0.4.
  `GraphicsBoot.IsCurrentIntegrated` now sends RDNA 2/3 parts (Radeon 6xxM to 8xxM) and Intel Arc
  to Medium. Older iGPUs stay on Low.

Two more causes were not the game's. Playthrough shots are captured at 1280x720, and the Editor's
Game view blurs at a scale above 1x. `docs/PLAYING.md` now says so, along with how to keep a
hybrid-GPU laptop on its discrete card. The user's laptop runs the build on its RTX 4060 at
Very High.

## Wrath of the Volcano, the whole concept (#255)

The Volcano cabinet already played the pay-anywhere, tumble-and-orbs game. This adds the three
things the genre's best-known version of it has and ours did not:

| | Before | Now |
|---|---|---|
| Orbs | x2-x100 | x2-x500; x250 and x500 are 4 in 10,000 orbs |
| Ante | none | 1.25x the stake, the volcano weight 36 -> 45 on the base reels: feature 1 in ~440 -> 1 in ~200 |
| Bonus buy | 137x | **100x** |
| Free-spin orb rate | 50 per mille | 32 per mille, so a feature is worth ~98x instead of ~130x |
| Base orb rate | 12 per mille | 14 per mille, which puts the return lost from the feature back into the base game |

Retriggers (+5 spins on three volcanoes in a free spin) were already in.

**Return, tuned by simulation under Mono** with `SlotMath.cs` compiled outside Unity, 3M spins
each: base game 95.3%, ante 95.9% of the larger stake, bought feature 97.6%. The tool is the
compiler that ships with the Editor. No .NET SDK is installed, and none is needed:

```bash
M=/d/Unity/Editors/6000.3.23f1/Editor/Data/MonoBleedingEdge
"$M/bin/mono.exe" "$M/lib/mono/4.5/csc.exe" -langversion:preview -optimize+ -out:sim.exe Sim.cs \
  Assets/_Project/Scripts/Casino/SlotMath.cs && "$M/bin/mono.exe" sim.exe
```

`Sim.cs` is a `Main` that calls `SlotMath.Spin` in a loop. Mono is the backend that ships, so its
totals are what the harness's golden numbers are checked against. The Sevens and Reef totals came
out identical to the .NET ones, as they must.

**The ante is a cabinet setting, not a bet.** It is a `SyncVar<bool>` on `SlotMachine`, flipped by
a fifth button (`SlotAction.Ante`, Volcano only). While it is on:
- `Stake` is `SlotMath.AnteStake(Bet)`, a quarter more, and every win is still counted on the bet;
- `BuyCost` is 0, so no one can buy the feature. That is the genre's rule: the ante and the buy are
  two ways to pay for the feature, and you pick one.

The flag rides with the seed (`RpcPlay`, `_lastAnte` for late joiners), the same as the buy's.
`SlotMath.Spin` ignores it on a bought spin and on a game without one, so a bad flag cannot change
a spin.

### Harness

`-slotTest`, a pair, is now 90 + 5 checks. The new checks:
- Volcano's golden totals: base, bought, and ante (`GoldenAnte`);
- the ante's return band;
- the ante button's stake, refusing the buy, paying the seed, and turning off again;
- four buttons on the Volcano.

## Fruit Tumble: the sweet pay-anywhere game (#256)

A fourth cabinet, `SlotKind.Fruit`, on the genre's other big pay-anywhere concept. Same 6x5 grid,
8+ anywhere and tumbles as the Volcano, but the multipliers work the other way round:

| | Wrath of the Volcano | Fruit Tumble |
|---|---|---|
| Base-game multipliers | lava orbs, 14 per mille of cells | **none** |
| Feature | 4+ volcanoes: 15 free spins | 4+ suns: **10** free spins |
| Free-spin multipliers | orbs x2-x500, **added up for the whole feature** | coconut bombs x2-x100, **each sequence its own, then gone** |
| Retrigger | 3 volcanoes: +5 | 3 suns: +5 |
| Ante / buy | 1.25x / 100x | 1.25x / 100x |
| Return (3M spins, Mono) | 95.3% / ante 95.9% / buy 97.6% | 95.5% / ante 96.3% / buy 95.2% |

Without base-game multipliers the pays had to carry the base game, so Fruit's table is about 3.3
times the Volcano's. The free-spin bomb rate (62 per mille) then tunes the feature to ~95x.

**One engine, two rule sets.** The tumble code in `SlotMath.Volcano` now takes a `TumbleRules`:
pays, scatter pays, free spins, the three sets of reel weights, multiplier rates and values, and
`Carry`. That last field is the whole difference in the free spins. With `Carry` on, a paying
sequence adds its orbs to a running multiplier that lasts the feature; with it off, the sequence is
multiplied by its own bombs only. The refactor kept the Volcano's RNG order, and its golden totals
are unchanged.

**The schedule** (`CasinoDays.Opens`) is still one game a day, now over six days: roulette,
blackjack, Coconut Sevens, Fruit Tumble, Reef Rush, then Wrath of the Volcano on day 6. The
Volcano moved back two days because it is going to the VIP room.

**Where it stands.** `SlotFactory` builds `SlotFruit.prefab` on the shared atlas, with fruit looks
prefixed `Fruit` because a look's name keys its mesh. The atlas now holds 44 of 64 colours. The
shack has no room for a fourth cabinet, so the cabinet goes on the floor with the big casino (#254).

### Harness

`-slotTest`, a pair, is now 108 + 5 checks. The new checks:
- Fruit's golden totals for base, bought and ante play;
- its return bands with and without the ante;
- eight berries paying Fruit's own table.

There are also behaviour checks read off the pictures of 2,000 bought features:
- a Fruit free spin always starts at multiplier 0, so its bombs never carry;
- a Volcano free spin does carry;
- no bomb ever lands in Fruit's base game.

`-casinoDaysTest` is 18 checks. It now finds the last opening day from the enum, so it no longer
hard-codes the number of games.

## The big casino and the VIP room (#254)

The shack is gone. The casino is now one 20 x 19 m building in two rooms, with every game in it.

```
 z  9  ┌──────────── front wall, 3 m doorway ────────────┐
       │  blackjack        roulette          Sevens      │   the floor
       │  (x -9)           (0, 3)            Fruit (x 9) │
 z -2.5├── bar ──── glass ─ VIP DOOR ─ glass ────────────┤   the partition
       │                                                 │   the VIP room
 z -10 └─────────── Volcano (x -1.2)  Reef (x 1.2) ──────┘
```

- **The floor** (z -2.5 to 9) holds roulette, blackjack, Coconut Sevens, Fruit Tumble, the bar and
  the two cage windows.
- **The VIP room** (z -10 to -2.5) holds Wrath of the Volcano and Reef Rush. Lagoon Catch (#257)
  will go there too.
- **The partition** is solid at its ends and glass either side of the door. A player who cannot
  afford the VIP games yet can always see them. The glass is a collider with nothing drawn: only its
  frame says there is a pane.

`GreyboxBuilder.BuildCasino` builds the room and `POIFactory` places each game. Both work from the
same numbers, and `CasinoTest` and `CasinoFloorTest` check that they still agree.

### The VIP door

`VipDoor` is a `NetworkBehaviour` and an `IInteractable`. It does not open. A door that opened for
one player would let in whoever stood behind them, so it teleports instead:

| Where you press it | What happens |
|---|---|
| On the floor, holding at least `Minimum` chips | You land 1.4 m inside the VIP room |
| On the floor, below the minimum | Nothing. The prompt says "VIP: hold 11,200 chips to enter (you have 9,800)" |
| In the VIP room | You land back on the floor, whatever you hold |

- `Minimum` is `CasinoDays.Scaled(5000)`. It grows with the day like every other stake: 5,000 on
  day 1, 11,200 on day 3.
- The chips are checked only at the door. Losing them inside does not throw anyone out.
- "Inside" means behind the door's front plane.

### Playtesting: `-casinoAll`

`-casinoAll` sets `CasinoDays.AllOpen`. Every game opens on day 1 and the VIP door is free, so
every gambling mechanic can be reached in a fresh game without grinding days.

Play mode has no command line, so the Editor has the same switch as a menu item: `EWYF/Casino: open everything` (`CasinoAllMenu`). It is kept in `EditorPrefs` and read by `CasinoDays.AllOpen` under `UNITY_EDITOR`, so it holds until unticked and never reaches a build.

### Deferred

The issue asked for high-limit VIP roulette and blackjack tables. They are not in this change.
`CasinoTest`, `BlackjackTest` and others find "the" table with `FindObjectsByType(...).First`, so a
second table needs those tests reworked first.

### Harness

`-casinoFloorTest` runs solo on `-scene island` and makes 36 checks:
- every game exists: each `SlotKind` cabinet, the roulette, the blackjack table and both cage windows;
- each game is inside the building, on the side of the glass it belongs to;
- each game can be reached: a clear line runs from 1.3 m in front of it to the middle of its room;
- the VIP minimum is 5,000 on day 1 and grows with the days;
- one chip short, the door refuses, prompts with the minimum, and leaves the player on the floor;
- at the minimum, the door lets the player in;
- broke and inside, the player can still leave;
- `-casinoAll` makes the door free and opens every game on day 1.

Two older checks assumed the shack:
- `SlotTest` now expects one cabinet for each `SlotKind`, not three;
- `DrunkTest` lets the barman stand up to 10 m from the table, not 6. The bar is now 8 m away.

## Lagoon Catch: the farming slot, with autoplay (#257)

The fifth cabinet, `SlotKind.Lagoon`, is the genre's fishing concept with our own symbols and
numbers. It stands in the VIP room and opens on day 7.

### The game

- **Grid:** 5x3, ten fixed lines (the first ten of Coconut Sevens').
- **Symbols:** seven payers, from shell to boat, then five fish:

  | Fish | Minnow | Snapper | Grouper | Marlin | Golden marlin |
  |---|---|---|---|---|---|
  | Worth | x2 | x5 | x10 | x25 | x50 |

  Each value is its own symbol, so the grid alone says what every fish is worth. On the lines,
  any fish counts as the same "fish" symbol.
- **The feature:** 3, 4 or 5 golden hooks anywhere pay 2x, 10x or 50x and start 10, 15 or 20 free
  spins.
- **The castaway** appears only in free spins:
  - he is a wild on the lines;
  - each castaway on screen collects every fish's value;
  - every fourth castaway caught adds ten spins and raises the collect multiplier to x2, x3, then
    x10, from the next spin on;
  - the board's title shows the multiplier.
- **Frames:** a free spin is one drop picture, plus a catch picture when castaways reel fish in.

| | Base | Buy (94x) |
|---|---|---|
| Return (2M spins, Mono) | 95.8% | ~95.5% |
| Feature | 1 in ~190 spins, ~100x on average | |

There is no ante.

### Autoplay

`SlotMath.HasAutoplay` is true for Lagoon only. Its cabinet has a fourth button where the ante
would sit.

**Owning an autoplay**
- The first press starts 10 spins in the presser's name.
- The owner can press again to raise it to 25, then 50, then 100. A press past 100 stops it.

**How it spins**
- `SlotMachine.Autoplay` waits for the last spin to pay, then calls `ServerSpin` for the owner.
- Each spin is an ordinary stake from the owner's wallet, so the ledger, the jackpot and the replay
  are unchanged.

**It stops when:**
- the spins run out;
- a stake bounces (out of chips);
- the owner's body despawns;
- or a spin hits the feature, which then plays out and pays like any other spin.

**While it runs, nobody else can spin.** `ServerSpin` refuses anything but the autoplay's own
press. Everybody else's prompt says "Ana is autoplaying: 37 spins left", and the slot board adds
`AUTO 37 (Ana)`.

`SlotMachine.Seeds` is a harness-only queue. The server takes its next seeds from it before
rolling its own, so a test can line up losers and a feature.

**Not done:** the issue's "the whole server hears the big win". `BigWin` is heard within 10 m, and
there is no server-wide announcement channel to put it on yet.

### Harness

`-slotTest`, a pair, is now 145 + 5 checks. The new checks:
- Lagoon's golden totals, base and bought;
- its return band;
- the catch on a hand-built grid (x2 + x10 + x50 = 62x);
- a castaway standing in for a boat, and five castaways paying as five boats on all ten lines;
- from 2,000 bought features: castaways reel fish in, the catch reaches x2 and x10, no castaway ever
  lands on the base reels, and no hook lands in free spins.

In the world, with queued seeds:
- 10 autoplay spins on three stakes' worth of chips: three spins, then a stop at zero chips;
- a feature on the third spin: the autoplay stops there and the feature pays;
- the owner can raise the autoplay and stop it;
- the other player is refused the cabinet meanwhile.

`-casinoFloorTest` (now 40 checks) expects Lagoon in the VIP room.

## Graphics presets and options (#241)

Escape > the right-hand column. One **Graphics preset** row — Low, Medium, High, Ultra — then every
option a preset is made of, each settable on its own. Change any one and the preset reads **Custom**.
The label is worked out from the values each time, never stored, so it cannot disagree with them.

### What a preset is

A preset is a quality level plus one row of `VideoSettings.Values`.

- **The level** picks the URP asset. That asset carries what URP will not change at run time: HDR,
  soft shadows, extra-light shadows, and which SSAO the renderer has.
- **The values** are written over that asset when they are applied:

| | Low | Medium | High | Ultra |
|---|---|---|---|---|
| Quality level | 1 (URP_Low) | 2 (URP_Medium) | 4 (URP_High) | 5 (URP_High) |
| Render scale | 77%, FSR 1 | 100% | 100% | 100% |
| Anti-aliasing | off | SMAA | SMAA | MSAA 4x + SMAA |
| Shadows | 1024², 40 m, 1 cascade | 2048², 80 m, 2 | 2048², 150 m, 4 | 4096², 250 m, 4 |
| Ambient occlusion | off | cheap | full | full |
| Grass (distance, density) | 0.5×, 0.6× | 0.8×, 0.85× | 1× | 1.25× |
| View (trees, terrain error) | 0.6×, 10 px | 0.85×, 7 px | 1×, 5 px | 1.25×, 3 px |
| Bloom | off | on | on | on |

Notes on the table:
- **Low** now uses FSR from 77% rather than full resolution. Bilinear from 80% was the blur in #201;
  FSR at a lower scale is sharper. This was the last open item of #240.
- **High** drops the MSAA 2x it had under SMAA. On flat-shaded geometry the second pass bought almost
  nothing, and High has to hold 140 fps at 1080p on a 4060 laptop.
- **Ultra** is for streamers and capture. The guess never picks it: `GraphicsBoot.Guess` tops out at
  High, and the harness checks that.

### Where each option is read

- **URP asset:** render scale, the upscaler, MSAA, and the shadow distance, resolution and cascades.
  Shadows "Off" is a zero distance, which is how URP itself turns a camera's shadows off.
- **The renderer's SSAO feature:** `SetActive` from the AO option. URP_Low's renderer now carries the
  cheap SSAO too, so the option has something to switch on there. The Low preset leaves it off.
- **Camera:** SMAA, set by `PostProcess.Refresh`, which also switches bloom.
- **Terrain:** `TerrainQuality` takes its multipliers from `VideoSettings.Terrain`. It no longer uses
  the quality level.
- **Not presets:** VSync and the frame cap are about the monitor, not the GPU. They are stored on
  their own and reapplied after every `SetQualityLevel`, which would otherwise restore the level's own
  `vSyncCount`. Anisotropic filtering moved here from `GraphicsBoot` for the same reason.

### Storage and boot

- **Storage:** the values go into one `PlayerPrefs` key as JSON (`ewyf.video`). The level stays in
  `GraphicsBoot.PreferenceKey`. `GameSettings.Quality` is gone; its only callers were the old
  quality cycler and the harness.
- **Boot:** `GraphicsBoot` sets the level, then calls `VideoSettings.Boot`.
  - A forced `-quality` brings that level's preset, so `-quality High` means High whatever the
    machine stored.
  - Otherwise the stored values are used, or the level's preset on a first run.
  - A headless run now also applies a *stored* choice, not only a forced one. Without that, the
    persistence half of the harness could not see it.

### Writes and caveats

- **Writes in the Editor:** the URP assets are edited in place. In a build nothing saves them. In
  the Editor the edits would outlive play mode, so the first write to each asset or feature takes an
  `EditorJsonUtility` snapshot, and leaving play mode restores it.
- **Soft or hard shadows** follow the level's asset, because URP keeps that setter internal. Medium
  shadows picked on Low are therefore hard.
- **Bloom on Low** does nothing visible. URP_Low has no HDR, so nothing crosses the 1.05 threshold.

### A bug the harness found

FishNet sets `Application.targetFrameRate` to its own 500 every time a server or client starts. The
player's frame cap was undone the moment they hosted.

- `VideoSettings.TakeFrameRate` sets both managers' rate to 0, which tells FishNet to leave the
  frame rate alone. It runs from `NetworkBootstrap.Awake` and from every apply.
- It only acts once `VideoSettings` has applied something. A headless harness with nothing stored
  keeps FishNet's 500 rather than spinning flat out.

### Harness

`-settingsTest write` / `read` grew from 13 + 8 to 23 + 10 checks.

The write half:
- Low, High and Ultra each set their level, read as themselves, and reach the URP asset, the SSAO
  feature and SMAA.
- One changed option reads Custom.
- An index off the end is clamped.
- VSync off and a 144 cap reach Unity.
- Everything reads back from `PlayerPrefs`.

The read half, in a new process, finds Custom on Ultra's level already applied to the asset, with
VSync off and the 144 cap. It then clears every key.

```
[SettingsTest] write: 23 passed, 0 failed.
[SettingsTest] read: 10 passed, 0 failed.
[QualityTest] 13 passed, 0 failed.
```

**Not checked by any harness:** whether the menu column looks right. Headless builds no canvas.

**Not added:** particle and water quality options. Nothing reads them yet; they arrive with #251 and
#247.

## The perf route and beauty shots (#239)

`World/LookRoute.cs` is the look epic's instrument: one fixed route of camera spots round the
island, flown by a camera of its own. Hooked in `NetworkBootstrap` beside `ShotTest`. It needs a
window (no `-nographics`) and a host, so it is not a headless harness. It measures and shoots, and a
person reads the result.

- **The route.** Spawn beach, wreck, jungle by the cave, village, trader, the casino floor, the
  slot row with every cabinet spinning, the island's highest point looking at its middle, and the
  plane. Each spot comes off a `Landmark`, so a moved POI moves its spot. A missing landmark is
  logged and skipped, so Island2 still runs a shorter route.
- **`-perfRoute <file.md>`** holds each spot 1.5 s to settle, then 5 s, with VSync and the cap off
  and the clock frozen at noon. It appends one markdown table: p50, p95 and worst frame time, then
  batches, SetPass calls, triangles and shadow casters off the render `ProfilerRecorder` counters.
  The counters exist in development builds only. `-commit <hash>` is written into the header,
  because a build has no git.
- **`-beautyShots <folder>`** shoots every spot at noon, dusk and night, at the monitor's native
  resolution, into `<folder>/<preset>`. The 1280x720 playthrough shots looked pixelated fullscreen;
  that was the capture size, not the game.
- **The camera.** A copy of the player's camera, tagged `MainCamera` so whatever follows the camera
  follows it. The player's camera is switched off, so the numbers are one camera's. Canvases are
  hidden so the shots are the world. Every slot machine is spun from the host's wallet, topped up
  for it, because that is the casino's worst case.
- **Two things fight the camera.** `PlayerCameraRig` puts a live `CinemachineBrain` on whatever
  `Camera.main` is, every frame, and a brain flies its camera to the player's eye; the route camera
  carries a disabled one so the rig leaves it alone. The first-person gun hangs off `Camera.main`
  too; the host's `CharacterSkin.ForceCarry` puts it away. Story beats stay quiet while a route
  runs (`LookRoute.Running`), or the title card lands in a shot.
- **`tools/perf-route.sh`** runs the route on every tier on both GPUs of the dev laptop: Medium,
  High and Ultra on the RTX 4060, and Low, Medium and High on the Radeon 760M, the min-spec proxy.
  It appends to `docs/PERF.md`. The iGPU run uses a copy of the build pinned to the power-saving GPU
  by a per-exe Windows GPU preference. A run whose log does not name the GPU it was meant for is
  failed, since Windows ignores the preference silently.
- **`-quality High` means the preset.** It used to pick Unity level 3, named "High", which is the
  Medium preset's URP asset. `GraphicsBoot.ByName` tries the preset names first.

**Baseline** (commit 9d12636, before the look pass): every tier meets its target. The 4060 is
CPU-bound at about 4 ms on Low and Medium. High's cost is batches and triangles at range: the
overlook and the plane draw 8 500-10 700 batches and 19-20 M triangles against Medium's 2 100 and
4.5 M. A look PR that makes Medium on the 760M worse by more than 1 ms at p95 says why.

The baseline shots show two things for later passes. From the overlook at dusk the sea is a flat
square with a hard edge and black beyond it (#247). The slot cabinets are flat colour with no glow,
lit or not (#252).

## Wind and ambient life (#244)

A world where nothing moves reads as dead. This is the cheapest fix: vertex maths in the shader
everything already wears, and three particle systems round the camera.

- **`EWYF/Stylized` sways behind a `_WIND` keyword** (`shader_feature_local_vertex`, so materials
  without it pay nothing). `ApplyWind` works in world space and runs in all four passes, so the
  shadow and the depth follow the leaf. The mask is height above the model's own pivot: the foot of
  a trunk stays put and the crown moves most, with no vertex colours painted. The bend grows with
  the square of that height, times the material's `_WindSway`. The phase comes from where the tree
  stands, so a grove does not sway in step. Leaf flutter (`_WindFlutter`) is a fast small wobble on
  top.
- **`World/Wind.cs`** sets the one wind as globals every frame. `_WindParams` packs direction xy,
  strength z and gust w, and `_WindDetail` turns flutter off when the grass option is at its lowest.
  The direction wanders 30 degrees either side of north-east over minutes. Gusts are slow noise,
  squared, so most of the time is calm and a gust is an event. `Wind.Strength` is the knob a storm
  turns. Nothing is networked: nobody can tell two palms on two screens apart.
- **Only flora sways, through "_Wind" twins.** The kits share materials across categories: the pirate
  atlas dresses both the palms and the wreck, and a wreck that bent in the wind would be a ghost
  ship. So `ArtLibrary.EnsureFloraPrefab` swaps every Tree and Plant renderer onto a copy of its
  material, `StyleLook.WindTwin`, saved next to the original with "_Wind" on its name. `StyleLook.Wear`
  turns the keyword on for those and off for everything else. Trees bend at sway 1. Plants are
  short, so they get sway 8. Leaves and flowers flutter fully, bark and palm trunks at 0.4.
  `FloraFactory.Bake` regenerates the twins and the prototypes. The twins add seven materials: the
  island has 38, against a budget of 48.
- **Terrain trees and their far LOD** wear the same twins, so they sway with no other work. The
  terrain's grass already waves through the terrain's own grass settings (`TerrainGenerator`); the
  grass pass, #246, owns that.
- **`World/AmbientLife.cs`** keeps three particle systems in boxes round `Camera.main`, so their cost
  does not grow with the island:
  - Leaves fall under canopy.
  - Sand blows along the beach, within two metres of the tide line, only in a gust.
  - Fireflies come out under trees at night.

  How much canopy is overhead comes from the terrain's tree list, binned once per scene into 10 m
  cells. The amount follows the grass option: 25% on Low, 60% on Medium, all of it above. Each
  particle is a soft dot generated at start; without a texture the particle shader draws white
  squares. The fireflies' material colour is over-bright, so bloom puts a halo on them.
- **Not done:** butterflies, which want a mesh and a flight path rather than a particle, and gulls,
  which the shore already has as animals.

**Harness.** `-lookTest` checks the materials, since wind and particles only exist with a GPU. Every
Tree and Plant terrain prototype must wear `_WIND`, and no renderer outside a Tree or Plant
`ArtVisual` may. The second check is the ghost ship. 14 passed.

**Cost** (`tools/perf-route.sh`, against the #239 baseline): on Medium on the 760M, the mean change in
p95 across the route is -0.1 ms and the worst spot is +0.1 ms, against a target of +0.5 ms. On High
on the 4060, the plane's p95 is 6.8 ms against the 7.1 ms target. Only the CPU-bound 4060 at Medium
shows the particles, at about +0.3 ms.

**The user's eye:** a 10 s clip at the jungle edge should show motion everywhere. Strength, sway
amounts and particle rates are first guesses.


## Lighting, sky and fog (#243)

The island looked flat at noon, muddy at dusk and black at night. This pass changes the sky, the fog
and the day's numbers. The toon ramp itself (`StylizedLighting.hlsl`) is unchanged.

- **`EWYF/Sky`** (`Art/Sky/StylizedSky.shader`) replaces Unity's procedural skybox. `Sky.mat` keeps
  its path and GUID, so no scene changes. It is one pass with no textures:
  - A zenith-to-horizon gradient, with a square root so most of the dome is the deep colour.
  - A warm wash and a halo round the sun, and an HDR sun disk that bloom catches.
  - A moon opposite the sun, and stars from a hash grid that twinkle.
  - Two layers of value-noise cloud on a flat ceiling. They drift with `Wind`'s `_WindParams`. They
    are bright where thin, shaded where thick, and silver near the sun.

  **The horizon is the fog colour, and so is everything below it.** The fogged far sea and the sky
  meet with no seam. The first version painted a darker "ground" below the horizon, and the edge of
  the water plane showed as a slab against it.
- **Height fog** (`Art/Stylized/StylizedFog.hlsl`). `StylizedFog` replaces `MixFog` in `Stylized`,
  `StylizedTerrain` and `Water`. It runs Unity's distance fog, then adds a mist that lies on the sea
  and thins upward. The mist is integrated along the view ray in closed form, so a hilltop seen
  across a misty valley is fogged by the valley. It reads the global `_HeightFog`: x is the density
  at sea level, y is the base height, z is the falloff per metre. `DayNightCycle` sets it each frame.
  It is a few ALU per pixel and only runs when Unity fog is on.
- **The light never grazes the ground.** `DayNightProfile.LightRotation` clamps the sun, and the moon
  by night, to at least `MinLightElevation` (12 degrees) off the horizon. `SunRotation` still drives
  the sky disk, so the sun you see sets. A light at two degrees turned every pebble into a
  forty-metre shadow streak.
- **The day's numbers** (`SkyFactory.EnsureProfile`; regenerate with
  `-executeMethod EscapeWithYourFriends.EditorTools.SkyFactory.Bake -rebuildSky`):
  - Dusk keeps its sun until 0.77.
  - The moon is at 0.45.
  - Fog colour runs pale blue, then peach, then deep blue.
  - Dusk fog is thinner than the first pass, which was an orange wall.
  - Ambient trilight is lifted at dusk and at night.
- **Ambient colours are sRGB, and Unity lights in linear.** A night sky ambient of 0.1 reaches the
  probe as about 0.01, a black screen. The first night values put the probe's green DC term at
  0.006; it is now 0.026, against 0.27 at noon. Author ambient by eye in sRGB, and check the probe,
  not the colour.
- **Unity's `Gradient` holds at most 8 colour keys.** With more, it logs an error and stays white,
  which painted the fog white at every hour. `SkyFactory.Gradient` now throws instead.

**Harness.** `-lookTest` adds four checks:
- the skybox wears `EWYF/Sky`;
- the light's lowest elevation over a whole day is at least the floor;
- `_HeightFog` is set;
- the ambient probe follows the clock: noon's DC term is above 0.2, night's is above 0.02 and below
  half of noon's.

18 passed.

**Cost:**

Nothing measurable. An A/B on the 4060 at High, `main` against this branch, two runs of each, interleaved:
the mean change in p95 over the nine spots is -0.1 ms, and every spot is within ±0.3 ms, apart from the
village at -0.9 ms, which is noise. A first single run showed +0.6 ms everywhere, including spots with no
sky. The machine was hot from the builds, so a lone run against an old baseline is not a measurement.
The clouds lost a third of their noise in the process (a one-octave second layer, no lookup toward the
sun). That made no difference either.

**The user's eye:** dusk grass in shade is still dark green under a violet fill. That is a choice
between mood and readability, and it is the user's.


## Cinematic story beats, a turning propeller, quiet bots

Three small things that came out of watching the playthrough bot at 1920x1080.

- **Story beats are two shots now** (`World/StoryBeat.cs`, #197). The first version circled the
  subject from 4.5 m up and 11 m out: a security camera, not a film. The user called the "Found them"
  beat on the beach embarrassing. A beat is now six seconds:
  1. A low wide (13 m out, 2.2 m up, 42 degrees) drifts ten degrees, smoothstepped like a dolly. It
     starts on the clear heading `ClearAngle` finds, so it does not film the inside of a palm.
  2. A few frames through black at 36%, so the jump reads as an edit.
  3. An eye-level two-shot: side-on to the line between the player and the subject, the subject on
     the near third. It creeps 12% closer over the shot. A Gaussian depth of field softens
     everything from two metres behind the subject. If the player is too close or too far to share
     the frame, or both sides are blocked, it is a three-quarter close-up of the subject alone.

  The beat's own global `Volume` (priority 50, weighted by the letterbox) adds a heavier vignette and
  a little grain. The bars are 2.39:1. The title is tracked-out capitals in the lower left, the way a
  film puts a name, and it plays only in the second shot. Still one hand-moved `CinemachineCamera`:
  it has no body or aim, so moving its transform is a cut, and the brain's blend does the way in and
  out. Headless still only keeps the bookkeeping, so no harness sees the shots; the playthrough's
  `beat_*.png` frames and the user's eye are the test.
- **The propeller turns** (`PlaneController.LateUpdate`, toward #249). It runs on every machine and is
  visual only. Throttle lives on the host, so it reads what every client already has: a driver in
  seat 0, a whole plane, and the plane's speed from its own movement, since a client's body is
  kinematic. It idles at 5 rev/s and is capped at 14. A two-blade propeller past about 15 rev/s
  strobes at 60 fps and looks stopped. `-flightTest` checks it is still on an unfinished plane and
  turning past 5 rev/s in the air (35 passed).
- **The windowed bots are silent and save nothing** (`GameSettings`, `RunSave`). `-playthrough`,
  `-beautyShots` and `-perfRoute` imply `-mute`, which is also a flag of its own, and never arm the
  run save. The playthrough had loaded the user's real `run.json`, with a finished plane, so the
  "Airworthy" beat never played and the bot failed "the story beats played". It would also have
  written over that save on the way out.


## Playtest fixes: a map, key prompts, fairer AI hits

From one playtest: lost on the island, downed by a boar "at full health", and stuck at the trader
pressing E while the shop wanted Tab.

- **The map on M** (`UI/WorldMap.cs`). `HudRoot` adds it, so a headless peer has none. On first open
  it bakes the terrain into a 512 x 512 picture: sea by depth, sand, grass and rock by height, rock
  wherever it is steep, and a hillshade off the terrain normals. It shows a square cut to the land
  plus a band of sea (`LandView`), not the whole mostly-sea terrain. Over that it draws every
  `Landmark` by name, hostile ones in red, each name in the first free slot of above, below, right or
  left (the camp's buildings are 30 m apart); the squad as coloured dots; you as an arrow along the
  camera; and the objective as a gold ring. M or Esc closes it. It is IMGUI: one texture and a few
  labels, drawn only while open.
- **The key prompt.** `PlayerInteractor` caches `[E]  <Prompt>` for whatever is aimed at and draws it
  under the crosshair. While seated it draws the vehicle's controls instead, at the left edge: car
  and boat, plane (Shift throttle, W/S nose, A/D bank), or passenger. Nothing on screen used to name
  either key.
- **E opens the trader and the chest.** `RequestInteract` sends neither to the server. It queues the
  same toggle as Tab (`PlayerInputReader.QueueInventory`), and the bag opens with the shop or chest
  beside it, and E closes it again. The chest's prompt is now "Open chest (n items)". `Storage.ServerInteract`'s quick
  store and take stay, because `StorageTest` and the bots use them.
- **Health is on screen.** `StatBars` grew a red health row on top. The user had no health bar, so a
  boar pack wore them down unseen and the last 10 points looked like a one-shot. `PlayerCameraRig`
  adds a red edge pulse per hit and a steady dim one below 35%.
- **AI blows are softer on players.** `Health.AiBlow` scales an animal's or a native's blow on a
  downable body by 0.7. From above half health, no single blow downs you. It is applied at the two AI
  hit sites (`Animal.Strike`, `Native.Land`), not in `TakeDamage`, so guns, falls and harness hits
  are untouched. `Struck` still reports the def's damage, which `-nativeTest` checks. An animal also
  hits the same victim at most once per 0.8 s (`Animal.VictimGap`), so a five-boar pack is five
  blows over four seconds, not five in one frame.

**Harness.** `-lookTest` adds two checks: the baked map has both sea and land, and every landmark
falls on it (20 passed). The playthrough presses E, not Tab, at the counter. It checks the shop
opened, and screenshots the map. `-animalTest` 141/0, `-nativeTest` 169/0, `-chestTest` 29/0 and
`-shopTest` 72/0 are unchanged. The prompt, the controls panel and the map are IMGUI, and headless
draws nothing, so the windowed playthrough is their only test.

## Buildings from a modular kit: the trader, the casino

The trader and the casino were walls made of Kenney's survival floor tile stood on its edge and a
roof made of one thatch model stretched over the whole footprint. From ten metres they read as a
pallet fence under a blob. A playtest called them embarrassing.

They are now built from Kenney's Fantasy Town Kit (CC0, pack `Town` in `ArtCatalog`): timber and
stone wall modules with doors and shuttered windows, gable roofs, wooden posts, plank decking,
banners, lanterns and a market stall. The greybox stays the authority, as everywhere in the art
pass: every wall box keeps its name and collider, so `CasinoTest`, `CasinoFloorTest`, the NavMesh
bake and the POI numbers see what they saw before.

Two helpers in `ArtDress` do the building:

- `Wall(parent, box, outward, rows, module, name, along)` cuts a wall box into square cells,
  `rows` high, and fits one module per cell. `module(i, row)` picks the model, so a wall can carry
  a window every other cell. The kit draws each module as a panel on one edge of its cell, with
  its shutters, door trim and banners facing the middle of the cell. That side is the outside.
  `Wall` reads it off the imported bounds rather than assuming it, because the FBX importer may
  mirror the kit's x. It then turns the module by quarter turns until that side faces `outward`.
- `Gable(parent, box, id, cell, name)` repeats a gable module along the box's longer side, one per
  `cell` metres. A single module stretched twenty metres would turn its eave trim into a plank.

The casino's outer walls have stone with shutters on the ground row and timber cross-frame above,
which is how the kit's own sample town is built. The VIP partition is timber only. There are red
banners and posts at the door, and a teal gable roof. The kit's floor deck has gaps, so the floor
box keeps its look underneath it. Grass used to show through.

Both roofs sit a little above the old canvas slabs. The slabs stay, so the ceiling seen from inside
the room is still a ceiling.

The trader has a timber house with a door, two windows and a window in each side wall. The front
is open because the trader stands inside. It has a gable roof on two posts, a deck, banners and
lanterns. The shop's sign moved under the front eave; it used to stand through the roof. The
networked `ShopCounter` gets a red market-stall awning in place of its greybox posts, shelf and
sign. `ShopFactory.Build` adds it in place under the marker `Stall`, so the prefab's GUID is kept.

Regenerate with `ArtExtract.Run -artZips <folder>`, `ArtLibrary.BuildAll`,
`GreyboxBuilder.BuildAll` and `ShopFactory.Build`. Checked by `-lookTest` (20/0), `-casinoTest`
(26/0), `-casinoFloorTest` (40/0) and `-shopTest` as a pair (72/0). The look itself is checked
with `-beautyShots`.

The native village is not part of this change. Its palisade huts and thatch are meant to look
primitive. A dedicated thatch would help them more than the town kit would.

## Footsteps: recorded, by surface, quieter

A playtest called running "horrible, it does not sound like steps, extremely annoying". The step
was a synthesised burst of white noise, so every stride was a hiss. It played at half volume
through the shared twelve-voice SFX pool.

`Footsteps` now plays recorded takes from Kenney's Impact Sounds (CC0), stored in
`Resources/Footsteps`. Each surface has five takes:

- grass on grass and dirt;
- the kit's snow on sand, because it is the same soft crunch;
- concrete on rock;
- wood on anything that is not the terrain: decks, floors, the casino.

A short ray under the body finds the ground. On terrain, the strongest splat layer at that point
picks the sound (`IslandSplat`). Each body has its own `AudioSource`, so steps never steal a voice
from a gunshot. The same take never plays twice in a row, and the pitch varies a little.

Your own steps play at 0.22 and other players' at 0.35, against 0.5 before. The synthesised
`Sound.Step` is deleted. `-audioTest` checks that all four surfaces have their five takes (60/0).

---

## Palms modelled in Blender (#282)

The user called the kit palms "very bad". They are now our own models, generated by
`tools/art/palms.py` and run headless:

```
blender -b --factory-startup -P tools/art/palms.py -- Assets/_Project/Art/Models/Palms
```

Without a folder argument the script builds a preview row in the open Blender instead.

**Nine variants.** Straight, Bend, Tall, Lean, Twin, Young and Old fill the beach slot. Fan and
Clump join the jungle slot. Each one has its own seed. The near mesh is 3.4k to 6.3k triangles,
under the 6.5k tree cap. A `<name>_Far` mesh of 270 to 760 triangles sits in the same FBX.

**What makes them read as palms:**

- a Bezier trunk with ring bands and lips, a root mound and blunt rootlets;
- a crown bulb with fronds placed by phyllotaxis;
- frond colour and droop that follow age: spears, mature fronds, then aging, dying and dry ones;
- coconut bunches in three ripeness colours.

Leaflets are two-sided geometry, not alpha cards, so there is no overdraw and no clip.

**Two textures for the whole family.** `PalmBark.png` and `PalmLeaves.png` are column ramps of
32 px columns, one column per part. They are written by a pure-Python PNG writer, so nothing
outside Blender is needed. Every palm therefore shares two materials, and they batch.

**Pipeline changes:**

- `ArtCatalog` has an `Own` author, `EWYF`, whose packs live under `Art/Models` instead of
  `ThirdParty`. `ArtExtract` skips them, since there is nothing to unzip.
- `EnsureFloraPrefab` uses the `_Far` mesh as LOD1 when a model ships one, and the near mesh as
  LOD0.
- `TexturedMaterial` turns culling off for any `Leaves` slot, alpha or not.
- `ArtVisual.Triangles` skips far meshes, so the tree cap counts LOD0 only.
- The `MeatRaw` row lost `upright`. It was the one row that failed `BuildAll`.

Regenerate with `ArtLibrary.BuildAll`, then `FloraFactory.Bake`, then `TerrainGenerator`
for islands 1 and 2. Island 1 places 1489 palms. The worst view at full LOD is about 60 palms,
or 377k triangles.

`-treeTest` passes 60/0 on island and 54/0 on island2. Trunk colliders are measured from the
mesh at 0.36 to 0.73 m.

---

## Jungle trees modelled in Blender (#248)

The Quaternius broadleaf trees are replaced by our own, generated by `tools/art/trees.py` in the
same way as the palms. The script imports `palms.py` for its mesh, spine and PNG helpers.

**Six species**, each with its own seed:

| Model | What it is |
|---|---|
| `Tree_Rain` | A short trunk, five great limbs and a wide, low dome. |
| `Tree_Kapok` | A tall, pale column on plank buttresses, with flat tiers of crown. |
| `Tree_Fig` | A strangler fig: three fused stems and aerial roots dropping to the ground. |
| `Tree_Flame` | A flame tree in flower, an umbrella of red over green. |
| `Tree_Young` | One slim stem with the light green of new growth. |
| `Tree_Old` | A crooked forest giant, hung with lianas. |

They fill the jungle slot with the fan and clump palms, so it has eight variants.

**How a canopy is built.** The skeleton is a recursive branch grower. Every fork tip, and a point
partway along each last fork, carries a puff. A puff is a low, dark, closed blob with a fringe of
diamond leaves rooted inside it and pointing out and down. The blob fills the gaps, so the sky
never shows through. The leaves make the silhouette.

Every foliage vertex carries a custom normal. It is a blend of "away from this puff" and "away from
the canopy's centre", not the face normal. Under the stylised shader's toon terminator a crown
therefore shades as a few soft lit masses, not thousands of flickering facets.

Foliage is one-sided and culls back faces. Its slot is named `Foliage`, not `Leaves`, so
`TexturedMaterial` leaves culling on, and `Sway` now gives `Foliage` the leaf flutter. The leaf
budget is whatever the wood leaves under 6.3k triangles, split across puffs by area.

**Three meshes per FBX:**

- `<name>`: the trunk, the limbs and the canopy.
- `<name>_Deco`: buttresses, surface roots, lianas and aerial roots. It is drawn on LOD0 only.
  `ArtLibrary.Trunk` skips it, because a fig's aerial roots stand metres from its trunk and a
  capsule round them would be an invisible wall.
- `<name>_Far`: the same skeleton, down to the trunk and the limbs. Each puff is its bare blob at
  full size, painted from the darker half of the leaf ramp so the LOD swap does not flash brighter.
  It is 430 to 1.2k triangles.

The rain tree and the flame tree are wider than they are tall, so their catalogue rows are not
`upright`. They take the pack's axis from the other four.

`-treeTest` passes 63/0 on island and 60/0 on island2. Measured trunks are 0.4 to 1.8 m across.


## Rocks, logs and stumps modelled in Blender (#248)

`tools/art/rocks.py` replaces the Kenney and Quaternius rocks, logs and stumps on the ground slot of
`IslandFlora` with eleven own models in `Art/Models/Rocks`, two textures (`Wood.png`, `Stone.png`,
the column-ramp sheets of the palms) and two materials (`EWYF_Rocks_Wood`, `EWYF_Rocks_Stone`).

- **Rocks** are an icosphere (3 subdivisions near, 2 far) pushed by a few lumps and then chiselled
  by 12 to 16 random planes, with flat normals so every cut reads as a facet. Faces that look up
  past a threshold take a moss column; the rest pick granite, warm, basalt or sandstone. A
  cluster is three of them. 320 triangles each near, 80 far.
- **Logs** are rings along a slightly bent spine with bark faces that turn to moss by a ragged
  per-face threshold, an end-grain disc painted in rings at the sawn end and a short jagged cap at
  the broken one. The mossy log carries six mushrooms. Stumps add surface roots.
- Every part draws from its own `random.Random(rng.random())`, so near and far LOD agree.

Catalogue rows keep their names (`Boulder`, `Log`, `Stump`, ...) so the greybox builder and the
terrain keep working; six new rows (`RockTall`, `RockMossy`, `RockCluster`, `RockSand`, plus
`LogMossy`, `StumpBroken`) widen the ground slot to eleven. `ArtVisual` caps rise to 1 000 for a
rock (the cluster is 960) and 700 for a log. `-treeTest` passes on both islands.


## Undergrowth and flowers modelled in Blender (#246)

`tools/art/plants.py` replaces the four Quaternius plants of the bush slot with fourteen of our own
in `Art/Models/Plants`, all on one material, `Leaves` (two-sided, fluttering), painted from one
16-column ramp sheet (`Textures/Leaves.png`): three greens, two broad-leaf columns with painted veins,
the bromeliad's green-to-red and magenta, the croton's red-to-gold, six petal colours, the heliconia
bract and a stem.

| Id | Model | What it is |
|---|---|---|
| Bush, Hibiscus, Croton | Plant_Bush, _Hibiscus, _Croton | trees.py's canopy, small: dark hulls under diamond leaves; red flowers; red-gold leaves |
| Fern, FernTall | Plant_Fern, _FernTall | arching fronds of paired leaflets, fiddleheads in the middle |
| Leafy | Plant_ElephantEar | heart-shaped blades on long petioles |
| Bromeliad | Plant_Bromeliad | channelled strap leaves, magenta heart, a flower cone |
| Heliconia, Strelitzia | Plant_Heliconia, _Strelitzia | paddle leaves on stalks; lobster-claw bracts; orange and blue crane flowers |
| Flowers, FlowersDaisy, FlowersLily, FlowersSpike | Flowers_Wild, _Daisy, _Lily, _Spike | flower clumps over grass blades: mixed, white, trumpet lilies, lupins |
| GrassTuft | Plant_Grass | tall blades and seed heads |

Two primitives make almost everything: `strap`, a leaf along a drooping arc whose midrib is raised or
sunk by `fold`, and `diamond`, two triangles on a spine, which is a leaflet, a petal or a bract.
Normals are bent off one point low in each plant, as the tree canopies are, so a fern shades as a
mound. The bush borrows `trees.hull` and `trees.leaves` by pointing their UV and slot globals at
this sheet.

Near meshes are 200 to 770 triangles, far meshes 35 to 225. The Quaternius plants had no far mesh
(900/900, 288/288, 360/360, 755/755), and the island scatters about nine thousand five hundred of
them, so past the LOD distance the undergrowth now costs a fifth of what it did. All fourteen stay
non-solid (radius 0).


## Highland pines modelled in Blender (#248)

`tools/art/pines.py` replaces Quaternius's four pines with five of ours in `Art/Models/Pines`
(`Pine_Classic`, `_Wide`, `_Tall`, `_Sparse`, `_Young`; catalogue ids `Pine` ... `PineYoung`, the
last one new in the highland slot). Two materials: `PineBark` (plated bark, three columns) and
`PineLeaves` (two-sided, fluttering: five needle ramps and two cone shades).

A pine is a tapering trunk and whorls of boughs up it. Each bough is `plants.strap` with every other
row pulled in, so one strip of triangles has the jagged edge of needle tufts; every other bough gets
a shorter, lighter one laid over it. Inside each whorl a dark cone fills what would otherwise be sky.
Normals point out of the trunk axis and up, so the tree shades as one cone. The lowest boughs' sag is
clamped so they sweep down to the ground and not through it. The sparse pine skips whorls (bare
stubs) and browns one in fourteen.

2 200 to 3 900 triangles near, 300 to 540 far; the Quaternius pines were 1 600 to 3 900 with no far
mesh at all.

## Slot cabinets and symbols modelled in Blender, reels that roll (#252)

The five cabinets were boxes in the palette's colours, and their symbols were primitives:
spheres for fruit, a rotated cube for a gem, and a seven of two bars that read backwards from
the front. Everything visible on a slot now comes from `tools/art/slots.py`.

### The models

```
blender -b --factory-startup -P tools/art/slots.py -- <absolute path>/Assets/_Project/Art/Casino/Models
```

It writes the following files:

- **`SlotSymbols.fbx`** holds 58 meshes named `Sym_<Look>`. Each symbol is built from a few
  spheres, lathes, extruded outlines and tubes. Every part gets an inverted-hull outline and a
  white gloss blob.
- **`SlotCabinets.fbx`** holds `Cab_<Kind>` plus three bulb groups for each game, named
  `Cab_<Kind>_Bulbs0..2`, with every third bulb in each group.
- **`Textures/Symbols.png`** is one 32-column ramp sheet. Each part is painted dark at its foot
  and light at its top, which gives the symbols the candy gradient that slot reels have.

The symbols are fitted to one unit, and `SlotFactory` scales them by the cell. The cabinets are
1:1 and share the shared measurements:

| Measurement | Value |
|---|---|
| Deck top (where the buttons sit) | 0.94 |
| Reel window centre | 1.5, sized to the game's grid |
| Depth behind the origin | at most 0.33, because the wall is 10 cm away |

Each cabinet has its own theme:

| Game | Body | Topper and details |
|---|---|---|
| Sevens | Red and gold | An arched marquee |
| Volcano | Basalt | A lava cone, with lava running down the corners |
| Reef | Teal | A brass porthole holding a starfish, with coral up the sides |
| Fruit | Striped pink | A fruit-stand awning under a heap of fruit |
| Lagoon | Bamboo | A thatched hut roof under a marlin |

Each cabinet has 5.8k to 7.2k triangles plus about 33 bulbs. The biggest symbol has 1.4k
triangles, counting its outline shell.

All of it, symbols and cabinets together, wears **one** material: the old `SlotAtlas.mat` with
the ramp sheet in place of the 8x8 colour atlas. `SlotAtlas.png` and `SlotMeshes.asset` are
deleted. The bulbs add one glowing material, `SlotBulbOn` (`StyleLook.Glowing`), which
`LookTest` counts like the flame. A bulb that is off wears the atlas, so its cream colour is
the unlit glass.

`SlotFactory.Build` imports both files as meshes only:

- no materials;
- the file's own normals;
- `globalScale` 1.

The bodies are the models. What a player bumps into is two collider-only boxes. The buttons
stay as palette cubes, each still its own NetworkObject.

The factory checks the seven before it saves anything. Its foot must sit at +x, which is the
viewer's left on a cabinet that faces +z. Its frontmost vertices, the gloss, must sit high at
+x. If the seven reads wrong, the batch exits with code 1.

### The reels

`SlotMachine` draws everything locally, as before. Only the timing moved into the arithmetic.

**Spin.** Each reel kicks back up a third of a cell, then runs at 15 cells a second. The cells
ride a drum: rows away from the middle roll back, foreshorten and stretch with the blur. A cell
that leaves the bottom of the window wraps to the top with the next symbol from a fixed strip
per reel. The strip is a hash of the column and the slot, the same on every peer.

**Land.** A reel lands by dropping its final symbols in from 0.6 cells above, overshooting home
on an ease-out-back, and settling within 0.32 s while the drum curve flattens. Reels land from
40% to 100% of the spin time. Spin time is `min(1.5 s, 62% of the frame)`, up from
`min(1.1 s, 60%)`.

**Win.** Winners dance with a growth beat, a hop and a wag, out of step from reel to reel, and
come forward of the glass. Everything else shrinks to 80% so the winners read. The tumble games
still burst their winners in the last quarter before the cascade.

**Anticipation.**

- `SlotMath.Scatter(kind)` gives each game's scatter and how many it takes:

  | Game | Scatter | Needed |
  |---|---|---|
  | Sevens | Star | 3 |
  | Volcano | Peak | 4 |
  | Fruit | Sun | 4 |
  | Reef | Chest | 3 |
  | Lagoon | Hook | 3 |

- `SlotMath.Tease(kind, frame)` returns the first reel after the reels that already show all but
  one of the needed scatters.
- `Spin` adds `TeaseSeconds` (1.2 s) to every such drop. The time is the arithmetic's, not the
  screen's, because the server pays at the sum of the frames.
- On screen, the held reels slow to a crawl and shiver. They land a slow beat apart, on a lower,
  louder thunk, after a rising whirr. Meanwhile the scatters already down throb in front.
- The harness measures the share of drops that are teased: 0.3% on Volcano and Fruit, which need
  four scatters, up to 4.6% on Sevens. Win totals are unchanged, so the golden-seed checks still hold.

**Lights.** The bulbs chase slowly at idle, at 12 steps a second while spinning and at 24 on a
held reel. After a win they all flash together, for 2.5 s or for 5 s on a win of 10x or more.
Materials are swapped only when the pattern moves on.

### Checks (`-slotTest`)

- Each cabinet wears `Cab_<Kind>`, only `Sym_*` symbols, and three bulb groups.
- During every spin some cell leaves its place, and after it every cell is home.
- The bulbs changed at least six times over the spins.
- For each game, the teased share of 4,000 seeds' drops is between 0.1% and 8%. Each teased drop
  holds the teased reels at least `TeaseSeconds` longer.

Rebuild the cabinets with `slots.py`, then `SlotFactory.Build`. The prefabs keep their GUIDs,
so the POIs need no rebake.

---

## Data-driven content

**Every piece of content that is not geometry is a ScriptableObject.**

`WeaponDef`, `ItemDef`, `UpgradeDef`, `VehicleUpgradeDef`, `BuffDef`, `FishDef`, recipes, POI entries,
shop inventories.
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

All three sit on the one framework shipped in #57 — seat definitions, enter/exit interaction, rider
attachment and driver ownership transfer, all validated by the host. Vehicle-versus-ragdoll collisions
launch players with force proportional to impact speed; this is a required feature, not a side
effect.

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
