# Capacity and Operating Limits

## Scope

These are cache-warm local measurements for `0.1.0-rc.2` packages built from source commit `4c5bbe953687023018eb3d2ca230f75e4e6f6c94`. Client, package-only Kestrel host, filesystem blobs, and PostgreSQL 18.3 shared one GMKtec NucBox M3 PLUS (Intel i9-12900HK, 14 cores/20 logical processors, 63.7 GiB RAM, Windows 10.0.26200). They are not an intended-server benchmark or SLA.

The final clean run used 15-second closed-loop stages, 64 KiB small objects, client concurrency 1/2/4/8/16, at most 1,000 retained listing objects, 16 and 64 MiB transfers at concurrency 1/2/4, and an overload phase capped at 16 clients. The harness reserved 2 GiB free disk beyond its estimate. AWS SDK retries were disabled, so a completed operation is not a hidden retry.

## Mixed Operations

Each small-object iteration performed PUT, full hash-verified GET, ListObjectsV2, a 4 KiB range GET, and DELETE. Attempts equaled completions and unexpected failures/timeouts/retries were zero.

| Concurrency | Operations | Operations/s | Upload p50 | Upload p95 | Upload p99 | Read p95 | Avg server CPU |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 695 | 46.17 | 77.83 ms | 206.23 ms | 238.49 ms | 21.94 ms | 2.10% |
| 2 | 455 | 30.01 | 231.77 ms | 475.17 ms | 3,045.23 ms | 55.08 ms | 1.93% |
| 4 | 390 | 25.16 | 641.34 ms | 1,376.09 ms | 1,741.68 ms | 112.27 ms | 1.90% |
| 8 | 595 | 37.98 | 872.75 ms | 2,412.88 ms | 2,578.34 ms | 112.08 ms | 1.80% |
| 16 | 1,330 | 87.44 | 862.48 ms | 1,292.84 ms | 1,411.65 ms | 25.69 ms | 3.34% |

The rate is non-monotonic and the stages are short. Shared-machine scheduling, cache state, and the closed-loop client affect the result; client saturation cannot be separated from server capacity here. Low CPU and one observed PostgreSQL connection indicate that CPU saturation was not reached. The configured safety ceiling of 16 clients was reached without a clear saturation knee, so this run does not establish maximum capacity.

At stage ends, managed heap ranged from 5.7 to 9.7 MiB, working set from 83.8 to 94.8 MiB, root bytes from 4.9 to 16.6 MiB, staging files were zero, and the database connection indicator was one. Database size was approximately 10-11 MiB. These are endpoint samples, not sub-second peaks or query-latency traces.

## Listing and Transfers

Twenty complete paginated listings were measured at each retained count with page size 100:

| Objects | p50 | p95 | p99 | Complete listings/s |
|---:|---:|---:|---:|---:|
| 10 | 4.63 ms | 15.29 ms | 58.24 ms | 126.26 |
| 100 | 7.62 ms | 17.70 ms | 29.86 ms | 104.84 |
| 1,000 | 43.59 ms | 62.97 ms | 82.69 ms | 21.72 |

The bounded 64 MiB transfers reached 3.80/60.12/46.85 MiB/s upload and 66.38/54.50/26.23 MiB/s download at concurrency 1/2/4. The slow single-upload result is an observed outlier and is retained in the evidence. Server managed-heap growth stayed below 4 MiB and working-set growth below 6.4 MiB across 16/64 MiB upload, download, and range cases. This verifies those sizes only, not the configured 512 MiB object ceiling.

## Admission and Recovery

With four active operations, two queued operations, 16 simultaneous 8 MiB slow uploads, and client retries disabled, six requests completed and ten received expected S3 `SlowDown` responses. There were no unexpected failures. Staging returned to zero and an immediate PUT/GET/DELETE recovery request succeeded. This demonstrates bounded admission and permit release for the tested profile.

## Conservative Envelope

For a controlled pilot on this exact local topology, start with client concurrency 4, server admission 8 active plus 4 queued, 64 MiB maximum objects, ListObjectsV2 page size 100, and at least a 2 GiB free-space reserve beyond expected retained and staging bytes. Alert on any staging growth after requests drain, unexpected 5xx, PostgreSQL pool pressure, or sustained p95 above the application target.

This envelope intentionally keeps headroom below the tested client ceiling and below the package host's 32/8 soak configuration. Remeasure on the intended server with separate client and server machines, realistic network/TLS, real disk, intended PostgreSQL pool/timeouts, representative object distribution, and longer stages before changing it. Do not infer shared-hosting, multi-node, network-filesystem, or production SLA support.

Machine-readable evidence: [thirteenth-capacity-summary.json](thirteenth-capacity-summary.json).
