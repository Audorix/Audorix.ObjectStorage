# Package-Only Soak Workflow

The workflow restores the harness against the pinned `0.1.0-rc.2` candidate packages, publishes a private executable, verifies candidate hashes, and launches a run-owned controller without placing the PostgreSQL connection string on the command line.

```powershell
.\soak\Launch.ps1 -DurationSeconds 7200 -Concurrency 4 -SmallKiB 64 -LargeMiB 4 -DiskBudgetMiB 1024
.\soak\Status.ps1
.\soak\Stop.ps1
```

`Status.ps1` reads periodic checkpoints. `Stop.ps1` validates PID, exact process start time, and the run marker before stopping the controller, then validates the recorded child-host start time before stopping that process. It never selects a process by port.

Each run receives a unique PostgreSQL source schema, bucket, credentials, blob root, restore database, and restored blob root. The successful path removes only those resources after reconciliation, restart, backup, restore, restored-host verification, and an original-dataset recheck. Summaries and logs remain under ignored `.soak/runs/<run-id>`.

The isolated host uses a five-second unreferenced-blob retention period. During the workload, the controller invokes the existing cleanup operation every two seconds so overwrite/delete churn stays within the declared disk budget. One-minute resource samples can miss shorter peaks; the final report treats that sampling interval as a measurement limitation.

The default two-hour acceptance budgets are recorded before the run in its checkpoint and final summary. Data-operation p95 must remain at or below 2 seconds. The maintenance cleanup p95 budget is 10 seconds, and intentionally throttled controlled cancellation has a 5-second p95 budget. Operation summaries include p50, p95, and p99. Resource checkpoints include heap, working set, cumulative server CPU, root bytes, staging files, and PostgreSQL connection/database indicators. The intended-server gate remains pending unless this workflow is run on that server with an externally supplied isolated PostgreSQL connection.
