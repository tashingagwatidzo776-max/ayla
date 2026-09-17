
## Pre-push hook (stale-copy caveat)

The hook file lives in the repo, but Git does not auto-update already-enabled
clones: after pulling a commit that changes `scripts/git-hooks/pre-push`, an
enabled clone keeps running the **old** copy until you run
`git config core.hooksPath scripts/git-hooks` again (re-running the config
command re-resolves it to the new file). This is why the runbook documents it
here rather than as "set and forget".
