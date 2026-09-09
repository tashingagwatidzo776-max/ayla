namespace Tf.Deriv;

/// <summary>An error response returned by the Deriv API.</summary>
public sealed class DerivApiException : Exception
{
    public string Code { get; }

    public DerivApiException(string code, string message)
        : base($"[{code}] {message}")
    {
        Code = code;
    }
}