# Local object storage demo

This sample runs the current source with PostgreSQL metadata and filesystem object bytes. It is intended for local evaluation only; the credentials and database password are public demo values.

## Requirements

- Docker Engine with the Compose plugin
- Ports `5080` and `54329` available

## Start

From the repository root:

```powershell
docker compose -f samples/Audorix.ObjectStorage.Demo/compose.yaml up --build
```

Open <http://localhost:5080/>. Upload a file, refresh the object list, and download it again. The S3 endpoint is `http://localhost:5080/s3`, with access key `local-demo-key`, secret `local-demo-secret-change-before-use`, region `us-east-1`, and bucket `demo`.

For AWS CLI v2, use a temporary profile/environment and configure the checksum modes required by this preview:

```powershell
$env:AWS_ACCESS_KEY_ID = "local-demo-key"
$env:AWS_SECRET_ACCESS_KEY = "local-demo-secret-change-before-use"
$env:AWS_DEFAULT_REGION = "us-east-1"
$env:AWS_REQUEST_CHECKSUM_CALCULATION = "WHEN_REQUIRED"
$env:AWS_RESPONSE_CHECKSUM_VALIDATION = "WHEN_REQUIRED"
aws --endpoint-url http://localhost:5080/s3 s3api list-objects-v2 --bucket demo
aws --endpoint-url http://localhost:5080/s3 s3 cp .\README.md s3://demo/readme-copy.md
aws --endpoint-url http://localhost:5080/s3 s3 cp s3://demo/readme-copy.md .\readme-download.md
```

The exact supported protocol operations and omissions are listed in [the S3 compatibility matrix](../../../docs/s3-compatibility-matrix.md).

## Stop and reset

Press Ctrl+C, then run:

```powershell
docker compose -f samples/Audorix.ObjectStorage.Demo/compose.yaml down
```

Compose retains the named database and object volumes. To remove this demo's data and start fresh, run `docker compose -f samples/Audorix.ObjectStorage.Demo/compose.yaml down --volumes`.

Do not publish this demo container or expose its ports to a network. Replace credentials, add TLS and application authentication, and follow the deployment and security documentation for any non-local deployment.
