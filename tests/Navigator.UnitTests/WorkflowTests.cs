using System.Text;

namespace Navigator.UnitTests;

/// <summary>
/// Guards the GitHub Actions pipelines: a build (CI publish smoke or release packaging), including one started
/// manually, must never start before every test has passed.
/// </summary>
[TestClass]
public sealed class WorkflowTests
{
    [TestMethod]
    public void Ci_ManualRun_PublishWaitsForAllTests()
    {
        var workflow = Workflow.Load("ci.yml");

        Assert.IsTrue(workflow.HasManualTrigger, "CI must stay manually runnable.");
        CollectionAssert.Contains(workflow.Needs("publish-smoke").ToList(), "test");
        Assert.IsTrue(workflow.DependsOn("publish-smoke", "quality"));
    }

    [TestMethod]
    public void Release_ManualRun_TestsRunBeforeVersionReservationAndPackaging()
    {
        var workflow = Workflow.Load("release.yml");

        Assert.IsTrue(workflow.HasManualTrigger, "Release must stay manually runnable.");
        Assert.AreEqual(0, workflow.Needs("test").Count, "Tests must be the first gate, not wait for a reserved version.");
        Assert.IsTrue(workflow.DependsOn("prepare", "test"), "No version tag may be pushed before the tests pass.");
        Assert.IsTrue(workflow.DependsOn("build", "test"));
        Assert.IsTrue(workflow.DependsOn("publish", "test"));
        Assert.IsFalse(workflow.DependsOn("test", "prepare"));
    }

    [TestMethod]
    public void Release_TestsRunUnitAndIntegrationSuites()
    {
        var job = Workflow.Load("release.yml").JobText("test");

        StringAssert.Contains(job, "tests/Navigator.UnitTests/Navigator.UnitTests.csproj");
        StringAssert.Contains(job, "tests/Navigator.IntegrationTests/Navigator.IntegrationTests.csproj");
    }

    [TestMethod]
    public void Parser_ReadsInlineListAndScalarNeeds()
    {
        var workflow = Workflow.Parse("""
            on:
              workflow_dispatch:
            jobs:
              a:
                runs-on: x
              b:
                needs: a
              c:
                needs: [ b , a ]
            """);

        Assert.IsTrue(workflow.HasManualTrigger);
        CollectionAssert.AreEqual(new[] { "a" }, workflow.Needs("b").ToArray());
        CollectionAssert.AreEqual(new[] { "b", "a" }, workflow.Needs("c").ToArray());
        Assert.IsTrue(workflow.DependsOn("c", "a"));
        Assert.IsFalse(workflow.DependsOn("a", "c"));
        Assert.ThrowsExactly<KeyNotFoundException>(() => workflow.Needs("missing"));
    }

    /// <summary>Minimal reader for the job graph of a workflow file (two-space indented YAML, as in the repo).</summary>
    private sealed class Workflow
    {
        private readonly Dictionary<string, List<string>> _needs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _jobText = new(StringComparer.Ordinal);

        public bool HasManualTrigger { get; private set; }

        public static Workflow Load(string fileName)
            => Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", fileName)));

        public static Workflow Parse(string text)
        {
            var workflow = new Workflow();
            var inJobs = false;
            string? job = null;
            var body = new StringBuilder();

            void Flush()
            {
                if (job is not null)
                    workflow._jobText[job] = body.ToString();
                body.Clear();
            }

            foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Trim() == "workflow_dispatch:")
                    workflow.HasManualTrigger = true;

                if (line.Length > 0 && line[0] != ' ' && line[0] != '#')
                {
                    Flush();
                    job = null;
                    inJobs = line == "jobs:";
                    continue;
                }

                if (!inJobs)
                    continue;

                if (line.StartsWith("  ", StringComparison.Ordinal) && line.Length > 2 && line[2] != ' ' && line[2] != '#' && line.EndsWith(':'))
                {
                    Flush();
                    job = line[2..^1];
                    workflow._needs[job] = [];
                    continue;
                }

                if (job is null)
                    continue;

                body.Append(line).Append('\n');
                const string needsKey = "    needs:";
                if (line.StartsWith(needsKey, StringComparison.Ordinal))
                {
                    var value = line[needsKey.Length..].Trim().Trim('[', ']');
                    workflow._needs[job].AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }

            Flush();
            return workflow;
        }

        public IReadOnlyList<string> Needs(string job) => _needs[job];

        public string JobText(string job) => _jobText[job];

        public bool DependsOn(string job, string dependency)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(Needs(job));
            while (pending.TryPop(out var current))
            {
                if (current == dependency)
                    return true;
                if (visited.Add(current))
                {
                    foreach (var next in Needs(current))
                        pending.Push(next);
                }
            }

            return false;
        }

        private static string RepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Navigator.slnx")))
                    return directory.FullName;
            }

            throw new InvalidOperationException("Repository root not found.");
        }
    }
}
