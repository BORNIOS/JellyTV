using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services.Tmdb;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

public class EpgSyncResult
{
    public bool Success { get; set; }

    public int TvgIdsFound { get; set; }

    public List<EpgSourceSyncResult> Sources { get; set; } = new();

    public string? Error { get; set; }
}
