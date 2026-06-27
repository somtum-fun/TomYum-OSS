// Async score-statistics processor (worker, not a web host). Consumes the Redis score
// queue, runs the processor chain, and writes pp/stats — decoupled from submission so
// heavy work never blocks the submit path. See ADR-0004 and
// memories/references/osu-queue-score-statistics.md. Scaffold only.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using var host = Host.CreateApplicationBuilder(args).Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TomYum.ScoreQueue");
logger.LogInformation("TomYum.ScoreQueue starting (scaffold) — Redis queue consumer + processor chain TODO.");
await host.RunAsync();
