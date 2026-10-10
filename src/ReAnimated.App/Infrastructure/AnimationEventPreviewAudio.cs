using System.IO;
using System.Windows.Media;

namespace ReAnimated.App.Infrastructure;

public sealed class AnimationEventPreviewAudio : IDisposable
{
    private readonly List<(MediaPlayer Player, bool StopOnEnd)> _voices = [];
    public event EventHandler<string>? Failed;

    public void Play(string path, double volume, bool stopOnEnd)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Sound was not found.", path);
        if (_voices.Count >= 32) { _voices[0].Player.Close(); _voices.RemoveAt(0); }
        var player = new MediaPlayer { Volume = Math.Clamp(volume, 0, 1) };
        player.MediaOpened += (_, _) => player.Play();
        player.MediaEnded += (_, _) => { player.Close(); _voices.RemoveAll(v => ReferenceEquals(v.Player, player)); };
        player.MediaFailed += (_, args) => { player.Close(); _voices.RemoveAll(v => ReferenceEquals(v.Player, player)); Failed?.Invoke(this, args.ErrorException.Message); };
        _voices.Add((player, stopOnEnd));
        player.Open(new Uri(Path.GetFullPath(path)));
    }
    public void Stop(bool animationEnd = false)
    {
        foreach (var voice in _voices.Where(v => !animationEnd || v.StopOnEnd).ToArray()) { voice.Player.Close(); _voices.Remove(voice); }
    }
    public void Dispose() => Stop();
}
