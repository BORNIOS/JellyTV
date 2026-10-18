using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

public interface ITmdbNormalizationService
{
    string NormalizeTitle(string title);
}
