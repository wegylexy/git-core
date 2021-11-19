using System.Net.Mail;

namespace FlyByWireless.GitCore;

public sealed record class User(string Name, string Email)
{
    internal static User ParseWithDateTimeOffset(ReadOnlySpan<char> span, out DateTimeOffset dto)
    {
        TimeSpan o = new(int.Parse(span[^5..^2]), int.Parse(span[^2..]), 0);
        span = span[..^6];
        var i = span.LastIndexOf(' ');
        dto = new(DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeSeconds(long.Parse(span[(i + 1)..])).UtcDateTime.Add(o), DateTimeKind.Unspecified), o);
        MailAddress ma = new(new(span[..i]));
        return new(ma.DisplayName, ma.Address);
    }

    public override string ToString() => $"{Name} <{Email}>";

    internal string ToString(DateTimeOffset dto) =>
        string.Concat(ToString(), ' ', dto.ToUnixTimeSeconds(), ' ', dto.Offset < TimeSpan.Zero ? '-' : '+', dto.Offset.ToString("hhmm"));
}
