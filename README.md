# TSP Puzzle Finder

A local C# program that generates Euclidean Travelling Salesman puzzles with 8–15 nodes, solves them exactly, and saves the most deceptive candidates in SQLite. It can also create one combined Unity-compatible `puzzles.json` database with a balanced set of puzzles from 9 through 15 nodes.

## What “difficult by eye” means

The program gives a high difficulty score to a puzzle when:

- its second-best route is only slightly longer than the optimum;
- several routes are within the chosen near-optimal percentage; and
- those routes use different edges, rather than merely reversing the same tour.

Candidate layouts include clusters, perturbed rings, paired corridors, and uniform random points. All coordinates are normalized to a 0–100 playing area.

## Install and run

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), open a terminal in this folder, then run:

```powershell
dotnet restore
dotnet run -- generate --attempts 500 --keep 25
dotnet run -- list
dotnet run -- show --id 1
dotnet run -- export --id 1 --out puzzle-1.json
```

## Generate the Unity puzzle database

This command tests each node count from 9 through 15 separately and keeps five qualifying puzzles at every level:

```powershell
dotnet run -c Release -- generate-unity --attempts-per-node 1000 --keep-per-node 5 --out puzzles.json
```

The generated file has the exact structure expected by `TspPuzzleLoader`: a top-level `puzzles` array, with `id`, `nodes`, and the closed `optimalPath` for every puzzle.

By default, the Unity generator rejects a candidate when:

- its second-best route is less than 1% longer than the optimum; or
- any pair of nodes is closer than 10% of the optimal route's average edge length.

It also rejects visually obvious puzzles when:

- the layout is narrow or elongated (bounding-box aspect ratio below 0.65);
- fewer than 25% of the nodes are inside the convex hull; or
- a simple nearest-neighbor route is less than 3% worse than the optimum.

The earlier two-chain corridor layout style has been removed.

You can change those filters with `--min-gap-percent`, `--min-spacing-percent`, `--min-aspect-ratio`, `--min-interior-percent`, and `--min-nearest-gap-percent`. Increase `--attempts-per-node` if the program cannot find the requested number of qualifying puzzles for a particular level.

For a more thorough search:

```powershell
dotnet run -c Release -- generate --attempts 5000 --keep 100 --alternatives 20 --seed 12345
```

Use the same `--seed` to reproduce a search. Add `--db another-name.db` to any command to select a different database.

## Database

The database is created automatically as `tsp-puzzles.db` and contains:

- `puzzles`: difficulty metrics and generation details;
- `nodes`: X/Y coordinates;
- `routes`: the exact optimum and retained near-optimal tours.

The JSON export is designed to be easy to import into a Unity game. Node `A` (index 0) is the fixed start used by the exact solver; the completed tour returns to A.

## Important performance note

The solver is an exact, K-best Held–Karp dynamic program. Fifteen-node searches are practical, but generating thousands of candidates can take time. Start with 100–500 attempts to check the output, then use a Release build for larger searches.
