# Fourteenth Slice Completion Report

## Result

A package-only, reproducible single-node Linux/Kestrel deployment bundle was created for the unchanged `0.1.0-rc.2` candidate and validated locally in isolated containers. No authorized intended server, DNS name, TLS certificate, or deployment credentials were supplied, so intended-server and TLS/proxy validation remain pending and no public infrastructure was changed.

The candidate remains source commit `4c5bbe953687023018eb3d2ca230f75e4e6f6c94`; thirteenth evidence commits remain `407ac691d927978054981518ab4cad213940c690` and `412dc1e688778f431de360f03eca4c0d182e2e8b`. This slice changes deployment host/scripts/docs only. It does not change or rebuild shipping packages and therefore does not require a new RC. Package hashes remain those in the rc.2 release manifest.

The deployment bundle and initial evidence are committed locally as `c4064a7f0a520b8b0a9181d6760104fdc9288adb`. The follow-up evidence-link commit changes only this report and the machine-readable commit field. No remote, tag, or push was created.

## Bundle and identities

`deployment/single-node` contains a minimal Production-only package consumer, Dockerfile, private and direct-TLS Compose profiles, optional isolated PostgreSQL profile, non-secret template, and ownership-aware build/provision/start/status/stop/validate/backup/restore/rollback scripts. The final local image is `sha256:ed51fa14ec1fdaec2aa615fa3313c4f986d27806b3b6059bc5ff4aeddfa178b3`; base-image and assembly identities are recorded in `deployment-validation-summary.json`.

The service ran as UID/GID 10001 with no capabilities, no-new-privileges, read-only root, persistent blob/Data Protection/backup volumes, 64 MiB tmpfs, 1 GiB memory, 2 CPU and 256 PID limits. Startup refused an empty substituted blob volume. It also requires the configured bucket, preventing a normal restart from silently accepting a different empty database. Secrets were generated for the isolated run, stored only in ignored DPAPI-protected local state/process environments, and were not copied into evidence.

## Local validation

At `http://127.0.0.1:5195/s3`, AWS CLI 2.37.6 passed PUT, HEAD and metadata, a special-character key, ListObjectsV2, range GET, full SHA-256-matched GET, and DELETE with checksum calculation/validation set to `when_required`. Invalid credentials failed with exit 254. A 67,108,865-byte upload exceeded the 64 MiB ceiling and failed with exit 254; protected metrics recorded one rejection and no leaked active operation.

The owned container stopped gracefully, was removed, and was recreated from the final image. A retained object remained byte-identical. Normal restart was separated from explicit provisioning; subsequent tests rejected both a substituted empty blob volume and a substituted empty PostgreSQL database. A paired PostgreSQL/blob backup was created and validated. Restore to a separate database and separate blob volume completed with reference reconciliation. A first restore attempt exposed root ownership on a new Docker volume; the script was corrected to run a narrow root ownership initializer before the non-root restore, and the successful rerun is the claimed evidence. These were deployment-script issues, not library defects.

A bounded local sample ran 20 iterations/100 CLI operations in 396.892 seconds: upload, hash-verified full read, list, range read, and delete. Failures were zero. Final protected metrics showed 8.895 ms average server latency, zero active operations, zero failures/rejections for that run, 128,843,776-byte working set, 7,718,560-byte managed heap, 907,266,043,904 free bytes, and zero staging files. This is not intended-server capacity or an SLA.

The pre-existing pilot was not stopped, rebuilt, or reconfigured. Its storage `5185`, API `5165`, and frontend `5273` endpoints each returned HTTP 200 after validation. The isolated deployment is left running at `5195` with its isolated PostgreSQL container.

Release builds for the four shipping projects and package-only deployment host passed in isolated output directories with zero warnings/errors. Regressions passed 39 with one environment-gated AWS CLI test skipped; the real CLI workflow was executed separately above. The package-only consumer passed 1/1. An aggregate solution build was additionally attempted but the pre-existing, unowned source host on port 5085 held its own Release output DLLs open; that process was not stopped. This process-lock result is not reported as a code failure.

## Remaining gates

The operator must provide an authorized target and the exact inputs listed in `single-node-deployment.md`. The server must then pass TLS/SigV4 proxy preservation, separate-client compatibility, resource measurement, backup/restore, replacement, and rollback rehearsal before approval. Cloudflare is neither selected nor verified. Public package distribution remains blocked because no project license has been supplied. No packages were published, source pushed, production data migrated, or public ports/DNS changed.
