# Story and campaign — "The Ash Isles"

Decided 2026-10-03:

- **Premise:** expedition and mystery.
- **Structure:** three islands, one act each.
- **Length:** about ten hours for a normal four-player run, about two hours for a speedrun.
- **Art:** generated in Blender.

This page is the authority on what happens and in what order. Systems are documented in
`ARCHITECTURE.md`; this page only says why they exist.

## Tone

An adventure film that four idiots wandered into. The mystery is played straight: a missing
grandfather, a lost plane, a cult and a volcano. The players are not straight, and the game never
punishes that. The comedy comes from the physics and from the friends. The story only has to be
good enough that a stream chat asks "wait, what was on the last page?".

## The premise

Four friends clear out the attic of Radu Voinea, a bush pilot who vanished in 1957. In a tin box
they find his flight journal, missing its last pages, and a hand-drawn chart of three islands
marked **INSULELE DE CENUȘĂ**, the Ash Isles. The last legible line reads: *"The idol is real. The
mountain is awake. I am going back for the others."*

They charter the cheapest boat on the coast, the *Marisol*, captained by a man called Bogdan who
does not ask questions. On the third night a storm tears the boat apart on the reef of the first
island.

## Act 1 — Wreck Island (about 3.5 h)

You wake on the beach beside what is left of the *Marisol*. Bogdan is gone. The island belongs to
three groups:

- **Pike**, a hermit castaway of thirty years who trades from a hut. He is the shop, and he knew
  Radu.
- **The Lucky Gull**, a smugglers' den on the hill. It is the casino. The smugglers came for the
  same legend and stayed for the cards.
- **The Ash Cult's outer village**. These are the natives. They take prisoners for the mountain.

**The goal: make the *Marisol*'s lifeboat seaworthy.** This used to be "buy four parts". It becomes
four parts, each from a different kind of play:

| Part | Where | Play |
|---|---|---|
| Hull planks | Crafted at the bench | Gather wood, craft |
| Outboard engine | Pike, expensive | Money: fishing, hunting, selling, the casino |
| Fuel | Stored in the cult village | Combat or stealth |
| The chart page | The cave, at the end of a dark crawl | Exploration, a light puzzle |

You cannot leave without the chart page, because the reef only opens on the route Radu drew. The
page is also the first journal page. Collecting it plays the first flashback.

## Act 2 — Temple Isle (about 4 h)

The lifeboat makes the crossing. On the beachhead is **Radu's plane**, a 1950s bush plane, wrecked
but whole enough to repair. The island is the cult's home: a temple in the jungle and the chief's
village on the cliffs.

**The goal: repair the plane and find Bogdan.** There are three parts, each in its own place:

- **Engine:** in the temple, behind traps and a guardian fight.
- **Wing:** at the bottom of the ravine where it fell. Hauling it out takes all four players and a
  vehicle.
- **Propeller:** hung as a trophy in the chief's hall. You take it by raid or by trade.

**Bogdan** is the one you left behind. He is the cult's prisoner, held for the mountain. Freed, he
follows you, and he is the only one who has flown anything. The next three journal pages are
spread over the island. The last one says the idol is in the volcano on the third island, and
that Radu went there alone.

## Act 3 — The Mountain (about 2.5 h)

You fly Radu's plane to the third island. You do not land well. The island is ash, obsidian and
lava light, and the dark is the danger here. The cult's inner sanctum runs up inside the volcano.

**The goal: reach the summit, take the idol, get out.** Taking the idol wakes the mountain, and the
eruption turns the island into a timed escape. Lava floods the low paths, ash cuts how far you can
see, and the plane waits on a ridge that is shrinking. It is the loudest ten minutes of the game.

Inside the summit you find **Radu's last camp** and the final journal page.

## Ending

The plane makes it off as the mountain goes. A short epilogue at home shows the idol on a shelf in
the attic, beside the tin box.

Post-credits: a radio crackles in the box with a 1957 call sign. The open thread is deliberate,
and it is the hook for a sequel or DLC.

## Cutscenes

Each one is in-engine, skippable, and short, under 40 s. Every player must have arrived before it
plays, and it plays for everyone at once.

| # | Cutscene | Trigger | Exists |
|---|---|---|---|
| 1 | The attic: the journal, the chart | New game | no |
| 2 | Storm, the *Marisol* breaks on the reef | After 1 | no |
| 3 | Waking on the beach | Spawn | yes (`IslandIntro`) |
| 4 | Flashback: Radu in this cave, 1957 | The first journal page | no |
| 5 | The crossing, the second island rising | Lifeboat launched | partly (`StoryBeat`) |
| 6 | Radu's plane on the beachhead | First sight of it | no |
| 7 | Bogdan freed | Freed | no |
| 8 | Take-off, the volcano on the horizon | Plane airborne | no |
| 9 | The idol lifted, the mountain wakes | Idol taken | no |
| 10 | The escape flight, the epilogue, the radio | Plane off Island 3 | no |

## Where the ten hours come from

None of it should come from grind. Most of the time is short, varied goals, each of which
teaches or uses one system.

| Block | Normal | Speedrun |
|---|---|---|
| Act 1: survival basics, base, first money | 1.0 h | 15 min |
| Act 1: the four lifeboat parts | 2.5 h | 35 min |
| Act 2: plane parts, Bogdan, journal pages | 4.0 h | 45 min |
| Act 3: ascent, idol, eruption | 2.5 h | 25 min |
| **Total** | **10 h** | **2 h** |

The speedrun is two hours because:

- the money part has a skill route: the casino and fishing reward knowledge over time spent;
- the journal pages after the first are optional;
- the ravine wing can be taken by a clever vehicle route instead of the long haul.

A normal run spends its extra time on fishing, hunting, building up the base, vehicle upgrades,
the casino, the optional pages, and failing at the fights.

Pacing is checked, not guessed. The playthrough bot already walks the critical path, so it
measures the speedrun floor. A playtest with real friends measures the normal time.
