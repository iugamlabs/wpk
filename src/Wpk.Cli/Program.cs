using System.Text;
using Wpk.Cli;
using Wpk.Cli.Platform;
using Wpk.Cli.Presentation;
using Wpk.Core;

Console.OutputEncoding = new UTF8Encoding(false);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var sources = new List<IInventorySource> { new RegistrySource(), new WinGetSource() };
var adapters = new List<IUpgradeAdapter> { new WinGetUpgradeAdapter() };
ScoopSettings? settings = null;
try
{
    settings = ScoopSettings.Discover();
    sources.Add(new ScoopSource(settings));
    adapters.Add(new ScoopUpgradeAdapter(settings));
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
{ sources.Add(new UnavailableSource("scoop", e.Message)); }
var runner = new ProcessRunner();
var doctor = new EnvironmentDoctor(runner, settings);
var terminal = new TerminalContext(Console.Out, Console.Error,
    terminal: !Console.IsOutputRedirected && !Console.IsErrorRedirected,
    interactive: !Console.IsInputRedirected,
    noColor: Environment.GetEnvironmentVariable("NO_COLOR") is not null);
var app = new CliApplication(new InventoryService(sources), adapters, runner, new DirectoryOpener(), Console.Out, Console.Error,
    doctor: doctor.InspectAsync, interactive: terminal.Interactive,
    renderer: new SpectreRenderer(terminal.Output, terminal.Error, terminal.Animated),
    userInteraction: new SpectreInteraction(terminal.Error, terminal.Interactive));
return await app.RunAsync(args, cancellation.Token);

internal sealed class UnavailableSource(string name, string message) : IInventorySource
{
    public string Name => name;
    public Task<SourceResult> CollectAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SourceResult([], [new(name, "configurationInvalid", message)], true));
}
