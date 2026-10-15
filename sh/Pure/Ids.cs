namespace LtDatabase;

/// <summary>
/// Unique ids for nonces and idempotency keys. They only need to be unique, not secret: a strictly increasing counter,
/// the clock and random bytes are mixed so ids differ within a run and across restarts even if the random source repeats.
/// </summary>
public static class Ids
{
    static long counter;
    static readonly Random Rng = new();

    public static string Next()
    {
        var bytes = new byte[16];
        Rng.NextBytes(bytes);
        var n = ++counter;
        var ticks = DateTime.UtcNow.Ticks;
        for (var i = 0; i < 8; i++)
        {
            bytes[i] ^= (byte)(n >> (8 * i));
            bytes[8 + i] ^= (byte)(ticks >> (8 * i));
        }
        return Wire.Base64Url(bytes);
    }
}
