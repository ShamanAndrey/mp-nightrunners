using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace NightRunnersMP.Ui;

/// <summary>
/// Asks GitHub for the newest release tag once per launch. Uses the /releases/latest redirect
/// (no API, no rate limit): GitHub answers 302 with Location .../releases/tag/vX.Y.Z.
/// Runs on a worker thread; the HUD reads Status from the game thread.
/// </summary>
public sealed class UpdateChecker
{
    public const string Repo = "ShamanAndrey/mp-nightrunners";
    public static string ReleasesUrl => $"https://github.com/{Repo}/releases/latest";

    public enum State { Idle, Checking, UpToDate, UpdateAvailable, Unavailable }

    private int _state = (int)State.Idle;
    public State Status => (State)_state;
    public string Current { get; private set; } = "0.0.0";
    public string? Latest { get; private set; }

    public void Start(string currentVersion)
    {
        Current = currentVersion;
        _state = (int)State.Checking;
        Task.Run(CheckAsync);
    }

    private async Task CheckAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"NightRunnersMP/{Current}");

            using var resp = await http.GetAsync(ReleasesUrl).ConfigureAwait(false);
            var location = resp.Headers.Location?.ToString();
            var idx = location?.LastIndexOf("/tag/", StringComparison.Ordinal) ?? -1;
            if (location == null || idx < 0) { _state = (int)State.Unavailable; return; } // no releases yet, or not a redirect

            var tag = location.Substring(idx + 5).Trim().TrimStart('v', 'V');
            Latest = tag;
            var latest = Normalize(tag);
            var current = Normalize(Current);
            if (latest == null || current == null) { _state = (int)State.Unavailable; return; }
            _state = (int)(latest > current ? State.UpdateAvailable : State.UpToDate);
        }
        catch
        {
            _state = (int)State.Unavailable; // offline, DNS, firewall: never bother the player about it
        }
    }

    /// <summary>"0.1" / "0.1.3" / "0.1.3.0" all compare sensibly.</summary>
    private static Version? Normalize(string text)
    {
        if (!Version.TryParse(text, out var v)) return null;
        return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
    }
}
