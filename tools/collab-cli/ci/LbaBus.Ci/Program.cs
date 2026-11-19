using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabViewBenchmarkActor.CollabBus.Ci;

/// <summary>
/// Declarative edge-case runner for lbabus. Each <c>cases/*.json</c> file describes one invocation
/// (args + env) and the expected exit code / stdout / stderr. The runner executes lbabus once per
/// case, compares against the expectation, and writes <c>results/&lt;name&gt;.json</c> (one file per
/// case, so both planes add cases without ever touching a shared manifest). Exit 0 iff every case
/// passed or was skipped; exit 1 if any case failed. No third-party dependencies, no shell.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main(string[] rawArgs)
    {
        var args = ArgMap.Parse(rawArgs);
        string repoRoot = Path.GetFullPath(args.Get("repo-root") ?? Directory.GetCurrentDirectory());
        string casesDir = Path.GetFullPath(args.Get("cases-dir") ?? Path.Combine(repoRoot, "tools", "collab-cli", "ci", "cases"));
        string resultsDir = Path.GetFullPath(args.Get("results-dir") ?? Path.Combine(repoRoot, "tools", "collab-cli", "ci", "results"));
        string? lbabus = args.Get("lbabus");

        if (lbabus is null)
        {
            Console.Error.WriteLine("lbabus-ci: --lbabus <path to lbabus.dll or native exe> is required.");
            return 2;
        }

        if (!Directory.Exists(casesDir))
        {
            Console.Error.WriteLine($"lbabus-ci: cases dir not found: {casesDir}");
            return 2;
        }

        Directory.CreateDirectory(resultsDir);
        string? endpoint = Environment.GetEnvironmentVariable("LBABUS_GITHUB_API");
        bool mockAvailable = !string.IsNullOrWhiteSpace(endpoint);
        bool ripgrepPresent = RipgrepPresent();

        string[] caseFiles = Directory.GetFiles(casesDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (caseFiles.Length == 0)
        {
            Console.Error.WriteLine($"lbabus-ci: no cases in {casesDir}");
            return 2;
        }

        Console.WriteLine($"lbabus-ci: {caseFiles.Length} case(s) from {Rel(repoRoot, casesDir)}");
        Console.WriteLine($"lbabus-ci: mock endpoint {(mockAvailable ? endpoint : "<none> (LBABUS_GITHUB_API unset — mock-requiring cases will SKIP)")}");
        Console.WriteLine($"lbabus-ci: ripgrep {(ripgrepPresent ? "present" : "absent (rg-requiring cases will SKIP; no-rg cases will RUN)")}");
        Console.WriteLine();

        int passed = 0, failed = 0, skipped = 0;
        foreach (string file in caseFiles)
        {
            TestCase tc;
            try
            {
                tc = JsonSerializer.Deserialize<TestCase>(File.ReadAllText(file), ReadOpts)
                     ?? throw new InvalidOperationException("null case");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL  {Path.GetFileName(file)}  (unreadable: {ex.Message})");
                continue;
            }

            if (tc.RequiresMock && !mockAvailable)
            {
                skipped++;
                WriteResult(resultsDir, tc.Name, new CaseResult(tc.Name, tc.Owner, "skip", null, tc.Expect.ExitCode, "requires mock (LBABUS_GITHUB_API unset)", null, null, Array.Empty<CheckResult>()));
                Console.WriteLine($"  SKIP  {tc.Name}  (requires mock)");
                continue;
            }

            if (tc.RequiresRipgrep && !ripgrepPresent)
            {
                skipped++;
                WriteResult(resultsDir, tc.Name, new CaseResult(tc.Name, tc.Owner, "skip", null, tc.Expect.ExitCode, "requires ripgrep (rg absent)", null, null, Array.Empty<CheckResult>()));
                Console.WriteLine($"  SKIP  {tc.Name}  (requires ripgrep)");
                continue;
            }

            if (tc.RequiresNoRipgrep && ripgrepPresent)
            {
                skipped++;
                WriteResult(resultsDir, tc.Name, new CaseResult(tc.Name, tc.Owner, "skip", null, tc.Expect.ExitCode, "requires ripgrep absent (rg present)", null, null, Array.Empty<CheckResult>()));
                Console.WriteLine($"  SKIP  {tc.Name}  (requires no ripgrep)");
                continue;
            }

            CaseResult result = RunCase(tc, repoRoot, lbabus, mockAvailable ? endpoint : null);
            WriteResult(resultsDir, tc.Name, result);
            if (result.Outcome == "pass")
            {
                passed++;
                Console.WriteLine($"  PASS  {tc.Name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  FAIL  {tc.Name}  (exit {result.ExitCode}, expected {result.ExpectedExitCode})");
                foreach (CheckResult c in result.Checks.Where(c => !c.Ok))
                {
                    Console.WriteLine($"          - {c.Kind}: {c.Detail}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"lbabus-ci: {passed} passed, {failed} failed, {skipped} skipped");
        return failed == 0 ? 0 : 1;
    }

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    /// <summary>One started lbabus process, its output being drained so a chatty child cannot block.</summary>
    private sealed record Running(Process Process, Task<string> Stdout, Task<string> Stderr, string Label, CaseExpect? Expect);

    private static CaseResult RunCase(TestCase tc, string repoRoot, string lbabus, string? mockEndpoint)
    {
        // A per-case scratch directory holds the files a case creates and the receive-logs it writes; {scratch}
        // in args and env values names it.
        string scratch = Path.Combine(Path.GetTempPath(), "lbabus-ci-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var background = new List<Running>();
        try
        {
            foreach ((string name, CaseFile file) in tc.Files ?? new())
            {
                string path = Path.GetFullPath(Path.Combine(scratch, name));
                if (!path.StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return new CaseResult(tc.Name, tc.Owner, "fail", -1, tc.Expect.ExitCode, null, null, null,
                        new[] { new CheckResult("files", false, $"{name} escapes the scratch directory") });
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Text ?? string.Concat(Enumerable.Repeat(file.Repeat ?? "x", file.Count ?? 1)));
            }

            var checks = new List<CheckResult>();
            foreach ((CaseStep step, int index) in (tc.Steps ?? Array.Empty<CaseStep>()).Select((s, i) => (s, i)))
            {
                if (step.WaitForBackground)
                {
                    FinishBackground(background, checks);
                }

                Running running = Start(step.Args, step.Env, tc, repoRoot, lbabus, mockEndpoint, scratch, $"step {index + 1}", step.Expect);
                if (step.Background)
                {
                    background.Add(running);
                    continue;
                }

                Finish(running, checks);
                if (checks.Any(c => !c.Ok))
                {
                    FinishBackground(background, checks);
                    return new CaseResult(tc.Name, tc.Owner, "fail", null, tc.Expect.ExitCode, null, null, null, checks.ToArray());
                }
            }

            if (tc.WaitForBackground)
            {
                FinishBackground(background, checks);
            }

            Running main = Start(tc.Args, tc.Env, tc, repoRoot, lbabus, mockEndpoint, scratch, "main", tc.Expect);
            string stdout = "", stderr = "";
            int exit = Finish(main, checks, ref stdout, ref stderr);
            FinishBackground(background, checks);
            bool ok = checks.All(c => c.Ok);
            return new CaseResult(tc.Name, tc.Owner, ok ? "pass" : "fail", exit, tc.Expect.ExitCode, null, Cap(stdout), Cap(stderr), checks.ToArray());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new CaseResult(tc.Name, tc.Owner, "fail", -1, tc.Expect.ExitCode, null, null, null,
                new[] { new CheckResult("spawn", false, ex.Message) });
        }
        finally
        {
            // A step that was never awaited (an exception above) must not outlive the case with its port and log open.
            foreach (Running running in background.Where(r => !r.Process.HasExited))
            {
                running.Process.Kill(entireProcessTree: true);
                running.Process.WaitForExit();
            }

            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void FinishBackground(List<Running> background, List<CheckResult> checks)
    {
        foreach (Running running in background)
        {
            Finish(running, checks);
        }

        background.Clear();
    }

    private static int Finish(Running running, List<CheckResult> checks)
    {
        string stdout = "", stderr = "";
        return Finish(running, checks, ref stdout, ref stderr);
    }

    private static int Finish(Running running, List<CheckResult> checks, ref string stdout, ref string stderr)
    {
        if (!running.Process.WaitForExit(StepTimeout))
        {
            running.Process.Kill(entireProcessTree: true);
            running.Process.WaitForExit();
            checks.Add(new($"{running.Label} timeout", false, $"did not exit within {StepTimeout.TotalSeconds:0}s"));
        }

        stdout = running.Stdout.GetAwaiter().GetResult();
        stderr = running.Stderr.GetAwaiter().GetResult();
        int exit = running.Process.ExitCode;
        // A step that declares no expectation must still exit 0.
        CaseExpect expect = running.Expect ?? new CaseExpect(0, null, null, null, null);
        string prefix = running.Label == "main" ? "" : running.Label + " ";
        checks.AddRange(Judge(expect, exit, stdout, stderr).Select(c => c with { Kind = prefix + c.Kind }));

        return exit;
    }

    private static IEnumerable<CheckResult> Judge(CaseExpect expect, int exit, string stdout, string stderr)
    {
        yield return new("exitCode", exit == expect.ExitCode, $"got {exit}, expected {expect.ExitCode}");
        foreach (string needle in expect.StdoutContains ?? Array.Empty<string>())
        {
            yield return new("stdoutContains", stdout.Contains(needle, StringComparison.Ordinal), $"stdout must contain {Quote(needle)}");
        }

        foreach (string needle in expect.StdoutNotContains ?? Array.Empty<string>())
        {
            yield return new("stdoutNotContains", !stdout.Contains(needle, StringComparison.Ordinal), $"stdout must NOT contain {Quote(needle)}");
        }

        foreach (string needle in expect.StderrContains ?? Array.Empty<string>())
        {
            yield return new("stderrContains", stderr.Contains(needle, StringComparison.Ordinal), $"stderr must contain {Quote(needle)}");
        }

        if (expect.StdoutEquals is { } exact)
        {
            yield return new("stdoutEquals", string.Equals(stdout.Replace("\r\n", "\n"), exact.Replace("\r\n", "\n"), StringComparison.Ordinal), "stdout must equal expected (CRLF-normalized)");
        }
    }

    private static Running Start(string[] caseArgs, Dictionary<string, string>? caseEnv, TestCase tc, string repoRoot, string lbabus,
        string? mockEndpoint, string scratch, string label, CaseExpect? expect)
    {
        string cwd = tc.Cwd is null ? repoRoot : Path.GetFullPath(Path.Combine(repoRoot, tc.Cwd));
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // A .dll is launched via the shared dotnet host; a native binary is launched directly.
        if (lbabus.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(Path.GetFullPath(lbabus));
        }
        else
        {
            psi.FileName = Path.GetFullPath(lbabus);
        }

        foreach (string a in caseArgs)
        {
            psi.ArgumentList.Add(Scratch(a, scratch));
        }

        // Hermetic env: strip any ambient coordination variables so a stray LBABUS_*/COLLAB_*/token on the
        // host cannot contaminate a case (e.g. a leaked LBABUS_SKIP_VERSION_CHECK silently bypassing the
        // version gate). Each case then declares exactly the env it needs. The shared mock endpoint is
        // injected only for mock-requiring cases; offline cases run with no endpoint (or their own).
        foreach (string key in psi.Environment.Keys
                     .Where(k => k.StartsWith("LBABUS_", StringComparison.OrdinalIgnoreCase)
                              || k.StartsWith("COLLAB_", StringComparison.OrdinalIgnoreCase)
                              || k.Equals("GH_TOKEN", StringComparison.OrdinalIgnoreCase)
                              || k.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            psi.Environment.Remove(key);
        }

        if (tc.RequiresMock && mockEndpoint is not null)
        {
            psi.Environment["LBABUS_GITHUB_API"] = mockEndpoint;
        }

        if (caseEnv is not null)
        {
            foreach ((string k, string v) in caseEnv)
            {
                psi.Environment[k] = Scratch(v, scratch);
            }
        }

        Process process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        return new Running(process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync(), label, expect);
    }

    private static string Scratch(string value, string scratch) => value.Replace("{scratch}", scratch, StringComparison.Ordinal);

    private static void WriteResult(string dir, string name, CaseResult result)
        => File.WriteAllText(Path.Combine(dir, $"{Sanitize(name)}.json"), JsonSerializer.Serialize(result, WriteOpts));

    private static bool RipgrepPresent()
    {
        try
        {
            var psi = new ProcessStartInfo("rg", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Cap(string s) => s.Length <= 4000 ? s : s[..4000] + "…";
    private static string Quote(string s) => "\"" + (s.Length <= 80 ? s : s[..80] + "…") + "\"";
    private static string Sanitize(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    private static string Rel(string root, string path)
    {
        string r = Path.GetRelativePath(root, path);
        return r.Replace('\\', '/');
    }
}

internal sealed record CaseExpect(
    int ExitCode,
    string[]? StdoutContains,
    string[]? StdoutNotContains,
    string[]? StderrContains,
    string? StdoutEquals);

internal sealed record CaseFile(string? Text, string? Repeat, int? Count);

internal sealed record CaseStep(
    string[] Args,
    Dictionary<string, string>? Env,
    bool Background,
    bool WaitForBackground,
    CaseExpect? Expect);

internal sealed record TestCase(
    string Name,
    string? Owner,
    string? Description,
    bool RequiresMock,
    bool RequiresRipgrep,
    bool RequiresNoRipgrep,
    Dictionary<string, string>? Env,
    string[] Args,
    string? Cwd,
    CaseExpect Expect,
    Dictionary<string, CaseFile>? Files = null,
    CaseStep[]? Steps = null,
    bool WaitForBackground = false);

internal sealed record CheckResult(string Kind, bool Ok, string Detail);

internal sealed record CaseResult(
    string Name,
    string? Owner,
    string Outcome,
    int? ExitCode,
    int ExpectedExitCode,
    string? Note,
    string? Stdout,
    string? Stderr,
    CheckResult[] Checks);

internal sealed class ArgMap
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public static ArgMap Parse(string[] args)
    {
        var m = new ArgMap();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string key = args[i][2..];
            string value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            m._map[key] = value;
        }

        return m;
    }

    public string? Get(string key) => _map.TryGetValue(key, out string? v) ? v : null;
}
