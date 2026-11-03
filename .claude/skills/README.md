# Claude Code Skills

Skill packages used when developing IoTSpy. Skills extend Claude's behavior with project-relevant expertise and activate automatically when relevant, or via `/skill-name`.

Each skill is a directory containing a `SKILL.md` with YAML frontmatter and markdown instructions. Skills under `.claude/skills/<name>/SKILL.md` are auto-discovered by Claude Code — no install step required.

## Skills

| Skill | Description |
|-------|-------------|
| `dotnet-engineer/` | Senior .NET engineering guidance — ASP.NET Core, EF Core, SignalR, Polly, xUnit/NSubstitute (project-agnostic) |
| `security-code-review/` | Systematic security review across input handling, authz, resources, errors, crypto, secrets, and supply chain |
| `threat-modeling/` | Structured threat modeling — STRIDE + OWASP + ATT&CK, calibrated severity, dual-use tool considerations |
| `iotspy-context/` | IoTSpy-specific architecture, conventions, and security caveats — companion to the three skills above |
| `publish-hygiene/` | Pre-commit/push/PR scan for details of the developer's own environment (public repo); generic patterns + a git-ignored private denylist in `.claude/sensitive-patterns.local.txt` |

The first three skills are deliberately project-agnostic so they don't drift as IoTSpy evolves. `iotspy-context` carries the project-specific layer and points back to `CLAUDE.md` / `AGENT.md` for facts that change (test counts, migration count, completed phases) instead of duplicating them.

## Usage

Skills activate automatically when relevant context is detected. Invoke explicitly with:

- `/dotnet-engineer` — .NET architecture, EF Core, SignalR, Polly, testing
- `/security-code-review` — security review before merging
- `/threat-modeling` — threat model for a new feature or design
- `/iotspy-context` — IoTSpy-specific facts and risks (use alongside the others when working in this repo)
- `/publish-hygiene` — before every commit, push or PR

## Updating skills

Edit `<name>/SKILL.md` directly. Skills are loaded from disk; changes take effect in the next session.

When IoTSpy state changes (test counts, migration count, completed phases), update `CLAUDE.md` and `AGENT.md` — the `iotspy-context` skill points back to those rather than duplicating.