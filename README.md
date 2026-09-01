# TSP Puzzle Finder

TSP Puzzle Finder is a .NET console program that generates, solves, evaluates, stores, and exports small Euclidean Traveling Salesperson Problem (TSP) puzzles.

Its primary purpose is to produce visually interesting puzzles for the Unity **TSP Puzzle Game**. Each puzzle contains 8–15 points on a normalized 2D board. The program finds the exact optimal tour, retains several of the best alternative tours, estimates the puzzle's difficulty, and can reject layouts that are too cramped, too obvious, or poorly suited to a mobile screen.

## What the program does

- Generates random point layouts using uniform, clustered, and perturbed-ring patterns.
- Normalizes coordinates to approximately `5`–`95` on each axis.
- Solves each puzzle exactly with a K-best Held–Karp dynamic-programming solver.
- Treats a route and its reverse as the same tour.
- Ranks puzzles using:
  - the percentage gap between the optimal and second-best routes;
  - the number of near-optimal alternatives;
  - how different the alternative routes are from the optimal route.
- Stores puzzles, nodes, routes, difficulty data, seeds, and generation metadata in SQLite.
- Lists and displays previously saved puzzles.
- Exports a detailed single-puzzle JSON file for inspection.
- Generates a balanced Unity JSON file containing puzzles for each requested node count.

All tours begin at node `0` (`A`). In Unity output, the optimal path also ends with `0`, explicitly closing the tour back to `A`.

## Requirements

- A current .NET SDK compatible with the project.
- The `Microsoft.Data.Sqlite` NuGet package.

Run commands from the directory containing the project file.

## Quick start for Unity

The most useful command for the Unity game is:

```bash
dotnet run -c Release -- generate-unity --attempts-per-node 1000 --keep-per-node 5 --out puzzles.json
```

By default, this attempts to create five puzzles for every node count from 9 through 15. It writes the selected puzzles to `puzzles.json` and also saves their full details in `tsp-puzzles.db`.

Copy the resulting JSON file to the Unity project as:

```text
Assets/Resources/puzzles.json
```

If too few puzzles pass the filters, increase `--attempts-per-node`.

## Commands

### `generate`

Generates a mixed set of candidates across a node-count range, calculates their difficulty, and saves the highest-ranked qualifying puzzles to SQLite.

```bash
dotnet run -- generate --attempts 2000 --keep 50 --seed 12345
```

Options:

| Option | Default | Meaning |
|---|---:|---|
| `--min-nodes` | `8` | Minimum nodes, from 8 to 15. |
| `--max-nodes` | `15` | Maximum nodes, from the selected minimum to 15. |
| `--attempts` | `500` | Total candidate layouts to test. |
| `--keep` | `25` | Highest-scoring qualifying candidates to save. |
| `--alternatives` | `12` | Number of distinct best routes retained by the exact solver. |
| `--near-percent` | `3` | Maximum error percentage for an alternative to count as near-optimal. |
| `--min-score` | `45` | Minimum difficulty score, from 0 to 100. |
| `--seed` | current tick count | Random seed. Supply a value to reproduce a run. |
| `--db` | `tsp-puzzles.db` | SQLite database filename or path. |

### `generate-unity`

Generates and filters candidates separately for every requested node count, keeps the most difficult qualifying puzzles for each count, saves them to SQLite, and writes a Unity-compatible JSON database.

```bash
dotnet run -c Release -- generate-unity \
  --min-nodes 9 \
  --max-nodes 15 \
  --attempts-per-node 5000 \
  --keep-per-node 10 \
  --out puzzles.json \
  --seed 12345
```

Options:

| Option | Default | Meaning |
|---|---:|---|
| `--min-nodes` | `9` | Minimum nodes, from 8 to 15. |
| `--max-nodes` | `15` | Maximum nodes, from the selected minimum to 15. |
| `--attempts-per-node` | `1000` | Candidate layouts tested for each node count. |
| `--keep-per-node` | `5` | Highest-scoring qualifying puzzles retained for each node count. |
| `--alternatives` | `12` | Number of distinct best routes retained by the exact solver. |
| `--min-gap-percent` | `1` | Minimum percentage difference between the optimal and second-best routes. |
| `--min-spacing-percent` | `10` | Minimum distance between any pair of nodes, as a percentage of the average optimal-tour edge length. |
| `--min-aspect-ratio` | `0.65` | Minimum short-side/long-side ratio of the point layout; rejects narrow layouts. |
| `--min-interior-percent` | `25` | Minimum percentage of nodes that must lie inside the convex hull. |
| `--min-nearest-gap-percent` | `3` | Minimum error of the nearest-neighbor tour relative to the optimum. |
| `--near-percent` | `3` | Maximum error percentage for an alternative to count as near-optimal. |
| `--min-score` | `0` | Minimum difficulty score, from 0 to 100. |
| `--seed` | current tick count | Random seed. Supply a value to reproduce a run. |
| `--out` | `puzzles.json` | Unity JSON output filename or path. |
| `--db` | `tsp-puzzles.db` | SQLite database filename or path. |

The Unity filters are intended to avoid:

- nodes that are too close together to select comfortably;
- long, thin layouts that display poorly;
- puzzles with too few interior points;
- puzzles whose second-best route is less than 1% worse than optimal;
- puzzles that are easily solved by repeatedly choosing the nearest unvisited node.

Setting a filter value to `0` disables that filter where supported.

### `list`

Lists saved puzzles in descending difficulty order.

```bash
dotnet run -- list --limit 50
```

The output includes the database ID, node count, difficulty score, second-best gap, near-optimal route count, and optimal route length.

### `show`

Displays one saved puzzle, its labeled coordinates, and up to ten retained routes.

```bash
dotnet run -- show --id 12
```

### `export`

Exports one saved puzzle with full analysis data, including coordinates and all retained routes.

```bash
dotnet run -- export --id 12 --out puzzle-12.json
```

This detailed export is different from the streamlined Unity database produced by `generate-unity`.

### `help`

Displays the program's built-in command summary.

```bash
dotnet run -- help
```

## Unity JSON format

`generate-unity` writes this structure:

```json
{
  "puzzles": [
    {
      "id": 1,
      "nodes": [
        { "x": 58.7, "y": 39.3 },
        { "x": 95.0, "y": 82.2 }
      ],
      "optimalPath": [0, 1, 0]
    }
  ]
}
```

- Coordinates are rounded to two decimal places.
- Node indexes correspond to labels `A`, `B`, `C`, and so on.
- `optimalPath` begins and ends at node `0` (`A`).
- The generated puzzle `id` is the ID assigned by the SQLite database.

## Difficulty score

The difficulty score ranges from 0 to 100 and combines three measurements:

1. **Second-best gap:** small but nonzero gaps receive more weight because the best alternative is harder to distinguish visually from the optimum.
2. **Near-optimal alternatives:** more alternatives within `--near-percent` increase the score.
3. **Route diversity:** alternatives that use substantially different edges increase the score.

The score is a ranking heuristic, not a guarantee of how difficult every player will find a puzzle. The additional `generate-unity` filters address mobile usability and obvious visual strategies separately.

## Database

The SQLite database is created automatically when the program starts. It contains:

- `puzzles`: summary statistics and generation metadata;
- `nodes`: each puzzle's coordinates;
- `routes`: the retained routes, lengths, ranks, and error percentages.

Generation commands append new puzzles; they do not replace previous rows. Use `--db` to keep different runs in separate database files.

## Exit codes

- `0`: command completed successfully.
- `1`: invalid input or another error occurred.
- `2`: `generate-unity` completed but found no qualifying puzzles.

