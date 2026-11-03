---
name: publish-hygiene
description: Review commits, commit messages and PR descriptions for details of the developer's own environment before they are committed, pushed or posted — lab IPs and hostnames, interface names, MACs, device fingerprints, account/device IDs, which devices are on the network and who makes them, infrastructure names, credentials. This repo is public. Use before every commit, push, PR creation or PR edit, and when asked whether something is safe to publish.
---

# Publish hygiene

IoTSpy is a **public** repository, and it is developed by pointing it at real devices on a real network. Investigation details leak easily into test fixtures ("use the IP I just saw"), code comments ("e.g. the X gateway"), commit messages ("seen on <lab host>") and PR descriptions. Once pushed, rewriting history doesn't fully undo it: GitHub keeps old commits reachable from PRs, and closed PRs can't be deleted without GitHub Support.

## When

Before **every** `git commit`, `git push`, `gh pr create` and `gh pr edit`/comment in this repo. Not just at the end: catch it in the commit, where fixing it is a one-line edit instead of a history rewrite.

## How

1. Run the scanner from the repo root (scans `origin/main...HEAD`, staged and unstaged changes, all commit messages in range, and any extra files such as a PR body draft):
   ```bash
   .claude/skills/publish-hygiene/scan.sh origin/main path/to/pr-body.md
   ```
   It applies generic patterns plus the developer's private denylist in `.claude/sensitive-patterns.local.txt` (git-ignored, never commit it, never quote its contents into tracked files, commits or PRs).
2. Read every hit and judge it. The scanner over-reports on purpose (e.g. `10.0.0.1` in an existing test, a GUID-like hash). It also can't catch everything, so read the diff and the messages yourself for the categories below.
3. Fix leaks **before** committing or pushing: swap in neutral values and re-run tests (placeholders can change test semantics: `telemetry.example.com` matches a pattern `example.com`; a substring assertion on an old IP no longer matches).
4. If something already leaked to the remote, stop and tell the user. Rewriting public history or closing PRs is their call.

## What counts as sensitive here

| Leak | Use instead |
|---|---|
| Lab/home IPs (RFC 1918), gateway addresses | `192.0.2.x`, `198.51.100.x`, `203.0.113.x` (RFC 5737) |
| Interface names from the dev box/VM (e.g. `enp3s0`) | `eth0`, `eth1` |
| MACs, OUIs, DHCP hostnames of real devices | made-up values, `aa:bb:cc:...` |
| Which devices or brands are on the network, and what they do | "a device", "an IoT gateway", "a telemetry uploader" |
| Real device fingerprints (JA3/JA3S), cert serials, real protocol magic bytes from a specific device | synthetic values |
| Account, user, device or session IDs pulled from captures | obviously synthetic IDs |
| Lab/infra names (VM names, SSH aliases, hostnames, deploy paths), personal paths | omit, or "a test VM" |
| Credentials, tokens, captured auth headers or bodies | never |

Fine to keep: well-known public hostnames used as generic examples (`example.com`, `firestore.googleapis.com`, `gateway.icloud.com`), documentation IP ranges, and loopback.

## Writing tests and messages without leaking

- Write fixtures with neutral values **from the start**, even when the bug was found on a real device.
- Describe *behaviour*, not the environment: "a client that reconnects ~48 times a minute", not "<brand> device at <ip>".
- PR test plans: "deployed to a test VM", not the VM's name.
