<!--
  PR TITLE — must follow Conventional Commits:
    feat: add server scheduling
    fix: steam reconnect loop
    perf(db): index ServerInstance by status
    chore: bump SteamKit2 to 3.1.0
    feat!: breaking change (bumps major version)

  Branch rules:
    feat/<name>, fix/<name>, chore/<name>  →  target: caster
-->

## Summary

<!-- What does this PR do? One paragraph is enough. -->

Closes #<!-- issue number, or remove this line -->

---

## Type of Change

<!-- Check all that apply -->

- [ ] `feat` — new feature
- [ ] `fix` — bug fix
- [ ] `perf` — performance improvement
- [ ] `refactor` — code restructure, no behaviour change
- [ ] `docs` — documentation only
- [ ] `chore` / `ci` — build, tooling, dependencies
- [ ] **Breaking change** — existing behaviour changes (add `!` to the PR title type)

---

## What Changed

<!-- Bullet points are fine. Focus on the *why*, not just the *what*. -->

-

---

## Testing

<!-- How did you verify this works? Which tests cover it? -->

- [ ] Existing tests pass (`dotnet test`)
- [ ] New tests added for the changed behaviour
- [ ] Manually tested — describe below if relevant

---

## Checklist

- [ ] PR title follows [Conventional Commits](https://www.conventionalcommits.org/) format
- [ ] Targets `caster`
- [ ] No unrelated changes mixed in (whitespace, refactors, unrelated fixes)
- [ ] EF Core migration included if any entity model changed
- [ ] I have read [CONTRIBUTING.md](../CONTRIBUTING.md)

