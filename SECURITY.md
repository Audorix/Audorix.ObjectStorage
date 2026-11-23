# Security policy

## Supported versions

Security fixes are currently accepted for the latest published release and the current default branch. The project is in public preview; release support windows may change before a stable 1.0 release.

## Reporting a vulnerability

Please do not report vulnerabilities in public issues. Use [GitHub's private vulnerability reporting](https://github.com/audorix/Audorix.ObjectStorage/security/advisories/new). If private reporting is not enabled, contact the maintainers through the security contact listed on the Audorix GitHub organization profile and include **Audorix.ObjectStorage security report** in the subject.

Include affected versions, deployment topology, reproduction steps, and impact. Do not include real credentials or customer data. We will acknowledge a report within five business days and coordinate a fix and disclosure timeline with the reporter.

## Deployment security notes

- Use HTTPS outside local development and store keys in a secret manager.
- Scope each S3 credential to the minimum buckets and operations required.
- Keep the S3 endpoint behind a trusted network boundary until the host's threat model and configuration have been reviewed.
- Implement explicit authorization for the application-facing API; its default behavior denies access.
- Use a persistent, access-controlled storage root and durable ASP.NET Core Data Protection keys.
- Run one active engine per storage root. This implementation is not multi-node safe.
- Follow [the hardening guide](docs/security-hardening-guide.md) and [deployment guide](docs/deployment-guide.md).
