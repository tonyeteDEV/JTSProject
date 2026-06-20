using NAudio.Wave;

namespace JTS_App.Services;

public sealed class PomodoroAlarmService
{
    private readonly object _gate = new();
    private WaveOutEvent? _player;

    public void StartCompletionBell()
    {
        try
        {
            lock (_gate)
            {
                if (_player is not null) return;
            }

            var player = new WaveOutEvent { DesiredLatency = 100 };
            player.Init(new RingingBellSampleProvider());
            player.PlaybackStopped += (_, _) =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_player, player))
                    {
                        _player = null;
                    }
                }

                player.Dispose();
            };

            lock (_gate)
            {
                _player = player;
            }

            player.Play();
        }
        catch (Exception ex)
        {
            App.Log("[PomodoroAlarmService] Failed to start completion bell: " + ex);
        }
    }

    public void StopCompletionBell()
    {
        try
        {
            WaveOutEvent? player;
            lock (_gate)
            {
                player = _player;
                _player = null;
            }

            player?.Stop();
        }
        catch (Exception ex)
        {
            App.Log("[PomodoroAlarmService] Failed to stop completion bell: " + ex);
        }
    }

    private sealed class RingingBellSampleProvider : ISampleProvider
    {
        private const int SampleRate = 44100;
        private const int Channels = 2;
        private const double CycleSeconds = 1.2;
        private const double BellSeconds = 0.85;
        private const double TwoPi = Math.PI * 2;
        private readonly int _cycleFrames = (int)(SampleRate * CycleSeconds);
        private readonly int _bellFrames = (int)(SampleRate * BellSeconds);
        private int _frame;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var framesRequested = count / Channels;
            var sampleIndex = offset;

            for (var i = 0; i < framesRequested; i++)
            {
                var cycleFrame = _frame % _cycleFrames;
                var sample = 0f;
                if (cycleFrame < _bellFrames)
                {
                    var time = (double)cycleFrame / SampleRate;
                    var attack = Math.Min(1.0, time / 0.018);
                    var decay = Math.Exp(-3.1 * time);
                    var shimmer = Math.Exp(-1.4 * time);
                    var tone =
                        Math.Sin(TwoPi * 880 * time) * 0.58 +
                        Math.Sin(TwoPi * 1320 * time) * 0.30 +
                        Math.Sin(TwoPi * 1760 * time) * 0.18 +
                        Math.Sin(TwoPi * 2460 * time) * 0.10 * shimmer;

                    sample = (float)(tone * attack * decay * 0.58);
                }

                for (var channel = 0; channel < Channels; channel++)
                {
                    buffer[sampleIndex++] = sample;
                }

                _frame++;
            }

            return framesRequested * Channels;
        }
    }
}
