# Reproducible Local Pilot

The pilot runs a separate, owned local instance without changing or stopping services already bound to other ports.

```powershell
./pilot/Provision.ps1
./pilot/Start.ps1
./pilot/Status.ps1
./pilot/Stop.ps1
```

Provisioning is idempotent. It creates ignored `.pilot` state, preserves existing secrets and files, uses the local PostgreSQL connection from `AUDORIX_PILOT_POSTGRES` or the ignored provider `database.json`, and never drops or recreates schemas. Secrets are generated into `.pilot/secrets.json`; the committed template contains placeholders only. Supply `AUDORIX_PILOT_POSTGRES` when the local database file is unavailable. Restrict access to `.pilot` to the application operator.

Default endpoints are:

- Storage host and S3 endpoint: `http://127.0.0.1:5185` and `/s3`.
- ReportStudio API and Development-only sign-in: `http://127.0.0.1:5165`.
- React frontend: `http://127.0.0.1:5273`.

The storage root, Data Protection keys, generated credentials, process records, and logs persist under ignored `.pilot`. The ObjectStorage metadata schema is `audorix_pilot_tenth`, its bucket is `reportstudio-pilot-tenth`, and ReportStudio retains its existing dedicated `report_studio` schema. Stop validates PID, process start time, and an exact command marker before acting; it never selects a process by port.

`Start.ps1` builds private Release outputs under `.pilot/apps`, provisions the bucket idempotently, waits for all three readiness endpoints, and writes ownership records only for processes it starts. Production authentication behavior is unchanged: Development profile endpoints are mapped only in Development, and test-header authentication is registered only in Testing.
