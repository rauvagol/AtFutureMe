using System.IO;
using System.Windows.Media;

namespace AtFutureMe.Services;

/// <summary>Plays Windows Media / system alert sounds at a chosen volume.</summary>
static class AlertSound
{
    static MediaPlayer? _player;
    static List<AlertSoundOption>? _options;

    public static IReadOnlyList<AlertSoundOption> Options()
    {
        if (_options != null)
            return _options;
        var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        var bag = new List<AlertSoundOption>();
        void Add(string id, string label, string file)
        {
            var path = Path.Combine(media, file);
            if (File.Exists(path))
                bag.Add(new AlertSoundOption(id, label, path));
        }

        Add("notify", "Notify", "Windows Notify.wav");
        Add("notify-email", "Notify email", "Windows Notify Email.wav");
        Add("notify-msg", "Notify messaging", "Windows Notify Messaging.wav");
        Add("notify-sms", "Notify SMS", "Windows Notify Messaging.wav");
        Add("ding", "Ding", "Windows Ding.wav");
        Add("exclamation", "Exclamation", "Windows Exclamation.wav");
        Add("asterisk", "Asterisk", "Windows Asterisk.wav");
        Add("critical", "Critical stop", "Windows Critical Stop.wav");
        Add("balloon", "Balloon", "Windows Balloon.wav");
        Add("alarm01", "Alarm 01", "Alarm01.wav");
        Add("alarm02", "Alarm 02", "Alarm02.wav");
        Add("alarm03", "Alarm 03", "Alarm03.wav");
        Add("ring01", "Ring 01", "Ring01.wav");
        Add("ring02", "Ring 02", "Ring02.wav");
        Add("chimes", "Chimes", "chimes.wav");
        Add("chord", "Chord", "chord.wav");
        Add("tada", "Tada", "tada.wav");

        if (bag.Count == 0)
            bag.Add(new AlertSoundOption("beep", "System beep", null));
        _options = bag;
        return _options;
    }

    public static string NormalizeId(string? id)
    {
        var options = Options();
        if (options.Count == 0)
            return "beep";
        if (!string.IsNullOrWhiteSpace(id)
            && options.Any(o => o.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase)))
            return id.Trim();
        return options[0].Id;
    }

    public static void Play(string? id, double volume)
    {
        var options = Options();
        if (options.Count == 0)
            return;
        var pickId = NormalizeId(id);
        var pick = options.FirstOrDefault(o => o.Id.Equals(pickId, StringComparison.OrdinalIgnoreCase));
        if (pick.Id.Length == 0)
            pick = options[0];
        volume = Math.Clamp(volume, 0, 1);
        try
        {
            if (pick.Path == null)
            {
                System.Media.SystemSounds.Beep.Play();
                return;
            }

            if (_player == null)
            {
                _player = new MediaPlayer();
                _player.MediaEnded += (_, _) =>
                {
                    try { _player?.Stop(); } catch { /* ignore */ }
                };
            }

            _player.Stop();
            _player.Open(new Uri(pick.Path));
            _player.Volume = volume;
            _player.Play();
        }
        catch (Exception ex)
        {
            App.Log("Alert sound failed: " + ex.Message);
        }
    }
}

readonly record struct AlertSoundOption(string Id, string Label, string? Path);
