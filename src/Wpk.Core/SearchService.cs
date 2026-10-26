namespace Wpk.Core;

public enum MatchKind { None, Fuzzy, Contains, Prefix, NormalizedExact, Exact, RecordId }
public sealed record SearchMatch(SoftwareInstance Instance, int Score, MatchKind Kind)
{
    public bool IsExact => Kind >= MatchKind.NormalizedExact;
}

public static class SearchService
{
    public static IReadOnlyList<SearchMatch> Find(IEnumerable<SoftwareInstance> instances, string query,
        bool exact = false, InstallScope? scope = null)
    {
        var matches = instances.Where(i => scope is null || i.Scope == scope)
            .Select(i =>
            {
                var score = Score(i, query, exact);
                var kind = score switch
                {
                    1000 => MatchKind.RecordId, 900 => MatchKind.Exact, 800 => MatchKind.NormalizedExact,
                    700 => MatchKind.Prefix, 600 => MatchKind.Contains, 300 => MatchKind.Fuzzy, _ => MatchKind.None
                };
                return new SearchMatch(i, score, kind);
            }).Where(m => m.Score > 0).ToArray();
        return matches.Where(m => m.Kind != MatchKind.Fuzzy || !matches.Any(d => d.Kind > MatchKind.Fuzzy))
            .OrderByDescending(m => m.Score).ThenBy(m => m.Instance.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Instance.Id).ToArray();
    }

    private static int Score(SoftwareInstance instance, string query, bool exact)
    {
        if (Identity.Same(instance.Id, query) || instance.Records.Any(r => Identity.Same(r.Id, query))) return 1000;
        var values = instance.Records.SelectMany(r => new[] { r.Name, r.NativeId }.Concat(r.Aliases));
        var q = Identity.Text(query);
        if (q.Length == 0) return 0;
        return values.Select(value =>
        {
            if (Identity.Same(value, query)) return 900;
            if (exact) return 0;
            var v = Identity.Text(value);
            if (v == q) return 800;
            if (v.StartsWith(q, StringComparison.Ordinal)) return 700;
            if (v.Contains(q, StringComparison.Ordinal)) return 600;
            var index = 0;
            foreach (var c in v) if (index < q.Length && q[index] == c) index++;
            return index == q.Length && q.Length >= 3 ? 300 : 0;
        }).DefaultIfEmpty(0).Max();
    }
}
