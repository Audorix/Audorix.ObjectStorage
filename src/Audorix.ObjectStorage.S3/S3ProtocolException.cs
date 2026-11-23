namespace Audorix.ObjectStorage.S3;

internal sealed class S3ProtocolException : Exception
{
    public S3ProtocolException(int statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }

    public string Code { get; }
}
