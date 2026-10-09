using System.Runtime.CompilerServices;
using MeetingAssist.Core.Assistant;
using MeetingAssist.Core.Audio;

namespace MeetingAssist.Tests;

/// <summary>
/// Test doubles shared by every fixture that only needs a session to run without touching a
/// real device or provider. Kept here, rather than duplicated per test class, because a session
/// test does not care which file declares "yields silence forever" — it cares that every file
/// agrees on what that means.
/// </summary>
internal static class Doubles
{
    /// <summary>Yields silence forever, so a session can run without touching a device.</summary>
    internal sealed class SilentSource(AudioChannelKind channel) : IAudioSource
    {
        public AudioChannelKind Channel => channel;
        public string Description => $"{channel} (silent)";

        public async IAsyncEnumerable<AudioFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var at = TimeSpan.Zero;
            var frame = TimeSpan.FromMilliseconds(50);
            while (!ct.IsCancellationRequested)
            {
                yield return new AudioFrame(channel, at, new byte[AudioFormat.BytesFor(frame)], false);
                at += frame;
                await Task.Delay(5, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Produces no audio at all, so the session runs without touching a device.</summary>
    internal sealed class EmptySource(AudioChannelKind channel) : IAudioSource
    {
        public AudioChannelKind Channel => channel;
        public string Description => $"{channel} (empty)";

        public async IAsyncEnumerable<AudioFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            while (!ct.IsCancellationRequested) await Task.Delay(20, ct).ConfigureAwait(false);
            yield break;
        }
    }

    /// <summary>Throws as soon as it is read, simulating a capture device disappearing.</summary>
    internal sealed class ExplodingSource(AudioChannelKind channel) : IAudioSource
    {
        public AudioChannelKind Channel => channel;
        public string Description => $"{channel} (throws)";

        public async IAsyncEnumerable<AudioFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw new InvalidOperationException("device disappeared");
#pragma warning disable CS0162 // Required to make this an iterator.
            yield break;
#pragma warning restore CS0162
        }
    }

    /// <summary>Streams a fixed sequence of chunks, one per yield.</summary>
    internal sealed class StubAssistant(params string[] chunks) : IAssistant
    {
        public string Name => "stub";
        public string ModelId => "stub-1";
        public AssistUsage? LastUsage => null;

        public async IAsyncEnumerable<string> AskAsync(
            AssistRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var c in chunks)
            {
                await Task.Yield();
                yield return c;
            }
        }
    }
}
