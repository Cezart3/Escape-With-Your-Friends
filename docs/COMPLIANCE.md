# Compliance checklist: gambling, alcohol, violence

**What this is for.** The game has a casino in it, and a Steam page that is wrong about a casino is
not a bug report — it is an app review. Valve bans real-money gambling outright, and getting the
store questionnaire wrong is grounds for taking the page down. This file is the list somebody signs
before the page goes live, and every line on it says *where the claim is enforced* rather than
asserting it, because a checklist nobody can check is a promise.

Written for #67. Re-read it whenever anything in `Casino/` or `Economy/` changes.

---

## 1. The money in this game is not money

| Claim | Where it is true | What proves it |
|---|---|---|
| Currency is earned in-game only. There is no way to buy it. | A `Wallet` starts at 500 and grows through `ServerAdd`, whose only non-test callers are the trader's buy-back and the revive machine's refund. Everything else players earn is *loot*, which becomes money only by being sold to the trader. There is no storefront, no entitlement check and no receipt anywhere in the project. | `-moneyTest` 29/0, `-economyTest` 27/0 |
| Chips are bought with in-game money and nothing else. | `Wallet.ServerBuyChips` is called from exactly one place: `Cashier.ServerInteract`, at the cage window, at 1:1. | `-chipsTest` 33/0 |
| Chips convert back to in-game money and nothing else. | `Wallet.ServerCashOut`, likewise called only from `Cashier`. The money it returns is the same in-game money the player already had. | `-chipsTest` 33/0 |
| Nothing outside the casino takes a payment in chips. | Every other counter in the game (`ShopCounter`, including #66's barman) spends `Wallet.Balance`. The barman was deliberately **not** given a chips price for this reason. | `-chipsTest`, `-drunkTest` 36/0 |
| Nothing leaves the game. No cash-out **to real money**, no trading, no marketplace, no Steam Inventory items. (Chips convert back to in-game money at the cage, which is a currency exchange inside a game and not a payout.) | `Wallet` is a `SyncVar` on a player object and is written to the save file. There is no Steam Inventory Service call, no `ISteamInventory`, no item schema, and no trade path between players' wallets. | grep: no `Steamworks.SteamInventory` reference exists |
| No paid loot boxes, and no unpaid ones either. | Nothing in the game is opened, unlocked or rolled for a price. Loot comes off bodies and out of containers, for free, and what is inside is decided by a drop table the player never pays to roll. | `-lootTest`, `-chestTest` |

**The one-sentence version for the questionnaire:** the casino is a minigame played with a
non-purchasable, non-transferable in-game token that can only be won or lost inside the game.

### What would break this

A future PR breaks compliance the moment it adds any of the following, and all three would need this
file rewritten and the store survey re-answered:

- any way to acquire money or chips with real currency, including a DLC that grants a starting purse;
- any way to move a wallet or a chip stack between accounts — trading, gifting, or a shared stash;
- any randomised reward behind a paid door.

Adding a second game in the casino is fine. Adding a price tag to the door is not.

---

## 2. Store page: what to tick

Answer Valve's questionnaire with these, and no others:

- **Does your game contain gambling with real money?** — **No.** This is the one that bans apps; the
  answer is no because none of the six rows above can be made true without new code.
- **Content descriptors** (Store page → Content survey):
  - **Gambling** — yes, "the game contains simulated gambling with an in-game currency". Roulette,
    a cage that sells chips, and a bet placed by aiming at a square on a table.
  - **Alcohol references** — yes. A barman sells grog; drinking it applies a visible drunk state with
    blurred vision and a lasting aim penalty. It is a mechanic rather than a background prop.
  - **Frequent violence** — yes. Firearms, melee weapons, ragdoll deaths, hunting animals, and
    hostile NPCs who fight back.
  - **Adult content / nudity / sexual content** — no.
  - **Drugs or tobacco** — no.
- **Mature content description** (the free-text box that appears once any descriptor is ticked):
  > This game contains simulated gambling (roulette played with an in-game token that cannot be
  > bought with real money and cannot be cashed out), alcohol use with in-game consequences, and
  > cartoon violence between players and against wildlife.

### Ratings, which are a separate thing

Steam does not rate games itself, but some storefront regions and some age-rating boards treat
simulated gambling and alcohol as rating inputs. Nothing here needs a board rating to ship on Steam
globally, and if a rating is ever sought (PEGI, USK, or a console port) these two descriptors are
the ones that will move it upward. Worth knowing before it is a surprise.

---

## 3. Sign-off

This checklist is accurate as of the casino being feature-complete through #66 — the cage, the
wheel, the board, the room and the barman.

- [ ] Re-read after the last casino change, before the store page goes live.
- [ ] Questionnaire answered as above.
- [ ] Mature content description pasted as above.

Signed: ____________________  Date: ____________
