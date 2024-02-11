namespace HanselminutesBot.Frontend.Models;

public record Source(string Title, string Uri, IEnumerable<string?> Speakers, IEnumerable<string?> Topics, string Description);
