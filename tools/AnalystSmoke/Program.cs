// AnalystSmoke — runs the production JournalAnalystService once against the
// live (or test-pointed) journal and prints the narrative it would post.
// The webhook URL is taken from the app's settings.json, so with a real URL
// configured this posts the real message; with it empty the send no-ops and
// the narrative is only printed.
//
// Usage:
//   dotnet run --project tools/AnalystSmoke [-p:OutputPath=bin/lockfree/]
//
// LLM endpoint: TF_LLM_BASE_URL / TF_LLM_MODEL / TF_LLM_API_KEY env vars
// (defaults: local Ollama, qwen3:0.6b). Without a reachable LLM the
// deterministic template narrative is printed — the tool always works.
using DongGfx.App.Infrastructure;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

// 🤖/📊 narratives must survive the pipe to scripts/soak_evening.py: the
// default console encoding on Windows mangles emoji into '?'.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var journalDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tf", "data", "journal");
using var journal = new TradeJournal(journalDir);
using var webhook = new WebhookService
{
    // Honor the app's configured webhook (settings.json) so the smoke test
    // exercises the same channel the real posts use.
    WebhookUrl = new SettingsService().Load().WebhookUrl
};
var analyst = new JournalAnalystService(journal, webhook,
    log: line => Console.Error.WriteLine($"[log] {line}"));
var narrative = await analyst.AnalyzeOnceAsync();
Console.WriteLine("=== NARRATIVE START ===");
Console.WriteLine(narrative ?? "(nothing to say — empty journal or toggle off)");
Console.WriteLine("=== NARRATIVE END ===");
