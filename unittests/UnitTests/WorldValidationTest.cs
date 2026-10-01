using Maps;
using Maps.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Linq;
using System.Text;

namespace UnitTests
{
    [TestClass]
    public class WorldValidationTest
    {
        private const string Header =
            "Hex  Name                 UWP       Remarks            {Ix} (Ex) [Cx] N B Z PBG W A    Stellar\n" +
            "---- -------------------- --------- ------------------ ---- ---- ---- - - - --- - ---- ----------\n";

        // Fixed-width columns matching Header.
        private static readonly int[] Widths = { 4, 20, 9, 18, 4, 4, 4, 1, 1, 1, 3, 1, 4, 10 };
        private static string Row(params string[] fields) =>
            string.Join(" ", fields.Select((f, i) => f.PadRight(Widths[i]))) + "\n";

        private static (WorldCollection worlds, ErrorLogger errors) Parse(string rows)
        {
            var errors = new ErrorLogger();
            var worlds = new WorldCollection();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Header + rows));
            worlds.Deserialize(stream, "SecondSurvey", errors);
            return (worlds, errors);
        }

        [TestMethod]
        public void PlaceholderExtensionsDoNotCrashValidation()
        {
            // Rows from res/Sectors/M1105/Nadir.txt: unexplored worlds with "----" for
            // {Ix}/(Ex)/[Cx]. Validation used to throw ("Input string was not in a correct
            // format"), which was reported as a parse error.
            var (worlds, errors) = Parse(
                "0101                      X87A000-0 Ba Lo Ni Wa        ---- ---- ----       XXX   XXXX           \n" +
                "0110                      X641000-0 Ba Lo Ni Po        ---- ---- ----       XXX   XXXX           \n");

            Assert.AreEqual(2, worlds.Count());
            var bad = errors.Records.Where(r => r.severity >= ErrorLogger.Severity.Error).ToList();
            Assert.AreEqual(0, bad.Count, string.Join("\n", bad.Select(r => r.message)));
            Assert.IsFalse(errors.Records.Any(r => r.message.Contains("Ix") || r.message.Contains("(Ex)") || r.message.Contains("[Cx]")),
                "placeholders are not reported");
        }

        [TestMethod]
        public void MalformedExtensionsAreWarningsNotCrashes()
        {
            var (worlds, errors) = Parse(
                Row("0101", "Testworld", "A788899-C", "Ri Pa Ph", "{ x}", "(12)", "", "-", "", "A", "703", "9", "ImDd", "G2 V"));

            Assert.AreEqual(1, worlds.Count());
            Assert.IsFalse(errors.Records.Any(r => r.severity >= ErrorLogger.Severity.Error),
                string.Join("\n", errors.Records.Select(r => r.message)));
            Assert.IsTrue(errors.Records.Any(r => r.severity == ErrorLogger.Severity.Warning && r.message.Contains("not a number")));
            Assert.IsTrue(errors.Records.Any(r => r.severity == ErrorLogger.Severity.Warning && r.message.Contains("(Ex) Economic=12 is malformed")));
        }

        [TestMethod]
        public void GenerationRuleChecksAreHints()
        {
            // TL 15 at a C starport with no modifiers is outside "mods + 1D": a hint, not a warning.
            var (_, errors) = Parse(
                Row("0101", "Hightech", "C867977-F", "Ga Hi In", "", "", "", "-", "-", "-", "103", "", "ImDd", "G2 V"));

            Assert.IsTrue(errors.Records.Any(r => r.severity == ErrorLogger.Severity.Hint && r.message.StartsWith("UWP: TL=")),
                string.Join("\n", errors.Records.Select(r => r.severity + ": " + r.message)));
            Assert.IsFalse(errors.Records.Any(r => r.severity == ErrorLogger.Severity.Warning && r.message.StartsWith("UWP: TL=")));
        }
    }
}
