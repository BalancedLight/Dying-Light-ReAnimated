using System.IO;
using System.Windows.Media;

namespace ReAnimated.App.Infrastructure;

/// <summary>Audio follows the editor timeline; it never advances the animation clock.</summary>
public sealed class SpeechPreviewAudio : IDisposable
{
    private readonly MediaPlayer _player = new();
    private bool _ready;
    private bool _playing;
    private double _seconds;
    private bool _requestedPlaying;
    public event EventHandler<string>? Failed;

    public SpeechPreviewAudio()
    {
        _player.MediaOpened += (_, _) => { _ready = true; Update(_seconds, _requestedPlaying, forceSeek: true); };
        _player.MediaFailed += (_, args) => { _ready = false; Failed?.Invoke(this, args.ErrorException.Message); };
    }

    public void Open(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Dialogue audio was not found.", fullPath);
        _ready = false;
        _playing = false;
        // MediaPlayer can keep an already-open URI without raising MediaOpened
        // again. Close first so repeated selections get a fresh ready event.
        _player.Close();
        _player.Open(new Uri(fullPath));
    }

    public void Update(double seconds, bool playing, bool forceSeek = false)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        _seconds = seconds;
        _requestedPlaying = playing;
        if (!_ready) return;
        if (_player.NaturalDuration.HasTimeSpan && seconds >= _player.NaturalDuration.TimeSpan.TotalSeconds)
            playing = false;
        if (forceSeek || !playing || Math.Abs(_player.Position.TotalSeconds - seconds) > .1)
            _player.Position = TimeSpan.FromSeconds(seconds);
        if (_playing == playing) return;
        if (playing) _player.Play(); else _player.Pause();
        _playing = playing;
    }

    public void Dispose() => _player.Close();
}
