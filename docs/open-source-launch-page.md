# Audorix.ObjectStorage

## A .NET object store you can embed in your ASP.NET Core application

Store object bytes on your filesystem, keep metadata in PostgreSQL, and let S3-compatible clients connect through your app's existing ASP.NET Core listener.

**Open source under the MIT License · .NET 10 · Public preview**

### Why it exists

Audorix.ObjectStorage brings an object-storage engine into the application you already operate. It does not ask you to run a second object-storage service or open another port. Use its direct .NET API from your application, or expose its optional HTTP endpoints to application users and S3 clients.

### Included

- Streaming object storage backed by local persistent disk
- PostgreSQL metadata provider
- Optional authenticated application API with a deny-by-default authorization contract
- Optional path-style S3-compatible endpoint with SigV4 authentication
- Full-object and single byte-range reads
- Explicit compatibility and deployment limits

### Preview limitations

This is an early release candidate, not a drop-in replacement for AWS S3 or a distributed object store. It supports one active engine per storage root. Multipart uploads, presigned URLs, virtual-host addressing, bucket administration, versioning, ACLs, tagging, and checksum trailers are not implemented. Review the [compatibility matrix](s3-compatibility-matrix.md) before choosing a client.

Local Kestrel and specific client workflows have been exercised, but production readiness, commercial shared-hosting support, multi-node operation, and power-loss durability are not claimed.

### Get started

1. Install the .NET 10 SDK and PostgreSQL.
2. Follow the [package quick start](package-quick-start.md).
3. Or launch the [local demo](../samples/Audorix.ObjectStorage.Demo/README.md) with Docker Compose.
4. Read [deployment](deployment-guide.md) and [security](security-hardening-guide.md) guidance before exposing it beyond localhost.

### Join the project

Use GitHub Issues to report reproducible problems, submit improvements through pull requests, and read [CONTRIBUTING.md](../CONTRIBUTING.md) first. Report security issues privately as described in [SECURITY.md](../SECURITY.md).

**License:** [MIT](../LICENSE) · **Third-party components:** [notices](../THIRD_PARTY_NOTICES.md)
