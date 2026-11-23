using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FH6Telemetry;

/// <summary>
/// 本地测试记录存储。采用“版本化 JSON 索引 + 每次测试独立 CSV 采样文件”的分层方案：
/// - 索引很小，启动读取快，单条记录可原子更新；
/// - 原始采样独立文件，可整体删除或按保留策略清理，不撑大索引；
/// - 全部通过 System.Text.Json DOM 读写，避免单文件发布下反射序列化被裁剪的风险。
///
/// 评估过 SQLite：主工程当前不引用 Microsoft.Data.Sqlite，引入会带来约 10 MB 原生依赖并影响
/// 单文件发布体积；本模块的数据量级（数百条记录）用 JSON 索引完全够用，且便于用户直接备份。
/// </summary>
public sealed class TelemetryStore
{
    public const int SchemaVersion = 1;
    private readonly string _root;
    private readonly string _recordsPath;
    private readonly string _samplesDir;
    private readonly object _sync = new();
    private readonly List<TestResult> _records = new();

    public IReadOnlyList<TestResult> Records { get { lock (_sync) return _records.ToArray(); } }
    public string RootPath => _root;
    public string? LastError { get; private set; }

    public TelemetryStore(string directory)
    {
        _root = directory;
        _recordsPath = Path.Combine(_root, "records.json");
        _samplesDir = Path.Combine(_root, "samples");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_samplesDir);
        Load();
    }

    private void Load()
    {
        lock (_sync)
        {
            _records.Clear();
            if (!File.Exists(_recordsPath)) return;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_recordsPath));
                if (document.RootElement.ValueKind != JsonValueKind.Object) return;
                if (!document.RootElement.TryGetProperty("records", out var array) || array.ValueKind != JsonValueKind.Array) return;
                foreach (var element in array.EnumerateArray())
                {
                    var record = ReadRecord(element);
                    if (record is not null) _records.Add(record);
                }
            }
            catch (Exception ex) { LastError = TelemetryText.Format("StoreLoadFail", ex.Message); }
        }
    }

    public void Save(TestResult result, IReadOnlyList<TelemetrySample>? samples = null, TelemetrySettings? settings = null)
    {
        lock (_sync)
        {
            var records = _records.Append(result).ToArray();
            var max = Math.Max(1, settings?.MaxStoredRecords ?? 500);
            var retained = records.Skip(Math.Max(0, records.Length - max)).ToArray();
            if (CommitIndex(retained) && samples is { Count: > 0 }) TryWriteSamples(result.Id, samples);
        }
    }

    public void Update(TestResult result)
    {
        lock (_sync)
        {
            var index = _records.FindIndex(r => r.Id == result.Id);
            if (index < 0) return;
            var before = _records[index];
            _records[index] = result;
            if (!WriteIndex()) _records[index] = before;
        }
    }

    public void Delete(string id)
    {
        lock (_sync)
        {
            if (_records.Any(record => record.Id == id)) CommitIndex(_records.Where(record => record.Id != id).ToArray());
        }
    }

    public void DeleteAll()
    {
        lock (_sync) CommitIndex([]);
    }

    public int PruneHistory(DateTime cutoff, int maxRecords)
    {
        lock (_sync)
        {
            var keep = _records.Where(record => record.RecordedAtLocal >= cutoff)
                .OrderByDescending(record => record.RecordedAtLocal).Take(Math.Max(1, maxRecords))
                .Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
            var retained = _records.Where(record => keep.Contains(record.Id)).ToArray();
            var remove = _records.Count - retained.Length;
            return remove > 0 && CommitIndex(retained) ? remove : 0;
        }
    }

    private bool CommitIndex(TestResult[] records)
    {
        var original = _records.ToArray();
        _records.Clear();
        _records.AddRange(records);
        if (!WriteIndex())
        {
            _records.Clear();
            _records.AddRange(original);
            return false;
        }
        var keep = records.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var record in original)
            if (!keep.Contains(record.Id)) TryDeleteSamples(record.Id);
        return true;
    }

    private bool WriteIndex()
    {
        try
        {
            var records = new JsonArray();
            foreach (var record in _records) records.Add(WriteRecord(record));
            var root = new JsonObject
            {
                ["version"] = SchemaVersion,
                ["savedAt"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                ["records"] = records,
            };
            // 原子写入：先写临时文件再替换，异常退出不会留下半截索引。
            // File.Replace 在部分文件系统/受限环境下会被拒绝（例如 exFAT 或沙箱环境），
            // 此时退化为带覆盖的 Move，仍然先写完整临时文件、不会留下半截 JSON。
            var temp = _recordsPath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            if (File.Exists(_recordsPath))
            {
                try { File.Replace(temp, _recordsPath, null); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    File.Move(temp, _recordsPath, overwrite: true);
                }
            }
            else File.Move(temp, _recordsPath);
            LastError = null;
            return true;
        }
        catch (Exception ex) { LastError = TelemetryText.Format("StoreSaveFail", ex.Message); return false; }
    }

    private static JsonObject WriteRecord(TestResult r)
    {
        var parameters = new JsonObject();
        foreach (var (key, value) in r.VehicleParameters) parameters[key] = value;
        return new JsonObject
        {
            ["id"] = r.Id, ["key"] = r.Key, ["title"] = r.Title, ["kind"] = r.Kind.ToString(),
            ["startValue"] = r.StartValue, ["endValue"] = r.EndValue,
            ["durationSeconds"] = Math.Round(r.DurationSeconds, 4),
            ["distanceMeters"] = Math.Round(r.DistanceMeters, 3),
            ["startSpeedKmh"] = Math.Round(r.StartSpeedKmh, 3), ["endSpeedKmh"] = Math.Round(r.EndSpeedKmh, 3),
            ["peakSpeedKmh"] = Math.Round(r.PeakSpeedKmh, 3),
            ["maxAccelerationG"] = Math.Round(r.MaxAccelerationG, 3), ["maxBrakingG"] = Math.Round(r.MaxBrakingG, 3),
            ["peakPowerKw"] = Math.Round(r.PeakPowerKw, 3), ["peakTorqueNm"] = Math.Round(r.PeakTorqueNm, 3),
            ["peakRpm"] = Math.Round(r.PeakRpm, 1),
            ["carOrdinal"] = r.CarOrdinal, ["carName"] = r.CarName, ["carClass"] = r.CarClass,
            ["carPerformanceIndex"] = r.CarPerformanceIndex, ["drivetrainType"] = r.DrivetrainType,
            ["gearAtFinish"] = r.GearAtFinish, ["sampleCount"] = r.SampleCount,
            ["valid"] = r.Valid, ["invalidReason"] = r.InvalidReason,
            ["recordedAt"] = r.RecordedAtLocal.ToString("O", CultureInfo.InvariantCulture),
            ["note"] = r.Note,
            ["vehicleParameters"] = parameters,
            ["driveAssist"] = r.DriveAssist is { } assist ? JsonSerializer.SerializeToNode(assist) : null,
        };
    }

    private static TestResult? ReadRecord(JsonElement element)
    {
        try
        {
            var parameters = new Dictionary<string, string>();
            if (element.TryGetProperty("vehicleParameters", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var property in p.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String) parameters[property.Name] = property.Value.GetString() ?? "";

            return new TestResult
            {
                Id = Text(element, "id"), Key = Text(element, "key"), Title = Text(element, "title"),
                Kind = Enum.TryParse<PerformanceTestKind>(Text(element, "kind"), out var kind) ? kind : PerformanceTestKind.Acceleration,
                StartValue = Number(element, "startValue"), EndValue = Number(element, "endValue"),
                DurationSeconds = Number(element, "durationSeconds"), DistanceMeters = Number(element, "distanceMeters"),
                StartSpeedKmh = Number(element, "startSpeedKmh"), EndSpeedKmh = Number(element, "endSpeedKmh"),
                PeakSpeedKmh = Number(element, "peakSpeedKmh"),
                MaxAccelerationG = Number(element, "maxAccelerationG"), MaxBrakingG = Number(element, "maxBrakingG"),
                PeakPowerKw = Number(element, "peakPowerKw"), PeakTorqueNm = Number(element, "peakTorqueNm"),
                PeakRpm = Number(element, "peakRpm"),
                CarOrdinal = (int)Number(element, "carOrdinal"), CarName = Text(element, "carName"),
                CarClass = (int)Number(element, "carClass"), CarPerformanceIndex = (int)Number(element, "carPerformanceIndex"),
                DrivetrainType = (int)Number(element, "drivetrainType"),
                GearAtFinish = (int)Number(element, "gearAtFinish"), SampleCount = (long)Number(element, "sampleCount"),
                Valid = element.TryGetProperty("valid", out var v) && v.ValueKind == JsonValueKind.True,
                InvalidReason = Text(element, "invalidReason"),
                RecordedAtLocal = DateTime.TryParse(Text(element, "recordedAt"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.Now,
                Note = Text(element, "note"),
                VehicleParameters = parameters,
                DriveAssist = ReadDriveAssist(element),
            };
        }
        catch { return null; }
    }

    private static DriveAssistRun? ReadDriveAssist(JsonElement element)
    {
        if (!element.TryGetProperty("driveAssist", out var value) || value.ValueKind != JsonValueKind.Object) return null;
        try
        {
            var run = value.Deserialize<DriveAssistRun>();
            if (run is not { Seconds: > 0, Shares: not null } || !Enum.IsDefined(run.Mode)) return null;
            return run with { Shares = run.Shares.Where(s => s.Feature != 0 && Enum.IsDefined(s.Feature) && Enum.IsDefined(s.Phase) &&
                s.Seconds > 0 && s.PhaseSeconds >= s.Seconds).ToArray() };
        }
        catch (JsonException) { return null; }
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static double Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;

    private void TryWriteSamples(string id, IReadOnlyList<TelemetrySample> samples)
    {
        try
        {
            var path = SampleFilePath(id);
            if (path is null) return;
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine("timestamp_ms,time_s,speed_kmh,rpm,power_kw,torque_nm,throttle,brake,clutch,steer,gear,long_g,lat_g,gear_change");
            var first = samples[0].TimestampMs;
            var lastGear = -1;
            foreach (var sample in samples)
            {
                var time = unchecked(sample.TimestampMs - first) / 1000.0;
                var change = lastGear >= 0 && sample.Gear != lastGear ? 1 : 0;
                lastGear = sample.Gear;
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{sample.TimestampMs},{time:0.###},{sample.SpeedKmh:0.###},{sample.Rpm:0.#},{sample.PowerKw:0.###},{sample.TorqueNm:0.###},{sample.Throttle:0.###},{sample.Brake:0.###},{sample.Clutch:0.###},{sample.Steer:0.###},{sample.Gear},0,0,{change}"));
            }
        }
        catch (Exception ex) { LastError = TelemetryText.Format("StoreSamplesFail", ex.Message); }
    }

    private void TryDeleteSamples(string id)
    {
        try
        {
            var path = SampleFilePath(id);
            if (path is not null) File.Delete(path);
        }
        catch { /* 删除失败不影响索引 */ }
    }

    public string? SamplePath(string id)
    {
        var path = SampleFilePath(id);
        return File.Exists(path) ? path : null;
    }

    private string? SampleFilePath(string id) => string.IsNullOrWhiteSpace(id) || id is "." or ".." ||
        id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.IndexOfAny(['/', '\\', ':']) >= 0
        ? null : Path.Combine(_samplesDir, id + ".csv");

    public string ExportCsv(IEnumerable<TestResult> records)
    {
        var path = Path.Combine(_root, $"export-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var builder = new StringBuilder();
        builder.AppendLine("time,key,title,kind,vehicle,pi,duration_s,distance_m,end_speed_kmh,peak_speed_kmh,peak_power_kw,peak_torque_nm,max_accel_g,max_brake_g,valid,note");
        foreach (var r in records)
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.RecordedAtLocal:yyyy-MM-dd HH:mm:ss},{r.Key},{r.Title},{r.Kind},{r.CarName},{r.CarPerformanceIndex}," +
                $"{r.DurationSeconds:0.###},{r.DistanceMeters:0.##},{r.EndSpeedKmh:0.##},{r.PeakSpeedKmh:0.##}," +
                $"{r.PeakPowerKw:0.##},{r.PeakTorqueNm:0.##},{r.MaxAccelerationG:0.###},{r.MaxBrakingG:0.###},{r.Valid},\"{r.Note.Replace("\"", "\"\"")}\""));
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return path;
    }

    public string ExportJson(IEnumerable<TestResult> records)
    {
        var path = Path.Combine(_root, $"export-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var array = new JsonArray();
        foreach (var record in records) array.Add(WriteRecord(record));
        var root = new JsonObject { ["version"] = SchemaVersion, ["exportedAt"] = DateTimeOffset.Now.ToString("O"), ["records"] = array };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return path;
    }

    /// <summary>生成 HTML 分析报告（可在浏览器中直接打印为 PDF）。</summary>
    public string ExportHtmlReport(IEnumerable<TestResult> records, IReadOnlyDictionary<string, string>? parameterComparison = null)
    {
        var list = records.ToList();
        var path = Path.Combine(_root, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.html");
        var builder = new StringBuilder();
        var lang = TelemetryText.HtmlLang(TelemetryText.CurrentLanguage);
        var reportTitle = Escape(TelemetryText.Get("ReportTitle"));
        builder.AppendLine($"<!DOCTYPE html><html lang=\"{lang}\"><head><meta charset=\"utf-8\"><title>{reportTitle}</title>");
        builder.AppendLine("<style>body{font-family:system-ui,'Microsoft YaHei UI','Yu Gothic UI','Malgun Gothic',sans-serif;background:#0f1420;color:#e6ebf5;margin:32px}h1{color:#ff9f43}h2{margin-top:28px;border-bottom:1px solid #2a3550;padding-bottom:6px}table{border-collapse:collapse;width:100%;margin-top:12px}th,td{border:1px solid #2a3550;padding:6px 10px;font-size:13px;text-align:left}th{background:#1a2233}.dim{color:#8a96ad;font-size:12px}</style></head><body>");
        builder.AppendLine($"<h1>{reportTitle}</h1><p class=\"dim\">{Escape(TelemetryText.Format("ReportGeneratedFmt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), list.Count))}</p>");
        if (parameterComparison is { Count: > 0 })
        {
            builder.AppendLine($"<h2>{Escape(TelemetryText.Get("ReportSnapshotTitle"))}</h2><table><tr><th>{Escape(TelemetryText.Get("ReportColParam"))}</th><th>{Escape(TelemetryText.Get("ReportColVal"))}</th></tr>");
            foreach (var (key, value) in parameterComparison) builder.AppendLine($"<tr><td>{Escape(key)}</td><td>{Escape(value)}</td></tr>");
            builder.AppendLine("</table>");
        }
        foreach (var group in list.GroupBy(r => r.CarName.Length > 0 ? r.CarName : TelemetryText.Format("CarUnknownTitle", r.CarOrdinal)))
        {
            builder.AppendLine($"<h2>{Escape(TelemetryText.Format("ReportCarFmt", group.Key))}</h2>");
            builder.AppendLine($"<table><tr><th>{Escape(TelemetryText.Get("ReportColTest"))}</th><th>{Escape(TelemetryText.Get("ReportColResult"))}</th><th>{Escape(TelemetryText.Get("ReportColEndSpeed"))}</th><th>{Escape(TelemetryText.Get("ReportColDist"))}</th><th>{Escape(TelemetryText.Get("ReportColPeakPower"))}</th><th>{Escape(TelemetryText.Get("ReportColPeakTorque"))}</th><th>{Escape(TelemetryText.Get("ReportColMaxG"))}</th><th>{Escape(TelemetryText.Get("ReportColTime"))}</th><th>{Escape(TelemetryText.Get("ReportColValid"))}</th></tr>");
            foreach (var r in group.OrderBy(r => r.RecordedAtLocal))
            {
                var value = r.Kind == PerformanceTestKind.Braking
                    ? $"{r.DurationSeconds:0.00} s / {r.DistanceMeters:0.0} m"
                    : r.Kind == PerformanceTestKind.TopSpeed ? $"{r.PeakSpeedKmh:0.0} km/h" : $"{r.DurationSeconds:0.000} s";
                var validText = r.Valid
                    ? Escape(TelemetryText.Get("YesWord"))
                    : Escape(TelemetryText.Format("NoReasonFmt", r.InvalidReason));
                builder.AppendLine($"<tr><td>{Escape(r.LocalizedTitle)}</td><td>{value}</td><td>{r.EndSpeedKmh:0.0} km/h</td>" +
                                   $"<td>{r.DistanceMeters:0.0} m</td><td>{r.PeakPowerKw:0.0} kW</td><td>{r.PeakTorqueNm:0.0} N·m</td>" +
                                   $"<td>{r.MaxAccelerationG:0.00}</td><td>{r.RecordedAtLocal:yyyy-MM-dd HH:mm}</td><td>{validText}</td></tr>");
            }
            builder.AppendLine("</table>");
        }
        builder.AppendLine($"<p class=\"dim\">{Escape(TelemetryText.Get("ReportDisclaimer"))}</p></body></html>");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public string Backup()
    {
        var target = Path.Combine(_root, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(target);
        lock (_sync)
        {
            if (File.Exists(_recordsPath)) File.Copy(_recordsPath, Path.Combine(target, "records.json"), true);
        }
        foreach (var file in Directory.EnumerateFiles(_samplesDir, "*.csv"))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        return target;
    }

    public string DescribeIntegrity()
    {
        lock (_sync)
        {
            var missing = _records.Count(r => SamplePath(r.Id) is null);
            return TelemetryText.Format("StoreIntegrityFmt", _records.Count, missing, _root);
        }
    }
}
