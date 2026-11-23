# Deployment Performance Clarification

## The 396.892-second workflow

The fourteenth-slice number came from an ad hoc PowerShell loop, not the load harness. Its stopwatch started after the temporary directory and 256 KiB source file were created, and stopped before temporary-file cleanup. It covered 20 sequential iterations, each launching five separate AWS CLI processes for PUT, full GET, ListObjectsV2, range GET, and DELETE: 100 CLI process launches and 100 network operations in total.

Setup/provisioning, container start, warm-up, backup/restore, replacement, and cleanup were outside the stopwatch. There was no deliberate pacing. The one aggregate stopwatch cannot separate AWS CLI startup/configuration/signing time from transfer and server time. Dividing 396.892 seconds by 100 would therefore not be request latency. The server middleware's 8.895 ms average was a separate aggregate for requests observed by the server and was not a per-operation percentile distribution.

## Focused measurement

The focused run used AWSSDK.S3 4.0.103.4 through one persistent client/connection pool against image `sha256:ed51fa14ec1fdaec2aa615fa3313c4f986d27806b3b6059bc5ff4aeddfa178b3` at loopback HTTP. SDK retries were zero, checksum behavior was `WHEN_REQUIRED`, and uploads disabled chunk encoding. It used 20 warm-ups, concurrency 1, 200 measured 64 KiB PUT/HEAD/GET/range/DELETE samples, 20 full listings of 250 retained objects at 50 per page (100 page samples), and 30 verified 8 MiB PUT/GET pairs. Test keys were isolated and deleted. Measured traffic was 264,765,440 uploaded and 265,584,640 downloaded bytes; 1,160 network requests with operation-level timing produced 1,620 latency samples with zero errors or retries. The count excludes 60 warm-up requests, 250 list-fixture PUTs, and post-measurement cleanup requests.

| Operation | Samples | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| 64 KiB PUT end-to-end | 200 | 10.22 ms | 15.07 ms | 23.30 ms |
| HEAD | 200 | 3.06 ms | 4.97 ms | 8.08 ms |
| 64 KiB GET headers | 200 | 3.59 ms | 5.95 ms | 9.58 ms |
| 64 KiB GET end-to-end | 200 | 3.75 ms | 6.09 ms | 11.23 ms |
| 4 KiB range end-to-end | 200 | 3.22 ms | 5.22 ms | 9.29 ms |
| DELETE | 200 | 6.94 ms | 10.55 ms | 14.79 ms |
| List page, 50 keys | 100 | 4.63 ms | 6.68 ms | 12.02 ms |
| 8 MiB PUT end-to-end | 30 | 72.82 ms | 151.97 ms | 169.21 ms |
| 8 MiB GET headers | 30 | 3.48 ms | 7.17 ms | 15.56 ms |
| 8 MiB GET end-to-end | 30 | 81.69 ms | 160.79 ms | 246.34 ms |

Measured aggregate transfer rates were 51.76 MiB/s upload and 58.17 MiB/s download. The measurement wall interval was 14.482 seconds. Client CPU increased by 7,609 ms. Server working set moved from 127.4 to 137.2 MiB and managed heap from 3.5 to 8.8 MiB; staging was zero before and after. Seven external Docker samples observed average/max CPU 48.06%/74.27% and average/max memory 86.40/96.27 MiB within the configured 2 CPU/1 GiB limits. These sparse samples do not capture every peak.

Header timing is time until the SDK returned response headers. Body timing begins after headers and includes body consumption plus integrity verification. End-to-end GET timing covers SDK invocation through verified body completion. PUT timing is end-to-end SDK completion.

## Comparison and limits

The thirteenth run used a different package-only Kestrel host configuration, short closed-loop stages across concurrency 1/2/4/8/16, Windows host execution, and shared-machine PostgreSQL/filesystem resources. This run used the deployment container, Docker Desktop networking/filesystem, the 8-active/4-queued profile, warm caches, and concurrency 1. Its persistent-client results explain why the CLI workflow's elapsed time was dominated by process overhead, but the two datasets are not capacity-equivalent. No saturation point, production SLA, intended-server capacity, or multi-node behavior is established.

Machine-readable evidence is in [fifteenth-performance-http.json](fifteenth-performance-http.json).
