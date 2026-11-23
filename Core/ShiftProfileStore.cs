using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FH6Telemetry;

internal sealed class ShiftProfileStore
{
    private const int MaximumProfiles = 1024;
    private readonly string? _path;
    private readonly List<Profile> _profiles;
    private Task<bool>? _write;
    private int _version;
    private int _writtenVersion;
    private int _writingVersion;

    public ShiftProfileStore(string? path)
    {
        _path = path is null ? null : Path.GetFullPath(path);
        _profiles = Load(_path);
    }

    public Profile? Find(ShiftVehicleConfiguration vehicle) => _profiles.Find(profile => profile.Vehicle.Matches(vehicle));

    public Profile Remember(ShiftVehicleConfiguration vehicle, double[] ratios, AdaptiveShiftStrategy.OutputBin[] curve)
    {
        Forget(vehicle);
        var profile = new Profile(vehicle, ratios, curve);
        _profiles.Insert(0, profile);
        if (_profiles.Count > MaximumProfiles) _profiles.RemoveAt(MaximumProfiles);
        _version++;
        return profile;
    }

    public void Forget(ShiftVehicleConfiguration vehicle)
    {
        if (_profiles.RemoveAll(profile => profile.Vehicle.Matches(vehicle)) > 0) _version++;
    }

    public void Save(bool flush = false)
    {
        if (_path is not { } path) return;
        if (_write is { } write)
        {
            if (!flush && !write.IsCompleted) return;
            if (write.GetAwaiter().GetResult()) _writtenVersion = _writingVersion;
            _write = null;
        }
        if (_version == _writtenVersion) return;

        var profiles = _profiles.ToArray();
        _writingVersion = _version;
        if (flush) Write(path, profiles);
        else _write = Task.Run(() => Write(path, profiles));
    }

    private static List<Profile> Load(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return [];
            var profiles = JsonSerializer.Deserialize<Profile[]>(File.ReadAllBytes(path));
            return profiles?.Where(IsValid).Take(MaximumProfiles).ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static bool IsValid(Profile? profile) =>
        profile is { Vehicle.Ordinal: > 0, Vehicle.RoundedMaxRpm: >= 500 and <= 100_000,
            Ratios.Length: AutomaticShiftSettings.MaximumForwardGears + 1, Curve.Length: AdaptiveShiftStrategy.BinCount }
        && profile.Ratios.All(ratio => ratio is >= 0 and <= 5000)
        && profile.Curve.All(bin => bin.Fraction is >= 0 and <= 1.005 && bin.Power is >= 0 and <= 10_000);

    private static bool Write(string path, Profile[] profiles)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(profiles));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    internal sealed record Profile(ShiftVehicleConfiguration Vehicle, double[] Ratios, AdaptiveShiftStrategy.OutputBin[] Curve);
}
