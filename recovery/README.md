# Failure-Recovery Harness

Run from the repository root:

```powershell
.\recovery\Run.ps1
```

The script verifies the pinned rc.2 packages and runs a package-only host with a unique schema, root, bucket, credentials, and a run-owned PostgreSQL TCP proxy. It interrupts only proxy connections owned by the run; it never stops PostgreSQL. The disk fault replaces the run-owned staging directory with a regular file, which is an explicit injected I/O-path failure and not a disk-full test.

Process termination phases use observable barriers: a non-empty staging file before blob publication, successful client acknowledgment, and a newly published blob before metadata outcome is known. The report separates acknowledged writes from uncertain outcomes, verifies only complete old or new bytes, exercises restart reads/list/range/overwrite/delete, and reconciles metadata, blobs, and staging after cleanup. Process termination is not power-loss certification.

Temporary state is under ignored `.recovery`; the final summary is `docs/thirteenth-recovery-summary.json`.
