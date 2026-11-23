using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace FH6Telemetry;

/// <summary>
/// 一条来自游戏车型数据库的静态车辆档案。数据由 <see cref="VehicleCatalogData"/> 携带，
/// 原始字段来自游戏运行时的只读数据表 <c>Data_Car</c> 与 <c>List_*</c> 查阅表。
///
/// 注意：这是车辆**原厂静态资料**（年份、车厂、产地、发动机、整备质量等），不是 Data Out
/// 的实时数据；其中的功率/扭矩/极速/红线是游戏数据库里的仿真标称值，与实车实测值可能不同。
/// </summary>
public sealed record VehicleInfo(
    int CarId,
    int Year,
    int MakeId,
    string Model,
    string MediaName,
    int CountryId,
    int DriveTypeId,
    int Cylinders,
    int Rotors,
    int AspirationId,
    int EngineConfigId,
    double DisplacementCc,
    double CurbWeightKg,
    double WeightDistribution,
    int NumGears,
    double PeakPowerKw,
    double PeakTorqueNm,
    double TopSpeedKmh,
    double RedlineRpm,
    double FrontTireWidthMm,
    double FrontTireAspect,
    double FrontWheelDiameterIn,
    double RearTireWidthMm,
    double RearTireAspect,
    double RearWheelDiameterIn,
    string MakeZh,
    string MakeEn,
    string CountryZh,
    string CountryEn)
{
    /// <summary>中文显示名：<c>2017 阿巴斯 124 Spider</c>。未知字段会自动省略。</summary>
    public string Name
    {
        get
        {
            var text = string.Join(' ', new[] { Year > 0 ? Year.ToString(CultureInfo.InvariantCulture) : "", MakeName, Model }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            return text;
        }
    }

    /// <summary>英文显示名：<c>2017 Abarth 124 Spider</c>，用于对照游戏内的英文名称。</summary>
    public string NameEn
    {
        get
        {
            var text = string.Join(' ', new[] { Year > 0 ? Year.ToString(CultureInfo.InvariantCulture) : "", MakeEn, Model }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            return text;
        }
    }

    public string MakeName => TelemetryText.CurrentLanguage == TelemetryLanguage.ZhCn
        ? (string.IsNullOrWhiteSpace(MakeZh) ? MakeEn : MakeZh)
        : (string.IsNullOrWhiteSpace(MakeEn) ? MakeZh : MakeEn);
    public string CountryName
    {
        get
        {
            var key = "Country" + CountryId.ToString(CultureInfo.InvariantCulture);
            var localized = TelemetryText.Get(key);
            if (!string.Equals(localized, key, StringComparison.Ordinal)) return localized;
            return TelemetryText.CurrentLanguage == TelemetryLanguage.ZhCn
                ? (string.IsNullOrWhiteSpace(CountryZh) ? CountryEn : CountryZh)
                : (string.IsNullOrWhiteSpace(CountryEn) ? CountryZh : CountryEn);
        }
    }
    public string DriveName => VehicleCatalog.DriveName(DriveTypeId);
    public string AspirationName => VehicleCatalog.AspirationName(AspirationId);
    public string EngineConfigName => VehicleCatalog.EngineConfigName(EngineConfigId);
    public bool IsElectric => EngineConfigId == 6;

    /// <summary>气缸数文本，转子发动机为“N 转子”，电动车为“电动机”。</summary>
    public string CylinderText => IsElectric
        ? TelemetryText.Get("CatElectricMotor")
        : Rotors > 0 ? TelemetryText.Format("CatRotorsFmt", Rotors)
        : Cylinders > 0 ? TelemetryText.Format("CatCylindersFmt", Cylinders)
        : "—";

    /// <summary>前轴重量比文本，例如 54/46（前/后）。</summary>
    public string WeightDistributionText => WeightDistribution is > 0 and < 1
        ? (WeightDistribution * 100).ToString("0", CultureInfo.InvariantCulture) + "/" +
          ((1 - WeightDistribution) * 100).ToString("0", CultureInfo.InvariantCulture)
        : "—";

    public string FrontTireText => FormatTire(FrontTireWidthMm, FrontTireAspect, FrontWheelDiameterIn);
    public string RearTireText => FormatTire(RearTireWidthMm, RearTireAspect, RearWheelDiameterIn);

    public string TireText => FrontTireText.Length == 0 && RearTireText.Length == 0 ? "—"
        : FrontTireText == RearTireText ? FrontTireText
        : FrontTireText + " / " + RearTireText;

    private static string FormatTire(double width, double aspect, double diameter)
    {
        var w = (int)Math.Round(width);
        var a = (int)Math.Round(aspect);
        var d = (int)Math.Round(diameter);
        return w > 0 && a > 0 && d > 0 ? $"{w}/{a} R{d}" : "";
    }

    public override string ToString() => Name;
}

/// <summary>
/// 通过编译进程序的车型副本，将 Data Out 的 <c>CarOrdinal</c> 对应到车厂、型号、
/// 年份、产地、动力与质量等静态档案。运行时只查询本地字典。
/// </summary>
public static class VehicleCatalog
{
    private sealed record Catalog(
        Dictionary<int, VehicleInfo> Cars,
        Dictionary<int, (string Zh, string En)> Makes,
        Dictionary<int, (string Zh, string En)> Countries,
        Dictionary<int, string> Aspirations,
        Dictionary<int, string> EngineConfigs,
        Dictionary<int, string> Drives);

    private static readonly Lazy<Catalog> Data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>目录收录的车型数量。</summary>
    public static int Count => Data.Value.Cars.Count;

    /// <summary>全部档案（只读），按 CarOrdinal 索引。</summary>
    public static IReadOnlyDictionary<int, VehicleInfo> All => Data.Value.Cars;

    /// <summary>按 Data Out 的 <c>CarOrdinal</c>（= 游戏数据库 <c>Data_Car.Id</c>）查询车辆档案。</summary>
    public static VehicleInfo? Find(int carOrdinal)
    {
        if (carOrdinal <= 0) return null;
        return Data.Value.Cars.TryGetValue(carOrdinal, out var info) ? info : null;
    }

    /// <summary>显示名；未收录时返回空字符串，由调用方决定如何回退。</summary>
    public static string DisplayName(int carOrdinal) => Find(carOrdinal)?.Name ?? "";

    /// <summary>车型数据库里的驱动形式（1=前驱 2=后驱 3=四驱，与 Data Out 的 0/1/2 不同）。</summary>
    public static string DriveName(int id) => id switch
    {
        1 => TelemetryText.Get("CatDriveFwd"),
        2 => TelemetryText.Get("CatDriveRwd"),
        3 => TelemetryText.Get("CatDriveAwd"),
        _ => Data.Value.Drives.TryGetValue(id, out var name) ? name : "—",
    };

    public static string AspirationName(int id)
    {
        var key = "CatAsp" + id.ToString(CultureInfo.InvariantCulture);
        var localized = TelemetryText.Get(key);
        if (!string.Equals(localized, key, StringComparison.Ordinal)) return localized;
        return Data.Value.Aspirations.TryGetValue(id, out var name) ? name : "—";
    }

    public static string EngineConfigName(int id)
    {
        var key = "CatEng" + id.ToString(CultureInfo.InvariantCulture);
        var localized = TelemetryText.Get(key);
        if (!string.Equals(localized, key, StringComparison.Ordinal)) return localized;
        return Data.Value.EngineConfigs.TryGetValue(id, out var name) ? name : "—";
    }

    /// <summary>车型数据库里的车厂名（中文优先，其他语言用英文原名）。</summary>
    public static string MakeName(int makeId)
    {
        if (!Data.Value.Makes.TryGetValue(makeId, out var make)) return "";
        return TelemetryText.CurrentLanguage == TelemetryLanguage.ZhCn
            ? (string.IsNullOrWhiteSpace(make.Zh) ? make.En : make.Zh)
            : (string.IsNullOrWhiteSpace(make.En) ? make.Zh : make.En);
    }

    /// <summary>车型数据库里的产地名。</summary>
    public static string CountryName(int countryId)
    {
        var key = "Country" + countryId.ToString(CultureInfo.InvariantCulture);
        var localized = TelemetryText.Get(key);
        if (!string.Equals(localized, key, StringComparison.Ordinal)) return localized;
        if (!Data.Value.Countries.TryGetValue(countryId, out var country)) return "";
        return TelemetryText.CurrentLanguage == TelemetryLanguage.ZhCn
            ? (string.IsNullOrWhiteSpace(country.Zh) ? country.En : country.Zh)
            : (string.IsNullOrWhiteSpace(country.En) ? country.Zh : country.En);
    }

    private static Catalog Load()
    {
        var makes = ParseTriples(VehicleCatalogData.Makes);
        var countries = ParseTriples(VehicleCatalogData.Countries);
        var aspirations = ParsePairs(VehicleCatalogData.Aspirations);
        var engineConfigs = ParsePairs(VehicleCatalogData.EngineConfigs);
        var drives = new Dictionary<int, string>();
        foreach (var (id, (zh, _)) in ParseTriples(VehicleCatalogData.Drives)) drives[id] = zh;

        var cars = new Dictionary<int, VehicleInfo>();
        foreach (var line in SplitLines(VehicleCatalogData.Cars))
        {
            var parts = line.Split('|');
            if (parts.Length < 25 || !int.TryParse(parts[0], out var carId) || carId <= 0) continue;
            var makeId = Int(parts[2]);
            var countryId = Int(parts[5]);
            makes.TryGetValue(makeId, out var make);
            countries.TryGetValue(countryId, out var country);
            var cylinder = Int(parts[7]);
            cars[carId] = new VehicleInfo(
                carId,
                Int(parts[1]),
                makeId,
                parts[3],
                parts[4],
                countryId,
                Int(parts[6]),
                cylinder,
                Int(parts[8]),
                Int(parts[9]),
                Int(parts[10]),
                Num(parts[11]),
                Num(parts[12]),
                Num(parts[13]),
                Int(parts[14]),
                Num(parts[15]),
                Num(parts[16]),
                Num(parts[17]),
                Num(parts[18]),
                Num(parts[19]),
                Num(parts[20]),
                Num(parts[21]),
                Num(parts[22]),
                Num(parts[23]),
                Num(parts[24]),
                make.Zh ?? "",
                make.En ?? "",
                country.Zh ?? "",
                country.En ?? "");
        }

        return new Catalog(cars, makes, countries, aspirations, engineConfigs, drives);
    }

    private static Dictionary<int, (string Zh, string En)> ParseTriples(string block)
    {
        var result = new Dictionary<int, (string, string)>();
        foreach (var line in SplitLines(block))
        {
            var parts = line.Split('|');
            if (parts.Length < 3 || !int.TryParse(parts[0], out var id)) continue;
            result[id] = (parts[1], parts[2]);
        }
        return result;
    }

    private static Dictionary<int, string> ParsePairs(string block)
    {
        var result = new Dictionary<int, string>();
        foreach (var line in SplitLines(block))
        {
            var parts = line.Split('|', 2);
            if (parts.Length < 2 || !int.TryParse(parts[0], out var id)) continue;
            result[id] = parts[1];
        }
        return result;
    }

    private static IEnumerable<string> SplitLines(string block)
    {
        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    private static int Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double Num(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
