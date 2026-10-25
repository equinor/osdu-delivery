## Summary

<!-- What the repository does after this change, and why. Link the issue it closes, if any (Closes #123). -->

## How it was verified

<!-- The suites, builds and checks you ran, and anything tried by hand (a page rendered, a CLI run, a flow planned).
     A check that reached a live OSDU names its approval and how its ids were cleaned up (CLAUDE.md). -->

## Checklist

- [ ] Tests added or updated for the behaviour changed (a `sqlflow/` extension point gets its own, in `sqlflow/`)
- [ ] The unit tier passes (`bash tools/test.sh`, which the pre-push hook runs). The full tier
      (`bash tools/test.sh --full`) runs on `main` once this lands; name here any suite this change needs that has not
      run yet, or add the `full-tests` label to run it on this pull request
- [ ] `dotnet build OsduDelivery.sln -c Release -t:Rebuild` has zero warnings and zero errors
- [ ] `osdu/gui`: `npm run build` and `npm run lint` pass
- [ ] A change to the catalog or the OSDU model ships with its migration, designer file and refreshed model snapshot
- [ ] `sqlflow/` changes only as generic extension points, each in its own `sqlflow:` commit with a row in
      `docs/sqlflow-changes.md`, and `bash tools/check-vendored-sqlflow.sh` passes
- [ ] No em dash (U+2014) in anything written, commit messages included
      (`bash tools/check-conventions.sh origin/main HEAD` checks the lines added)
- [ ] No literal secret anywhere: only `${env:NAME}` and `${keyvault:...}` references
- [ ] Docs updated with the behaviour, `osdu/docs/reference/manifest.json` regenerated when a page or its frontmatter
      changed, and `CHANGELOG.md` has its entry
- [ ] Each commit lands as written (rebase merge): its subject says what the repository now does, and no commit or
      text here carries AI attribution
