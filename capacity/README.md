# Capacity Harness

Run from the repository root:

```powershell
.\capacity\Run.ps1 -StageSeconds 15 -Concurrency 1,2,4,8,16 -SmallObjectKiB 64 -LargeObjectMiB 64 -MaxListObjects 1000
```

The script verifies the pinned rc.2 hashes, restores and publishes the harness from the candidate-only NuGet configuration, and creates unique schemas, roots, buckets, and child processes. It runs closed-loop small-object stages, 10/100/1,000-object paginated listing stages, bounded 16/64 MiB transfers, and a 16-client offered-load phase against four active plus two queued server operations. The overload phase requires explicit `SlowDown` responses and a successful recovery request.

Defaults cap stage duration, concurrency, object count, retained bytes, and large-transfer size. The transfer harness requires its estimate plus 2 GiB of free disk. Results are cache-warm, single-machine measurements: the load generator, Kestrel host, filesystem, and PostgreSQL share the local computer. Do not translate them into an intended-server SLA.

Temporary files and package caches are under ignored `.capacity`; the final machine-readable summary is `docs/thirteenth-capacity-summary.json`.
