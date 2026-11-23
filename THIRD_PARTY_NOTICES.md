# Third-Party Notices

No S3Server, Less3, or AWSSignatureGenerator source is copied into Audorix.ObjectStorage.

## S3Server

- Project: https://github.com/jchristn/S3Server
- Reviewed package: 7.3.2
- Package repository commit: `03a7177acd605a32416184fda076d3e518816bf9`
- License: MIT
- Use: source and architecture evaluation only. It is not a runtime dependency after the second slice.

## AWSSignatureGenerator

- Project: https://github.com/jchristn/AWSSignatureGenerator
- Reviewed package: 1.1.0
- Package repository commit: `5c23c036a5c2d8bc30c286cdd8304de64487c68b`
- License: MIT
- Use: API/source evaluation only. No source was copied because its standard signed-payload API requires the body while constructing the signature.

## Less3

- Project: https://github.com/jchristn/Less3
- License: MIT
- Use: architecture/reference review only. No source code copied.

## Npgsql

- Project: https://github.com/npgsql/npgsql
- Package: Npgsql 9.0.4
- License: PostgreSQL License
- Use: production PostgreSQL metadata provider.

## AWS SDK for .NET

- Project: https://github.com/aws/aws-sdk-net
- Test package: AWSSDK.S3 4.0.103.4
- License: Apache-2.0
- Use: compatibility tests only; it is not a dependency of the shipping S3 adapter DLL.
