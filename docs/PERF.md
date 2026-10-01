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
