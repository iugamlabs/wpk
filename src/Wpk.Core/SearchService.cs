namespace Wpk.Core;

public sealed record SearchMatch(SoftwareInstance Instance, int Score);

public static class SearchService
{
    public static IReadOnlyList<SearchMatch> Find(IEnumerable<SoftwareInstance> instances, string query,
        bool exact = false, InstallScope? scope = null)
    {
        return instances.Where(i => scope is null || i.Scope == scope)
            .Select(i => new SearchMatch(i, Score(i, query, exact))).Where(m => m.Score > 0)
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
