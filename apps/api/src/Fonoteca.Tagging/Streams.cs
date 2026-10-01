namespace Fonoteca.Tagging;

internal static class Streams
{
    /// <summary>Moves everything from <paramref name="from"/> to the end up by <paramref name="by"/> bytes, last chunk first.</summary>
    public static void Shift(Stream stream, long from, int by)
    {
        var buffer = new byte[81920];
        var remaining = stream.Length - from;

        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            var at = from + remaining - count;

            stream.Position = at;
            stream.ReadExactly(buffer, 0, count);
            stream.Position = at + by;
            stream.Write(buffer, 0, count);

            remaining -= count;
        }
    }
}
