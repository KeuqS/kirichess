using System.Text.RegularExpressions;
using KiriChess.Server.Infrastructure.OpenApi;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// The generalisable guard 10c.2 asked for, in <see cref="LegalDocumentDriftTests"/> and
/// <see cref="MetricsContractTests"/>'s own idiom: a test that fails when a claimed control is
/// missing, rather than trusting a roadmap tick nobody re-reads.
///
/// <c>codeql.yml</c> was deleted on 2026-08-12 by a commit about something else entirely (its
/// message lists four unrelated fixes and states, correctly, that code scanning cannot pass on a
/// private personal-account repo) - a good decision with no mechanism attached to notice it had
/// also silently un-shipped the control docs/ROADMAP.md's 9a.2 claims still runs on every pull
/// request, every push to main/dev, and weekly. Restored 2026-08-19 when the repo became public.
/// This class still asserts the claimed workflows exist and fire on the triggers their comments
/// claim, including CodeQL.
/// </summary>
public sealed class CiWorkflowDriftTests
{
    private static readonly Regex ShaPinnedAction =
        new(@"uses:\s*\S+@([0-9a-f]{40})\b", RegexOptions.Compiled);

    private static readonly Regex DigestPinnedImage =
        new(@"semgrep/semgrep@sha256:[0-9a-f]{64}\b", RegexOptions.Compiled);

    private static string WorkflowsDir => Path.Combine(OpenApiExport.FindRepoRoot(), ".github", "workflows");

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(WorkflowsDir, fileName));

    private static IEnumerable<string> WorkflowFiles() =>
        Directory.EnumerateFiles(WorkflowsDir, "*.yml")
            .Concat(Directory.EnumerateFiles(WorkflowsDir, "*.yaml"))
            .Select(path => Path.GetFileName(path)!);

    /// <summary>
    /// One entry per workflow this repo's docs/gates assume exists. Adding a workflow means adding
    /// a line here; removing one (deliberately) means removing its line here in the same commit,
    /// which is the point - a removal that forgets this file fails the build instead of the roadmap
    /// quietly going stale.
    /// </summary>
    [Theory]
    [InlineData("ci.yml")]
    [InlineData("codeql.yml")]
    [InlineData("deploy.yml")]
    [InlineData("offsite-backup.yml")]
    [InlineData("ops-probes.yml")]
    [InlineData("restore-drill.yml")]
    [InlineData("secret-scan.yml")]
    [InlineData("semgrep.yml")]
    public void Claimed_workflow_file_exists(string fileName)
    {
        Assert.True(
            File.Exists(Path.Combine(WorkflowsDir, fileName)),
            $"{fileName} is missing from .github/workflows/ but something in docs/ROADMAP.md or a "
            + "CI comment still claims it runs. Either restore it or remove every claim that it exists.");
    }

    /// <summary>
    /// Restored 2026-08-19 once the repo is public: code scanning is free on a public
    /// personal-account repo. Same trigger shape as <c>semgrep.yml</c> - a PR-only
    /// trigger never rescans untouched files after a ruleset update, which is what the weekly
    /// <c>schedule</c> below is for and why it is asserted separately.
    ///
    /// <c>dev</c> was dropped from the push list on 2026-09-04, deliberately. The release PR's head
    /// branch IS dev, so listing it there ran this workflow twice on every commit: two identical
    /// scans of one SHA. The rescan concern this test was written against is unaffected, because it
    /// is the weekly cron that answers it, not the push trigger.
    /// </summary>
    [Fact]
    public void Codeql_workflow_runs_on_every_pr_push_to_main_and_weekly()
    {
        var yaml = Read("codeql.yml");

        Assert.Contains("pull_request:", yaml);
        Assert.Contains("branches: [main]", yaml);
        Assert.DoesNotContain("branches: [main, dev]", yaml);
        Assert.Contains("concurrency:", yaml);
        Assert.Contains("schedule:", yaml);
        Assert.Contains("cron:", yaml);
        Assert.Contains("csharp", yaml);
        Assert.Contains("javascript-typescript", yaml);
    }

    /// <summary>
    /// Phase 10c.2: static analysis on every PR, on push to main, and weekly - the trigger shape
    /// 9a.2 claimed for the deleted codeql.yml, restored here for semgrep.yml. A trigger silently
    /// narrowed to "pull_request only" (dropping the weekly rescan) is precisely how a ruleset update
    /// on untouched code would stop being caught, without any PR ever looking wrong. That is why the
    /// weekly <c>cron</c> is asserted on its own line below and must survive any trigger edit.
    ///
    /// <c>dev</c> left the push list on 2026-09-04 for the de-duplication reason recorded on the
    /// CodeQL test above. Narrowing to PR-only would still be a regression; narrowing from
    /// "main and dev" to "main" is not, because dev only ever reaches main through a PR.
    /// </summary>
    [Fact]
    public void Semgrep_workflow_runs_on_every_pr_push_to_main_and_weekly()
    {
        var yaml = Read("semgrep.yml");

        Assert.Contains("pull_request:", yaml);
        Assert.Contains("branches: [main]", yaml);
        Assert.DoesNotContain("branches: [main, dev]", yaml);
        Assert.Contains("concurrency:", yaml);
        Assert.Contains("schedule:", yaml);
        Assert.Contains("cron:", yaml);
    }

    /// <summary>
    /// <c>semgrep ci</c> wants a SEMGREP_APP_TOKEN and uploads findings to Semgrep Cloud - the
    /// same shape of entitlement CodeQL needed. <c>semgrep scan</c> is the road that works on a
    /// private repo because nothing leaves the job.
    /// </summary>
    [Fact]
    public void Semgrep_workflow_runs_scan_not_ci()
    {
        var commandLines = Read("semgrep.yml")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains("semgrep", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(commandLines, line => line.StartsWith("semgrep scan", StringComparison.Ordinal));
        Assert.DoesNotContain(commandLines, line => line.StartsWith("semgrep ci", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>semgrep scan</c> without <c>--error</c> reports findings and exits 0 - a silently
    /// non-blocking static-analysis job is worse than an absent one, because it looks like coverage
    /// in the Actions tab. This is the one line standing between "gate" and "decoration".
    /// </summary>
    [Fact]
    public void Semgrep_workflow_fails_the_build_on_a_finding()
    {
        Assert.Contains("--error", Read("semgrep.yml"));
    }

    /// <summary>
    /// Same privacy default this repo applies elsewhere (docs/legal/PRIVACY.md): the job does not
    /// ping Semgrep's anonymous usage endpoint.
    /// </summary>
    [Fact]
    public void Semgrep_workflow_disables_metrics()
    {
        Assert.Contains("--metrics off", Read("semgrep.yml"));
    }

    /// <summary>
    /// The analyser image is pinned by digest for the same reproducibility reason 10c.3 pins the
    /// app's own base images. A floating <c>semgrep/semgrep:latest</c> would silently change what
    /// CI runs the next time the tag moves.
    /// </summary>
    [Fact]
    public void Semgrep_image_is_pinned_by_digest()
    {
        Assert.Matches(DigestPinnedImage, Read("semgrep.yml"));
    }

    /// <summary>
    /// Every third-party action this repo calls is pinned to a full commit SHA (Phase 10c.2, found
    /// by the same Semgrep job this test is named after) rather than a mutable tag or branch - a
    /// moved tag is exactly how the trivy-action and kics-github-action supply-chain compromises
    /// happened. Enumerating the directory, rather than a hardcoded list, is the point: a new
    /// workflow that ships <c>uses: some/action@v3</c> fails this even if nobody added it to
    /// <see cref="Claimed_workflow_file_exists"/>.
    /// </summary>
    [Fact]
    public void Every_workflow_pins_third_party_actions_to_a_commit_sha()
    {
        var files = WorkflowFiles().ToList();
        Assert.True(files.Count > 0, ".github/workflows/ contained no .yml/.yaml files; the layout changed.");

        foreach (var fileName in files)
        {
            var lines = Read(fileName)
                .Split('\n')
                .Where(line =>
                {
                    var trimmed = line.TrimStart();
                    return trimmed.StartsWith("- uses:") || trimmed.StartsWith("uses:");
                })
                .ToList();

            Assert.True(lines.Count > 0, $"{fileName}: no `uses:` steps matched; the workflow's shape changed.");
            Assert.All(lines, line => Assert.Matches(ShaPinnedAction, line));
        }
    }

    /// <summary>
    /// Phase 10c.1's weekly dump stays off until GitHub's DPA is accepted. A run copies production
    /// onto a GitHub-hosted runner, and that processing has no Art. 28 contract yet
    /// (docs/legal/DPA-CHECKLIST.md). <c>workflow_dispatch</c> remains so the job can be turned
    /// back on by hand in the same change that records the acceptance; an uncommented schedule
    /// before that marker is removed is the failure this test exists to catch.
    /// </summary>
    [Fact]
    public void Offsite_backup_workflow_stays_manual_until_the_github_dpa_is_filed()
    {
        var yaml = Read("offsite-backup.yml");

        Assert.Contains("github-dpa-before-schedule", yaml);
        Assert.Contains("workflow_dispatch:", yaml);
        Assert.Contains("scripts/offsite-backup.sh", yaml);

        var activeSchedule = yaml.Split('\n').Any(line =>
        {
            var trimmed = line.TrimStart();
            return !trimmed.StartsWith('#') &&
                   (trimmed.StartsWith("schedule:") || trimmed.StartsWith("cron:") || trimmed.StartsWith("- cron:"));
        });
        Assert.False(activeSchedule, "offsite-backup.yml has an active schedule. File GitHub's DPA first, then restore the Sunday cron in the same change.");
    }

    /// <summary>
    /// restore-drill.yml (PITR inside Neon) and offsite-backup.yml (dump out of Neon) both create
    /// a compute endpoint on the same project. Overlapping them is how a leaked branch starts
    /// costing money; the shared concurrency group is the thing that prevents that, and it has to
    /// stay the same string in both files.
    /// </summary>
    [Fact]
    public void Neon_branching_workflows_share_a_concurrency_group()
    {
        const string group = "neon-branch-jobs";
        Assert.Contains($"group: {group}", Read("restore-drill.yml"));
        Assert.Contains($"group: {group}", Read("offsite-backup.yml"));
    }

    /// <summary>
    /// Phase 10c.5: a synthetic that only runs when someone remembers to press the button is not
    /// watching whether a game can be played. The schedule plus <c>workflow_dispatch</c> is the
    /// claimed control; the two scripts are the claimed probe.
    /// </summary>
    [Fact]
    public void Ops_probes_workflow_runs_on_a_schedule_and_on_dispatch()
    {
        var yaml = Read("ops-probes.yml");

        Assert.Contains("schedule:", yaml);
        Assert.Contains("cron:", yaml);
        Assert.Contains("workflow_dispatch:", yaml);
        Assert.Contains("scripts/synthetic-play.sh", yaml);
        Assert.Contains("scripts/neon-watch.sh", yaml);
    }

    /// <summary>
    /// I-2-4: while Redis is off, a backend deploy must not create Fly's default HA pair. Two
    /// machines with an in-process GameRegistry is silent split-brain, not an error. UI deploys
    /// are stateless and are not required to carry the flag.
    /// </summary>
    [Fact]
    public void Backend_deploys_pin_ha_false()
    {
        var yaml = Read("deploy.yml");
        var backendDeploys = yaml
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line =>
                line.Contains("flyctl deploy", StringComparison.Ordinal)
                && line.Contains("fly.backend", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            backendDeploys.Count > 0,
            "deploy.yml: no flyctl deploy line mentioning fly.backend matched; the workflow's shape changed.");
        Assert.All(
            backendDeploys,
            line => Assert.Contains("--ha=false", line, StringComparison.Ordinal));
    }

    /// <summary>
    /// I-3-5: <c>deploy.yml</c>'s own safety net was the one CI claim this class did not hold - a
    /// refactor that dropped the <c>verify-main</c> job, or its ancestor check, or narrowed it to
    /// something other than the tag/manual-production trigger, would have passed every test above.
    /// <c>docs/ops/DEPLOYMENT.md</c> states "Only commits reachable from main can deploy" as a
    /// load-bearing property; this is the test that makes removing the check fail the build.
    /// </summary>
    [Fact]
    public void Deploy_workflow_verifies_main_before_tag_or_manual_production_deploy()
    {
        var yaml = Read("deploy.yml");

        Assert.Contains("verify-main:", yaml);
        Assert.Contains("git merge-base --is-ancestor", yaml);
        Assert.Contains(
            "if: startsWith(github.ref, 'refs/tags/v') || (github.event_name == 'workflow_dispatch' && github.event.inputs.environment == 'production')",
            yaml);
    }

    /// <summary>
    /// I-3-5 / I-3-1: <c>verify-main</c> only proves the tagged commit is on <c>main</c>, not that
    /// CI passed on it, since <c>ci.yml</c> never runs on a tag push. <c>require-ci-success</c> is
    /// the gate that closes that hole; this pins that both the job and its dependency on the
    /// production deploy jobs exist, not merely that some job somewhere queries the Actions API.
    /// </summary>
    [Fact]
    public void Deploy_workflow_requires_ci_success_before_production_deploy()
    {
        var yaml = Read("deploy.yml");

        Assert.Contains("require-ci-success:", yaml);
        Assert.Contains("workflows/ci.yml/runs", yaml);
        Assert.Contains("needs: [verify-main, require-ci-success]", yaml);
    }

    /// <summary>
    /// I-3-5: staging deploys off every push to <c>main</c> with no ancestor check, which is
    /// correct (it IS the push to main) but only if the trigger stays exactly that push, not some
    /// broader condition a future edit could widen to an arbitrary branch.
    /// </summary>
    [Fact]
    public void Deploy_workflow_gates_staging_backend_on_push_to_main()
    {
        var yaml = Read("deploy.yml");

        Assert.Contains(
            "if: (github.event_name == 'push' && github.ref == 'refs/heads/main') || (github.event_name == 'workflow_dispatch' && github.event.inputs.environment == 'staging')",
            yaml);
    }

    /// <summary>
    /// I-3-6: a partial production release (backend shipped, UI failed or the run was cancelled
    /// between the two) leaves two different wire contracts live with nothing but the run's own
    /// colour saying so. This pins that an explicit completeness check still exists and still
    /// depends on both halves, rather than a future edit quietly dropping it as unreachable dead
    /// code because the happy path never fails it.
    /// </summary>
    [Fact]
    public void Deploy_workflow_confirms_both_apps_land_together_on_a_tagged_release()
    {
        var yaml = Read("deploy.yml");

        Assert.Contains("production-release-complete:", yaml);
        Assert.Contains("needs: [production-backend, production-ui]", yaml);
        Assert.Contains("needs.production-backend.result", yaml);
        Assert.Contains("needs.production-ui.result", yaml);
    }

    /// <summary>
    /// I-3-4: superfly/flyctl-actions has no tagged releases upstream, so a commit-SHA pin on it
    /// could never be bumped by Dependabot - the exact thing the SHA-pin sweep above cannot catch,
    /// since a frozen-forever SHA is still a valid 40-hex SHA. Asserting the action's absence is
    /// the only way to pin "this stays fixed", rather than re-introducing a pin nothing can move.
    ///
    /// It checks <c>uses:</c> lines rather than the whole file on purpose. A bare substring search
    /// also matches the comment in deploy.yml that explains why the action was removed, so it would
    /// fail the moment anyone documented the decision, and the cheapest way to make it pass again
    /// would be to delete that explanation. The property worth pinning is that no step depends on
    /// the action, not that the file never says its name.
    /// </summary>
    [Fact]
    public void Deploy_workflow_does_not_depend_on_flyctl_actions()
    {
        var usesLines = Read("deploy.yml")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- uses:", StringComparison.Ordinal)
                || l.StartsWith("uses:", StringComparison.Ordinal));

        Assert.DoesNotContain(usesLines, l => l.Contains("flyctl-actions", StringComparison.Ordinal));
    }

    /// <summary>
    /// I-2-8: the Fly-to-Neon string checks the certificate. <c>SSL Mode=VerifyFull</c> is the
    /// mode in the deploy doc and both scripts that open that hop. The local dump-restore target
    /// stays <c>SSL Mode=Disable</c> because that Postgres has no certificate.
    /// </summary>
    [Fact]
    public void Neon_connection_strings_use_verify_full()
    {
        var root = OpenApiExport.FindRepoRoot();
        var deployment = File.ReadAllText(Path.Combine(root, "docs", "ops", "DEPLOYMENT.md"));
        var restoreDrill = File.ReadAllText(Path.Combine(root, "scripts", "restore-drill.sh"));
        var offsite = File.ReadAllText(Path.Combine(root, "scripts", "offsite-backup.sh"));

        Assert.Contains("SSL Mode=VerifyFull", deployment, StringComparison.Ordinal);
        Assert.Contains("SSL Mode=VerifyFull", restoreDrill, StringComparison.Ordinal);
        Assert.Contains("SSL Mode=%s", offsite, StringComparison.Ordinal);
        Assert.Contains(
            "parse_postgres_uri_to_npgsql \"$RESTORE_DATABASE_URL\" VerifyFull",
            offsite,
            StringComparison.Ordinal);
        Assert.Contains("SSL Mode=Disable", offsite, StringComparison.Ordinal);
        Assert.DoesNotContain("Trust Server Certificate", deployment, StringComparison.Ordinal);
        Assert.DoesNotContain("Trust Server Certificate", restoreDrill, StringComparison.Ordinal);
        Assert.DoesNotContain("Trust Server Certificate", offsite, StringComparison.Ordinal);
    }
    
    /// <summary>
    /// I-3-3: one repository <c>FLY_API_TOKEN</c> used to deploy all four apps. Each job now reads
    /// an environment secret scoped to that one app. The same secret name on
    /// <c>environment: production</c> and <c>environment: staging</c> is a different token.
    /// A bare <c>secrets.FLY_API_TOKEN</c> (no app suffix) would put the wide credential back.
    /// </summary>
    [Fact]
    public void Deploy_workflow_uses_per_app_fly_tokens()
    {
        var yaml = Read("deploy.yml");

        Assert.Equal(4, CountOccurrences(yaml, "secrets.FLY_API_TOKEN_API }}"));
        Assert.Equal(4, CountOccurrences(yaml, "secrets.FLY_API_TOKEN_UI }}"));
        Assert.DoesNotContain("secrets.FLY_API_TOKEN }}", yaml, StringComparison.Ordinal);
        Assert.Contains("environment: production", yaml, StringComparison.Ordinal);
        Assert.Contains("environment: staging", yaml, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var start = 0;
        while (start <= text.Length - value.Length)
        {
            var found = text.IndexOf(value, start, StringComparison.Ordinal);
            if (found < 0)
                break;
            count++;
            start = found + value.Length;
        }

        return count;
    }
}
