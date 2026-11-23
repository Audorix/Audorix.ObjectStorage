# Contributing

Thanks for helping improve Audorix.ObjectStorage. Issues and pull requests are welcome.

## Before opening an issue

- Search existing issues and discussions.
- Include the .NET SDK version, operating system, storage provider, and a minimal reproduction.
- Never include credentials, production object names, customer data, or unredacted logs.
- For suspected vulnerabilities, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Development setup

1. Install the .NET 10 SDK and PostgreSQL 14 or later.
2. Clone the repository and restore/build the solution:

   ```powershell
   dotnet restore Audorix.ObjectStorage.slnx
   dotnet build Audorix.ObjectStorage.slnx --configuration Release --no-restore
   ```

3. For PostgreSQL-backed tests, set `AUDORIX_OBJECTSTORAGE_POSTGRES` to a disposable database connection string. Without it, database integration tests are skipped unless a local test connection file is present.
4. Run the test project:

   ```powershell
   dotnet test tests/Audorix.ObjectStorage.Tests/Audorix.ObjectStorage.Tests.csproj --configuration Release
   ```

5. Run the local demo from [its guide](samples/Audorix.ObjectStorage.Demo/README.md).

Tests that contact PostgreSQL or a real HTTP host must use isolated schemas and storage roots. Do not point tests at production data.

## Pull request expectations

- Keep changes focused and explain the behavior being changed.
- Add or update tests for behavior changes, including error paths and cancellation where relevant.
- Update public documentation and the S3 compatibility matrix when behavior or support changes.
- Do not claim S3 support for operations that have not been tested.
- Preserve streaming behavior for object bodies; avoid whole-object buffering.
- Run the relevant test suite and report the exact commands and results.
- Never commit secrets, generated storage data, local database files, or customer content.

## Style

Follow the existing C# style, nullable annotations, asynchronous APIs, and cancellation-token conventions. Avoid unrelated formatting changes.

## License

By submitting a contribution, you agree that it will be licensed under the repository's MIT License. You retain copyright in your contribution.
