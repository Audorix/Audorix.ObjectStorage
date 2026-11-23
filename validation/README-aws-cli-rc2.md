# AWS CLI rc.2 Validation

The validation requires AWS CLI v2 at its normal per-user Windows installation path and the ignored local PostgreSQL configuration (or `AUDORIX_OBJECTSTORAGE_POSTGRES`). Run:

```powershell
.\validation\Run-AwsCliRc2.ps1
```

The script restores a package-only host from the pinned rc.2 candidate feed, creates isolated local resources, and supplies credentials only through environment variables. Supported operations set:

```text
AWS_REQUEST_CHECKSUM_CALCULATION=when_required
AWS_RESPONSE_CHECKSUM_VALIDATION=when_required
```

It verifies PUT, HEAD, user metadata, full GET hash, ListObjectsV2 with a reserved-character key, range GET, and DELETE. It then removes those overrides and requires the unsupported default additional-checksum upload to fail explicitly while leaving no object. Commands recorded in the summary redact credentials, local file paths, and the ephemeral endpoint.
