using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;

namespace largumaDev.Utils;

public record BoardGame(
  string Id,
  string Name,
  int Year,
  string Thumbnail,
  int NumPlays,
  double Rating,
  int MinPlayers,
  int MaxPlayers,
  int PlayTime
);

public static class Bgg
{
  private static IMemoryCache? _cache;
  private static string? _bggCookie;
  private static readonly HttpClient http = new()
  {
    BaseAddress = new Uri("https://boardgamegeek.com/xmlapi2/")
  };

  public static void MapRoutes(WebApplication app)
  {
    http.DefaultRequestHeaders.UserAgent.ParseAdd("LargumaDev/1.0 (https://larguma.dev)");
    _cache = app.Services.GetRequiredService<IMemoryCache>();

    // Read BGG session credentials from config (env vars: Bgg__Username, Bgg__Token)
    IConfiguration config = app.Services.GetRequiredService<IConfiguration>();
    string? bggUser = config["Bgg:Username"];
    string? bggToken = config["Bgg:Token"];
    if (!string.IsNullOrEmpty(bggUser) && !string.IsNullOrEmpty(bggToken))
    {
      _bggCookie = $"bggusername={bggUser}; bggpassword={bggToken};";
    }

    _ = app.MapGet("/bgg/collection/{username}/clear-cache", async (string username, HttpResponse response) =>
    {
      string result = await ClearCache(username);
      await response.WriteAsync(result);
    });

    _ = app.MapGet("/bgg/collection/{username}/games", async (string username) =>
    {
      try
      {
        string xml = await GetCollection(username);
        List<BoardGame> games = ParseCollection(xml);
        return Results.Json(games);
      }
      catch (Exception ex)
      {
        return Results.Problem(ex.Message);
      }
    });
  }

  public static async Task<string> ClearCache(string username)
  {
    username = username.Trim().ToLower();
    string cacheKey = $"bgg_collection_{username}";
    _cache?.Remove(cacheKey);
    return "Cache cleared for " + username;
  }

  private static List<BoardGame> ParseCollection(string xml)
  {
    XDocument doc = XDocument.Parse(xml);
    List<BoardGame> games = [];

    foreach (XElement item in doc.Root?.Elements("item") ?? [])
    {
      string id = item.Attribute("objectid")?.Value ?? "";
      string name = item.Element("name")?.Value ?? "";
      _ = int.TryParse(item.Element("yearpublished")?.Value, out int year);
      _ = int.TryParse(item.Element("numplays")?.Value, out int numPlays);

      string thumbnail = item.Element("thumbnail")?.Value ?? "";
      if (thumbnail.StartsWith("//")) thumbnail = "https:" + thumbnail;

      XElement? stats = item.Element("stats");
      _ = int.TryParse(stats?.Attribute("minplayers")?.Value, out int minPlayers);
      _ = int.TryParse(stats?.Attribute("maxplayers")?.Value, out int maxPlayers);
      _ = int.TryParse(stats?.Attribute("playingtime")?.Value, out int playTime);
      _ = double.TryParse(
        stats?.Element("rating")?.Element("average")?.Attribute("value")?.Value,
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture,
        out double rating
      );

      games.Add(new BoardGame(id, name, year, thumbnail, numPlays, Math.Round(rating, 2), minPlayers, maxPlayers, playTime));
    }

    games = [.. games.OrderBy(g => Guid.NewGuid())];

    return games;
  }

  private static async Task<string> GetCollection(string username)
  {
    username = username.Trim().ToLower();
    
    string cacheKey = $"bgg_collection_{username}";
    if (_cache?.TryGetValue(cacheKey, out string? cachedXml) == true && cachedXml != null)
    {
      return cachedXml;
    }

    string url = $"collection?username={Uri.EscapeDataString(username)}&subtype=boardgame&excludesubtype=boardgameexpansion&own=1&stats=1";

    // BGG returns 202 when the collection is queued for processing, retry with backoff
    for (int attempt = 0; attempt < 5; attempt++)
    {
      if (_bggCookie != null)
      {
        http.DefaultRequestHeaders.Remove("Cookie");
        http.DefaultRequestHeaders.Add("Cookie", _bggCookie);
      }
      HttpResponseMessage response = await http.GetAsync(url);

      if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
      {
        await Task.Delay(2000 * (attempt + 1));
        continue;
      }

      _ = response.EnsureSuccessStatusCode();
      string xml = await response.Content.ReadAsStringAsync();

      _ = (_cache?.Set(cacheKey, xml, TimeSpan.FromDays(1)));

      return xml;
    }

    throw new Exception("BGG API did not return collection data after multiple retries");
  }
}
