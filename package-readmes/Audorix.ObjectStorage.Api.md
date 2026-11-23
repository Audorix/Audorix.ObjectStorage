# Audorix.ObjectStorage.Api

Adds authenticated browse, metadata, download, view, byte-range, and HEAD routes to an ASP.NET Core application.

```csharp
using Audorix.ObjectStorage.Api;
using Audorix.ObjectStorage.Api.DependencyInjection;
using Audorix.ObjectStorage.Api.Routing;

builder.Services.AddAuthentication().AddCookie();
builder.Services.AddAuthorization();
builder.Services.AddAudorixObjectStorageApi(options =>
    options.ExposedMetadataKeys.Add("display-name"));
builder.Services.AddSingleton<IObjectStorageApiAuthorization, ApplicationStorageAuthorization>();

app.UseAuthentication();
app.UseAuthorization();
app.MapAudorixObjectStorageApi("/object-storage");
```

The default `IObjectStorageApiAuthorization` denies every request. The host must register authentication and an authorization implementation that translates application identities and logical scopes into bounded bucket/key-prefix grants. S3 credentials are separate from application identities.

This release candidate has no declared project license. Do not publish it to a public package feed until the owner supplies licensing terms.
