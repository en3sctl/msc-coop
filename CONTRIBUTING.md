# Contributing to msc-coop

Contributions are welcome through pull requests to this repository. The rules
below are modelled on the Linux kernel's process. A PR that does not follow
them will be sent back without review.

## License and sign-off

The project is licensed under CC BY-NC-SA 4.0 (see `LICENSE`). By contributing
you agree that your work is published under the same license.

Every commit must carry a `Signed-off-by:` line with your real name or a
consistent public handle, certifying the
[Developer Certificate of Origin](https://developercertificate.org/):

    Signed-off-by: Jane Doe <jane@example.com>

`git commit -s` adds it for you. Commits without a sign-off are not merged.

Do not submit code copied from closed-source mods, decompiled mods or any
source whose license is incompatible with CC BY-NC-SA 4.0. Code taken from
MIT-licensed projects must keep its original copyright notice.

## Commits

One logical change per commit. A commit must build and pass the tests on its
own, so `git bisect` keeps working. Do not mix refactoring, formatting and
behaviour changes in one commit.

Message format:

    area: short imperative summary under 72 chars

    Explain what the problem was and why this change fixes it. Wrap the
    body at 72 columns. Describe the reasoning, not a line-by-line
    narration of the diff.

    Fixes: #123
    Signed-off-by: Jane Doe <jane@example.com>

- `area` is the part of the tree you touched: `net`, `session`, `transport`,
  `vehicles`, `fsm`, `recon`, `docs`, `build`.
- The summary is in the imperative mood: "net: fix sequence wrap compare",
  not "fixed" or "fixes".
- No trailing period in the summary. No emoji.
- No tool or generator attribution trailers or footers. The author of a
  commit is the person who signs it off and answers for it.

## Pull requests

- Rebase on the current `master` before opening a PR. No merge commits.
- Keep a PR to one topic. Large features are split into a reviewable series.
- The PR description says what changed, why, and how you tested it in game
  (game build, MSCLoader version, number of players, network conditions).
- Address review comments by amending the relevant commits and
  force-pushing, not by stacking "fix review" commits.

## Code style

- Match the surrounding code. Consistency beats personal preference.
- C# targeting .NET 3.5 (Mono 2.0 in Unity 5.0). No APIs that do not exist
  there: no `Task`, `async`, `ValueTuple`, `Span`, `Math.Clamp`.
- No allocations on the per-frame or per-packet path: no LINQ, no `foreach`
  over interfaces, no string building, no closures, no boxing. Allocate at
  load time and reuse.
- Cache `GameObject.Find`, `GetComponent` and PlayMaker variable lookups at
  load time. Never call them per frame.
- Network input is untrusted. Bound every length and index read from a
  packet. Malformed packets are dropped, never thrown on.
- Comments explain *why*, never *what*. If the code needs a comment to say
  what it does, rewrite the code. No banner comments, no commented-out code,
  no XML doc boilerplate restating member names.
- No debug logging left in submitted code.

## Testing

- `dotnet test` must pass. New logic in `MSCCoop.Net` comes with unit tests.
- Game-facing changes must be tested in game with at least two players, and
  with the built-in network simulator at 150 ms latency and 3% loss.
- Report regressions with the session log and, for desyncs, the dump files
  produced by the desync detector.

## Reporting bugs

Open an issue with the game build, MSCLoader version, the list of installed
mods, what you expected, what happened, and the `output_log.txt` of every
player involved.
