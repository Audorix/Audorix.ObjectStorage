# Open-source launch checklist

This checklist prepares a public repository and first public package release. Items that require a repository owner or GitHub/NuGet account are marked **Owner action**; this workspace cannot complete account-level publication.

## Prepared in this repository

- [x] MIT license and package license expression
- [x] Updated project README with status, scope, limitations, installation path, and demo pointer
- [x] Contributor, security, support, and code-of-conduct policies
- [x] Documentation index, architecture overview, launch page copy, and competitor review
- [x] Local-only demo with PostgreSQL and sample S3 client commands
- [x] GitHub Actions build/test workflow and structured bug-report form
- [x] Third-party notices retained and surfaced from the README

## Owner actions before making a repository public

- [ ] **Inspect the complete Git history for credentials, customer data, production identifiers, copied material, and private deployment details.** Fixing the current tree does not remove information from earlier commits.
- [ ] Review whether historical slice reports, security review evidence, load-test output, pilot scripts, deployment scripts, and recovery material are appropriate for public visibility. Move or remove internal-only materials before launch, and rewrite Git history if those materials were ever public-sensitive.
- [x] Set the canonical repository URL to `https://github.com/audorix/Audorix.ObjectStorage` in package metadata and launch documentation.
- [ ] Create or transfer the repository into the `audorix` GitHub organization, then verify that the URL and private security reporting are live.
- [ ] Enable GitHub private vulnerability reporting and configure maintainer contact details; then verify the SECURITY.md reporting route works.
- [ ] Set repository description, topics (`dotnet`, `object-storage`, `s3-compatible`, `postgresql`), default branch, Discussions/Issues settings, and a release policy.
- [ ] Configure protected branch rules and require CI before merge.
- [ ] Add NuGet.org package ownership/publishing credentials as a narrowly scoped GitHub Actions secret only after choosing an initial package version and confirming package IDs are available.
- [ ] Decide whether to publish all four packages together and verify package metadata/readmes/license in a clean NuGet consumer.
- [ ] Publish a tagged preview release only after clean-clone build, test, demo startup, and AWS SDK/AWS CLI checks have passed on the release commit.
- [ ] Replace `0.1.0-rc.2` references with the exact release version if it changes.

## Release gate

The README accurately calls this a public preview. Do not present it as a drop-in or distributed S3 replacement. Before a stable release, close the documented platform-test gaps, define support policy, and review all protocol claims against the compatibility matrix.
