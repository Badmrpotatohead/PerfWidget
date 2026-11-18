using System;
using System.Reflection;

namespace PerfWidget;

static class Program
{
    public static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--version") >= 0)
        {
            Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?");
            return 0;
        }
        if (Headless.TryRun(args) is int rc) return rc;
        if (Array.IndexOf(args, "--feed") >= 0)
        {
            int k = Array.IndexOf(args, "--parent-pid");
            int? parent = k >= 0 && k + 1 < args.Length && int.TryParse(args[k + 1], out var pid) ? pid : null;
            return Headless.Feed(parent);
        }
        Console.Error.WriteLine("""
            perfwidget-sampler: PerfWidget's machine sampler, no window.
              --feed [--parent-pid N]   keep live.json and history current until process N exits
              --json [path|-]           one snapshot in live.json format
              --dump [path|-]           one snapshot as text
              --history [min] [path|-]  digest of recorded history
              --version
            """);
        return 64;
    }
}
