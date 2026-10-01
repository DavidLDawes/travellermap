using Maps;
using Maps.Admin;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace UnitTests
{
    /// <summary>
    /// Validates all sector data and metadata in res/Sectors (data files parse, allegiance
    /// codes are defined, routes are sane, XML matches res/sectors.xsd).
    ///
    /// Existing problems are listed in test/data-validation-baseline.txt; the test fails only
    /// on errors that are not in the baseline, so data edits can't introduce new ones.
    /// After fixing data, regenerate the baseline by running this test with the
    /// environment variable TM_UPDATE_BASELINE=1. (tools/validate does the same on .NET 10.)
    /// </summary>
    [TestClass]
    public class DataValidationTest
    {
        [TestMethod]
        public void ValidateAllData()
        {
            var validator = new DataValidator();
            validator.ValidateAll(SectorMap.GetInstance(), ResourceManager.GetDedicatedInstance());

            // Optional full report (errors and warnings) for triage: TM_VALIDATION_REPORT=<path>
            string reportPath = Environment.GetEnvironmentVariable("TM_VALIDATION_REPORT");
            if (!string.IsNullOrEmpty(reportPath))
            {
                File.WriteAllLines(reportPath,
                    new[] { "severity\tcategory\twhere\tmessage" }.Concat(validator.Findings.Select(f =>
                        $"{f.Severity}\t{f.Category}\t{f.Where}\t{f.Message.Replace('\t', ' ')}")),
                    new UTF8Encoding(false));
                Console.WriteLine($"Wrote {validator.Findings.Count} findings to {reportPath}");
            }

            string baselinePath = Path.Combine(TestSetup.RepoRoot, "test", "data-validation-baseline.txt");

            if (Environment.GetEnvironmentVariable("TM_UPDATE_BASELINE") == "1")
            {
                File.WriteAllLines(baselinePath, validator.FormatBaseline(), new UTF8Encoding(false));
                Console.WriteLine($"Wrote {validator.Errors.Count()} entries to {baselinePath}");
                return;
            }

            var (added, fixedCount) = validator.CompareToBaseline(File.ReadAllLines(baselinePath));
            Console.WriteLine("Findings:\n  " + string.Join("\n  ", validator.Summary()));
            if (fixedCount > 0)
                Console.WriteLine($"{fixedCount} baseline error(s) no longer occur. Regenerate the baseline (TM_UPDATE_BASELINE=1) to lock in the fix.");

            if (added.Count > 0)
            {
                Assert.Fail($"{added.Count} new data error(s) not in test/data-validation-baseline.txt:\n" +
                    string.Join("\n", added.Take(50)) +
                    (added.Count > 50 ? $"\n... and {added.Count - 50} more" : ""));
            }
        }
    }
}
