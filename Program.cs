using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TspPuzzleFinder;

internal static class Program
{
    private const string DefaultDb = "tsp-puzzles.db";

    private static int Main(string[] args)
    {
        try
        {
            var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
            var options = Options.Parse(args.Skip(1));
            var dbPath = options.Get("db", DefaultDb);
            using var db = new PuzzleDatabase(dbPath);
            db.Initialize();

            return command switch
            {
                "generate" => Generate(db, options),
                "generate-unity" => GenerateUnity(db, options),
                "list" => List(db, options),
                "show" => Show(db, options),
                "export" => Export(db, options),
                "help" or "--help" or "-h" => Help(),
                _ => throw new ArgumentException($"Unknown command '{command}'. Run with 'help'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int Generate(PuzzleDatabase db, Options o)
    {
        int minNodes = o.GetInt("min-nodes", 8, 8, 15);
        int maxNodes = o.GetInt("max-nodes", 15, minNodes, 15);
        int attempts = o.GetInt("attempts", 500, 1, 1_000_000);
        int keep = o.GetInt("keep", 25, 1, attempts);
        int alternatives = o.GetInt("alternatives", 12, 2, 50);
        int seed = o.GetInt("seed", Environment.TickCount, int.MinValue, int.MaxValue);
        double nearPercent = o.GetDouble("near-percent", 3.0, 0.01, 25.0);
        double minScore = o.GetDouble("min-score", 45.0, 0, 100);

        Console.WriteLine($"Generating {attempts} candidates (seed {seed})...");
        var rng = new Random(seed);
        var best = new PriorityQueue<PuzzleCandidate, double>();

        for (int i = 0; i < attempts; i++)
        {
            int n = rng.Next(minNodes, maxNodes + 1);
            var points = CandidateGenerator.Create(n, rng);
            var solution = KBestHeldKarp.Solve(points, alternatives);
            var candidate = DifficultyAnalyzer.Analyze(points, solution, nearPercent, seed, i);

            if (candidate.DifficultyScore >= minScore)
            {
                best.Enqueue(candidate, candidate.DifficultyScore);
                if (best.Count > keep) best.Dequeue();
            }

            if ((i + 1) % Math.Max(1, attempts / 20) == 0)
                Console.Write($"\rProgress: {i + 1}/{attempts}; qualifying: {best.Count}   ");
        }

        Console.WriteLine();
        var selected = new List<PuzzleCandidate>();
        while (best.TryDequeue(out var candidate, out _)) selected.Add(candidate);
        selected.Reverse();

        foreach (var candidate in selected)
        {
            long id = db.Insert(candidate);
            Console.WriteLine($"Saved #{id}: {candidate.Points.Count} nodes, difficulty {candidate.DifficultyScore:F1}, " +
                              $"gap {candidate.SecondBestGapPercent:F3}%, near routes {candidate.NearOptimalCount}");
        }

        Console.WriteLine($"Saved {selected.Count} puzzles to {Path.GetFullPath(db.Path)}");
        return 0;
    }

    private static int GenerateUnity(PuzzleDatabase db, Options o)
    {
        int minNodes = o.GetInt("min-nodes", 9, 8, 15);
        int maxNodes = o.GetInt("max-nodes", 15, minNodes, 15);
        int attemptsPerNode = o.GetInt("attempts-per-node", 1000, 1, 1_000_000);
        int keepPerNode = o.GetInt("keep-per-node", 5, 1, attemptsPerNode);
        int alternatives = o.GetInt("alternatives", 12, 2, 50);
        int seed = o.GetInt("seed", Environment.TickCount, int.MinValue, int.MaxValue);
        double nearPercent = o.GetDouble("near-percent", 3.0, 0.01, 25.0);
        double minScore = o.GetDouble("min-score", 0.0, 0, 100);
        double minGapPercent = o.GetDouble("min-gap-percent", 1.0, 0, 100);
        double minSpacingPercent = o.GetDouble("min-spacing-percent", 10.0, 0, 100);
        double minAspectRatio = o.GetDouble("min-aspect-ratio", 0.65, 0, 1);
        double minInteriorPercent = o.GetDouble("min-interior-percent", 25.0, 0, 100);
        double minNearestNeighborGapPercent = o.GetDouble("min-nearest-gap-percent", 3.0, 0, 100);
        string outputFile = o.Get("out", "puzzles.json");

        Console.WriteLine($"Generating {keepPerNode} Unity puzzles for each node count {minNodes}-{maxNodes} (seed {seed})...");
        Console.WriteLine($"Filters: second-best gap >= {minGapPercent:F2}%; minimum pair spacing >= {minSpacingPercent:F2}% of average optimal edge.");
        Console.WriteLine($"Visual filters: aspect ratio >= {minAspectRatio:F2}; interior nodes >= {minInteriorPercent:F1}%; nearest-neighbor error >= {minNearestNeighborGapPercent:F1}%.");

        var rng = new Random(seed);
        var selectedByNodeCount = new SortedDictionary<int, List<PuzzleCandidate>>();

        for (int nodeCount = minNodes; nodeCount <= maxNodes; nodeCount++)
        {
            var best = new PriorityQueue<PuzzleCandidate, double>();
            int rejectedForSpacing = 0;
            int rejectedForGap = 0;
            int rejectedForScore = 0;
            int rejectedForShape = 0;
            int rejectedForInterior = 0;
            int rejectedForNearestNeighbor = 0;

            for (int attempt = 0; attempt < attemptsPerNode; attempt++)
            {
                var points = CandidateGenerator.Create(nodeCount, rng);

                if (!MeetsMinimumAspectRatio(points, minAspectRatio))
                {
                    rejectedForShape++;
                    continue;
                }

                if (!HasEnoughInteriorNodes(points, minInteriorPercent))
                {
                    rejectedForInterior++;
                    continue;
                }

                var solution = KBestHeldKarp.Solve(points, alternatives);

                if (!MeetsMinimumSpacing(points, solution[0].Length, minSpacingPercent))
                {
                    rejectedForSpacing++;
                    continue;
                }

                if (!MeetsNearestNeighborGap(points, solution[0].Length, minNearestNeighborGapPercent))
                {
                    rejectedForNearestNeighbor++;
                    continue;
                }

                var candidate = DifficultyAnalyzer.Analyze(points, solution, nearPercent, seed, attempt);

                if (candidate.SecondBestGapPercent < minGapPercent)
                {
                    rejectedForGap++;
                    continue;
                }

                if (candidate.DifficultyScore < minScore)
                {
                    rejectedForScore++;
                    continue;
                }

                best.Enqueue(candidate, candidate.DifficultyScore);
                if (best.Count > keepPerNode) best.Dequeue();

                if ((attempt + 1) % Math.Max(1, attemptsPerNode / 10) == 0)
                    Console.Write($"\r{nodeCount} nodes: {attempt + 1}/{attemptsPerNode}; retained {best.Count}   ");
            }

            Console.WriteLine();
            var selected = new List<PuzzleCandidate>();
            while (best.TryDequeue(out var candidate, out _)) selected.Add(candidate);
            selected.Reverse();
            selectedByNodeCount[nodeCount] = selected;

            Console.WriteLine($"{nodeCount} nodes: selected {selected.Count}/{keepPerNode}; rejected shape {rejectedForShape}, interior {rejectedForInterior}, spacing {rejectedForSpacing}, nearest {rejectedForNearestNeighbor}, gap {rejectedForGap}, score {rejectedForScore}.");
            if (selected.Count < keepPerNode)
                Console.WriteLine($"Warning: only {selected.Count} qualifying {nodeCount}-node puzzles were found. Increase --attempts-per-node if needed.");
        }

        var unityPuzzles = new List<object>();
        foreach (var pair in selectedByNodeCount)
        {
            foreach (var candidate in pair.Value)
            {
                long id = db.Insert(candidate);
                unityPuzzles.Add(new
                {
                    id,
                    nodes = candidate.Points.Select(point => new
                    {
                        x = Math.Round(point.X, 2),
                        y = Math.Round(point.Y, 2)
                    }),
                    optimalPath = candidate.Routes[0].Tour.Concat(new[] { 0 })
                });

                Console.WriteLine($"Saved #{id}: {candidate.Points.Count} nodes, difficulty {candidate.DifficultyScore:F1}, gap {candidate.SecondBestGapPercent:F3}%");
            }
        }

        var json = JsonSerializer.Serialize(
            new { puzzles = unityPuzzles },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputFile, json);

        Console.WriteLine($"Saved {unityPuzzles.Count} puzzles to {Path.GetFullPath(outputFile)}");
        Console.WriteLine($"Database: {Path.GetFullPath(db.Path)}");
        return unityPuzzles.Count > 0 ? 0 : 2;
    }

    private static bool MeetsMinimumSpacing(
        IReadOnlyList<Point2> points,
        double optimalRouteLength,
        double minimumSpacingPercent)
    {
        if (minimumSpacingPercent <= 0) return true;

        double averageOptimalEdgeLength = optimalRouteLength / points.Count;
        double requiredDistance = averageOptimalEdgeLength * minimumSpacingPercent / 100.0;

        for (int i = 0; i < points.Count; i++)
        for (int j = i + 1; j < points.Count; j++)
        {
            double dx = points[i].X - points[j].X;
            double dy = points[i].Y - points[j].Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < requiredDistance) return false;
        }

        return true;
    }

    private static bool MeetsMinimumAspectRatio(
        IReadOnlyList<Point2> points,
        double minimumAspectRatio)
    {
        if (minimumAspectRatio <= 0) return true;

        double width = points.Max(p => p.X) - points.Min(p => p.X);
        double height = points.Max(p => p.Y) - points.Min(p => p.Y);
        double longerDimension = Math.Max(width, height);
        if (longerDimension <= 1e-9) return false;

        return Math.Min(width, height) / longerDimension >= minimumAspectRatio;
    }

    private static bool HasEnoughInteriorNodes(
        IReadOnlyList<Point2> points,
        double minimumInteriorPercent)
    {
        int requiredInteriorNodes =
            (int)Math.Ceiling(points.Count * minimumInteriorPercent / 100.0);
        if (requiredInteriorNodes <= 0) return true;

        int hullNodeCount = ConvexHull(points).Count;
        int interiorNodeCount = points.Count - hullNodeCount;
        return interiorNodeCount >= requiredInteriorNodes;
    }

    private static List<Point2> ConvexHull(IReadOnlyList<Point2> points)
    {
        var sorted = points
            .Distinct()
            .OrderBy(p => p.X)
            .ThenBy(p => p.Y)
            .ToList();

        if (sorted.Count <= 1) return sorted;

        var lower = new List<Point2>();
        foreach (var point in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], point) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(point);
        }

        var upper = new List<Point2>();
        for (int i = sorted.Count - 1; i >= 0; i--)
        {
            var point = sorted[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], point) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static double Cross(Point2 origin, Point2 a, Point2 b) =>
        (a.X - origin.X) * (b.Y - origin.Y) -
        (a.Y - origin.Y) * (b.X - origin.X);

    private static bool MeetsNearestNeighborGap(
        IReadOnlyList<Point2> points,
        double optimalRouteLength,
        double minimumGapPercent)
    {
        if (minimumGapPercent <= 0) return true;

        bool[] visited = new bool[points.Count];
        int current = 0;
        visited[current] = true;
        double length = 0;

        for (int step = 1; step < points.Count; step++)
        {
            int nearest = -1;
            double nearestDistance = double.MaxValue;

            for (int candidate = 0; candidate < points.Count; candidate++)
            {
                if (visited[candidate]) continue;
                double distance = Distance(points[current], points[candidate]);
                if (distance < nearestDistance)
                {
                    nearest = candidate;
                    nearestDistance = distance;
                }
            }

            visited[nearest] = true;
            current = nearest;
            length += nearestDistance;
        }

        length += Distance(points[current], points[0]);
        double gapPercent = 100.0 * (length - optimalRouteLength) / optimalRouteLength;
        return gapPercent >= minimumGapPercent;
    }

    private static double Distance(Point2 a, Point2 b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static int List(PuzzleDatabase db, Options o)
    {
        int limit = o.GetInt("limit", 50, 1, 1000);
        Console.WriteLine("ID   Nodes  Difficulty  Gap %     Near  Optimal");
        foreach (var p in db.List(limit))
            Console.WriteLine($"{p.Id,-4} {p.NodeCount,-6} {p.Difficulty,10:F1}  {p.GapPercent,7:F3}  {p.NearCount,4}  {p.OptimalLength:F2}");
        return 0;
    }

    private static int Show(PuzzleDatabase db, Options o)
    {
        long id = o.RequiredLong("id");
        var p = db.Load(id) ?? throw new ArgumentException($"Puzzle #{id} not found.");
        Console.WriteLine($"Puzzle #{id}: {p.Points.Count} nodes; difficulty {p.DifficultyScore:F1}");
        Console.WriteLine($"Optimal: {p.Routes[0].Length:F3}   Gap: {p.SecondBestGapPercent:F3}%   Near-optimal: {p.NearOptimalCount}");
        Console.WriteLine("Nodes: " + string.Join(", ", p.Points.Select((x, i) => $"{Label(i)}({x.X:F1},{x.Y:F1})")));
        foreach (var r in p.Routes.Take(10))
            Console.WriteLine($"#{r.Rank,-2} {r.Length,9:F3}  +{r.GapPercent,6:F3}%  {FormatTour(r.Tour)}");
        return 0;
    }

    private static int Export(PuzzleDatabase db, Options o)
    {
        long id = o.RequiredLong("id");
        string file = o.Get("out", $"puzzle-{id}.json");
        var p = db.Load(id) ?? throw new ArgumentException($"Puzzle #{id} not found.");
        var json = JsonSerializer.Serialize(new
        {
            id,
            nodeCount = p.Points.Count,
            difficulty = p.DifficultyScore,
            secondBestGapPercent = p.SecondBestGapPercent,
            nearOptimalCount = p.NearOptimalCount,
            nodes = p.Points.Select((x, i) => new { index = i, label = Label(i), x = x.X, y = x.Y }),
            routes = p.Routes.Select(r => new { r.Rank, r.Length, r.GapPercent, r.Tour })
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(file, json);
        Console.WriteLine($"Exported {Path.GetFullPath(file)}");
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
TSP Puzzle Finder - discovers visually deceptive Euclidean TSP puzzles.

Commands:
  generate [options]  Generate, solve, rank, and save candidate puzzles
  generate-unity [options]  Generate balanced 9-15 node puzzles and write Unity JSON
  list [--limit N]    List the best saved puzzles
  show --id N         Show nodes and best routes for one puzzle
  export --id N [--out file.json]  Export a puzzle for a game

Generate options:
  --min-nodes 8       Minimum nodes (8-15)
  --max-nodes 15      Maximum nodes (8-15)
  --attempts 500      Candidates to test
  --keep 25           Highest-ranked qualifying candidates to save
  --alternatives 12   Distinct best routes retained by exact solver
  --near-percent 3    Defines a near-optimal route
  --min-score 45      Minimum difficulty score (0-100)
  --seed N            Reproducible random seed
  --db filename       SQLite database (default tsp-puzzles.db)

Generate-unity options:
  --min-nodes 9              Minimum nodes (8-15)
  --max-nodes 15             Maximum nodes (8-15)
  --attempts-per-node 1000   Candidates tested for every node count
  --keep-per-node 5          Qualifying puzzles retained for every node count
  --alternatives 12          Distinct best routes retained by exact solver
  --min-gap-percent 1        Reject puzzles with a smaller second-best gap
  --min-spacing-percent 10   Minimum pair spacing as % of average optimal edge
  --min-aspect-ratio 0.65    Reject narrow or elongated layouts
  --min-interior-percent 25  Minimum percentage of nodes inside convex hull
  --min-nearest-gap-percent 3  Required error from nearest-neighbor route
  --near-percent 3           Defines a near-optimal route
  --min-score 0              Minimum difficulty score (0-100)
  --seed N                   Reproducible random seed
  --out puzzles.json         Unity JSON output filename
  --db filename              SQLite database (default tsp-puzzles.db)

Example:
  dotnet run -- generate --attempts 2000 --keep 50 --seed 12345
  dotnet run -c Release -- generate-unity --attempts-per-node 1000 --keep-per-node 5 --out puzzles.json
""");
        return 0;
    }

    internal static string Label(int i) => i < 26 ? ((char)('A' + i)).ToString() : i.ToString(CultureInfo.InvariantCulture);
    private static string FormatTour(int[] tour) => string.Join("-", tour.Select(Label)) + "-A";
}

internal readonly record struct Point2(double X, double Y);
internal sealed record RouteResult(int Rank, double Length, int[] Tour, double GapPercent);
internal sealed record PuzzleCandidate(List<Point2> Points, List<RouteResult> Routes, double DifficultyScore,
    double SecondBestGapPercent, int NearOptimalCount, double AlternativeDiversity, int Seed, int CandidateNumber);

internal static class CandidateGenerator
{
    public static List<Point2> Create(int n, Random rng)
    {
        // Avoid corridor layouts: their two obvious chains are often easy to solve by eye.
        int style = rng.Next(3);
        var p = style switch
        {
            0 => Clustered(n, rng),
            1 => PerturbedRing(n, rng),
            _ => Uniform(n, rng)
        };
        Normalize(p);
        return p;
    }

    private static List<Point2> Uniform(int n, Random r) =>
        Enumerable.Range(0, n).Select(_ => new Point2(5 + 90 * r.NextDouble(), 5 + 90 * r.NextDouble())).ToList();

    private static List<Point2> Clustered(int n, Random r)
    {
        int clusters = r.Next(2, Math.Min(5, n));
        var centers = Uniform(clusters, r);
        return Enumerable.Range(0, n).Select(i =>
        {
            var c = centers[i % clusters];
            return new Point2(c.X + Gaussian(r) * 11, c.Y + Gaussian(r) * 11);
        }).ToList();
    }

    private static List<Point2> PerturbedRing(int n, Random r)
    {
        var p = new List<Point2>();
        double phase = r.NextDouble() * Math.PI * 2;
        for (int i = 0; i < n; i++)
        {
            double a = phase + 2 * Math.PI * i / n + Gaussian(r) * .12;
            double radius = 35 + Gaussian(r) * 9;
            p.Add(new Point2(50 + Math.Cos(a) * radius, 50 + Math.Sin(a) * radius));
        }
        return p;
    }

    private static double Gaussian(Random r) => Math.Sqrt(-2 * Math.Log(Math.Max(1e-12, r.NextDouble()))) * Math.Cos(2 * Math.PI * r.NextDouble());

    private static void Normalize(List<Point2> p)
    {
        double minX = p.Min(x => x.X), maxX = p.Max(x => x.X), minY = p.Min(x => x.Y), maxY = p.Max(x => x.Y);
        double scale = 90 / Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-9);
        for (int i = 0; i < p.Count; i++) p[i] = new Point2(5 + (p[i].X - minX) * scale, 5 + (p[i].Y - minY) * scale);
    }
}

internal static class KBestHeldKarp
{
    private sealed record Partial(double Cost, int[] Path);

    public static List<RouteResult> Solve(IReadOnlyList<Point2> points, int k)
    {
        int n = points.Count;
        var d = Distances(points);
        var states = new Dictionary<(int Mask, int Last), List<Partial>>();
        for (int j = 1; j < n; j++) states[(1 << j, j)] = [new Partial(d[0, j], [0, j])];

        for (int size = 2; size < n; size++)
        {
            foreach (int mask in MasksOfSize(n, size))
            {
                for (int last = 1; last < n; last++)
                {
                    if ((mask & (1 << last)) == 0) continue;
                    int prevMask = mask ^ (1 << last);
                    var candidates = new List<Partial>();
                    for (int prev = 1; prev < n; prev++)
                    {
                        if ((prevMask & (1 << prev)) == 0 || !states.TryGetValue((prevMask, prev), out var paths)) continue;
                        foreach (var path in paths)
                        {
                            var extended = new int[path.Path.Length + 1];
                            path.Path.CopyTo(extended, 0);
                            extended[^1] = last;
                            candidates.Add(new Partial(path.Cost + d[prev, last], extended));
                        }
                    }
                    if (candidates.Count > 0)
                        states[(mask, last)] = candidates.OrderBy(x => x.Cost).Take(k).ToList();
                }
            }
        }

        int full = ((1 << n) - 1) ^ 1;
        var tours = new List<Partial>();
        for (int last = 1; last < n; last++)
            if (states.TryGetValue((full, last), out var paths))
                tours.AddRange(paths.Select(x => new Partial(x.Cost + d[last, 0], x.Path)));

        // A tour and its reversal are equivalent; canonicalize before ranking.
        var distinct = tours.OrderBy(x => x.Cost)
            .GroupBy(x => CanonicalKey(x.Path))
            .Select(g => g.First()).Take(k).ToList();
        double optimum = distinct[0].Cost;
        return distinct.Select((x, i) => new RouteResult(i + 1, x.Cost, x.Path,
            100 * (x.Cost - optimum) / optimum)).ToList();
    }

    private static string CanonicalKey(int[] path)
    {
        string forward = string.Join(',', path.Skip(1));
        string reverse = string.Join(',', path.Skip(1).Reverse());
        return string.CompareOrdinal(forward, reverse) <= 0 ? forward : reverse;
    }

    private static IEnumerable<int> MasksOfSize(int n, int size)
    {
        int limit = 1 << n;
        for (int mask = 0; mask < limit; mask++)
            if ((mask & 1) == 0 && System.Numerics.BitOperations.PopCount((uint)mask) == size) yield return mask;
    }

    private static double[,] Distances(IReadOnlyList<Point2> p)
    {
        var d = new double[p.Count, p.Count];
        for (int i = 0; i < p.Count; i++)
        for (int j = i + 1; j < p.Count; j++)
        {
            double dx = p[i].X - p[j].X;
            double dy = p[i].Y - p[j].Y;
            d[i, j] = d[j, i] = Math.Sqrt(dx * dx + dy * dy);
        }
        return d;
    }
}

internal static class DifficultyAnalyzer
{
    public static PuzzleCandidate Analyze(List<Point2> points, List<RouteResult> routes, double nearPercent, int seed, int number)
    {
        double gap = routes.Count > 1 ? routes[1].GapPercent : 100;
        int near = routes.Count(r => r.GapPercent <= nearPercent) - 1;
        double diversity = routes.Skip(1).Where(r => r.GapPercent <= nearPercent)
            .Select(r => EdgeDifference(routes[0].Tour, r.Tour)).DefaultIfEmpty(0).Average();

        // Small-but-nonzero gaps are deceptive; diversity rewards genuinely different alternatives.
        double gapScore = 45 * Math.Exp(-gap / 1.5);
        double countScore = 30 * Math.Min(1, near / 5.0);
        double diversityScore = 25 * diversity;
        double score = Math.Clamp(gapScore + countScore + diversityScore, 0, 100);
        return new PuzzleCandidate(points, routes, score, gap, near, diversity, seed, number);
    }

    private static double EdgeDifference(int[] a, int[] b)
    {
        var ea = Edges(a); var eb = Edges(b);
        return 1.0 - ea.Intersect(eb).Count() / (double)a.Length;
    }

    private static HashSet<(int, int)> Edges(int[] t)
    {
        var e = new HashSet<(int, int)>();
        for (int i = 0; i < t.Length; i++)
        {
            int a = t[i], b = t[(i + 1) % t.Length];
            e.Add(a < b ? (a, b) : (b, a));
        }
        return e;
    }
}

internal sealed class PuzzleDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    public string Path { get; }
    public PuzzleDatabase(string path) { Path = path; _connection = new SqliteConnection($"Data Source={path}"); _connection.Open(); }
    public void Dispose() => _connection.Dispose();

    public void Initialize()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
        PRAGMA foreign_keys=ON;
        CREATE TABLE IF NOT EXISTS puzzles (
          id INTEGER PRIMARY KEY, created_utc TEXT NOT NULL, node_count INTEGER NOT NULL,
          difficulty REAL NOT NULL, optimal_length REAL NOT NULL, second_gap_percent REAL NOT NULL,
          near_count INTEGER NOT NULL, diversity REAL NOT NULL, seed INTEGER NOT NULL, candidate_number INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS nodes (
          puzzle_id INTEGER NOT NULL, node_index INTEGER NOT NULL, x REAL NOT NULL, y REAL NOT NULL,
          PRIMARY KEY(puzzle_id,node_index), FOREIGN KEY(puzzle_id) REFERENCES puzzles(id) ON DELETE CASCADE);
        CREATE TABLE IF NOT EXISTS routes (
          puzzle_id INTEGER NOT NULL, rank INTEGER NOT NULL, length REAL NOT NULL, gap_percent REAL NOT NULL, tour TEXT NOT NULL,
          PRIMARY KEY(puzzle_id,rank), FOREIGN KEY(puzzle_id) REFERENCES puzzles(id) ON DELETE CASCADE);
        CREATE INDEX IF NOT EXISTS ix_puzzles_difficulty ON puzzles(difficulty DESC);
        """;
        cmd.ExecuteNonQuery();
    }

    public long Insert(PuzzleCandidate p)
    {
        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
        INSERT INTO puzzles(created_utc,node_count,difficulty,optimal_length,second_gap_percent,near_count,diversity,seed,candidate_number)
        VALUES($created,$nodes,$difficulty,$optimal,$gap,$near,$diversity,$seed,$number);
        SELECT last_insert_rowid();
        """;
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$nodes", p.Points.Count);
        cmd.Parameters.AddWithValue("$difficulty", p.DifficultyScore); cmd.Parameters.AddWithValue("$optimal", p.Routes[0].Length);
        cmd.Parameters.AddWithValue("$gap", p.SecondBestGapPercent); cmd.Parameters.AddWithValue("$near", p.NearOptimalCount);
        cmd.Parameters.AddWithValue("$diversity", p.AlternativeDiversity); cmd.Parameters.AddWithValue("$seed", p.Seed);
        cmd.Parameters.AddWithValue("$number", p.CandidateNumber); long id = (long)cmd.ExecuteScalar()!;

        foreach (var (point, index) in p.Points.Select((x, i) => (x, i)))
        {
            cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO nodes VALUES($id,$i,$x,$y)";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$i", index);
            cmd.Parameters.AddWithValue("$x", point.X); cmd.Parameters.AddWithValue("$y", point.Y); cmd.ExecuteNonQuery();
        }
        foreach (var r in p.Routes)
        {
            cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO routes VALUES($id,$rank,$length,$gap,$tour)";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$rank", r.Rank); cmd.Parameters.AddWithValue("$length", r.Length);
            cmd.Parameters.AddWithValue("$gap", r.GapPercent); cmd.Parameters.AddWithValue("$tour", string.Join(',', r.Tour)); cmd.ExecuteNonQuery();
        }
        tx.Commit(); return id;
    }

    public IEnumerable<(long Id, int NodeCount, double Difficulty, double GapPercent, int NearCount, double OptimalLength)> List(int limit)
    {
        using var cmd = _connection.CreateCommand(); cmd.CommandText = "SELECT id,node_count,difficulty,second_gap_percent,near_count,optimal_length FROM puzzles ORDER BY difficulty DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit); using var r = cmd.ExecuteReader();
        while (r.Read()) yield return (r.GetInt64(0), r.GetInt32(1), r.GetDouble(2), r.GetDouble(3), r.GetInt32(4), r.GetDouble(5));
    }

    public PuzzleCandidate? Load(long id)
    {
        using var cmd = _connection.CreateCommand(); cmd.CommandText = "SELECT difficulty,second_gap_percent,near_count,diversity,seed,candidate_number FROM puzzles WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id); using var header = cmd.ExecuteReader(); if (!header.Read()) return null;
        double difficulty = header.GetDouble(0), gap = header.GetDouble(1), diversity = header.GetDouble(3); int near = header.GetInt32(2), seed = header.GetInt32(4), number = header.GetInt32(5); header.Close();
        cmd.CommandText = "SELECT x,y FROM nodes WHERE puzzle_id=$id ORDER BY node_index"; var points = new List<Point2>(); using (var r = cmd.ExecuteReader()) while (r.Read()) points.Add(new(r.GetDouble(0), r.GetDouble(1)));
        cmd.CommandText = "SELECT rank,length,gap_percent,tour FROM routes WHERE puzzle_id=$id ORDER BY rank"; var routes = new List<RouteResult>(); using (var r = cmd.ExecuteReader()) while (r.Read()) routes.Add(new(r.GetInt32(0), r.GetDouble(1), r.GetString(3).Split(',').Select(int.Parse).ToArray(), r.GetDouble(2)));
        return new(points, routes, difficulty, gap, near, diversity, seed, number);
    }
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public static Options Parse(IEnumerable<string> args)
    {
        var a = args.ToArray(); var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) throw new ArgumentException($"Expected option, got '{a[i]}'.");
            string key = a[i][2..]; if (++i >= a.Length) throw new ArgumentException($"Missing value for --{key}."); o._values[key] = a[i];
        }
        return o;
    }
    public string Get(string key, string fallback) => _values.GetValueOrDefault(key, fallback);
    public int GetInt(string key, int fallback, int min, int max) { int v = _values.TryGetValue(key, out var s) ? int.Parse(s, CultureInfo.InvariantCulture) : fallback; return v >= min && v <= max ? v : throw new ArgumentOutOfRangeException(key, $"Must be {min}..{max}."); }
    public double GetDouble(string key, double fallback, double min, double max) { double v = _values.TryGetValue(key, out var s) ? double.Parse(s, CultureInfo.InvariantCulture) : fallback; return v >= min && v <= max ? v : throw new ArgumentOutOfRangeException(key, $"Must be {min}..{max}."); }
    public long RequiredLong(string key) => _values.TryGetValue(key, out var s) ? long.Parse(s, CultureInfo.InvariantCulture) : throw new ArgumentException($"--{key} is required.");
}
