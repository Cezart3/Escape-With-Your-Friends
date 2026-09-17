# Shipping on Steam

Everything for M9 that does not need money, an account or a person: the upload script (#87), the
content declarations (#89), the store copy (#88) and the release checklist (#93). What each of these
still waits on is stated at the top of its section.

---

## Uploading a build (#87)

`tools/steam/` holds the whole of it:

| File | What |
|---|---|
| `app_build.vdf` | The SteamPipe build script. Placeholders, no ids. |
| `depot_content.vdf` | One content depot: the Windows player, minus `.pdb`, Burst debug folders and logs. |
| `upload.ps1` | Fills the placeholders from environment variables and runs `steamcmd` with no prompt. |

```powershell
$env:EWYF_APPID = "480"      # the real id once the Steam Direct fee clears (#85)
$env:EWYF_DEPOT = "481"
$env:EWYF_USER  = "the build account"
$env:EWYF_BRANCH = "beta"    # empty uploads without setting anything live

"/d/Unity/Editors/6000.3.23f1/Editor/Unity.exe" -batchmode -quit -projectPath "D:\Proiecte\JocStupid" `
  -logFile "D:\Builds\release.log" -executeMethod EscapeWithYourFriends.EditorTools.BuildTool.PerformBuild `
  -buildOutput "D:\Builds\EWYF-release"

pwsh tools/steam/upload.ps1 -Content D:\Builds\EWYF-release -Description "0.1.0 first beta"
```

**Nothing is ever set live by the script.** `SetLive` points at a beta branch, and promoting it is a
click somebody makes after running that build. A pipeline that can publish to everybody in one
command eventually will.

**Steam Guard** needs one interactive login on the machine that uploads (`steamcmd +login <user>`,
then the emailed code). After that the account is remembered and the script runs unattended, which
is #87's acceptance criterion. It cannot be run here: it needs the app id, so the issue stays open
until #85 clears.

**Launch options** to set on the partner site, once there is an app:

| Executable | Arguments | Description |
|---|---|---|
| `EscapeWithYourFriends.exe` | *(none)* | Play |

The game needs no flags. Everything the harness uses is a command-line switch that a player never
sees, and the demo is a separate build made with `BuildTool -demo` (#90), not a flag.

---

## Content descriptors and age rating (#89)

Checked against what the code actually does, not against what the design document hoped for. Steam
asks for these as free text plus checkboxes; this is the text.

**Violence.** Comedic and cartoonish, but constant. Players punch each other, taser each other, run
each other over, shoot firearms at animals and at hostile natives, and die. Bodies go limp and are
thrown around. There is no blood, no dismemberment and no gore anywhere in the project - the whole
joke is physical slapstick, and a body that is "dead" is a prop your friends drag to a machine that
brings it back.

- **Steam descriptor:** *Frequent Violence or Gore* → **no**. *Some Violence* → **yes**, described as
  cartoon violence between players and against wildlife and hostile NPCs.

**Alcohol.** There is an NPC who trades drinks for buffs, and drinking blurs the screen
(`DrunkVision`) while making the player tougher. It is a trade-off, it is played for laughs, and it
is never required to finish the game.

- **Steam descriptor:** *references to alcohol* → **yes**.

**Gambling.** There is a roulette table with chips (`Casino/RouletteWheel.cs`, `Economy/Wallet.cs`).
Chips are bought with in-game money earned by playing, the wheel's result is decided by the host's
`System.Random`, and that is the end of it. There is **no** way to buy chips or in-game money with
real money, no way to cash anything out, no loot boxes, no paid randomised items, and no
microtransactions of any kind - the project has no purchasing code at all.

- **Steam descriptor:** *gambling themes with virtual currency* → **yes**; *real-money gambling* →
  **no**. This is the distinction Valve bans games over, so it is worth restating in the store
  description in plain words.

**Adult content:** none. No nudity, no sexual content, no drugs beyond the alcohol above.

**Expected rating:** teen-ish. Steam passes these answers to the regional bodies; nothing else here
needs doing. **Verifiable now**, which is why this section ships before the app exists - the only
thing missing is somebody ticking the boxes on the partner site.

---

## Store page copy (#88)

Capsules, screenshots and the trailer need the art pass and a person with a capture key. The words do
not, and they are the part that gets rewritten most.

**Short description** (under 300 characters):

> Four friends, one island, and a plane that does not work yet. Punch each other, fish, gamble your
> savings away, run your friends over, and haul their bodies to the machine that brings them back.
> Co-op survival where the physics are the punchline.

**Long description**, in the order Valve's template wants it:

> **You are stranded. Your friends are the problem.**
>
> Escape With Your Friends is a four-player co-op survival game about getting off an island, and
> about how much slower that goes when everybody can pick everybody else up and throw them.
>
> **Survive.** Hunger, thirst, stamina and the night. Fish, hunt, craft, and keep a shared chest
> nobody respects.
>
> **Earn.** Sell what you catch, buy weapons and upgrades, and pay for the boat that takes you to the
> second island - the one that shoots back.
>
> **Lose it all.** There is a roulette table. The chips are worthless everywhere except here, and
> here they are everything. Nobody has ever left the table richer than the person who never sat down.
>
> **Die stupidly.** Get punched, tasered, run over, dropped from a height, or abducted by the locals
> and hung up in their village. Your friends have to come and get you.
>
> **Escape.** Find the plane's parts, carry them home, put it back together, learn to fly it badly,
> and go back for the person who was here before you.

**Tags**, in priority order: Co-op, Multiplayer, Survival, Comedy, Physics, Open World, Third
Person, Funny, Online Co-Op, Crafting.

**A line the review needs to see**, because the casino will be looked at: *"All gambling in this game
uses virtual chips earned by playing. There are no purchases of any kind, and nothing can be cashed
out."*

---

## Release checklist (#93)

In order. Most of it is waiting, which is why it starts early.

1. **Steam Direct fee paid** (#85) and the partner account through verification. Up to 30 days.
2. **Tax interview and banking done** (#86). Nothing can be sold until this clears.
3. **App id issued**, depots created, launch option set as above (#87).
4. **Store page filled in** (#88) and submitted. Capsules, five screenshots, a trailer that leads
   with somebody being run over.
5. **Content descriptors answered** (#89), copied from the section above.
6. **A build uploaded to a beta branch** and played end to end by four people who did not write it.
7. **Demo build** (#90) uploaded as its own app, if the demo is going in Next Fest (#91).
8. **Achievements configured** on the partner site with the ids already in the code (#92):
   `RAN_OVER_FRIEND`, `LOST_IT_ALL`, `DIED_TEN`, `FIRST_TRY`.
9. **Pricing set**, including regional pricing. Take Valve's suggested regional numbers rather than
   converting by hand; a wrong number in one region is a refund queue.
10. **Build review passed.** Valve runs the build once. Failures are usually a missing launch option
    or a crash on a machine with no Steam client running - test that case, because it is the one
    nobody tries.
11. **The 30-day wait.** The store page must be public for 30 days before release. Announce the date
    at the start of it, not the end.
12. **Launch.** Then the first patch, which there always is.
