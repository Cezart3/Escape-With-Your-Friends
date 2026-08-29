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

The `NetworkManager` object carries four components. FishNet creates the rest of its sub-managers
itself in `Awake`, so only these are worth pinning down in the scene:

| Component | Why it is set explicitly |
|---|---|
| `NetworkManager` | Holds `DefaultPrefabObjects`. FishNet can find it by scanning the project, but assigning it writes the reference into the scene where a diff can see it |
| `TimeManager` | Tick rate 30. FishNet already defaults to 30; leaving it implicit means a package update could quietly change how fast the whole game simulates |
| `Tugboat` | Plain UDP on port 7770, 8 clients. Placeholder — shipping runs on the Steam transport (#13) |
| `NetworkBootstrap` | Starts the connection from command-line arguments |

**Why Tugboat when shipping is Steam.** The Steam transport needs a running Steam client and an app
id, which makes it useless headless. Tugboat needs neither, so four instances can be launched from one
script. Both are FishNet transports behind the same interface, so nothing above this layer knows which
one is loaded.

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

The island is **generated from a seed, never hand-sculpted.** An editor script produces the heightmap
from domain-warped noise with an island falloff mask, then derives the splatmap from height and slope
rules (sand near sea level, grass inland, rock on steep slopes). Vegetation is placed by biome mask
using Terrain tree and detail instancing with LOD groups.

This matters for two reasons: the same seed reproduces the island byte-for-byte, and regenerating it
is a single terminal command. No manual editor work sits between an idea and a testable world.

Island 1 is 1024×1024 world units (~1 km²). Island 2 is 512×512 — smaller, denser, meaner.

Points of interest are placed by a `POISpawner` reading a config list. Adding a landmark is a data
edit, not an editor session.

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
