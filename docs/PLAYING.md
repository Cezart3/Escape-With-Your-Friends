# Playing it yourself

Everything in this project has been verified headless, which means a machine has confirmed it works
and nobody has ever looked at it. This page is for looking at it.

Expect greybox. Every character is a capsule, every building is a box, the buggy is four cylinders
and a crate. That is deliberate — art is M8 — so judge the *feel*, not the looks.

---

## Starting it

1. Open the project in Unity Hub (`D:\Proiecte\JocStupid`, Unity **6000.3.23f1**).
2. Open `Assets/_Project/Scenes/Bootstrap.unity`.
3. Press **Play**.

That is the whole procedure. In the editor the game auto-hosts, loads the island, and drops you at
base camp with a body. No menu, no lobby, no command line — a shipped build waits for a Steam lobby,
the editor does not.

First load takes a while: the terrain, the navmesh and the POIs are all baked assets, but the island
is a kilometre square and the first frame has to light it.

### A second player

There is no in-editor split screen. To see networking actually work you need a build:

```
"/d/Unity/Editors/6000.3.23f1/Editor/Unity.exe" -quit -batchmode -nographics \
  -projectPath "D:/Proiecte/JocStupid" \
  -executeMethod EscapeWithYourFriends.EditorTools.BuildTool.PerformBuild \
  -scriptingBackend mono -logFile build.log
```

Then press Play in the editor (that is the host) and launch the build as a client:

```
BuildOutput/EscapeWithYourFriends.exe -client -address 127.0.0.1 -port 7777 -playerKey me:second
```

Two windows, one island. `-playerKey` is what the server uses to recognise you across a reconnect;
any string works, it just has to differ between the two.

---

## Controls

| Key | Verb |
|---|---|
| **W A S D** | Move |
| **Mouse** | Look |
| **Shift** | Sprint |
| **Ctrl** | Crouch — and the handbrake while driving |
| **Space** | Jump |
| **E** | Interact: pick someone up, open a chest, use the shop, get in or out of a vehicle |
| **Left mouse** | Attack — punch, swing, or fire |
| **Right mouse** | Taser |
| **F** | Use what is in your hand: eat, drink, bandage |
| **R** | Reload |
| **G** | Tap to drop, hold to throw |
| **1–5 / scroll** | Hotbar |
| **Tab** or **I** | Inventory |

Driving reuses the movement keys: **W/S** throttle and reverse, **A/D** steer, **Ctrl** handbrake.

---

## A tour, in the order things are worth trying

**At base camp**, where you spawn:

- The **buggy** is parked about eight metres away. Press **E** to get in — the first person in drives.
  W to go. It is tuned to be grippy and easy to roll; getting it onto its roof is the point, and it
  picks itself up after three seconds upside down. It burns fuel, dents when you hit things, and
  both are fixed where it stands: hold a **fuel can** or a piece of **scrap metal** and press E next
  to it. The trader also sells four parts — engine, tyres, armour, tank — fitted the same way, by
  holding one and pressing E.
- The **Revive Machine** is the big noisy box. It is what brings a dead friend back, and it wants the
  corpse carried to it.
- The **crafting bench** turns what you pick up into things worth having.
- Two **chests** store what you cannot carry. The carry limit is 40 kg and it is enforced.

**Around the island:**

- The **shop** is its own building, a walk from camp. It sells weapons, ammo and food and buys back
  whatever you drag in at half price. You start with some money.
- **Animals** wander and can be hunted. Meat needs cooking; raw meat will make you ill.
- **Fish** are in the sea and the lake. Equip a rod, aim at water, left mouse, then fight the bar.
  Fishing is currently the best money in the game, at about 41 coins a minute.
- The **casino**, the **wreck** and the **cave** are placed and enterable; the casino is an empty
  building until M6.
- The **native village** is roughly 300 m from spawn and is hostile. Natives see you further by day
  and hear you further at night. They carry loot worth the walk; a village native carries more than a
  wandering one.
- Going down near natives gets you **abducted** — dragged back to the village and hung on a hook. A
  friend has to cut you loose. This is the single funniest thing in the build right now.

**With a second player:**

- Punch them. Taser them. Pick up whatever is left and throw it.
- Carry a downed friend to the Revive Machine. Or into the buggy's cargo bed and drive them there.
- Four people in one buggy is the thing #57 was built to survive; if anybody gets flung out, that is
  a bug worth reporting with the log.

---

## What is not there yet

No menu, no Steam lobby UI in the editor path, no casino, no boat, no plane, no second island, no
art, no music. Damage numbers, walk speed and prices are all first-draft numbers that exist to be
argued with.

The honest question this build can answer is the one M1 set: **is it funny for twenty minutes with
nothing else in the game?** If it is not, no amount of content later fixes that.

---

## If it does not start

- **Compile errors in the console** — run the batchmode compile and read the log rather than
  squinting at the editor: `Unity.exe -quit -batchmode -projectPath . -logFile compile.log`.
- **You spawn under the world** — there is a guard that returns anything below y = -30 to a spawn
  point, so wait a second rather than restarting.
- **No island, just a grey plane** — the Bootstrap scene's `GameSceneLoader` loads `Island` by
  default; check it was not left on `Arena` or `none`.
- **Nothing responds to input** — the editor Game view needs focus, and the cursor is locked on
  Play. Escape releases it.
