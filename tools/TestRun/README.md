# TestRun

Runs the normal test suite (`tests/GameOne.Tests`) split across several `dotnet test` processes at
once, and keeps the record of test times. The tests run one at a time within a process, as Jolt's
allocator, factory and type registry are process-wide; separate processes each have their own, so
they can run side by side.

```sh
dotnet run --project tools/TestRun -- [--shards N] [--before-merge] [--record] [--keep-results DIR] [--machine-key]
```

It builds the solution once (Debug), lists the tests (`dotnet test --list-tests`), splits their
classes into shards balanced by this machine's times in the record (a test it has no time for
counts 1 s), and starts every shard at once, each filtered to its classes. Any test marked
`[Trait("Run", "Alone")]` is left out of the shards and run afterwards in a process of its own.
The long tests carry `[Trait("Run", "Long")]` (a test in `TestRunTests` checks every one does);
a normal run skips them, so it counts them nothing in the balance, though the record keeps their
times. Then it checks every listed test has exactly one result (a skipped test's counts) and
names any listed test with none or any result not listed. It prints each process's time, the
tests much slower than the record (at least 50% and 1 s slower), the tests over the limit (7 s,
`Report.LimitSeconds`, worked out from the record), each failed test, and the totals (passed,
failed, skipped) with the wall time. It exits 0 only if every process exited 0 and gave results
that could be read, every listed test had its one result, and no test failed. A results file
that can't be read is named with its shard, and the rest is still reported. A test that runs for
10 minutes is taken as hung (`--blame-hang-timeout`): its process is stopped, and the test named.

- `--shards`: how many processes share the suite (default: 8, the count measured fastest on a
  machine of 22 logical cores; fewer in proportion on fewer cores, never fewer than 1).
- `--before-merge`: sets `GAMEONE_LONG=1` in the tests' environment (the long playthroughs and
  the other long tests, `[PlaythroughFact(Long = true)]` and `[LongFact]`, then run and are
  balanced by their recorded times), for the run before a merge. Without it, `GAMEONE_LONG` is
  kept out of the tests' environment even if it is set in the shell, so a normal run is always
  the normal suite.
- `--record`: after a run that passed, writes this machine's Debug entry into
  `tests/GameOne.Tests/TestTimes.json` (when, on what commit, in how many shards, the wall time and
  each test's seconds: an executed one's own, a skipped one's old time kept, so a normal run's
  record keeps the long tests' times), keeping every other machine's. Take it on purpose and
  commit it, as `PerfBaseline.json` is: the record only reports, and a run never fails because a
  test got slower. It is taken with the default process count, so `--record` with `--shards`
  other than the default is refused: a run is compared with the record, and the limit worked out
  from it, in times taken under the same load. Take it with `--before-merge`, so the long tests'
  times are in it and the balancer knows them before a merge (a normal `--record` keeps what is
  there for them).
- `--keep-results DIR`: writes each process's TRX file (`shard0.trx` and on, `alone.trx`) into
  `DIR` and keeps them, to compare two runs test by test or to read one in full. `DIR` must be
  empty or not yet there, so that one run's files are never read as another's. There is no
  merged file: the TRX files together are the run.
- `--machine-key`: prints this machine's key and exits.

The record is keyed by the machine's hashed name, CPU and processor count (`Machine.Key`, the same
key as `PerfBaseline.json`'s), so it names no machine; Debug's times only, as that is what
`dotnet test` builds.

Plain `dotnet test GameOne.slnx` still runs the same tests, one process, serially.

The perf (`ShortPerfTests`, `PerformanceTests`) and soak tests (`SoakTests`) are skipped here, as
they are in a plain run: they keep their own commands (see `tests/GameOne.Tests/README.md`), since
sharing the machine would spoil their numbers. The runner keeps `GAMEONE_PERF`, `GAMEONE_SOAK` and
`GAMEONE_WATCH` (and `GAMEONE_LONG`, but for `--before-merge`) out of the tests' environment even
if they are set in the shell.

Ctrl+C stops every process it started, with their test hosts. It deletes its results folder
(unless `--keep-results`) and shuts down the build servers however it ends.
