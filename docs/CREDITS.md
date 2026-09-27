# Credits

Third-party art in *Escape With Your Friends*. Everything listed is **CC0 1.0** (public domain
dedication): usable in a commercial, closed-source game, no attribution required. It is credited
anyway, because these people made the game look like something, and because this file is the
record of where every model came from.

Rules for adding to this file are in `docs/ART-PLAN.md` §3. No NC, no ND, no share-alike. CC-BY
only with a line here. Anything whose licence could not be read goes in the plan as a risk,
never straight into the game.

## In the game

| Asset | Author | Source | Licence | Used for |
|---|---|---|---|---|
| Stylized Nature MegaKit (Standard) | Quaternius | https://quaternius.com/packs/stylizednaturemegakit.html | CC0 1.0 | Jungle trees, pines, bushes, ferns, grass, flowers, rocks |
| Pirate Kit | Quaternius | https://quaternius.com/packs/piratekit.html | CC0 1.0 | Palms, the shipwreck, cave cliffs, barrels, chests, buckets, bottles, the wrecked boat |
| Pirate Kit | Kenney | https://kenney.nl/assets/pirate-kit | CC0 1.0 | Thatched roofs, crates |
| Survival Kit | Kenney | https://kenney.nl/assets/survival-kit | CC0 1.0 | Logs, stumps, floors and walls, the camp, tools |
| Furniture Kit | Kenney | https://kenney.nl/assets/furniture-kit | CC0 1.0 | The bar, bar stools, the roulette table |
| Holiday Kit | Kenney | https://kenney.nl/assets/holiday-kit | CC0 1.0 | The casino's string lights, lanterns |

Kenney: www.kenney.nl · https://www.kenney.nl/donate

## Planned (not in the build yet)

| Asset | Author | Source | Licence | For |
|---|---|---|---|---|
| Watercraft Pack | Kenney | https://kenney.nl/assets/watercraft-pack | CC0 1.0 | The boat |
| Car Kit | Kenney | https://kenney.nl/assets/car-kit | CC0 1.0 | The buggy |
| Weapon Pack | Kenney | https://kenney.nl/assets/weapon-pack | CC0 1.0 | Firearms, knife |
| Universal Base Characters | Quaternius | https://quaternius.com/packs/universalbasecharacters.html | CC0 1.0 | Players, natives, NPCs |
| Universal Animation Library | Quaternius | https://quaternius.com/packs/universalanimationlibrary.html | CC0 1.0 | Every human animation |

Quaternius: https://quaternius.com · https://www.patreon.com/quaternius

## Where each licence was read

Recorded because the build machine could not open the authors' own pages (see `ART-PLAN.md` §1):

- **Kenney kits:** the `License.txt` shipped inside each kit, as mirrored at
  `github.com/shorepine/kenney` (commit `3694c68`). It reads: *"License: (Creative Commons Zero,
  CC0) … You can use this content for personal, educational, and commercial purposes."*
  `ArtExtract` copies each kit's own `License.txt` next to its models, so the copy in
  `Assets/_Project/Art/ThirdParty/` is the one from the zip you downloaded.
- **Universal Animation Library:** the CC0 1.0 legal code shipped with the free edition, as
  re-uploaded at `github.com/J-Ponzo/gltf-universal-animation-library`.
- **Universal Base Characters:** the Quaternius pack page, as quoted by search results. **Check the
  zip's licence file when downloading.**
  Its geometry (65-joint UE skeleton, 14 318 / 15 060 triangles, three materials, no hair) was
  read from a glTF re-export of the Superhero Male and Female found in a public game repository;
  that repository carried no licence for them, so this is evidence of shape, not of terms.
  `CharacterArt` refuses a zip without a licence file and copies the one it finds to
  `Assets/_Project/Art/ThirdParty/Quaternius/UniversalBaseCharacters/License.txt`.
- **Quaternius Stylized Nature MegaKit**: `License_Standard.txt` inside the zip Cezar downloaded
  (2026-09-27): "CC0 1.0 Universal (CC0 1.0) Public Domain Dedication".
- **Quaternius Pirate Kit**: the download (a Google Drive folder, zipped by Drive) carries no licence
  file. CC0 is what the pack's page states; `ArtExtract` writes that claim into the extracted
  `License.txt` and logs a warning. **Check the page** before shipping.
