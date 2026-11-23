using System;
using System.IO;

namespace FH6Telemetry;

/// <summary>Opens the standalone profile and optionally imports earlier telemetry into an empty profile.</summary>
internal static class StandaloneTelemetryStorage
{
    public static TelemetrySettingsFile Open(string directory, string? legacyDirectory, out string? notice, string? portableDirectory = null)
    {
        var settingsFile = SelectStorage(directory, portableDirectory, out notice);
        if (legacyDirectory is null || File.Exists(settingsFile.FilePath)) return settingsFile;

        var legacySettingsFile = new TelemetrySettingsFile(legacyDirectory);
        var settings = legacySettingsFile.Load();
        var oldRecords = legacySettingsFile.GetRecordDirectory(settings);
        var oldIndex = Path.Combine(oldRecords, "records.json");
        if (!File.Exists(legacySettingsFile.FilePath) && !File.Exists(oldIndex)) return settingsFile;

        try
        {
            settings.DataDirectory = "";
            CopyHistory(oldIndex, oldRecords, settingsFile.GetRecordDirectory(settings));
            notice = settingsFile.Save(settings) ?? notice;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            notice = TelemetryText.Format("StorageCopyFailed", ex.Message);
        }
        return settingsFile;
    }

    private static TelemetrySettingsFile SelectStorage(string directory, string? portableDirectory, out string? notice)
    {
        notice = null;
        var standard = new TelemetrySettingsFile(directory);
        if (portableDirectory is null) return standard;

        var portable = new TelemetrySettingsFile(portableDirectory);
        var useExistingPortable = !File.Exists(standard.FilePath) && File.Exists(portable.FilePath);
        try
        {
            EnsureWritable(useExistingPortable ? portableDirectory : directory);
            return useExistingPortable ? portable : standard;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EnsureWritable(useExistingPortable ? directory : portableDirectory);
            notice = useExistingPortable
                ? TelemetryText.Get("StorageDirFallback")
                : TelemetryText.Get("StorageAppDataFallback");
            return useExistingPortable ? standard : portable;
        }
    }

    private static void EnsureWritable(string directory)
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, $".write-{Guid.NewGuid():N}.tmp");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    private static void CopyHistory(string oldIndex, string oldRecords, string newRecords)
    {
        var index = Path.Combine(newRecords, "records.json");
        if (!File.Exists(oldIndex) || File.Exists(index)) return;

        var oldSamples = Path.Combine(oldRecords, "samples");
        var samples = Path.Combine(newRecords, "samples");
        Directory.CreateDirectory(samples);
        if (Directory.Exists(oldSamples))
        {
            foreach (var sample in Directory.EnumerateFiles(oldSamples, "*.csv"))
                CopyIfMissing(sample, Path.Combine(samples, Path.GetFileName(sample)));
        }
        CopyIfMissing(oldIndex, index);
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (File.Exists(destination)) return;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, destination);
        }
        catch (IOException) when (File.Exists(destination)) { }
        finally { File.Delete(temporary); }
    }
}
