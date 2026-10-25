using System.Net.Http;
using System.Text;
using DongGfx.GwenHost;

// DongGfx.GwenHost — the desk's GWEN confluence model, hosted by the app
// itself (llama.cpp via LLamaSharp). No third-party app on the box, no
// token quotas (local inference), and the process is disposable by design:
// the app supervises it, and a crash here degrades to "no verdict" — the
// fail-neutral contract — never to a halted desk.
//
// Usage:
//   DongGfx.GwenHost <model.gguf> [--port 11435]
//   DongGfx.GwenHost <model.gguf> --probe   one-shot load+generate, prints
//                                           timing, exits (ops check)

var argsList = args.ToList();
var probe = argsList.Remove("--probe");
var port = 11435;
var portIdx = argsList.IndexOf("--port");
if (portIdx >= 0 && portIdx + 1 < argsList.Count &&
    int.TryParse(argsList[portIdx + 1], out var p))
{
    port = p;
    argsList.RemoveRange(portIdx, 2);
}
var gguf = argsList.FirstOrDefault();

if (string.IsNullOrWhiteSpace(gguf))
{
    var env = Environment.GetEnvironmentVariable("TF_GWEN_GGUF");
    gguf = string.IsNullOrWhiteSpace(env) ? null : env;
}
if (gguf is null || !File.Exists(gguf))
{
    Console.Error.WriteLine($"gwen-host: model GGUF not found: {gguf ?? "(none given; pass a path or set TF_GWEN_GGUF)"}");
    return 2;
}

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"gwen-host: model={Path.GetFileName(gguf)} ({new FileInfo(gguf).Length / (1024.0 * 1024 * 1024):0.00} GiB) port={port}");

using var model = new GwenModelService(gguf);

if (probe)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var reply = await model.GenerateAsync(
            "SNAPSHOT: symbol=EURUSD tf=M1 session=late regime=Range adx=14 atr_pct=0.02 " +
            "spread_pts=8. PROPOSED: buy, conf 0.62, stop 9.0 pips, target_r 2.0 " +
            "(swing-high 1.08420 above). BARS 12:00:1.08310,1.08340,1.08290,1.08320 " +
            "12:01:1.08320,1.08350,1.08300,1.08330");
        Console.WriteLine($"gwen-host: probe ok in {sw.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine("gwen-host: reply: " + reply.Trim());
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"gwen-host: probe FAILED after {sw.Elapsed.TotalSeconds:0.0}s: {ex.GetType().Name}: {ex.Message}");
        // The real cause is almost always nested (native loader failures
        // surface as TypeInitializationException with the interesting
        // message levels down — ops needs the leaf, not the wrapper).
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            Console.Error.WriteLine($"gwen-host:   inner: {inner.GetType().Name}: {inner.Message}");
        }
        return 1;
    }
}

using var server = new GwenHostServer(
    port,
    (prompt, ct) => model.GenerateAsync(prompt, ct),
    () => model.IsLoaded);
server.Start();
Console.WriteLine($"gwen-host: ready on http://127.0.0.1:{port}/ (model loads lazily on first /api/chat)");

var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.TrySetResult();
await done.Task;
Console.WriteLine("gwen-host: shutting down");
return 0;
