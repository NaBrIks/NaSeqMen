using System.Diagnostics;

namespace NaSeqMen.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var suites = new (string SuiteName, IReadOnlyList<(string Name, Func<Task> Run)> Tests)[]
        {
            ("InterceptionLoopTests", InterceptionLoopTests.GetTests()),
            ("HotkeySwitchTests", HotkeySwitchTests.GetTests()),
            ("ObsAndControlsTests", ObsAndControlsTests.GetTests()),
            ("ScriptingAndControlsTests", ScriptingAndControlsTests.GetTests()),
        };

        var totalSw = Stopwatch.StartNew();
        var passed = 0;
        var failed = 0;
        var failures = new List<(string TestName, Exception Error)>();

        Console.WriteLine("======================================================================");
        Console.WriteLine(" NaSeqMen Automated Test Suite");
        Console.WriteLine("======================================================================");

        foreach (var (suiteName, tests) in suites)
        {
            Console.WriteLine();
            Console.WriteLine($"[{suiteName}] ({tests.Count} tests)");

            foreach (var (name, run) in tests)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    await run();
                    sw.Stop();
                    passed++;
                    Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds} ms)");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    failed++;
                    failures.Add(($"{suiteName}.{name}", ex));
                    Console.WriteLine($"  FAIL  {name} ({sw.ElapsedMilliseconds} ms)");
                    Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        totalSw.Stop();
        Console.WriteLine();
        Console.WriteLine("======================================================================");
        Console.WriteLine($" Results: {passed} passed, {failed} failed, {passed + failed} total in {totalSw.ElapsedMilliseconds} ms");
        Console.WriteLine("======================================================================");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failure Details:");
            foreach (var (testName, ex) in failures)
            {
                Console.WriteLine($"--- {testName} ---");
                Console.WriteLine(ex.ToString());
            }
            return 1;
        }

        return 0;
    }
}
