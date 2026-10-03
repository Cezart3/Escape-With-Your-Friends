# Performance

The perf route's results (#239), newest at the bottom. One table per tier and GPU: nine spots, each
held for five seconds with VSync and the frame cap off, at 1920x1080 fullscreen. Times are
milliseconds per frame. Batches, SetPass calls, triangles and shadow casters are frame averages off
the render profiler counters.

Run it with `tools/perf-route.sh` after a development build. It does every tier on the RTX 4060 and
the Radeon 760M (the min-spec proxy) and appends here. See docs/ARCHITECTURE.md, "The perf route
and beauty shots (#239)".

**Targets** (epic #238):

| Tier | Target | Measured on |
|---|---|---|
| Low | p95 <= 9.1 ms (110 fps) | Radeon 760M |
| Medium | p95 <= 16.7 ms (60 fps) | Radeon 760M |
| High | p95 <= 7.1 ms (140 fps) | RTX 4060 Laptop |
| Ultra | p95 <= 16.7 ms (60 fps) | RTX 4060 Laptop |

A look PR that makes Medium p95 on the 760M worse by more than 1 ms says why in its body.

## Baseline, before the look pass

All four targets are met. Two things stand out:

- **The 4060 at Low and Medium is CPU-bound at about 4 ms.** It is barely faster than the 760M
  there. The CPU is the floor on every tier.
- **High's cost is batches and triangles at range.** The cliff overlook and the plane on High draw
  8 500-10 700 batches and 19-20 M triangles; Medium draws 2 100 and 4.5 M. The view distance and
  the shadow cascades are what make the difference, and they are where the optimisation pass starts.

### 2026-10-01 22:05, Medium on 'Medium', 9d12636

NVIDIA GeForce RTX 4060 Laptop GPU (7956MB), 1920x1080 fullscreen, quality 'Medium', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 4.0 | 4.7 | 7.1 | 2419 | 78 | 4520k | 31 |
| wreck | 3.3 | 4.1 | 5.2 | 1061 | 56 | 2596k | 54 |
| jungle (cave) | 3.7 | 4.4 | 7.0 | 1167 | 84 | 1964k | 113 |
| village with natives | 3.3 | 4.0 | 4.9 | 464 | 66 | 460k | 123 |
| trader | 3.7 | 4.3 | 5.2 | 1985 | 55 | 2456k | 305 |
| casino floor | 3.3 | 3.9 | 4.6 | 822 | 52 | 541k | 242 |
| casino, every slot spinning | 3.1 | 3.7 | 5.4 | 537 | 47 | 743k | 112 |
| cliff overlook | 3.8 | 4.4 | 5.9 | 2102 | 64 | 4484k | 28 |
| the plane | 4.0 | 4.5 | 5.5 | 2407 | 92 | 3776k | 306 |

### 2026-10-01 22:06, High on 'Very High', 9d12636

NVIDIA GeForce RTX 4060 Laptop GPU (7956MB), 1920x1080 fullscreen, quality 'Very High', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 5.4 | 6.0 | 21.6 | 8332 | 114 | 13880k | 335 |
| wreck | 3.9 | 4.4 | 7.7 | 4294 | 81 | 10798k | 113 |
| jungle (cave) | 4.2 | 4.8 | 6.1 | 4672 | 131 | 5397k | 418 |
| village with natives | 3.8 | 4.3 | 5.5 | 1838 | 101 | 1333k | 209 |
| trader | 4.0 | 4.6 | 5.8 | 4665 | 84 | 5492k | 671 |
| casino floor | 3.6 | 4.2 | 5.5 | 1840 | 76 | 1401k | 491 |
| casino, every slot spinning | 3.3 | 3.9 | 5.9 | 957 | 61 | 1011k | 234 |
| cliff overlook | 6.0 | 6.5 | 7.1 | 8543 | 119 | 19262k | 138 |
| the plane | 6.1 | 6.7 | 7.4 | 10680 | 141 | 20076k | 614 |

### 2026-10-01 22:08, Ultra on 'Ultra', 9d12636

NVIDIA GeForce RTX 4060 Laptop GPU (7956MB), 1920x1080 fullscreen, quality 'Ultra', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 6.6 | 7.3 | 7.9 | 10071 | 125 | 15135k | 690 |
| wreck | 3.9 | 4.4 | 5.5 | 4680 | 86 | 11022k | 164 |
| jungle (cave) | 4.6 | 5.1 | 6.9 | 6441 | 143 | 6231k | 510 |
| village with natives | 3.9 | 4.4 | 5.3 | 2075 | 99 | 1496k | 283 |
| trader | 4.1 | 4.7 | 5.5 | 5302 | 89 | 5966k | 813 |
| casino floor | 3.6 | 4.2 | 5.3 | 1811 | 75 | 1460k | 452 |
| casino, every slot spinning | 3.4 | 4.0 | 5.3 | 1398 | 67 | 1786k | 263 |
| cliff overlook | 7.3 | 7.9 | 8.4 | 10940 | 117 | 21899k | 318 |
| the plane | 7.5 | 8.1 | 8.6 | 13251 | 146 | 25453k | 640 |

### 2026-10-01 22:09, Low on 'Low', 9d12636

AMD Radeon(TM) 760M (15969MB), 1920x1080 fullscreen, quality 'Low', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 5.1 | 6.3 | 7.2 | 1270 | 50 | 2545k | 10 |
| wreck | 3.8 | 4.8 | 6.3 | 222 | 24 | 467k | 40 |
| jungle (cave) | 4.5 | 5.7 | 6.2 | 580 | 57 | 1086k | 67 |
| village with natives | 4.2 | 5.3 | 5.8 | 289 | 43 | 401k | 89 |
| trader | 4.8 | 6.5 | 8.0 | 1414 | 30 | 1954k | 59 |
| casino floor | 4.0 | 4.9 | 7.2 | 691 | 29 | 479k | 206 |
| casino, every slot spinning | 4.0 | 5.1 | 5.6 | 365 | 22 | 356k | 57 |
| cliff overlook | 4.5 | 5.9 | 7.1 | 732 | 41 | 1316k | 11 |
| the plane | 4.6 | 5.6 | 6.2 | 1171 | 64 | 1564k | 103 |

### 2026-10-01 22:10, Medium on 'Medium', 9d12636

AMD Radeon(TM) 760M (15969MB), 1920x1080 fullscreen, quality 'Medium', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 7.9 | 8.1 | 9.5 | 2415 | 74 | 4520k | 29 |
| wreck | 4.9 | 6.5 | 7.9 | 1059 | 53 | 2596k | 52 |
| jungle (cave) | 5.9 | 6.6 | 8.7 | 1172 | 83 | 2001k | 118 |
| village with natives | 4.7 | 5.9 | 6.4 | 465 | 61 | 478k | 121 |
| trader | 6.8 | 7.0 | 7.6 | 1985 | 55 | 2463k | 305 |
| casino floor | 4.8 | 6.1 | 7.1 | 822 | 52 | 550k | 242 |
| casino, every slot spinning | 4.6 | 5.9 | 6.4 | 544 | 47 | 749k | 117 |
| cliff overlook | 10.1 | 10.3 | 10.8 | 2105 | 67 | 4484k | 28 |
| the plane | 6.4 | 6.9 | 7.8 | 2406 | 91 | 3778k | 306 |

### 2026-10-01 22:11, High on 'Very High', 9d12636

AMD Radeon(TM) 760M (15969MB), 1920x1080 fullscreen, quality 'Very High', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 14.1 | 14.4 | 14.8 | 8321 | 126 | 13874k | 332 |
| wreck | 7.9 | 8.1 | 8.6 | 4297 | 82 | 10809k | 119 |
| jungle (cave) | 9.6 | 9.8 | 10.1 | 4658 | 128 | 5348k | 408 |
| village with natives | 5.8 | 6.8 | 8.1 | 1826 | 99 | 1325k | 200 |
| trader | 10.5 | 10.7 | 11.0 | 4654 | 84 | 5477k | 671 |
| casino floor | 6.1 | 6.4 | 7.8 | 1828 | 76 | 1393k | 491 |
| casino, every slot spinning | 5.6 | 5.9 | 6.8 | 1279 | 67 | 1636k | 196 |
| cliff overlook | 18.8 | 19.0 | 19.3 | 8532 | 112 | 19239k | 140 |
| the plane | 13.4 | 13.6 | 13.9 | 10677 | 140 | 20065k | 613 |

## Wind and ambient life (#244)

On Medium on the 760M, p95 moves by -0.1 ms on average across the route and by +0.1 ms at the worst spot, against a budget of +0.5 ms.
The CPU-bound 4060 at Medium pays about +0.3 ms for the particle systems. On High, the worst p95 is 6.8 ms, against a target of 7.1 ms.

### 2026-10-01 22:24, Medium on 'Medium', 17daa68

NVIDIA GeForce RTX 4060 Laptop GPU (7956MB), 1920x1080 fullscreen, quality 'Medium', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 4.3 | 4.8 | 7.6 | 2418 | 86 | 4520k | 29 |
| wreck | 3.7 | 4.2 | 5.7 | 1061 | 59 | 2597k | 53 |
| jungle (cave) | 4.0 | 4.6 | 6.9 | 1173 | 90 | 1994k | 117 |
| village with natives | 3.8 | 4.3 | 5.2 | 475 | 66 | 519k | 135 |
| trader | 4.0 | 4.6 | 5.2 | 1986 | 64 | 2458k | 305 |
| casino floor | 3.7 | 4.3 | 5.1 | 824 | 59 | 546k | 242 |
| casino, every slot spinning | 3.5 | 4.1 | 6.8 | 497 | 52 | 464k | 109 |
| cliff overlook | 4.1 | 4.7 | 6.3 | 2105 | 70 | 4484k | 28 |
| the plane | 4.4 | 5.0 | 6.6 | 2414 | 97 | 3781k | 306 |

### 2026-10-01 22:25, High on 'Very High', 17daa68

NVIDIA GeForce RTX 4060 Laptop GPU (7956MB), 1920x1080 fullscreen, quality 'Very High', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 5.4 | 6.1 | 7.3 | 8339 | 146 | 13887k | 335 |
| wreck | 4.1 | 4.8 | 6.5 | 4297 | 102 | 10797k | 115 |
| jungle (cave) | 4.5 | 5.2 | 5.9 | 4634 | 158 | 5355k | 400 |
| village with natives | 4.0 | 4.6 | 6.3 | 1800 | 105 | 1313k | 191 |
| trader | 4.3 | 4.9 | 6.3 | 4655 | 106 | 5468k | 671 |
| casino floor | 3.9 | 4.5 | 6.4 | 1831 | 93 | 1382k | 491 |
| casino, every slot spinning | 3.6 | 4.2 | 6.0 | 1049 | 76 | 1057k | 249 |
| cliff overlook | 6.0 | 6.6 | 7.3 | 8561 | 126 | 19255k | 153 |
| the plane | 6.1 | 6.8 | 7.8 | 10681 | 162 | 20065k | 614 |

### 2026-10-01 22:26, Ultra on 'Ultra', 17daa68

NVIDIA GeForce RTX 4060 Laptop GPU (7956MB), 1920x1080 fullscreen, quality 'Ultra', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 6.6 | 7.3 | 7.7 | 10085 | 166 | 15125k | 696 |
| wreck | 4.2 | 4.8 | 6.1 | 4682 | 101 | 11039k | 164 |
| jungle (cave) | 4.9 | 5.6 | 6.6 | 6407 | 167 | 6217k | 480 |
| village with natives | 3.9 | 4.5 | 5.6 | 2024 | 111 | 1437k | 260 |
| trader | 4.2 | 4.8 | 6.0 | 5305 | 111 | 5967k | 813 |
| casino floor | 3.8 | 4.4 | 5.4 | 1814 | 92 | 1464k | 452 |
| casino, every slot spinning | 3.6 | 4.2 | 5.1 | 1402 | 89 | 1798k | 270 |
| cliff overlook | 7.3 | 7.9 | 8.4 | 10943 | 130 | 21900k | 331 |
| the plane | 7.5 | 8.2 | 8.7 | 13270 | 170 | 25484k | 644 |

### 2026-10-01 22:27, Low on 'Low', 17daa68

AMD Radeon(TM) 760M (15969MB), 1920x1080 fullscreen, quality 'Low', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 5.0 | 6.7 | 7.6 | 1267 | 53 | 2545k | 10 |
| wreck | 4.1 | 5.0 | 5.6 | 236 | 39 | 467k | 40 |
| jungle (cave) | 4.6 | 5.7 | 6.1 | 572 | 53 | 1086k | 67 |
| village with natives | 4.2 | 5.3 | 5.7 | 282 | 39 | 401k | 87 |
| trader | 4.8 | 6.4 | 7.0 | 1426 | 38 | 1951k | 59 |
| casino floor | 4.1 | 4.9 | 5.6 | 704 | 33 | 478k | 206 |
| casino, every slot spinning | 3.9 | 4.8 | 5.2 | 391 | 31 | 634k | 55 |
| cliff overlook | 4.5 | 5.8 | 6.5 | 737 | 46 | 1316k | 11 |
| the plane | 4.7 | 5.6 | 6.2 | 1165 | 67 | 1557k | 103 |

### 2026-10-01 22:29, Medium on 'Medium', 17daa68

AMD Radeon(TM) 760M (15969MB), 1920x1080 fullscreen, quality 'Medium', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 7.5 | 7.8 | 9.3 | 2416 | 84 | 4520k | 29 |
| wreck | 4.9 | 6.3 | 6.9 | 1059 | 57 | 2596k | 52 |
| jungle (cave) | 5.9 | 6.7 | 24.0 | 1189 | 89 | 2008k | 128 |
| village with natives | 4.8 | 6.0 | 6.4 | 490 | 69 | 481k | 139 |
| trader | 6.7 | 6.9 | 9.1 | 1993 | 64 | 2464k | 305 |
| casino floor | 4.9 | 6.0 | 6.8 | 831 | 61 | 551k | 242 |
| casino, every slot spinning | 4.6 | 5.4 | 6.2 | 458 | 53 | 458k | 108 |
| cliff overlook | 9.9 | 10.1 | 10.5 | 2107 | 72 | 4485k | 28 |
| the plane | 6.3 | 7.0 | 7.8 | 2408 | 97 | 3775k | 306 |

### 2026-10-01 22:30, High on 'Very High', 17daa68

AMD Radeon(TM) 760M (15969MB), 1920x1080 fullscreen, quality 'Very High', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 13.7 | 14.0 | 68.5 | 8316 | 146 | 13874k | 328 |
| wreck | 7.6 | 7.9 | 8.3 | 4294 | 97 | 10798k | 115 |
| jungle (cave) | 9.7 | 9.9 | 10.3 | 4749 | 165 | 5425k | 457 |
| village with natives | 5.8 | 6.8 | 8.5 | 1828 | 104 | 1292k | 205 |
| trader | 10.4 | 10.5 | 10.8 | 4655 | 106 | 5476k | 671 |
| casino floor | 6.1 | 6.4 | 7.1 | 1829 | 93 | 1394k | 491 |
| casino, every slot spinning | 6.0 | 6.4 | 7.6 | 1049 | 76 | 1069k | 249 |
| cliff overlook | 18.7 | 18.9 | 19.4 | 8532 | 126 | 19244k | 138 |
| the plane | 13.2 | 13.3 | 13.8 | 10683 | 163 | 20067k | 614 |

### 2026-10-03 15:17, Medium on 'Medium', ead085b

NVIDIA GeForce RTX 4060 Laptop GPU (7956MB), 1920x1080 fullscreen, quality 'Medium', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 4.3 | 5.3 | 9.8 | 2998 | 78 | 1713k | 173 |
| wreck | 3.6 | 4.3 | 14.3 | 2084 | 50 | 753k | 27 |
| jungle (cave) | 3.8 | 4.4 | 5.3 | 1236 | 88 | 950k | 173 |
| village with natives | 3.1 | 3.5 | 4.5 | 317 | 53 | 302k | 52 |
| trader | 3.5 | 4.0 | 7.3 | 1726 | 63 | 1128k | 175 |
| base camp | 3.4 | 3.9 | 5.5 | 1546 | 61 | 789k | 65 |
| casino front | 3.2 | 3.9 | 6.5 | 833 | 61 | 876k | 215 |
| casino floor | 3.2 | 3.8 | 5.1 | 645 | 56 | 684k | 174 |
| casino tables | 3.4 | 4.0 | 5.7 | 1707 | 72 | 1487k | 316 |
| casino, every slot spinning | 3.0 | 3.5 | 5.7 | 734 | 57 | 730k | 164 |
| the buggy | 3.5 | 4.0 | 27.5 | 1586 | 63 | 704k | 68 |
| the boat | 3.9 | 4.5 | 23.7 | 2230 | 68 | 1069k | 32 |
| the barman | 3.5 | 4.3 | 25.7 | 706 | 60 | 749k | 189 |
| the castaway | 4.2 | 5.0 | 34.0 | 2921 | 74 | 1597k | 108 |
| a native | 3.3 | 3.8 | 6.2 | 381 | 48 | 218k | 31 |
| a boar | 4.1 | 4.8 | 6.3 | 2914 | 80 | 1535k | 184 |
| a deer | 4.1 | 4.7 | 6.0 | 2925 | 69 | 1293k | 50 |
| a gull | 4.0 | 4.8 | 5.9 | 2459 | 71 | 1089k | 57 |
| a jaguar | 3.5 | 4.1 | 5.1 | 669 | 60 | 499k | 83 |
| a stag | 3.1 | 3.6 | 4.6 | 167 | 43 | 166k | 40 |
| cliff overlook | 4.0 | 4.5 | 6.8 | 2926 | 61 | 1244k | 19 |
| the volcano | 3.2 | 3.8 | 11.4 | 592 | 48 | 299k | 27 |
| the plane | 4.1 | 4.8 | 6.2 | 2775 | 77 | 1519k | 97 |

### 2026-10-03 15:20, High on 'Very High', ead085b

NVIDIA GeForce RTX 4060 Laptop GPU (7956MB), 1920x1080 fullscreen, quality 'Very High', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 4.9 | 5.8 | 8.2 | 9503 | 138 | 6630k | 1096 |
| wreck | 4.2 | 5.0 | 27.2 | 6518 | 83 | 2267k | 121 |
| jungle (cave) | 4.2 | 4.8 | 5.5 | 3929 | 150 | 2701k | 503 |
| village with natives | 4.3 | 9.9 | 26.4 | 1414 | 79 | 1172k | 247 |
| trader | 4.2 | 12.3 | 24.2 | 4788 | 102 | 4469k | 964 |
| base camp | 4.1 | 4.6 | 6.4 | 4326 | 94 | 2745k | 444 |
| casino front | 3.8 | 4.2 | 6.7 | 2028 | 91 | 2620k | 552 |
| casino floor | 3.6 | 4.2 | 5.2 | 1566 | 81 | 2074k | 454 |
| casino tables | 4.3 | 4.8 | 12.1 | 5998 | 124 | 6785k | 1722 |
| casino, every slot spinning | 3.8 | 5.1 | 8.3 | 1194 | 77 | 1653k | 356 |
| the buggy | 4.5 | 5.0 | 5.9 | 7179 | 112 | 3076k | 343 |
| the boat | 4.9 | 5.5 | 7.7 | 10206 | 124 | 3961k | 156 |
| the barman | 4.0 | 4.9 | 6.0 | 1842 | 86 | 2630k | 594 |
| the castaway | 5.4 | 6.4 | 7.6 | 8348 | 122 | 4979k | 574 |
| a native | 4.7 | 5.9 | 7.7 | 6837 | 131 | 3183k | 270 |
| a boar | 3.2 | 3.8 | 4.7 | 155 | 39 | 131k | 54 |
| a deer | 4.4 | 4.8 | 5.8 | 5818 | 132 | 4730k | 987 |
| a gull | 3.6 | 4.1 | 6.3 | 2354 | 66 | 929k | 75 |
| a jaguar | 3.7 | 6.5 | 13.2 | 877 | 79 | 333k | 83 |
| a stag | 5.0 | 5.8 | 7.2 | 11606 | 119 | 5016k | 483 |
| cliff overlook | 5.0 | 5.6 | 7.7 | 11135 | 113 | 3862k | 108 |
| the volcano | 3.5 | 3.9 | 5.7 | 1397 | 69 | 595k | 62 |
| the plane | 5.4 | 5.9 | 6.9 | 12987 | 134 | 6052k | 454 |

### 2026-10-03 15:22, Ultra on 'Ultra', ead085b

NVIDIA GeForce RTX 4060 Laptop GPU (7956MB), 1920x1080 fullscreen, quality 'Ultra', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 5.0 | 5.7 | 7.8 | 11373 | 145 | 8900k | 1678 |
| wreck | 4.2 | 4.8 | 24.7 | 6954 | 87 | 2678k | 217 |
| jungle (cave) | 4.4 | 4.9 | 5.7 | 5074 | 151 | 3137k | 590 |
| village with natives | 3.8 | 4.3 | 5.1 | 1804 | 82 | 1533k | 375 |
| trader | 4.2 | 4.7 | 5.3 | 5786 | 107 | 6214k | 1426 |
| base camp | 4.2 | 4.6 | 6.8 | 4922 | 98 | 3542k | 658 |
| casino front | 3.8 | 4.4 | 5.4 | 2284 | 94 | 3315k | 710 |
| casino floor | 3.6 | 4.2 | 5.0 | 1720 | 80 | 2466k | 546 |
| casino tables | 4.4 | 4.8 | 5.8 | 7457 | 135 | 9241k | 2512 |
| casino, every slot spinning | 3.5 | 4.0 | 6.2 | 1166 | 77 | 1705k | 380 |
| the buggy | 4.5 | 5.0 | 5.8 | 7875 | 113 | 3796k | 498 |
| the boat | 5.1 | 5.7 | 7.3 | 11638 | 126 | 4514k | 287 |
| the barman | 3.7 | 4.2 | 5.2 | 2080 | 84 | 3150k | 750 |
| the castaway | 4.9 | 5.5 | 6.8 | 9759 | 128 | 6720k | 1126 |
| a native | 3.6 | 4.1 | 4.8 | 1005 | 71 | 657k | 133 |
| a boar | 3.1 | 3.6 | 4.3 | 159 | 45 | 144k | 71 |
| a deer | 4.1 | 4.6 | 6.6 | 2915 | 106 | 1980k | 401 |
| a gull | 3.1 | 3.6 | 5.3 | 209 | 51 | 206k | 87 |
| a jaguar | 5.8 | 6.3 | 7.1 | 14343 | 154 | 7076k | 687 |
| a stag | 3.6 | 4.1 | 5.0 | 1408 | 97 | 1588k | 342 |
| cliff overlook | 5.3 | 5.9 | 6.7 | 13378 | 114 | 5515k | 636 |
| the volcano | 3.5 | 4.0 | 4.8 | 1640 | 72 | 727k | 123 |
| the plane | 5.8 | 6.5 | 7.7 | 15816 | 142 | 8226k | 901 |

### 2026-10-03 15:25, Low on 'Low', ead085b

AMD Radeon(TM) 760M (15969MB), 1920x1080 fullscreen, quality 'Low', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 4.7 | 5.6 | 15.4 | 1683 | 49 | 881k | 15 |
| wreck | 4.2 | 5.2 | 5.6 | 596 | 25 | 289k | 15 |
| jungle (cave) | 4.4 | 5.3 | 5.9 | 756 | 58 | 571k | 59 |
| village with natives | 4.2 | 5.2 | 5.8 | 185 | 25 | 206k | 25 |
| trader | 4.6 | 5.7 | 6.5 | 1333 | 39 | 800k | 41 |
| base camp | 4.7 | 5.6 | 6.9 | 1051 | 36 | 622k | 32 |
| casino front | 4.7 | 5.6 | 7.8 | 648 | 37 | 594k | 100 |
| casino floor | 4.7 | 5.6 | 7.9 | 443 | 32 | 373k | 54 |
| casino tables | 4.3 | 5.5 | 6.3 | 1057 | 41 | 705k | 69 |
| casino, every slot spinning | 4.3 | 5.3 | 6.4 | 502 | 33 | 372k | 41 |
| the buggy | 4.4 | 5.3 | 11.8 | 553 | 35 | 295k | 33 |
| the boat | 4.4 | 5.5 | 7.0 | 681 | 36 | 443k | 11 |
| the barman | 4.3 | 5.4 | 6.2 | 464 | 32 | 371k | 38 |
| the castaway | 4.7 | 5.7 | 6.3 | 1657 | 44 | 921k | 18 |
| a native | 4.5 | 5.6 | 6.3 | 902 | 31 | 402k | 23 |
| a boar | 5.0 | 6.1 | 6.9 | 1904 | 56 | 1108k | 49 |
| a deer | 4.2 | 5.2 | 5.8 | 600 | 33 | 299k | 12 |
| a gull | 4.6 | 5.5 | 6.6 | 1228 | 34 | 687k | 9 |
| a jaguar | 4.1 | 5.2 | 5.9 | 200 | 28 | 146k | 15 |
| a stag | 4.6 | 5.5 | 6.3 | 1079 | 46 | 782k | 22 |
| cliff overlook | 4.7 | 5.8 | 6.3 | 1503 | 33 | 775k | 5 |
| the volcano | 4.1 | 5.2 | 6.0 | 337 | 18 | 214k | 11 |
| the plane | 4.8 | 5.7 | 6.8 | 1385 | 45 | 880k | 23 |

### 2026-10-03 15:28, Medium on 'Medium', ead085b

AMD Radeon(TM) 760M (15969MB), 1920x1080 fullscreen, quality 'Medium', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 6.2 | 7.9 | 9.2 | 2996 | 78 | 1708k | 172 |
| wreck | 5.2 | 6.7 | 7.4 | 2084 | 50 | 753k | 27 |
| jungle (cave) | 5.3 | 6.7 | 7.2 | 1236 | 88 | 950k | 173 |
| village with natives | 5.0 | 6.4 | 7.2 | 322 | 53 | 323k | 57 |
| trader | 5.4 | 6.8 | 9.1 | 1725 | 63 | 1133k | 175 |
| base camp | 5.3 | 6.7 | 7.3 | 1548 | 61 | 802k | 66 |
| casino front | 5.3 | 6.2 | 7.9 | 833 | 61 | 883k | 215 |
| casino floor | 5.3 | 6.5 | 7.3 | 644 | 56 | 690k | 174 |
| casino tables | 5.7 | 6.6 | 7.6 | 1705 | 71 | 1480k | 316 |
| casino, every slot spinning | 5.1 | 6.1 | 7.0 | 734 | 57 | 731k | 164 |
| the buggy | 5.2 | 6.7 | 9.4 | 1588 | 63 | 710k | 69 |
| the boat | 5.5 | 7.1 | 7.4 | 2230 | 68 | 1069k | 32 |
| the barman | 5.6 | 6.5 | 7.6 | 706 | 60 | 755k | 189 |
| the castaway | 6.0 | 7.5 | 8.4 | 2920 | 74 | 1601k | 108 |
| a native | 4.8 | 6.1 | 6.5 | 858 | 60 | 420k | 27 |
| a boar | 4.9 | 6.1 | 6.5 | 1311 | 59 | 583k | 52 |
| a deer | 5.6 | 7.1 | 7.7 | 2881 | 86 | 1645k | 135 |
| a gull | 5.0 | 6.3 | 6.9 | 1517 | 54 | 613k | 38 |
| a jaguar | 4.6 | 5.7 | 6.4 | 173 | 41 | 121k | 33 |
| a stag | 4.9 | 6.3 | 6.8 | 1928 | 57 | 632k | 25 |
| cliff overlook | 6.0 | 7.8 | 8.1 | 2920 | 58 | 1216k | 16 |
| the volcano | 4.6 | 5.9 | 6.4 | 592 | 48 | 299k | 27 |
| the plane | 5.8 | 7.3 | 8.1 | 2774 | 76 | 1520k | 97 |

### 2026-10-03 15:31, High on 'Very High', ead085b

AMD Radeon(TM) 760M (15969MB), 1920x1080 fullscreen, quality 'Very High', vsync 0, AMD Ryzen 5 7640HS w/ Radeon 760M Graphics 

| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |
|---|---|---|---|---|---|---|---|
| spawn beach | 9.2 | 9.4 | 11.7 | 9503 | 138 | 6628k | 1095 |
| wreck | 6.5 | 8.3 | 9.0 | 6519 | 86 | 2269k | 122 |
| jungle (cave) | 7.0 | 7.8 | 9.3 | 3927 | 152 | 2715k | 504 |
| village with natives | 5.8 | 6.9 | 8.4 | 1418 | 78 | 1183k | 249 |
| trader | 7.3 | 7.9 | 9.8 | 4788 | 102 | 4473k | 964 |
| base camp | 6.7 | 7.4 | 9.9 | 4326 | 94 | 2753k | 445 |
| casino front | 7.2 | 7.5 | 8.8 | 2028 | 91 | 2624k | 552 |
| casino floor | 7.5 | 7.8 | 9.1 | 1568 | 81 | 2086k | 454 |
| casino tables | 9.2 | 9.5 | 10.6 | 6000 | 126 | 6789k | 1722 |
| casino, every slot spinning | 6.4 | 6.7 | 7.5 | 1607 | 83 | 1623k | 321 |
| the buggy | 6.9 | 7.6 | 9.8 | 7179 | 112 | 3076k | 343 |
| the boat | 7.5 | 8.2 | 9.7 | 10210 | 124 | 3971k | 158 |
| the barman | 8.0 | 8.3 | 9.4 | 1842 | 86 | 2637k | 594 |
| the castaway | 8.5 | 8.8 | 9.9 | 8352 | 122 | 4992k | 577 |
| a native | 6.1 | 7.0 | 8.1 | 2139 | 94 | 1731k | 383 |
| a boar | 7.6 | 8.0 | 9.5 | 8142 | 122 | 4430k | 392 |
| a deer | 7.6 | 8.9 | 10.2 | 12304 | 160 | 4994k | 300 |
| a gull | 8.5 | 9.0 | 10.9 | 11360 | 159 | 6192k | 861 |
| a jaguar | 9.3 | 9.7 | 10.7 | 12124 | 146 | 6801k | 665 |
| a stag | 5.8 | 6.7 | 7.9 | 1389 | 81 | 1400k | 331 |
| cliff overlook | 8.4 | 9.1 | 10.0 | 11124 | 107 | 3811k | 102 |
| the volcano | 5.1 | 6.6 | 7.4 | 1397 | 69 | 595k | 62 |
| the plane | 8.4 | 9.1 | 10.2 | 12985 | 135 | 6042k | 453 |
