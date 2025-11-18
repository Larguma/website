using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace largumaDev.Utils;

public static class Modrinth
{
  private static IMemoryCache? _cache;
  private static readonly HttpClient http = new()
  {
    BaseAddress = new Uri("https://api.modrinth.com/v2/")
  };

  public static void MapRoutes(WebApplication app)
  {
    http.DefaultRequestHeaders.UserAgent.ParseAdd("LargumaDev/1.0 (contact@larguma.com)");
    _cache = app.Services.GetRequiredService<IMemoryCache>();
    app.MapGet("/modrinth/badge/{slug}", async (string slug, HttpResponse response) =>
    {
      try
      {
        string svg = await GenerateBadge(slug);
        response.ContentType = "image/svg+xml";

        // Add cache headers for browser caching
        TimeSpan maxAge = new(7, 0, 0, 0); // 7 days
        response.Headers.CacheControl = "public, max-age=" + maxAge.TotalSeconds.ToString("0");

        await response.WriteAsync(svg);
      }
      catch (Exception ex)
      {
        response.StatusCode = 500;
        await response.WriteAsync("Error: " + ex.Message);
      }
    });
  }

  public static async Task<string> GenerateBadge(string slug)
  {
    // Check cache first
    string cacheKey = $"modrinth_badge_{slug}";
    if (_cache?.TryGetValue(cacheKey, out string? cachedSvg) == true && cachedSvg != null)
    {
      return cachedSvg;
    }
    var projectResp = await http.GetAsync($"project/{slug}");
    if (!projectResp.IsSuccessStatusCode)
    {
      throw new Exception($"Project '{slug}' not found");
    }

    var projectJson = await projectResp.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(projectJson);
    var root = doc.RootElement;

    string name = root.GetProperty("title").GetString() ?? slug;
    string iconUrl = root.GetProperty("icon_url").GetString() ?? "";
    string description = root.GetProperty("description").GetString() ?? "";
    int downloads = root.GetProperty("downloads").GetInt32();

    // Download and convert icon to base64 for embedding
    string iconDataUrl = "";
    if (!string.IsNullOrEmpty(iconUrl))
    {
      try
      {
        var iconBytes = await http.GetByteArrayAsync(iconUrl);
        string mimeType = iconUrl.EndsWith(".png") ? "image/png" :
                          iconUrl.EndsWith(".jpg") || iconUrl.EndsWith(".jpeg") ? "image/jpeg" :
                          "image/png";
        iconDataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(iconBytes)}";
      }
      catch
      {
        iconDataUrl = "";
      }
    }

    var versionsResp = await http.GetAsync($"project/{slug}/version");
    string version = "";
    if (versionsResp.IsSuccessStatusCode)
    {
      var versionsJson = await versionsResp.Content.ReadAsStringAsync();
      using var vdoc = JsonDocument.Parse(versionsJson);
      var vroot = vdoc.RootElement;

      if (vroot.ValueKind == JsonValueKind.Array && vroot.GetArrayLength() > 0)
      {
        var first = vroot[0];
        version = first.GetProperty("version_number").GetString() ?? "";
      }
    }

    // SVG
    string bg = "#11111b";
    string surface0 = "#313244";
    string text = "#cdd6f4";
    string mauve = "#cba6f7";
    string green = "#a6e3a1";

    int iconSize = 100;
    int iconPadding = 4;
    int nameWidth = name.Length * 14;
    int versionWidth = version.Length * 14;
    string downloadsTxt = FormatNumber(downloads);
    int downloadsWidth = downloadsTxt.Length * 14 + 30;
    int totalWidth = iconSize + iconPadding + nameWidth + versionWidth + downloadsWidth;
    int totalHeight = iconSize + 4;
    int fontSize = 20;

    var sb = new StringBuilder();
    sb.AppendLine($@"<svg xmlns=""http://www.w3.org/2000/svg"" xmlns:xlink=""http://www.w3.org/1999/xlink"" width=""{totalWidth}"" height=""{totalHeight}"">");

    // Background
    sb.AppendLine($@"  <rect width=""{totalWidth}"" height=""{totalHeight}"" fill=""{bg}"" rx=""10""/>");

    // Icon
    if (!string.IsNullOrEmpty(iconDataUrl))
    {
      sb.AppendLine($@"  <clipPath id=""iconClip"">");
      sb.AppendLine($@"    <rect x=""2"" y=""2"" width=""{iconSize}"" height=""{iconSize}"" rx=""10""/>");
      sb.AppendLine($@"  </clipPath>");
      sb.AppendLine($@"  <image x=""2"" y=""2"" width=""{iconSize}"" height=""{iconSize}"" xlink:href=""{iconDataUrl}"" clip-path=""url(#iconClip)""/>");
    }

    // Section
    int nameX = iconSize + iconPadding;
    int versionX = nameX + nameWidth;
    int downloadsX = versionX + versionWidth;

    sb.AppendLine($@"  <rect x=""{nameX}"" width=""{nameWidth}"" height=""{totalHeight}"" fill=""{surface0}""/>");
    sb.AppendLine($@"  <rect x=""{versionX}"" width=""{versionWidth}"" height=""{totalHeight}"" fill=""{mauve}"" fill-opacity=""0.3""/>");
    sb.AppendLine($@"  <rect x=""{downloadsX}"" width=""{downloadsWidth}"" height=""{totalHeight}"" fill=""{green}"" fill-opacity=""0.3""/>");

    // Text
    sb.AppendLine($@"  <g fill=""{text}"" text-anchor=""middle"" font-size=""{fontSize}"">");
    sb.AppendLine($@"    <text x=""{nameX + nameWidth / 2}"" y=""{totalHeight / 2 + 4}"" font-weight=""600"">{name}</text>");
    sb.AppendLine($@"    <text x=""{versionX + versionWidth / 2}"" y=""{totalHeight / 2 + 4}"" fill=""{mauve}"">v{version}</text>");
    sb.AppendLine($@"    <text x=""{downloadsX + downloadsWidth / 2}"" y=""{totalHeight / 2 + 4}"" fill=""{green}"" font-size=""{fontSize}"">{downloadsTxt}↓</text>");
    sb.AppendLine($@"  </g>");

    sb.AppendLine("</svg>");

    string svg = sb.ToString();

    _cache?.Set(cacheKey, svg, TimeSpan.FromDays(7));

    return svg;
  }

  private static string FormatNumber(double num)
  {
    if (num >= 1000000000)
      return $"{num / 1000000000.0:F1}B";
    if (num >= 1000000)
      return $"{num / 1000000.0:F1}M";
    if (num >= 1000)
      return $"{num / 1000.0:F1}K";
    return num.ToString();
  }
}
