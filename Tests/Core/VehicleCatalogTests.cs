using FH6Telemetry;

internal static class VehicleCatalogTests
{
    public static void Register(Action<string, Action> run)
    {
        run("内置车型副本可直接按 Data Out 车型编号查询", () =>
        {
            Check(VehicleCatalog.Count >= 691, "内置副本应保留原有车型，实际 " + VehicleCatalog.Count);
            Check(VehicleCatalog.Find(2740) is not null, "应能按车型编号 2740 查到车辆档案");
            Check(VehicleCatalog.Find(4232) is { Name: "2005 保时捷 Cayman WTA", NumGears: 6 }, "应能从内置副本查到 4232 的车名和原厂挡数");
            Check(VehicleCatalog.Find(0) is null && VehicleCatalog.Find(-5) is null, "非法编号应返回空");
            Check(VehicleCatalog.Find(999999) is null, "未收录编号应返回空");
            Check(VehicleCatalog.DisplayName(999999).Length == 0, "未收录编号不应生成假车名");
        });

        run("已知车型返回车厂、型号、年份、产地与动力详细资料", () =>
        {
            var abarth = VehicleCatalog.Find(2740) ?? throw new InvalidOperationException("缺少 2740 档案");
            Check(abarth.Name == "2017 阿巴斯 124 Spider", "中文车名不正确：" + abarth.Name);
            Check(abarth.NameEn == "2017 Abarth 124 Spider", "英文车名不正确：" + abarth.NameEn);
            Check(abarth.MakeName == "阿巴斯" && abarth.MakeEn == "Abarth", "车厂信息不正确");
            Check(abarth.Model == "124 Spider" && abarth.Year == 2017, "型号/年份不正确：" + abarth.Model + " / " + abarth.Year);
            Check(abarth.CountryName == "意大利", "产地不正确：" + abarth.CountryName);
            Check(abarth.DriveName == "后驱", "驱动形式不正确：" + abarth.DriveName);
            Check(abarth.CylinderText == "4 缸", "气缸数文本不正确：" + abarth.CylinderText);
            Check(abarth.AspirationName == "涡轮增压" && abarth.EngineConfigName == "直列", "发动机描述不正确");
            Check(Math.Abs(abarth.DisplacementCc - 1368) < 0.5, "排量不正确：" + abarth.DisplacementCc);
            Check(Math.Abs(abarth.CurbWeightKg - 1123.5) < 0.2, "整备质量不正确：" + abarth.CurbWeightKg);
            Check(abarth.NumGears == 6, "挡数不正确：" + abarth.NumGears);
            Check(Math.Abs(abarth.PeakPowerKw - 123) < 0.11, "标称功率不正确：" + abarth.PeakPowerKw);
            Check(Math.Abs(abarth.PeakTorqueNm - 249.7) < 0.11, "标称扭矩不正确：" + abarth.PeakTorqueNm);
            Check(abarth.WeightDistributionText == "54/46", "前后配重文本不正确：" + abarth.WeightDistributionText);
            Check(abarth.FrontTireText == "205/45 R17", "前轮胎规格不正确：" + abarth.FrontTireText);

            var beat = VehicleCatalog.Find(3852) ?? throw new InvalidOperationException("缺少 3852 档案");
            Check(beat.Name == "1991 本田 Beat" && beat.Model == "Beat", "本田 Beat 档案不正确：" + beat.Name);
            var classic = VehicleCatalog.Find(247) ?? throw new InvalidOperationException("缺少 247 档案");
            Check(classic.Model == "2000GT" && classic.MakeName == "丰田" && classic.Year == 1969,
                "丰田 2000GT 档案不正确：" + classic.Name);
        });

        run("电动车型显示电动机，不虚构排量与进气形式", () =>
        {
            var nevera = VehicleCatalog.Find(3625) ?? throw new InvalidOperationException("缺少 3625 档案");
            Check(nevera.IsElectric, "Rimac Nevera 应为电动车型");
            Check(nevera.CylinderText == "电动机", "电动车气缸文本应为电动机：" + nevera.CylinderText);
            Check(Math.Abs(nevera.PeakPowerKw - 1411.5) < 0.2, "电动车标称功率不正确：" + nevera.PeakPowerKw);
            Check(Math.Abs(nevera.DisplacementCc) < 0.001, "电动车不应有排量：" + nevera.DisplacementCc);
        });

        run("全部车型名称完整且不含内部标记", () =>
        {
            var count = 0;
            foreach (var (carOrdinal, info) in VehicleCatalog.All)
            {
                count++;
                Check(info.CarId == carOrdinal, "档案编号与索引不一致：" + carOrdinal);
                Check(info.Name.Length > 0, "车型 " + carOrdinal + " 缺少显示名");
                Check(info.Model.Length > 0, "车型 " + carOrdinal + " 缺少型号");
                Check(info.MakeName.Length > 0, "车型 " + carOrdinal + " 缺少车厂");
                Check(!info.Name.Contains("  "), "车型 " + carOrdinal + " 车名含连续空格：" + info.Name);
                Check(!info.Name.Contains("Traffic"), "车型 " + carOrdinal + " 车名含内部标记：" + info.Name);
                Check(!info.Name.Contains("Formula Drift Formula"), "车型 " + carOrdinal + " 车名重复：" + info.Name);
                Check(!info.Name.Contains("DeBerti DeBerti") && !info.Name.Contains("AMG AMG"),
                    "车型 " + carOrdinal + " 车厂重复：" + info.Name);
            }
            Check(count == VehicleCatalog.Count, "目录数量不一致");
        });
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
