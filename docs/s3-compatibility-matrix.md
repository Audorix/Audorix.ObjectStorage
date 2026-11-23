# S3 Compatibility Matrix

| Area | Supported | Client requirement |
| --- | --- | --- |
| PutObject | Yes | Signed SHA-256 payload; `UseChunkEncoding = false`. |
| GetObject | Yes | Full object or one byte range; configure response checksum validation `WHEN_REQUIRED`. |
| Explicit range | Yes | `bytes=0-99`; inclusive offsets, HTTP 206. |
| Open-ended range | Yes | `bytes=100-`; extends through EOF. |
| Suffix range | Yes | `bytes=-100`; returns the final bytes. |
| End beyond EOF | Yes | End is clamped to the final byte. |
| Unsatisfiable range | Yes | HTTP 416 `InvalidRange` and `Content-Range: bytes */length`. |
| Malformed range | Rejected | HTTP 400 `InvalidArgument`. |
| Multiple ranges | No | Explicit HTTP 501 `NotImplemented`; no multipart response or first-range fallback. |
| HeadObject | Yes | Full metadata headers, `Accept-Ranges: bytes`, zero response body; ranged HEAD is 501. |
| DeleteObject | Yes | Existing bucket; missing objects still return 204. |
| Authorization | SigV4 header only | Region must match; default maximum clock skew is 15 minutes. |
| Addressing | Path-style only | `ForcePathStyle = true`. |
| Endpoint path | Yes | Include mapped path in `ServiceURL`; proxy must preserve it. |
| Content-MD5 | Optional on PUT | Validated while streaming. |
| User metadata | Yes | `x-amz-meta-*`; every Amazon header must be included in SigV4 `SignedHeaders`. |
| ListObjectsV2 | Yes | Path-style, SigV4-authenticated; separate `ListBucket` permission. |
| List prefix/delimiter | Yes | CommonPrefixes count toward MaxKeys; SQL `%` and `_` are literal key characters. |
| List pagination | Yes | MaxKeys 0-1000, StartAfter, opaque expiring HMAC tokens bound to listing parameters and the issuing access key; current authorization is rechecked. |
| List URL encoding | Yes | `encoding-type=url`; key-bearing response fields are percent encoded. |
| `aws-chunked` | No | Explicit `501 NotImplemented`. |
| Checksum trailers / `x-amz-checksum-*` | No | Set request/response checksum mode to `WHEN_REQUIRED`. |
| Conditional requests, including If-Range | No | Explicit `501 NotImplemented`; validators are not interpreted. |
| Other bucket APIs, multipart, presigned URLs | No | Outside this slice. |
| Virtual-host addressing | No | Outside this slice. |

ETag is an opaque version token, not a digest: `"<blob-id>-1"`.

Range behavior is based on the [AWS GetObject API](https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObject.html) and [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#name-range).

Tested client: AWSSDK.S3 4.0.103.4. AWS CLI v2 was unavailable during the sixth-slice run, so the new ranged CLI check is present but skipped rather than reported as passed. Earlier AWS CLI 2.37.6 full-object and listing evidence remains valid.

## Authenticated application API

`Audorix.ObjectStorage.Api` is separate from the S3 protocol surface above. It uses the host's ASP.NET Core authentication plus a deny-by-default scope authorization contract. Its `/objects`, `/metadata`, `/download`, and `/view` routes expose logical keys rather than S3 buckets.

The application API supports full and single-range GET/HEAD responses. Unlike the S3 adapter, malformed, multiple, and conditional range requests return HTTP 400 JSON errors. Authenticated denial and nonexistent resources both return 404. See [api-integration.md](api-integration.md) for the complete REST contract and content-type policy.
