namespace Wpk.Core;

public sealed class InventoryService(IEnumerable<IInventorySource> sources) : IInventoryService
{
    public async Task<InventorySnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(sources.Select(async source =>
        {
            try { return await source.CollectAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                return new SourceResult([], [new(source.Name, "sourceFailed", e.Message)], true);
            }
        }));
        return new(new InstanceMerger().Merge(results.SelectMany(r => r.Records)),
            results.SelectMany(r => r.Diagnostics).ToArray(), results.Any(r => r.Partial));
    }
}
