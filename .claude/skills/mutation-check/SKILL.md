---
name: mutation-check
description: Prove a Fonoteca change's tests bite by running scripted source mutants against single xUnit v3 test methods in a scratch worktree. Use before handing a .NET change to the verifier, or when a verifier reports surviving mutants.
---

# Mutation check for a .NET change

Run it in the scratch worktree, never in the live repo: `dotnet watch` runs the API from there.

1. List the mutants as tuples: `(name, file, exact snippet, replacement, test args)`.
   - The snippet must occur exactly once. Assert `count == 1` so a stale snippet fails loudly instead of testing nothing.
   - Never mutate a condition to a literal `false`/`true`: CS0162 (unreachable code) is an error here, so the mutant only fails the build. Use an always-false comparison instead (`if (id == Guid.Empty)`).
   - Pick one mutant per branch, guard and filter the change adds. The ones that pay off most are: drop a guard (`&& row.ParentId == null` → ``), invert an early return (`return X;` → `continue;`), drop a fallback (`a ?? b` → `a`), drop a written attribute, and widen a filter.
   - Test args go to the xUnit v3 binary directly: `-method Fonoteca.Integration.Tests.Class.Method` or `-class ...`. Not `--filter`, and not `dotnet test`, which hangs at exit.
2. Loop in Python. For each mutant:
   - write the mutated file
   - `dotnet build <test project> -m:1 -nr:false`
   - run `bin/Debug/net10.0/<Project>` with the args
   - parse `Total: n, Errors: e, Failed: f` after stripping ANSI codes
   - in a `finally`, restore the original text **and `os.utime` the file**, or the next build can skip recompiling it.
   - `Failed > 0` is "killed". A build failure is its own outcome: report it, never count it as killed.
   - Let the script take mutant names as arguments, so a run that was killed partway can resume. A kill skips the `finally`, so afterwards check that every snippet is back in its file, and restore any that isn't, before you build anything.
3. End with an unmutated run of the same tests. Without a clean pass, "killed" might mean "always failing".
4. Set `TMPDIR` to scratch in the subprocess env (the test corpus leaks `/tmp`), and `source ~/.local/opt/env.sh` in the shell that starts the script.

A survivor is a test gap until shown otherwise. The usual causes:
- **The shared class database hides it.** One album in the database makes "starred albums" and "all albums" the same list. Assert the empty case after an unstar or delete, not only the full one.
- **A check nothing exercises.** An id that exists against one that doesn't: add the not-found request.
- **A boundary the fixtures never reach.** Add the cases one either side of it.

Close the gaps test-only, then rerun just the survivors plus a clean run: copy the script, filter `M` down to those names.
