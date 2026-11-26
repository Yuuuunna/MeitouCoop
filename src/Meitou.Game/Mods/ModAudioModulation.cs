using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// Pre-composed modulation values for one sound category, read lock-free on the audio thread.
/// </summary>
readonly record struct AudioChannelModulation(float Rate, float LowPass, float Gain)
{
    /// <summary>
    /// No modulation on this channel.
    /// </summary>
    internal static readonly AudioChannelModulation Identity = new(1, 1, 1);
}

/// <summary>
/// Immutable snapshot of all active mods' composed audio modulations, swapped atomically onto the audio player for lock-free reads on
/// the audio thread.
/// </summary>
sealed class AudioModulationSnapshot(AudioChannelModulation world, AudioChannelModulation music, AudioChannelModulation ui)
{
    /// <summary>
    /// Modulation for world and ambience sounds.
    /// </summary>
    internal readonly AudioChannelModulation World = world;

    /// <summary>
    /// Modulation for music tracks.
    /// </summary>
    internal readonly AudioChannelModulation Music = music;

    /// <summary>
    /// Modulation for interface sounds (the GUI sound object).
    /// </summary>
    internal readonly AudioChannelModulation Ui = ui;
}

/// <summary>
/// Composes per-mod <see cref="AudioModulation"/> entries and publishes a lock-free snapshot to the audio player.
/// Multiple mods compose multiplicatively for <see cref="AudioModulation.Rate"/> and <see cref="AudioModulation.Gain"/>, and by minimum for
/// <see cref="AudioModulation.LowPass"/>, clamped to the valid ranges.
/// </summary>
sealed class ModAudioModulations
{
    /// <summary>
    /// Guards <see cref="_entries"/> and <see cref="_player"/> across host/mod threads.
    /// </summary>
    readonly object _gate = new();

    /// <summary>
    /// Active non-identity modulations by mod id.
    /// </summary>
    readonly Dictionary<string, AudioModulation> _entries = new(StringComparer.Ordinal);
    /// <summary>
    /// Initialize-time defaults copied independently into each ready world.
    /// </summary>
    readonly Dictionary<string, AudioModulation> _initial = new(StringComparer.Ordinal);
    /// <summary>
    /// Authority owners supersede their same-owner local input, including an explicit identity reading.
    /// </summary>
    readonly HashSet<string> _authorityOwners = new(StringComparer.Ordinal);
    /// <summary>
    /// Controls belonging to the currently presented authority view.
    /// </summary>
    IReadOnlyList<ModAudioView> _authority = [];


    /// <summary>
    /// The audio player receiving the composed snapshot; null when audio is off or not yet attached.
    /// </summary>
    Action<AudioModulationSnapshot?>? _player;

    /// <summary>
    /// The latest composed snapshot; null when no mod is modulating audio.
    /// </summary>
    internal AudioModulationSnapshot? Current { get; private set; }

    /// <summary>
    /// Connects this registry to <paramref name="player"/> and pushes the current snapshot.
    /// </summary>
    internal void Attach(Action<AudioModulationSnapshot?>? player)
    {
        lock (_gate)
        {
            _player = player;
            _player?.Invoke(Current);
        }
    }

    /// <summary>
    /// Replaces current authority membership without composing a same-owner client default twice.
    /// </summary>
    internal void ApplyAuthority(IReadOnlyList<ModAudioView> views)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_authority, views) || _authority.SequenceEqual(views)) return;

            _authority = views;
            _authorityOwners.Clear();
            foreach (var view in views) _authorityOwners.Add(view.Owner);
            PublishLocked();
        }
    }

    /// <summary>
    /// Retains a world-independent Initialize-time setting, including an explicit identity default.
    /// </summary>
    internal void SetInitial(string owner, AudioModulation modulation)
    {
        lock (_gate) _initial[owner] = modulation.Clamped();
    }

    /// <summary>
    /// Seeds only the supplied world's presentation. A replacement-save candidate cannot change another world's controls.
    /// </summary>
    internal void InitializeWorld(World world)
    {
        KeyValuePair<string, AudioModulation>[] defaults;
        lock (_gate) defaults = [.. _initial];
        foreach (var (owner, modulation) in defaults) world.ModVisuals.InitializeAudio(world, owner, modulation);
    }

    /// <summary>
    /// Sets or updates <paramref name="modId"/>'s audio modulation (identity clears it) and publishes the composed snapshot.
    /// </summary>
    internal void Set(string modId, AudioModulation modulation)
    {
        var clamped = modulation.Clamped();
        lock (_gate)
        {
            if (clamped.IsIdentity) _entries.Remove(modId);
            else _entries[modId] = clamped;

            PublishLocked();
        }
    }

    /// <summary>
    /// Removes <paramref name="modId"/>'s modulation (e.g. on clear or failed initialization).
    /// </summary>
    internal void Drop(string modId)
    {
        lock (_gate)
        {
            _initial.Remove(modId);
            if (!_entries.Remove(modId)) return;

            PublishLocked();
        }
    }

    /// <summary>
    /// Recomputes <see cref="Current"/> from <see cref="_entries"/> and pushes it to <see cref="_player"/>.
    /// </summary>
    void PublishLocked()
    {
        if (_entries.Count == 0 && _authority.Count == 0)
        {
            Current = null;
            _player?.Invoke(null);
            return;
        }

        float wRate = 1, wLow = 1, wGain = 1;
        float mRate = 1, mLow = 1, mGain = 1;
        float uRate = 1, uLow = 1, uGain = 1;

        var local = _entries.Where(pair => !_authorityOwners.Contains(pair.Key)).Select(pair => pair.Value);
        foreach (var entry in local.Concat(_authority.Select(view => view.Modulation)))
        {
            wRate *= entry.Rate;
            wLow = MathF.Min(wLow, entry.LowPass);
            wGain *= entry.Gain;

            if (entry.AffectMusic)
            {
                mRate *= entry.Rate;
                mLow = MathF.Min(mLow, entry.LowPass);
                mGain *= entry.Gain;
            }

            if (entry.AffectUi)
            {
                uRate *= entry.Rate;
                uLow = MathF.Min(uLow, entry.LowPass);
                uGain *= entry.Gain;
            }
        }

        var world = ClampChannel(wRate, wLow, wGain);
        var music = ClampChannel(mRate, mLow, mGain);
        var ui = ClampChannel(uRate, uLow, uGain);
        Current = world == AudioChannelModulation.Identity && music == AudioChannelModulation.Identity && ui == AudioChannelModulation.Identity
            ? null : new AudioModulationSnapshot(world, music, ui);
        _player?.Invoke(Current);
    }

    /// <summary>
    /// Clamps composed channel multipliers to the valid audio ranges.
    /// </summary>
    static AudioChannelModulation ClampChannel(float rate, float lowPass, float gain) => new(
        Math.Clamp(rate, AudioModulation.MinRate, AudioModulation.MaxRate),
        Math.Clamp(lowPass, AudioModulation.MinLowPass, 1),
        Math.Clamp(gain, 0, AudioModulation.MaxGain));
}
